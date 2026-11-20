using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PbixMigrator.Services
{
    // Thrown when either the TakeOver or BindToGateway call fails, so callers
    // can distinguish "the XMLA metadata push worked but binding failed" from
    // every other failure mode in the pipeline.
    public class FabricConnectionBindException : Exception
    {
        public string Step { get; }
        public int? StatusCode { get; }
        public string CorrelationId { get; }

        public FabricConnectionBindException(string step, int? statusCode, string correlationId, string message)
            : base(message)
        {
            Step          = step;
            StatusCode    = statusCode;
            CorrelationId = correlationId;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FabricConnectionBinder
    //
    // Run this immediately AFTER the XMLA metadata push finishes (i.e. after
    // MigrateSchemaToLakehouseDirectQuery / SaveStructureOnly's final
    // SaveChanges() returns), for exactly the reason the SSO error earlier in
    // this conversation traced back to: a freshly-added DirectQuery table
    // defaults to Entra ID SSO pass-through, which a service principal can't
    // satisfy. These two calls take ownership of the dataset's data source
    // credentials and re-point them at the pre-created fixed-identity cloud
    // connection (Service Principal auth, SSO explicitly off) instead.
    //
    // Order matters and is enforced by this class, not left to the caller:
    //   1. Default.TakeOver  — the caller becomes the owner of the dataset's
    //      data source credentials. Required before BindToGateway will
    //      accept a new credential/connection for a data source it doesn't
    //      already own.
    //   2. Default.BindToGateway — points the dataset's data source(s) at
    //      the cloud connection object (gatewayObjectId here is Fabric's
    //      virtual-network/cloud-connection gateway id, not an on-prem
    //      gateway; datasourceObjectIds are the specific connection(s) to
    //      bind, e.g. the one created for the Lakehouse SQL endpoint).
    //
    // Docs:
    //   https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/take-over-in-group
    //   https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/bind-to-gateway-in-group
    // ─────────────────────────────────────────────────────────────────────────
    public class FabricConnectionBinder
    {
        private const string ApiBase = "https://api.powerbi.com/v1.0/myorg";

        private readonly HttpClient _http;
        private readonly ILogger<FabricConnectionBinder> _log;

        // Environment identifiers — fixed for this workspace/connection setup.
        // Passed in via constructor rather than hardcoded as consts so a
        // different workspace/connection can reuse this class without a
        // recompile; the values from the request are simply the defaults a
        // caller would supply today.
        private readonly string _workspaceId;
        private readonly string _gatewayObjectId;
        private readonly string _datasourceObjectId;

        public FabricConnectionBinder(
            HttpClient http,
            ILogger<FabricConnectionBinder> log,
            string workspaceId       = "acdcfb13-67d7-4579-93c4-b97a95a0a4b3",
            string gatewayObjectId   = "00000000-0000-0000-0000-000000000000",
            string datasourceObjectId = "daa3b1c5-1699-4583-b5cc-2d0a0c491104")
        {
            _http               = http ?? throw new ArgumentNullException(nameof(http));
            _log                = log  ?? throw new ArgumentNullException(nameof(log));
            _workspaceId        = workspaceId;
            _gatewayObjectId    = gatewayObjectId;
            _datasourceObjectId = datasourceObjectId;
        }

        /// <summary>
        /// Runs TakeOver then BindToGateway, in that order, for the given
        /// dataset. Call this right after the XMLA metadata push completes —
        /// before any report render or refresh attempts to query the new
        /// DirectQuery table(s), so nothing hits the SSO pass-through default
        /// in between.
        /// </summary>
        public async Task BindDatasetToFixedIdentityConnectionAsync(
            string datasetId,
            string bearerToken,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(datasetId))
                throw new ArgumentException("datasetId is required.", nameof(datasetId));
            if (string.IsNullOrWhiteSpace(bearerToken))
                throw new ArgumentException("bearerToken is required.", nameof(bearerToken));

            _log.LogInformation(
                "[FabricConnectionBinder] Starting bind for dataset {datasetId} in workspace {workspaceId} " +
                "→ gateway {gatewayId}, datasource {datasourceId}",
                datasetId, _workspaceId, _gatewayObjectId, _datasourceObjectId);

            await TakeOverAsync(datasetId, bearerToken, ct);
            await BindToGatewayAsync(datasetId, bearerToken, ct);

            _log.LogInformation(
                "[FabricConnectionBinder] Dataset {datasetId} is now bound to the fixed-identity connection — " +
                "SSO pass-through no longer required for its DirectQuery table(s).",
                datasetId);
        }

        // ── Step 1: Default.TakeOver ────────────────────────────────────────
        private async Task TakeOverAsync(string datasetId, string bearerToken, CancellationToken ct)
        {
            var url = $"{ApiBase}/groups/{_workspaceId}/datasets/{datasetId}/Default.TakeOver";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body          = await SafeReadBodyAsync(response, ct);
                var correlationId = ExtractCorrelationId(response);
                _log.LogError(
                    "[FabricConnectionBinder][TakeOver] Failed for dataset {datasetId}: {status} {reason}. " +
                    "CorrelationId={correlationId}. Response body length={bodyLength}.",
                    datasetId, (int)response.StatusCode, response.ReasonPhrase, correlationId, body.Length);

                throw new FabricConnectionBindException(
                    "Default.TakeOver",
                    (int)response.StatusCode,
                    correlationId,
                    $"Default.TakeOver failed for dataset '{datasetId}': HTTP {(int)response.StatusCode} {response.ReasonPhrase}. CorrelationId={correlationId}.");
            }

            _log.LogInformation("[FabricConnectionBinder][TakeOver] Succeeded for dataset {datasetId}.", datasetId);
        }

        // ── Step 2: Default.BindToGateway ───────────────────────────────────
        private async Task BindToGatewayAsync(string datasetId, string bearerToken, CancellationToken ct)
        {
            var url = $"{ApiBase}/groups/{_workspaceId}/datasets/{datasetId}/Default.BindToGateway";

            var payload = new BindToGatewayRequest
            {
                GatewayObjectId     = _gatewayObjectId,
                DatasourceObjectIds = new[] { _datasourceObjectId }
            };
            var json = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body          = await SafeReadBodyAsync(response, ct);
                var correlationId = ExtractCorrelationId(response);
                _log.LogError(
                    "[FabricConnectionBinder][BindToGateway] Failed for dataset {datasetId}: {status} {reason}. " +
                    "CorrelationId={correlationId}. Response body length={bodyLength}.",
                    datasetId, (int)response.StatusCode, response.ReasonPhrase, correlationId, body.Length);

                throw new FabricConnectionBindException(
                    "Default.BindToGateway",
                    (int)response.StatusCode,
                    correlationId,
                    $"Default.BindToGateway failed for dataset '{datasetId}': HTTP {(int)response.StatusCode} {response.ReasonPhrase}. CorrelationId={correlationId}.");
            }

            _log.LogInformation(
                "[FabricConnectionBinder][BindToGateway] Succeeded for dataset {datasetId} → gateway {gatewayId}.",
                datasetId, _gatewayObjectId);
        }

        // Power BI REST errors are almost always small JSON bodies, but never
        // assume — read defensively so a malformed/huge body can't itself
        // throw out of the error-handling path.
        private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch
            {
                return "<failed to read response body>";
            }
        }

        // Power BI's REST APIs surface a correlation/request id for support
        // cases under one of a couple of header names depending on the
        // front-end that served the response; check both.
        private static string ExtractCorrelationId(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("RequestId", out var requestIdValues))
                return string.Join(",", requestIdValues);
            if (response.Headers.TryGetValues("x-ms-request-id", out var msRequestIdValues))
                return string.Join(",", msRequestIdValues);
            return "(none returned)";
        }

        private class BindToGatewayRequest
        {
            [System.Text.Json.Serialization.JsonPropertyName("gatewayObjectId")]
            public string? GatewayObjectId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("datasourceObjectIds")]
            public string[]? DatasourceObjectIds { get; set; }
        }
    }
}
