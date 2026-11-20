using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using PbixMigrator.Models;

namespace PbixMigrator.Services
{
    // Thrown when the Power BI Import API rejects a PBIX because it carries a
    // protected sensitivity label that the current auth couldn't open (app-only
    // auth can never open these; even a delegated user token can hit this if
    // that user isn't covered by the label's policy). Kept as a distinct type
    // so callers (MigrationController) can return a specific, actionable
    // status/message instead of a generic 502.
    public class PowerBiLabelBlockedException : Exception
    {
        public PowerBiLabelBlockedException(string message) : base(message) { }
    }

    public class PowerBIService
    {
        private readonly ILogger<PowerBIService> _log;
        private readonly IHttpClientFactory _httpFactory;
        private const string PbiScope = "https://analysis.windows.net/powerbi/api/.default";
        private static readonly TimeSpan DefaultLakehouseDatasourceDiscoveryTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DefaultLakehouseDatasourceDiscoveryPollDelay = TimeSpan.FromSeconds(2);
        public PowerBIService(ILogger<PowerBIService> log, IHttpClientFactory httpFactory)
        {
            _log = log;
            _httpFactory = httpFactory;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Retry wrapper for transient network failures against the Power BI
        // REST API. "An existing connection was forcibly closed by the remote
        // host" (SocketException 10054) happens when a pooled keep-alive
        // connection from IHttpClientFactory gets closed server-side (or by a
        // proxy/load balancer) between requests — it has nothing to do with
        // auth, payload, or workspace/report IDs, and a fresh attempt on a new
        // connection almost always succeeds. Without a retry, one blip turns
        // a healthy pipeline into a hard 500 at the very last step (embed
        // token generation), even though everything before it — dataset
        // rebuild, SaveChanges — already succeeded.
        //
        // Retries on: HttpRequestException (covers the wrapped SocketException/
        // IOException seen in the logs), TaskCanceledException (client-side
        // timeout), and IOException. Does NOT retry on non-transient HTTP
        // status codes (4xx) — those are handled by the caller's own
        // IsSuccessStatusCode checks after a successful round-trip.
        // ─────────────────────────────────────────────────────────────────────
        private async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<Task<HttpResponseMessage>> sendAsync,
            string description,
            int maxAttempts = 3)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return await sendAsync();
                }
                catch (Exception ex) when (
                    attempt < maxAttempts &&
                    (ex is HttpRequestException
                     || ex is IOException
                     || ex is SocketException
                     || ex is TaskCanceledException))
                {
                    var backoffMs = 500 * attempt;
                    _log.LogWarning(ex,
                        "[PBI] Transient network error on {desc} (attempt {attempt}/{max}) — " +
                        "retrying in {backoff}ms. This is typically a pooled connection being " +
                        "reset by the remote host, not an auth/config problem.",
                        description, attempt, maxAttempts, backoffMs);
                    await Task.Delay(backoffMs);
                }
            }

            // Final attempt: let any exception propagate to the caller as-is,
            // with a message that makes clear retries were already exhausted.
            try
            {
                return await sendAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Power BI REST call failed after {maxAttempts} attempt(s) ({description}). Check backend diagnostics.", ex);
            }
        }

        // ── Resolve the token to use for a given call ───────────────────────────
        // Every Power BI REST call in this file — including pushing the
        // uploaded PBIX into the service via ImportPbixAsync AND generating
        // embed tokens via GenerateEmbedTokenAsync — now runs as the
        // signed-in user via a DELEGATED token, not the service principal.
        // The frontend acquires this token interactively via MSAL (see
        // msalAuth.js signInAndAcquireAllTokens / getReportEmbedAccessToken)
        // and sends it up as PowerBIConfig.UserAccessToken on every request;
        // it is never stored server-side beyond the current operation, and
        // no client-secret/app-only acquisition happens here anymore.
        //
        // Because AAD access tokens are audience+user scoped, this method
        // does not attempt to refresh or re-acquire the token itself if it
        // has expired — that has to happen back on the frontend (silent
        // refresh or a new popup) and be resent. A 401 from Power BI here
        // means the token expired mid-session; surface that to the caller
        // rather than silently falling back to any other auth.
        //
        // (GetDelegatedAccessTokenAsync below via ROPC remains a separate,
        // legacy fallback path used ONLY for sensitivity-label-protected
        // PBIX imports where a stored username/password is explicitly
        // configured. It is unrelated to this method and is NEVER used for
        // embed token generation.)
        private Task<string> GetEffectiveTokenAsync(PowerBIConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.UserAccessToken))
                throw new InvalidOperationException(
                    "No signed-in user token was provided. All Power BI REST calls in this service — " +
                    "including embed token generation — now run as the signed-in user (delegated auth), " +
                    "not a service principal. Sign in with Microsoft on the frontend (SignInGate) and " +
                    "make sure the acquired token is sent as PowerBIConfig.UserAccessToken.");

            return Task.FromResult(cfg.UserAccessToken);
        }

        // ── Acquire AAD token via delegated user (ROPC) ─────────────────────────
        // Only used as an explicit fallback for label-protected PBIX imports —
        // service-principal (app-only) auth cannot open MIP-protected files at
        // all; Azure AD requires a real signed-in user for that. ROPC is the
        // only non-interactive way to get a delegated token from a backend
        // service, but it is legacy and Microsoft is actively deprecating it:
        // it fails hard if the account has MFA or is under Conditional Access,
        // and it requires holding the account's password. Treat this as a
        // stopgap, not the long-term design — the real fix is removing the
        // sensitivity label upstream, or moving to an interactive/on-behalf-of
        // delegated flow with a refresh token instead of a stored password.
        //
        // IMPORTANT: this method is called from exactly one place in this
        // file — the label-blocked retry branch inside ImportPbixAsync.
        // GenerateEmbedTokenAsync never calls this.
        private async Task<string> GetDelegatedAccessTokenAsync(PowerBIConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.UserAccountUpn) || string.IsNullOrWhiteSpace(cfg.UserAccountPassword))
                throw new InvalidOperationException(
                    "Delegated auth was requested but UserAccountUpn/UserAccountPassword are not configured. " +
                    "Set them via user-secrets/environment/Key Vault — never in appsettings.json in plaintext.");

            var app = PublicClientApplicationBuilder
                .Create(cfg.ClientId)
                .WithAuthority($"https://login.microsoftonline.com/{cfg.TenantId}")
                .Build();

            try
            {
                var result = await app.AcquireTokenByUsernamePassword(
                        new[] { PbiScope }, cfg.UserAccountUpn, cfg.UserAccountPassword)
                    .ExecuteAsync();
                return result.AccessToken;
            }
            catch (MsalException ex)
            {
                throw new InvalidOperationException(
                    "ROPC token acquisition failed. This almost always means the account has MFA/Conditional " +
                    "Access enabled (ROPC does not support either), or the credentials are wrong. " +
                    $"AAD error: {ex.ErrorCode}. Check backend diagnostics for details.", ex);
            }
        }

        // ── Resolve workspace: verify the configured ID exists, or auto-discover
        //    the first workspace the SP has access to as a fallback.
        private async Task<string> ResolveWorkspaceIdAsync(
            HttpClient http, string configuredWorkspaceId, ILogger log)
        {
            // 1. Try the configured ID first
            if (!string.IsNullOrWhiteSpace(configuredWorkspaceId))
            {
                var checkUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{configuredWorkspaceId}";
                var checkResp = await SendWithRetryAsync(
                    () => http.GetAsync(checkUrl), $"GET groups/{configuredWorkspaceId}");
                if (checkResp.IsSuccessStatusCode)
                {
                    log.LogInformation("Workspace '{id}' verified OK", configuredWorkspaceId);
                    return configuredWorkspaceId;
                }
                var checkBody = await checkResp.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Selected Power BI workspace '{configuredWorkspaceId}' could not be accessed ({(int)checkResp.StatusCode}). " +
                    $"Confirm the signed-in user has access to that workspace. {checkBody}");
            }

            throw new InvalidOperationException("A Power BI workspace must be selected before migration.");

        }

        // ── Build+send the multipart import request. Factored out so it can be
        //    called a second time with a different bearer token on the label-
        //    blocked ROPC retry path without duplicating the multipart setup.
        private static async Task<HttpResponseMessage> PostImportAsync(
            HttpClient http, string url, byte[] pbixBytes, string reportName)
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(pbixBytes);
            fileContent.Headers.ContentType =
                new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", $"{reportName}.pbix");
            return await http.PostAsync(url, content);
        }

        public async Task ValidateXmlaWorkspaceAsync(PowerBIConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.XmlaEndpoint))
                throw new InvalidOperationException("XMLA endpoint is required for the selected Power BI workspace.");
            var token = await GetEffectiveTokenAsync(cfg);
            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var workspaceId = await ResolveWorkspaceIdAsync(http, cfg.WorkspaceId, _log);
            var response = await SendWithRetryAsync(
                () => http.GetAsync($"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}"),
                "GET selected workspace for XMLA validation");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Selected workspace '{workspaceId}' could not be resolved for XMLA validation ({(int)response.StatusCode}).");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var workspaceName = doc.RootElement.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            var endpoint = cfg.XmlaEndpoint.Trim().TrimEnd('/').TrimEnd(';');
            var endpointWorkspace = Uri.UnescapeDataString(endpoint[(endpoint.LastIndexOf('/') + 1)..]);
            if (string.IsNullOrWhiteSpace(workspaceName) ||
                !string.Equals(endpointWorkspace, workspaceName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"XMLA endpoint workspace '{endpointWorkspace}' does not match selected workspace " +
                    $"'{workspaceName ?? cfg.WorkspaceId}'. Configure the XMLA endpoint for the selected workspace.");
        }

        private async Task VerifyDatasetInWorkspaceAsync(HttpClient http, string workspaceId, string datasetId, string reportName)
        {
            var url = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}/datasets";
            var response = await SendWithRetryAsync(() => http.GetAsync(url), "GET datasets in selected workspace");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Could not discover semantic models in selected workspace '{workspaceId}' " +
                    $"({(int)response.StatusCode}). Verify Power BI API access and try again.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var count = doc.RootElement.GetProperty("value").EnumerateArray().Count(d =>
                string.Equals(d.TryGetProperty("id", out var id) ? id.GetString() : null,
                    datasetId, StringComparison.OrdinalIgnoreCase));
            if (count != 1)
                throw new InvalidOperationException(
                    $"Published semantic model '{reportName}' (dataset '{datasetId}') was not found in selected " +
                    $"workspace '{workspaceId}'. Dataset discovery found {count} matching models.");
        }

        // A protected sensitivity label (MIP) on the PBIX cannot be read by a
        // service-principal auth flow (AcquireTokenForClient) — Microsoft only
        // supports delegated (user) auth for label-protected files. This
        // surfaces from the Import API as MipClientNoPolicy / MipPolicyDoesNotExist
        // / a generic "protected content" message. Without this check it just
        // looks like an unexplained 400/403, so callers waste time suspecting
        // auth/config instead of the label.
        private static bool IsLabelBlockedError(string errBody) =>
            errBody.Contains("MipClientNoPolicy", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("MipPolicyDoesNotExist", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("SensitivityLabel", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("ProtectedByRms", StringComparison.OrdinalIgnoreCase);

        // ── Recover dataset/report IDs after a "Failed" import ────────────────
        // Power BI's Import API triggers an automatic refresh right after
        // import completes. When that refresh fails — most commonly
        // FailedToRefreshPackageAfterImportError, e.g. for models with
        // measure-only/columnless tables the service can't refresh — the
        // import as a whole is reported "Failed", but the dataset and report
        // artifacts are frequently still created in the workspace. Since this
        // pipeline immediately rebuilds the model via XMLA anyway, that first
        // refresh doesn't need to have succeeded — we just need the artifacts
        // to exist so XMLA can fix them up before anyone actually queries them.
        private async Task<(string reportId, string datasetId)?> TryRecoverCreatedArtifactsAsync(
            HttpClient http, string workspaceId, string reportName, HashSet<string> datasetIdsBeforeImport)
        {
            try
            {
                var dsUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}/datasets";
                var dsResp = await http.GetAsync(dsUrl);
                if (!dsResp.IsSuccessStatusCode) return null;
                using var dsDoc = JsonDocument.Parse(await dsResp.Content.ReadAsStringAsync());
                var datasets = dsDoc.RootElement.GetProperty("value").EnumerateArray()
                    .Where(d => string.Equals(
                        d.TryGetProperty("name", out var dn) ? dn.GetString() : null,
                        reportName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (datasets.Count != 1) return null;
                var dataset = datasets[0];
                var datasetId = dataset.GetProperty("id").GetString()!;
                if (datasetIdsBeforeImport.Contains(datasetId)) return null;

                var rptUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}/reports";
                var rptResp = await http.GetAsync(rptUrl);
                if (!rptResp.IsSuccessStatusCode) return null;
                using var rptDoc = JsonDocument.Parse(await rptResp.Content.ReadAsStringAsync());
                var report = rptDoc.RootElement.GetProperty("value").EnumerateArray()
                    .FirstOrDefault(r => string.Equals(
                        r.TryGetProperty("name", out var rn) ? rn.GetString() : null,
                        reportName, StringComparison.OrdinalIgnoreCase));
                if (report.ValueKind == JsonValueKind.Undefined) return null;
                var reportId = report.GetProperty("id").GetString()!;

                return (reportId, datasetId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Recovery lookup for '{name}' threw — treating as not recoverable.", reportName);
                return null;
            }
        }

        // ── Upload (import) PBIX to staging workspace ─────────────────────────
        public async Task<(string reportId, string datasetId)> ImportPbixAsync(
            PowerBIConfig cfg, byte[] pbixBytes, string reportName)
        {
            var token = await GetEffectiveTokenAsync(cfg);
            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            var workspaceId = await ResolveWorkspaceIdAsync(http, cfg.WorkspaceId, _log);

            // Capture current IDs before import. A failed same-name lookup must
            // never turn an older model into this migration's target dataset.
            var beforeResponse = await SendWithRetryAsync(
                () => http.GetAsync($"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}/datasets"),
                "GET workspace datasets before import");
            if (!beforeResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Could not discover semantic models in selected workspace '{workspaceId}' before publishing " +
                    $"({(int)beforeResponse.StatusCode}). Verify Power BI API access and retry.");
            using var beforeDoc = JsonDocument.Parse(await beforeResponse.Content.ReadAsStringAsync());
            var datasetIdsBeforeImport = beforeDoc.RootElement.GetProperty("value").EnumerateArray()
                .Select(d => d.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var url = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}" +
                      $"/imports?datasetDisplayName={Uri.EscapeDataString(reportName)}" +
                      $"&nameConflict=CreateOrOverwrite";

            _log.LogInformation(
                "Importing '{report}' to workspace {id} …", reportName, workspaceId);

            var resp = await PostImportAsync(http, url, pbixBytes, reportName);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                bool isLabelBlocked = IsLabelBlockedError(errBody);
                        _log.LogWarning("[PBI] Import failed with HTTP {status}; response body length={bytes}; sensitivity-label error={isLabelBlocked}",
                            (int)resp.StatusCode, errBody.Length, isLabelBlocked);

                if (isLabelBlocked && !string.IsNullOrWhiteSpace(cfg.UserAccountUpn))
                {
                    // Retry once with a ROPC token bound to a specific configured
                    // account. Note the PRIMARY import above already runs as a
                    // delegated user now (GetEffectiveTokenAsync/UserAccessToken,
                    // acquired interactively via MSAL) — this ROPC fallback only
                    // still matters if that interactively-signed-in user is a
                    // different, more-privileged account than the one holding
                    // the sensitivity-label policy, or as a non-interactive
                    // alternative when UserAccountUpn/Password are explicitly
                    // configured. It is not "switching from app-only to
                    // delegated" anymore — both paths are delegated.
                    _log.LogWarning(
                        "[PBI] Import of '{report}' blocked by sensitivity label — retrying once with " +
                        "ROPC-acquired delegated auth ({upn})…", reportName, cfg.UserAccountUpn);

                    string delegatedToken;
                    try
                    {
                        delegatedToken = await GetDelegatedAccessTokenAsync(cfg);
                    }
                    catch (Exception authEx)
                    {
                        throw new PowerBiLabelBlockedException(
                            $"'{reportName}.pbix' has a protected sensitivity label, and the delegated-auth " +
                            $"fallback also failed: {authEx.Message}");
                    }

                    http.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", delegatedToken);
                    resp = await PostImportAsync(http, url, pbixBytes, reportName);

                    if (!resp.IsSuccessStatusCode)
                    {
                        var retryErrBody = await resp.Content.ReadAsStringAsync();
                        throw new PowerBiLabelBlockedException(
                            $"'{reportName}.pbix' still failed to import under delegated auth. This usually " +
                            $"means the account '{cfg.UserAccountUpn}' doesn't have permission to read the " +
                            $"label, not that the pipeline is misconfigured. HTTP status: {(int)resp.StatusCode}.");
                    }

                    _log.LogInformation(
                        "[PBI] Delegated-auth retry succeeded for '{report}'.", reportName);
                }
                else if (isLabelBlocked)
                {
                    throw new PowerBiLabelBlockedException(
                        $"'{reportName}.pbix' has a protected sensitivity label. Service-principal " +
                        $"auth (AcquireTokenForClient) cannot open label-protected PBIX files — this " +
                        $"is a Microsoft platform limitation, not a bug in this pipeline. Either remove " +
                        $"the sensitivity label from the file in Power BI Desktop before uploading, or " +
                        $"configure UserAccountUpn/UserAccountPassword to enable the delegated-auth " +
                        $"fallback. HTTP status: {(int)resp.StatusCode}.");
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Power BI import API returned {(int)resp.StatusCode} ({resp.ReasonPhrase}) " +
                        $"for workspace '{workspaceId}'. Check backend diagnostics.");
                }
            }

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var importId = doc.RootElement.GetProperty("id").GetString()!;

            _log.LogInformation("Import queued — importId={id}. Polling …", importId);

            // Poll until import completes (up to 90 seconds)
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(3000);
                var pollUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}" +
                              $"/imports/{importId}";
                var pollResp = await http.GetAsync(pollUrl);
                if (!pollResp.IsSuccessStatusCode)
                {
                    var errBody = await pollResp.Content.ReadAsStringAsync();
                    throw new InvalidOperationException(
                        $"Polling import status returned HTTP {(int)pollResp.StatusCode}. Check backend diagnostics.");
                }
                var pollBody = await pollResp.Content.ReadAsStringAsync();
                using var pollDoc = JsonDocument.Parse(pollBody);
                var importState = pollDoc.RootElement.GetProperty("importState").GetString();
                _log.LogInformation("Import state (attempt {i}): {state}", i + 1, importState);

                if (importState == "Succeeded")
                {
                    var reports  = pollDoc.RootElement.GetProperty("reports");
                    var datasets = pollDoc.RootElement.GetProperty("datasets");
                    if (reports.GetArrayLength() != 1)
                        throw new InvalidOperationException(
                            $"Power BI import for '{reportName}' returned {reports.GetArrayLength()} reports; cannot identify the intended report safely.");
                    var rId = reports[0].GetProperty("id").GetString()!;
                    var datasetIds = datasets.EnumerateArray()
                        .Select(d => d.TryGetProperty("id", out var id) ? id.GetString() : null)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (datasetIds.Count != 1)
                        throw new InvalidOperationException(
                            $"Power BI import for '{reportName}' returned {datasetIds.Count} semantic model IDs; cannot safely select the target dataset.");
                    var dId = datasetIds[0]!;
                    await VerifyDatasetInWorkspaceAsync(http, workspaceId, dId, reportName);
                    _log.LogInformation("Import succeeded — reportId={r} datasetId={d}", rId, dId);
                    return (rId, dId);
                }
                if (importState == "Failed")
                {
                    // Try to surface the error detail from the import object
                    var errorDetail = pollDoc.RootElement.TryGetProperty("error", out var errProp)
                        ? errProp.ToString() : "(no detail)";

                    bool isRefreshAfterImport =
                        errorDetail.Contains("FailedToRefreshPackageAfterImportError", StringComparison.OrdinalIgnoreCase);

                    if (isRefreshAfterImport)
                    {
                        _log.LogWarning(
                            "Import reported Failed with FailedToRefreshPackageAfterImportError for '{report}'. " +
                            "This is Power BI's automatic post-import refresh choking (commonly on measure-only " +
                            "or otherwise columnless tables) — the dataset/report are often created anyway. " +
                            "Checking workspace {ws} for them before giving up…", reportName, workspaceId);

                        var recovered = await TryRecoverCreatedArtifactsAsync(http, workspaceId, reportName, datasetIdsBeforeImport);
                        if (recovered != null)
                        {
                            _log.LogInformation(
                                "Recovered dataset {d} / report {r} for '{report}' despite the refresh failure — continuing.",
                                recovered.Value.datasetId, recovered.Value.reportId, reportName);
                            return recovered.Value;
                        }

                        _log.LogError(
                            "No dataset/report named '{report}' found in workspace {ws} — cannot recover.",
                            reportName, workspaceId);
                    }

                    throw new Exception($"Power BI import failed. Detail: {errorDetail}");
                }
            }
            throw new TimeoutException("Power BI import timed out after 90 seconds.");
        }

        // ── List all workspaces the signed-in user can access ───────────────────
        public async Task<List<WorkspaceInfo>> ListWorkspacesAsync(PowerBIConfig cfg)
        {
            var token = await GetEffectiveTokenAsync(cfg);
            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var listUrl = "https://api.powerbi.com/v1.0/myorg/groups?$top=100";
            var listResp = await SendWithRetryAsync(() => http.GetAsync(listUrl), "GET groups (ListWorkspacesAsync)");
            var listBody = await listResp.Content.ReadAsStringAsync();

            if (!listResp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Cannot list workspaces ({(int)listResp.StatusCode}). " +
                    $"Verify the signed-in user is a Member/Admin of the target workspace. " +
                    $"Body: {listBody}");

            using var doc = JsonDocument.Parse(listBody);
            var result = new List<WorkspaceInfo>();
            foreach (var w in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                result.Add(new WorkspaceInfo
                {
                    Id   = w.GetProperty("id").GetString()!,
                    Name = w.TryGetProperty("name", out var n) ? n.GetString()! : "(unnamed)",
                    Type = w.TryGetProperty("type", out var t) ? t.GetString()! : ""
                });
            }
            return result;
        }

        // ── Generate embed token for the report ───────────────────────────────
        // Uses the signed-in user's DELEGATED token (GetEffectiveTokenAsync),
        // same as every other call in this file — see that method's comment
        // for why this is no longer the service principal. Note this is
        // unrelated to the ROPC path mentioned below: ROPC was only ever
        // worth its MFA/Conditional-Access fragility as an attempted SSO
        // workaround for the Lakehouse DirectQuery datasource.
        // That workaround doesn't work — Power BI's App-owns-data SSO
        // passthrough only supports Azure SQL Database as the DirectQuery
        // source, not a Fabric Lakehouse SQL Analytics Endpoint (see
        // BindLakehouseDatasourceCredentialsAsync). The actual fix is a
        // FIXED credential bound directly to the datasource, so no
        // `identities` entry is needed here anymore — every viewer queries
        // through that one bound identity regardless of who's embedding.
        // If you separately need per-viewer RLS (not SSO) on this dataset,
        // add an `identities` array back with real `roles` values — that's
        // a different, still-supported mechanism from what was removed here.
        // ─────────────────────────────────────────────────────────────────────
        // Poll the Power BI REST API's own refresh-history for "Completed"
        // BEFORE requesting an embed token.
        //
        // TabularModelService.WaitForModelReadyAsync (engine side) confirms
        // the AS engine itself considers every table Ready. This method
        // confirms the separate thing that actually gates Premium_ASWL_Error
        // in practice: the Power BI SERVICE's control-plane view of the
        // dataset (the layer the embed/GenerateToken call reads from) has
        // caught up with that engine state. There is a real propagation gap
        // between "XMLA commit finished" and "the service's own metadata
        // cache reflects the new schema as usable" — hitting GenerateToken
        // inside that gap is the other half of this failure mode, and it is
        // NOT visible from the TOM/XMLA session at all, only from the REST
        // API's own refresh-history status.
        //
        // If no refresh was requested at all (e.g. a structure-only save with
        // nothing in the refresh queue), the refresh list can legitimately
        // come back empty — that's treated as "nothing to wait for", not an
        // error.
        // ─────────────────────────────────────────────────────────────────────
        public async Task WaitForDatasetRefreshCompleteAsync(
            PowerBIConfig cfg,
            string workspaceId,
            string datasetId,
            TimeSpan? timeout = null,
            TimeSpan? pollInterval = null)
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(10);
            var effectiveGap     = pollInterval ?? TimeSpan.FromSeconds(5);
            var sw = Stopwatch.StartNew();
            var attempt = 0;

            var token = await GetEffectiveTokenAsync(cfg);
            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resolvedWorkspaceId = await ResolveWorkspaceIdAsync(http, workspaceId, _log);

            while (true)
            {
                attempt++;
                var url = $"https://api.powerbi.com/v1.0/myorg/groups/{resolvedWorkspaceId}" +
                          $"/datasets/{datasetId}/refreshes?$top=1";

                var resp = await SendWithRetryAsync(() => http.GetAsync(url), $"GET refresh history {datasetId}");
                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = await resp.Content.ReadAsStringAsync();

                    // Direct Lake / Fabric-lakehouse-backed datasets have no
                    // Import-style refresh history — the service rejects
                    // GET .../refreshes for them with 415 Unsupported Media
                    // Type (occasionally also surfaced as a 400 mentioning
                    // "Invalid dataset" / "Model-based dataset"). That is not
                    // a failure: it means the service-side refresh-history
                    // API is simply the wrong signal for this model type, and
                    // TabularModelService.WaitForModelReadyAsync (engine-side,
                    // already run before this method is called) is what
                    // actually gated readiness for Direct Lake. Treat it the
                    // same as an empty history — nothing to wait on here.
                    var isDirectLakeUnsupported =
                        resp.StatusCode == System.Net.HttpStatusCode.UnsupportedMediaType ||
                        errBody.Contains("Invalid dataset", StringComparison.OrdinalIgnoreCase) ||
                        errBody.Contains("Model-based dataset", StringComparison.OrdinalIgnoreCase);

                    if (isDirectLakeUnsupported)
                    {
                        _log.LogInformation(
                            "[WaitForDatasetRefresh] Refresh-history API returned {code} for dataset {ds} — " +
                            "this is expected for a Direct Lake / Fabric-backed model (no Import-style refresh " +
                            "history exists). Engine-side readiness already confirmed by WaitForModelReadyAsync; " +
                            "skipping the service-side poll and proceeding.",
                            (int)resp.StatusCode, datasetId);
                        return;
                    }

                    throw new InvalidOperationException(
                        $"Failed to read refresh history for dataset {datasetId} ({(int)resp.StatusCode}). Body: {errBody}");
                }

                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var values = doc.RootElement.GetProperty("value");

                if (values.GetArrayLength() == 0)
                {
                    _log.LogInformation(
                        "[WaitForDatasetRefresh] No refresh history entries for dataset {ds} — nothing to wait on, proceeding.",
                        datasetId);
                    return;
                }

                var latest = values[0];
                var status = latest.TryGetProperty("status", out var st) ? st.GetString() : null;

                switch (status)
                {
                    case "Completed":
                        _log.LogInformation(
                            "[WaitForDatasetRefresh] Latest refresh Completed after {attempt} poll(s), {elapsed}ms — service-side state caught up.",
                            attempt, sw.ElapsedMilliseconds);
                        return;

                    case "Failed":
                    case "Disabled":
                        var errMsg = latest.TryGetProperty("serviceExceptionJson", out var svcEx)
                            ? svcEx.GetString() : "(no serviceExceptionJson)";
                        var msg = $"Dataset {datasetId} refresh ended with status '{status}': {errMsg}";
                        _log.LogError("[WaitForDatasetRefresh] {msg}", msg);
                        throw new InvalidOperationException(msg);

                    default: // "Unknown" == still in progress, or any other in-flight state
                        if (sw.Elapsed >= effectiveTimeout)
                        {
                            var timeoutMsg = $"Dataset {datasetId} refresh did not reach 'Completed' within " +
                                             $"{effectiveTimeout.TotalMinutes:0} min (last status: '{status}').";
                            _log.LogError("[WaitForDatasetRefresh] {msg}", timeoutMsg);
                            throw new TimeoutException(timeoutMsg);
                        }
                        _log.LogInformation(
                            "[WaitForDatasetRefresh] Poll {attempt}: refresh status '{status}' — waiting {gap}s before re-checking.",
                            attempt, status, effectiveGap.TotalSeconds);
                        await Task.Delay(effectiveGap);
                        break;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Last line of defense: even after both readiness checks above pass,
        // a small/shared F-SKU capacity can still return a transient
        // Premium_ASWL_Error / ASWorkloadError under momentary memory
        // pressure (e.g. dual old+new schema validation overlap on smaller
        // SKUs). Retry the token call itself, with backoff, specifically for
        // that error signature — do NOT retry on generic 4xx (auth/config)
        // failures, those will not resolve by waiting.
        // ─────────────────────────────────────────────────────────────────────
        public async Task<(string EmbedUrl, string EmbedToken)> GenerateEmbedTokenWithRetryAsync(
            PowerBIConfig cfg, string reportId, int maxAttempts = 4)
        {
            var delay = TimeSpan.FromSeconds(3);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return await GenerateEmbedTokenAsync(cfg, reportId);
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransientAswlError(ex))
                {
                    _log.LogWarning(
                        "[GenerateEmbedToken] Attempt {attempt}/{max} hit a transient ASWL/workload error — " +
                        "retrying in {delay}s. This is expected occasionally on smaller SKUs under memory " +
                        "pressure; it is not an auth/config problem. {msg}",
                        attempt, maxAttempts, delay.TotalSeconds, ex.Message);
                    await Task.Delay(delay);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                }
            }

            // Final attempt — let any exception propagate as-is to the caller.
            return await GenerateEmbedTokenAsync(cfg, reportId);
        }

        private static bool IsTransientAswlError(Exception ex) =>
            ex.Message.IndexOf("Premium_ASWL_Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("ASWorkloadError", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("workload", StringComparison.OrdinalIgnoreCase) >= 0 &&
            ex.Message.Contains("(500)");

        private static string SanitizeRestErrorBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "(empty response body)";
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                    var message = error.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                    var requestId = error.TryGetProperty("requestId", out var reqEl) ? reqEl.GetString() : null;
                    return JsonSerializer.Serialize(new
                    {
                        error = new
                        {
                            code,
                            message,
                            requestId
                        }
                    });
                }
            }
            catch
            {
                // Fall through to bounded plain-text response.
            }

            return body.Length <= 2000 ? body : body[..2000] + "...";
        }

        public async Task<(string EmbedUrl, string EmbedToken)> GenerateEmbedTokenAsync(
            PowerBIConfig cfg, string reportId)
        {
            var token = await GetEffectiveTokenAsync(cfg); // ← delegated signed-in-user token
            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            // Resolve workspace (same as import — use verified ID)
            var workspaceId = await ResolveWorkspaceIdAsync(http, cfg.WorkspaceId, _log);

            // Get embed URL
            var metaUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}" +
                          $"/reports/{reportId}";
            var metaResp = await SendWithRetryAsync(() => http.GetAsync(metaUrl), $"GET report metadata {reportId}");
            if (!metaResp.IsSuccessStatusCode)
            {
                var errBody = await metaResp.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Failed to fetch report metadata ({(int)metaResp.StatusCode}). Body: {errBody}");
            }
            var metaBody = await metaResp.Content.ReadAsStringAsync();
            using var metaDoc = JsonDocument.Parse(metaBody);
            var embedUrl = metaDoc.RootElement.GetProperty("embedUrl").GetString()!;

            // Generate embed token.
            // accessLevel="Edit" is required for the visual repair layer's
            // report.save() flow. Do not set allowSaveAs here: this app saves
            // the existing imported report, and Power BI requires the extra
            // Content.Create scope when allowSaveAs is present.
            var tokenUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}" +
                           $"/reports/{reportId}/GenerateToken";
            var tokenPayload = JsonSerializer.Serialize(new
            {
                accessLevel = "Edit"
            });
            var tokenResp = await SendWithRetryAsync(
                () => http.PostAsync(tokenUrl, new StringContent(tokenPayload, Encoding.UTF8, "application/json")),
                $"POST GenerateToken {reportId}");
            if (!tokenResp.IsSuccessStatusCode)
            {
                var errBody = await tokenResp.Content.ReadAsStringAsync();
                var sanitizedBody = SanitizeRestErrorBody(errBody);
                _log.LogError(
                    "[GenerateEmbedToken] Power BI GenerateToken failed for report {reportId} in workspace {workspaceId} (HTTP {status}). Sanitized body: {body}",
                    reportId, workspaceId, (int)tokenResp.StatusCode, sanitizedBody);
                throw new InvalidOperationException(
                    $"Failed to generate embed token via POST /groups/{workspaceId}/reports/{reportId}/GenerateToken " +
                    $"(HTTP {(int)tokenResp.StatusCode}). Body: {sanitizedBody}");
            }
            var tokenBody = await tokenResp.Content.ReadAsStringAsync();
            using var tokenDoc = JsonDocument.Parse(tokenBody);
            var embedToken = tokenDoc.RootElement.GetProperty("token").GetString()!;

            _log.LogInformation(
                "Embed token generated for report {r} — embedUrl={url}, tokenLength={len}, accessLevel=Edit, " +
                "allowSaveAs=false, auth=delegated-user (signed in via MSAL, tenant={tenant}), Lakehouse access " +
                "via fixed datasource credential (see BindLakehouseDatasourceCredentialsAsync), no SSO " +
                "identities needed. Note: since this GenerateToken call now runs as the signed-in user rather " +
                "than the service principal, that user needs at least Contributor on the workspace themselves " +
                "for the call to succeed — workspace access can no longer be granted to the app registration alone.",
                reportId, embedUrl, embedToken.Length, cfg.TenantId);

            return (embedUrl, embedToken);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Automates the manual "Dataset Settings → Data source credentials"
        // step in the Power BI Service. Pushing an M expression via TOM only
        // defines the query — Power BI Service still needs stored, encrypted
        // credentials for that data source before a refresh can pull any
        // rows, which is exactly why a freshly-pushed Snowflake table can
        // show correct columns with silently zero rows (see SaveChanges'
        // credential-error detection).
        //
        // Flow, per the Power BI REST API "Datasources"/"Gateways" contract:
        //   1. GET  .../datasets/{id}/datasources           — find the
        //      Snowflake datasource(s) bound to this dataset, and which
        //      gateway (real or the implicit cloud one) owns each.
        //   2. GET  .../gateways/{gatewayId}                — that gateway's
        //      RSA public key. Credentials are NEVER sent in the clear.
        //   3. RSA-OAEP(SHA1)-encrypt a UTF-16LE credential JSON blob with
        //      that key, base64-encode it.
        //   4. POST .../gateways/{gatewayId}/datasources/{datasourceId}
        //      with the encrypted payload.
        //   5. Kick off a dataset refresh so data actually loads now that
        //      credentials exist — fire-and-forget; refreshes are async on
        //      the service side regardless of whether we wait for it here.
        //
        // Returns one human-readable status line per Snowflake datasource
        // found, so the caller can log/surface exactly what happened instead
        // of this failing silently the way the missing step did before.
        // ─────────────────────────────────────────────────────────────────────
        public async Task<List<string>> BindSnowflakeCredentialsAsync(
            PowerBIConfig cfg, string datasetId, SnowflakeConfig sfCfg)
        {
            var results = new List<string>();

            if (string.IsNullOrWhiteSpace(sfCfg.Username) || string.IsNullOrWhiteSpace(sfCfg.PatToken))
            {
                results.Add("Skipped credential binding: SnowflakeConfig.Username/PatToken not set.");
                _log.LogWarning("[PBI creds] {msg}", results[0]);
                return results;
            }

            var token = await GetEffectiveTokenAsync(cfg);
            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var workspaceId = await ResolveWorkspaceIdAsync(http, cfg.WorkspaceId, _log);

            // ── 1. List this dataset's datasources ──────────────────────────
            var dsUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}" +
                        $"/datasets/{datasetId}/datasources";
            var dsResp = await SendWithRetryAsync(() => http.GetAsync(dsUrl), $"GET datasources {datasetId}");
            if (!dsResp.IsSuccessStatusCode)
            {
                var errBody = await dsResp.Content.ReadAsStringAsync();
                _log.LogError("[PBI creds] Failed to list datasources for dataset {datasetId} ({status}); response body length={bytes}", datasetId, (int)dsResp.StatusCode, errBody.Length);
                results.Add($"Failed to list datasources for dataset {datasetId} (HTTP {(int)dsResp.StatusCode}). Check backend diagnostics.");
                return results;
            }

            using var dsDoc = JsonDocument.Parse(await dsResp.Content.ReadAsStringAsync());
            var allDatasources = dsDoc.RootElement.TryGetProperty("value", out var valEl)
                ? valEl.EnumerateArray().ToList()
                : new List<JsonElement>();

            // Snowflake shows up either as datasourceType == "Snowflake"
            // (native connector) or as an "Extension" datasource whose
            // connectionDetails mention Snowflake/the configured account —
            // match broadly so this survives connector-version differences
            // rather than hardcoding one exact shape.
            bool LooksLikeSnowflake(JsonElement ds)
            {
                var type = ds.TryGetProperty("datasourceType", out var t) ? t.GetString() ?? "" : "";
                var connectionDetailsRaw = ds.TryGetProperty("connectionDetails", out var cd)
                    ? cd.ToString()
                    : "";
                return type.Contains("Snowflake", StringComparison.OrdinalIgnoreCase)
                    || connectionDetailsRaw.Contains("snowflake", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(sfCfg.Account) &&
                        connectionDetailsRaw.Contains(sfCfg.Account, StringComparison.OrdinalIgnoreCase));
            }

            var snowflakeDatasources = allDatasources.Where(LooksLikeSnowflake).ToList();

            if (snowflakeDatasources.Count == 0)
            {
                var msg = $"No Snowflake datasource found among {allDatasources.Count} datasource(s) for dataset {datasetId} — nothing to bind credentials to yet.";
                _log.LogWarning("[PBI creds] {msg}", msg);
                results.Add(msg);
                return results;
            }

            // Credential blob format Power BI's REST API expects, per its
            // documented "Basic" credential type: a JSON object naming each
            // credential field, UTF-16LE-encoded before RSA encryption.
            // Snowflake's Power BI connector accepts a personal access token
            // supplied as the password half of Basic auth.
            var credentialJson = JsonSerializer.Serialize(new
            {
                credentialData = new[]
                {
                    new { name = "username", value = sfCfg.Username },
                    new { name = "password", value = sfCfg.PatToken }
                }
            });
            var credentialPlainBytes = Encoding.Unicode.GetBytes(credentialJson); // UTF-16LE

            foreach (var ds in snowflakeDatasources)
            {
                var datasourceId = ds.TryGetProperty("datasourceId", out var dId) ? dId.GetString() : null;
                var gatewayId    = ds.TryGetProperty("gatewayId", out var gId) ? gId.GetString() : null;

                if (string.IsNullOrWhiteSpace(datasourceId) || string.IsNullOrWhiteSpace(gatewayId))
                {
                    var msg = "Skipped one Snowflake datasource entry — missing datasourceId/gatewayId in the API response.";
                    _log.LogWarning("[PBI creds] {msg}", msg);
                    results.Add(msg);
                    continue;
                }

                try
                {
                    // ── 2. Gateway's RSA public key ──────────────────────────
                    var gwUrl = $"https://api.powerbi.com/v1.0/myorg/gateways/{gatewayId}";
                    var gwResp = await SendWithRetryAsync(() => http.GetAsync(gwUrl), $"GET gateway {gatewayId}");
                    if (!gwResp.IsSuccessStatusCode)
                    {
                        var errBody = await gwResp.Content.ReadAsStringAsync();
                        throw new InvalidOperationException(
                            $"Failed to fetch gateway {gatewayId} (HTTP {(int)gwResp.StatusCode}).");
                    }
                    using var gwDoc = JsonDocument.Parse(await gwResp.Content.ReadAsStringAsync());
                    var pk = gwDoc.RootElement.GetProperty("publicKey");
                    var modulusStr  = pk.GetProperty("modulus").GetString();
                    var exponentStr = pk.GetProperty("exponent").GetString();
                    if (string.IsNullOrWhiteSpace(modulusStr) || string.IsNullOrWhiteSpace(exponentStr))
                        throw new InvalidOperationException($"Gateway {gatewayId} returned an incomplete public key.");
                    var modulus  = Convert.FromBase64String(modulusStr);
                    var exponent = Convert.FromBase64String(exponentStr);

                    // ── 3. RSA-OAEP(SHA1) encrypt, base64-encode ─────────────
                    using var rsa = RSA.Create();
                    rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
                    var encryptedBytes = rsa.Encrypt(credentialPlainBytes, RSAEncryptionPadding.OaepSHA1);
                    var encryptedCredentials = Convert.ToBase64String(encryptedBytes);

                    // ── 4. Submit to the datasource ──────────────────────────
                    var updateUrl = $"https://api.powerbi.com/v1.0/myorg/gateways/{gatewayId}/datasources/{datasourceId}";
                    var updatePayload = JsonSerializer.Serialize(new
                    {
                        credentialDetails = new
                        {
                            credentialType       = "Basic",
                            credentials          = encryptedCredentials,
                            encryptedConnection   = "Encrypted",
                            encryptionAlgorithm   = "RSA-OAEP",
                            privacyLevel          = "Organizational"
                        }
                    });
                    var updateResp = await SendWithRetryAsync(
                        () => http.PatchAsync(updateUrl, new StringContent(updatePayload, Encoding.UTF8, "application/json")),
                        $"PATCH datasource credentials {datasourceId}");

                    if (!updateResp.IsSuccessStatusCode)
                    {
                        var errBody = await updateResp.Content.ReadAsStringAsync();
                        throw new InvalidOperationException(
                            $"Credential update rejected (HTTP {(int)updateResp.StatusCode}).");
                    }

                    var okMsg = $"Bound Snowflake credentials to datasource '{datasourceId}' (gateway '{gatewayId}').";
                    _log.LogInformation("[PBI creds] {msg}", okMsg);
                    results.Add(okMsg);
                }
                catch (Exception ex)
                {
                    var msg = $"Failed to bind credentials for datasource '{datasourceId}'. Check backend diagnostics.";
                    _log.LogError(ex, "[PBI creds] Failed to bind credentials for datasource {datasourceId}", datasourceId);
                    results.Add(msg);
                }
            }

            // ── 5. Kick off a refresh now that credentials (may) exist ──────
            // Fire-and-forget: dataset refreshes are async on the service
            // side regardless, and a failed refresh here (e.g. credentials
            // were STILL rejected) surfaces on the next SaveChanges() call's
            // warnings the same way it did before this method existed.
            try
            {
                var refreshUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}" +
                                  $"/datasets/{datasetId}/refreshes";
                var refreshPayload = JsonSerializer.Serialize(new { notifyOption = "NoNotification" });
                var refreshResp = await SendWithRetryAsync(
                    () => http.PostAsync(refreshUrl, new StringContent(refreshPayload, Encoding.UTF8, "application/json")),
                    $"POST refresh {datasetId}");

                results.Add(refreshResp.IsSuccessStatusCode
                    ? "Triggered a dataset refresh to load data now that credentials are bound."
                    : $"Credential binding done, but triggering the refresh failed ({(int)refreshResp.StatusCode}) — a subsequent refresh (scheduled or manual) will still pick up the new credentials.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[PBI creds] Failed to trigger post-credential refresh for {ds} — non-fatal.", datasetId);
                results.Add("Credential binding done, but triggering the refresh threw an error — a subsequent refresh will still pick up the new credentials.");
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Roadblock 2 — the "credential gap" step. Call this between
        // TabularModelService.SaveStructureOnly() (Transaction 1) and
        // TabularModelService.RequestFullRefreshAndSave() (Transaction 2).
        //
        // CORRECTED: this used to be a documented no-op on the assumption
        // that the Fabric Lakehouse SQL Analytics Endpoint "just resolves"
        // via Entra ID with no credential needed. That assumption is wrong
        // for embedded viewing: Power BI's SSO-passthrough support for
        // DirectQuery under App-owns-data embedding (service principal OR
        // ROPC/master-user auth — both count) is limited to Azure SQL
        // Database. A Fabric Lakehouse SQL Analytics Endpoint is a
        // different connector and isn't on that supported list, which is
        // exactly what produced Premium_ASWL_Error / "Single sign-on (SSO)
        // credential is not provided" at embed time.
        //
        // Fix: bind a FIXED identity (not SSO) to the datasource — every
        // embedded viewer then queries the Lakehouse as the signed-in user
        // who ran the migration, same as BindSnowflakeCredentialsAsync does
        // for Snowflake, just with credentialType="OAuth2" and the user's
        // own delegated token instead of a username/password pair:
        //   1. GET  .../datasets/{id}/datasources — find the Lakehouse SQL
        //      Analytics Endpoint datasource + its owning gateway.
        //   2. GET  .../gateways/{gatewayId} — that gateway's RSA public key.
        //   3. Use LakehouseSqlAccessToken as the fixed credential; the Power BI
        //      REST token is only for REST API calls, not SQL query execution.
        //   4. RSA-OAEP(SHA1)-encrypt { credentialData: [{ name:"accessToken",
        //      value: <token> }] }, base64-encode it.
        //   5. PATCH .../gateways/{gatewayId}/datasources/{datasourceId} with
        //      credentialType="OAuth2" and useEndUserOAuth2Credentials=false
        //      — false is what makes this a FIXED identity instead of SSO
        //      passthrough; that flag is the actual fix for the SSO error.
        //   6. Kick off a refresh, same as the Snowflake path.
        //
        // The signed-in user needs their own read access to the Lakehouse's
        // SQL Analytics Endpoint (Fabric workspace role, or an explicit
        // SQL-endpoint permission) — otherwise this binds successfully but
        // queries still fail with a SQL-side auth error.
        // ─────────────────────────────────────────────────────────────────────
        public async Task<List<string>> BindLakehouseDatasourceCredentialsAsync(
            PowerBIConfig cfg,
            string workspaceId,
            string datasetId,
            TimeSpan? datasourceDiscoveryTimeout = null,
            TimeSpan? datasourceDiscoveryPollDelay = null)
        {
            var results = new List<string>();

            var pbiToken = await GetEffectiveTokenAsync(cfg);
            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pbiToken);

            // ── 1. List this dataset's datasources ──────────────────────────
            var dsUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}" +
                        $"/datasets/{datasetId}/datasources";

            // The Lakehouse SQL Analytics Endpoint shows up as datasourceType
            // "Sql" (same M function as Azure SQL — Sql.Database(...)) with
            // connectionDetails.server matching cfg's configured Lakehouse
            // server. Match broadly on server/database strings rather than
            // hardcoding an exact datasourceType, since connector metadata
            // shape can vary by tenant/connector version.
            bool LooksLikeLakehouseSql(JsonElement ds)
            {
                var type = ds.TryGetProperty("datasourceType", out var t) ? t.GetString() ?? "" : "";
                var server = TryGetConnectionDetail(ds, "server");
                var database = TryGetConnectionDetail(ds, "database");
                return type.Equals("Sql", StringComparison.OrdinalIgnoreCase)
                    && (!string.IsNullOrWhiteSpace(cfg.LakehouseSqlServer) &&
                        string.Equals(server, cfg.LakehouseSqlServer, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(cfg.LakehouseSqlDatabase) ||
                        string.Equals(database, cfg.LakehouseSqlDatabase, StringComparison.OrdinalIgnoreCase));
            }

            static string? TryGetConnectionDetail(JsonElement ds, string name)
            {
                return ds.TryGetProperty("connectionDetails", out var cd) &&
                       cd.ValueKind == JsonValueKind.Object &&
                       cd.TryGetProperty(name, out var value)
                    ? value.GetString()
                    : null;
            }

            object SummarizeDatasource(JsonElement ds)
            {
                var connectionDetailKeys = ds.TryGetProperty("connectionDetails", out var cd) &&
                                           cd.ValueKind == JsonValueKind.Object
                    ? cd.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray()
                    : Array.Empty<string>();
                var datasourceId = ds.TryGetProperty("datasourceId", out var dId) ? dId.GetString() : null;
                var gatewayId = ds.TryGetProperty("gatewayId", out var gId) ? gId.GetString() : null;
                return new
                {
                    datasourceType = ds.TryGetProperty("datasourceType", out var t) ? t.GetString() : null,
                    hasDatasourceId = !string.IsNullOrWhiteSpace(datasourceId),
                    hasGatewayId = !string.IsNullOrWhiteSpace(gatewayId),
                    connectionDetailKeys,
                    serverMatches = string.Equals(TryGetConnectionDetail(ds, "server"), cfg.LakehouseSqlServer, StringComparison.OrdinalIgnoreCase),
                    databaseMatches = string.IsNullOrWhiteSpace(cfg.LakehouseSqlDatabase) ||
                                      string.Equals(TryGetConnectionDetail(ds, "database"), cfg.LakehouseSqlDatabase, StringComparison.OrdinalIgnoreCase)
                };
            }

            var discoveryTimeout = datasourceDiscoveryTimeout ?? DefaultLakehouseDatasourceDiscoveryTimeout;
            var discoveryPollDelay = datasourceDiscoveryPollDelay ?? DefaultLakehouseDatasourceDiscoveryPollDelay;
            var discoveryTimer = Stopwatch.StartNew();
            var discoveryAttempt = 0;
            List<JsonElement> allDatasources;

            while (true)
            {
                discoveryAttempt++;
                var dsResp = await SendWithRetryAsync(() => http.GetAsync(dsUrl), $"GET datasources {datasetId}");
                var status = (int)dsResp.StatusCode;
                _log.LogInformation(
                    "[PBI creds] Datasource discovery attempt {attempt}: GET datasources for dataset {datasetId} returned HTTP {status}.",
                    discoveryAttempt, datasetId, status);

                if (dsResp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    dsResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var errBody = await dsResp.Content.ReadAsStringAsync();
                    _log.LogError(
                        "[PBI creds] Datasource discovery authentication/authorization failure for dataset {datasetId} (HTTP {status}); response body length={bytes}. Not retrying.",
                        datasetId, status, errBody.Length);
                    results.Add($"Failed to list datasources for dataset {datasetId} (HTTP {status}). Authentication or authorization failed; not retrying as a propagation delay.");
                    return results;
                }

                if (!dsResp.IsSuccessStatusCode)
                {
                    var errBody = await dsResp.Content.ReadAsStringAsync();
                    _log.LogError(
                        "[PBI creds] Failed to list datasources for dataset {datasetId} (HTTP {status}); response body length={bytes}. Not retrying non-success REST response as propagation.",
                        datasetId, status, errBody.Length);
                    results.Add($"Failed to list datasources for dataset {datasetId} (HTTP {status}). Check backend diagnostics.");
                    return results;
                }

                using var dsDoc = JsonDocument.Parse(await dsResp.Content.ReadAsStringAsync());
                allDatasources = dsDoc.RootElement.TryGetProperty("value", out var valEl)
                    ? valEl.EnumerateArray().Select(e => e.Clone()).ToList()
                    : new List<JsonElement>();
                var matchedCount = allDatasources.Count(LooksLikeLakehouseSql);

                _log.LogInformation(
                    "[PBI creds] Datasource discovery attempt {attempt}: found {total} datasource(s), {matched} matching Lakehouse SQL server '{server}'.",
                    discoveryAttempt, allDatasources.Count, matchedCount, cfg.LakehouseSqlServer ?? "");
                _log.LogInformation(
                    "[PBI creds] Datasource discovery attempt {attempt}: sanitized datasource entries: {entries}",
                    discoveryAttempt, JsonSerializer.Serialize(allDatasources.Select(SummarizeDatasource)));

                if (matchedCount > 0) break;

                if (discoveryTimer.Elapsed >= discoveryTimeout)
                {
                    break;
                }

                var remaining = discoveryTimeout - discoveryTimer.Elapsed;
                var delay = remaining < discoveryPollDelay ? remaining : discoveryPollDelay;
                _log.LogInformation(
                    "[PBI creds] Lakehouse datasource not visible yet for dataset {datasetId}; waiting {delayMs}ms before retrying discovery.",
                    datasetId, Math.Max(0, (int)delay.TotalMilliseconds));
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
            }

            var lakehouseDatasources = allDatasources.Where(LooksLikeLakehouseSql).ToList();

            if (lakehouseDatasources.Count == 0)
            {
                var msg = $"No Lakehouse SQL Analytics Endpoint datasource found among {allDatasources.Count} " +
                          $"datasource(s) for dataset {datasetId} matching server '{cfg.LakehouseSqlServer}' " +
                          $"and database '{cfg.LakehouseSqlDatabase}' — " +
                          $"after {discoveryAttempt} discovery attempt(s) over {discoveryTimer.Elapsed.TotalSeconds:F1}s.";
                _log.LogWarning("[PBI creds] {msg}", msg);
                results.Add(msg);
                return results;
            }

            static bool HasRequiredDatasourceIdentifiers(JsonElement ds)
            {
                var datasourceId = ds.TryGetProperty("datasourceId", out var dId) ? dId.GetString() : null;
                var gatewayId = ds.TryGetProperty("gatewayId", out var gId) ? gId.GetString() : null;
                return !string.IsNullOrWhiteSpace(datasourceId) && !string.IsNullOrWhiteSpace(gatewayId);
            }

            var bindableLakehouseDatasources = lakehouseDatasources
                .Where(HasRequiredDatasourceIdentifiers)
                .ToList();
            var unbindableLakehouseDatasources = lakehouseDatasources
                .Where(ds => !HasRequiredDatasourceIdentifiers(ds))
                .ToList();

            if (unbindableLakehouseDatasources.Count > 0)
            {
                var note = $"Observed {unbindableLakehouseDatasources.Count} matching Lakehouse datasource entr" +
                           $"{(unbindableLakehouseDatasources.Count == 1 ? "y" : "ies")} without datasourceId/gatewayId. " +
                           "The gateway credential update API requires both identifiers.";
                _log.LogWarning(
                    "[PBI creds] {note} Sanitized entries: {entries}",
                    note, JsonSerializer.Serialize(unbindableLakehouseDatasources.Select(SummarizeDatasource)));
                if (bindableLakehouseDatasources.Count > 0)
                {
                    results.Add(note + " A separate bindable matching datasource was found and will be used.");
                }
            }

            if (bindableLakehouseDatasources.Count == 0)
            {
                var automaticBindResult = await BindLakehouseAutomaticCloudConnectionAsync(
                    cfg, workspaceId, datasetId, lakehouseDatasources.Select(SummarizeDatasource));
                results.Add(automaticBindResult);
                if (automaticBindResult.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) ||
                    automaticBindResult.StartsWith("No ", StringComparison.OrdinalIgnoreCase))
                {
                    return results;
                }
            }

            // ── 3. Delegated user token for the fixed identity ───────────────
            // The fixed credential must be scoped to the Lakehouse SQL
            // Analytics Endpoint, not to the Power BI REST API.
            _log.LogInformation(
                "[PBI creds] Lakehouse SQL datasource discovered after {attempt} attempt(s) and {elapsedMs}ms.",
                discoveryAttempt, discoveryTimer.ElapsedMilliseconds);

            if (bindableLakehouseDatasources.Count > 0 && string.IsNullOrWhiteSpace(cfg.LakehouseSqlAccessToken))
            {
                var msg = "No Azure SQL delegated token was supplied for the Lakehouse SQL Analytics Endpoint. " +
                          "Acquire a database.windows.net token before binding DirectQuery datasource credentials.";
                _log.LogError("[PBI creds] {msg}", msg);
                results.Add(msg);
                return results;
            }

            var credentialJson = JsonSerializer.Serialize(new
            {
                credentialData = new[]
                {
                    new { name = "accessToken", value = cfg.LakehouseSqlAccessToken }
                }
            });
            var credentialPlainBytes = Encoding.Unicode.GetBytes(credentialJson); // UTF-16LE

            foreach (var ds in bindableLakehouseDatasources)
            {
                var datasourceId = ds.TryGetProperty("datasourceId", out var dId) ? dId.GetString() : null;
                var gatewayId    = ds.TryGetProperty("gatewayId", out var gId) ? gId.GetString() : null;

                try
                {
                    // ── 2. Gateway's RSA public key ──────────────────────────
                    // A Fabric Lakehouse SQL Analytics Endpoint is a cloud/
                    // on-demand connection, not a traditional on-prem
                    // gateway. Its gatewayId still resolves against
                    // /gateways/{id}, but that endpoint reports
                    // "publicKey": null for cloud-type gateways — the RSA
                    // key only lives on the /gatewayClusters/{id} resource
                    // for those. Try /gateways first, fall back to
                    // /gatewayClusters if publicKey comes back null/absent,
                    // and fail with a clear message (rather than a raw
                    // GetProperty-on-null crash) if neither has one.
                    JsonElement? pk = null;
                    string? gatewayKind = null;

                    var gwUrl = $"https://api.powerbi.com/v1.0/myorg/gateways/{gatewayId}";
                    var gwResp = await SendWithRetryAsync(() => http.GetAsync(gwUrl), $"GET gateway {gatewayId}");
                    if (gwResp.IsSuccessStatusCode)
                    {
                        using var gwDoc = JsonDocument.Parse(await gwResp.Content.ReadAsStringAsync());
                        if (gwDoc.RootElement.TryGetProperty("publicKey", out var gwPk) &&
                            gwPk.ValueKind == JsonValueKind.Object)
                        {
                            pk = gwPk.Clone();
                            gatewayKind = "gateway";
                        }
                    }

                    if (pk == null)
                    {
                        var gwcUrl = $"https://api.powerbi.com/v1.0/myorg/gatewayClusters/{gatewayId}";
                        var gwcResp = await SendWithRetryAsync(() => http.GetAsync(gwcUrl), $"GET gatewayCluster {gatewayId}");
                        if (gwcResp.IsSuccessStatusCode)
                        {
                            using var gwcDoc = JsonDocument.Parse(await gwcResp.Content.ReadAsStringAsync());
                            if (gwcDoc.RootElement.TryGetProperty("publicKey", out var gwcPk) &&
                                gwcPk.ValueKind == JsonValueKind.Object)
                            {
                                pk = gwcPk.Clone();
                                gatewayKind = "gatewayCluster";
                            }
                        }
                    }

                    if (pk == null)
                        throw new InvalidOperationException(
                            $"Gateway/gatewayCluster {gatewayId} did not return an RSA public key from either " +
                            $"/gateways or /gatewayClusters — cannot encrypt the fixed credential for this " +
                            $"Lakehouse datasource. This usually means the connection type doesn't support " +
                            $"fixed-identity credential binding the way this method expects.");

                    var modulusStr  = pk.Value.TryGetProperty("modulus", out var modEl) ? modEl.GetString() : null;
                    var exponentStr = pk.Value.TryGetProperty("exponent", out var expEl) ? expEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(modulusStr) || string.IsNullOrWhiteSpace(exponentStr))
                        throw new InvalidOperationException($"{gatewayKind} {gatewayId} returned an incomplete public key.");
                    var modulus  = Convert.FromBase64String(modulusStr);
                    var exponent = Convert.FromBase64String(exponentStr);

                    // ── 4. RSA-OAEP(SHA1) encrypt, base64-encode ─────────────
                    using var rsa = RSA.Create();
                    rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
                    var encryptedBytes = rsa.Encrypt(credentialPlainBytes, RSAEncryptionPadding.OaepSHA1);
                    var encryptedCredentials = Convert.ToBase64String(encryptedBytes);

                    // ── 5. Submit to the datasource — FIXED identity ─────────
                    // useEndUserOAuth2Credentials=false is the actual fix for
                    // Premium_ASWL_Error: it stores this token as a single
                    // fixed credential instead of requiring SSO passthrough.
                    var updateUrl = $"https://api.powerbi.com/v1.0/myorg/gateways/{gatewayId}/datasources/{datasourceId}";
                    var updatePayload = JsonSerializer.Serialize(new
                    {
                        credentialDetails = new
                        {
                            credentialType             = "OAuth2",
                            credentials                = encryptedCredentials,
                            encryptedConnection         = "Encrypted",
                            encryptionAlgorithm         = "RSA-OAEP",
                            privacyLevel                = "Organizational",
                            useEndUserOAuth2Credentials = false
                        }
                    });
                    var updateResp = await SendWithRetryAsync(
                        () => http.PatchAsync(updateUrl, new StringContent(updatePayload, Encoding.UTF8, "application/json")),
                        $"PATCH Lakehouse datasource credentials {datasourceId}");

                    if (!updateResp.IsSuccessStatusCode)
                    {
                        var errBody = await updateResp.Content.ReadAsStringAsync();
                        throw new InvalidOperationException(
                            $"Credential update rejected (HTTP {(int)updateResp.StatusCode}).");
                    }

                    var okMsg = $"Bound fixed-identity OAuth2 credential to Lakehouse datasource '{datasourceId}' " +
                                $"(gateway '{gatewayId}') — SSO passthrough disabled (useEndUserOAuth2Credentials=false).";
                    _log.LogInformation("[PBI creds] {msg}", okMsg);
                    results.Add(okMsg);
                }
                catch (Exception ex)
                {
                    var msg = $"Failed to bind credentials for Lakehouse datasource '{datasourceId}'. " +
                              $"Note: this token expires in ~1 hour (service-principal client-credentials token) — " +
                              $"re-run this bind step periodically, or on a scheduled refresh failure, to renew it.";
                    _log.LogError(ex, "[PBI creds] {msg}", msg);
                    results.Add(msg);
                }
            }

            // ── 6. Kick off a refresh now that credentials exist ────────────
            try
            {
                var refreshUrl = $"https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}" +
                                  $"/datasets/{datasetId}/refreshes";
                var refreshPayload = JsonSerializer.Serialize(new { notifyOption = "NoNotification" });
                var refreshResp = await SendWithRetryAsync(
                    () => http.PostAsync(refreshUrl, new StringContent(refreshPayload, Encoding.UTF8, "application/json")),
                    $"POST refresh {datasetId}");

                results.Add(refreshResp.IsSuccessStatusCode
                    ? "Triggered a dataset refresh to load data now that the fixed credential is bound."
                    : $"Credential binding done, but triggering the refresh failed ({(int)refreshResp.StatusCode}) — a subsequent refresh (scheduled or manual) will still pick up the new credential.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[PBI creds] Failed to trigger post-credential refresh for {ds} — non-fatal.", datasetId);
                results.Add("Credential binding done, but triggering the refresh threw an error — a subsequent refresh will still pick up the new credential.");
            }

            return results;
        }

        private async Task<string> BindLakehouseAutomaticCloudConnectionAsync(
            PowerBIConfig cfg,
            string workspaceId,
            string datasetId,
            IEnumerable<object> sanitizedDatasourceEntries)
        {
            if (string.IsNullOrWhiteSpace(cfg.FabricAccessToken))
            {
                return "No Fabric delegated token was supplied for semantic model cloud connection binding. " +
                       "Acquire a Fabric token with SemanticModel.ReadWrite.All or Item.ReadWrite.All before retrying.";
            }

            if (string.IsNullOrWhiteSpace(cfg.LakehouseSqlServer) ||
                string.IsNullOrWhiteSpace(cfg.LakehouseSqlDatabase))
            {
                return "Failed to bind Fabric automatic cloud connection: Lakehouse SQL server/database was not resolved.";
            }

            var path = $"{cfg.LakehouseSqlServer};{cfg.LakehouseSqlDatabase}";
            var bindUrl = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}" +
                          $"/semanticModels/{datasetId}/bindConnection";
            var payload = JsonSerializer.Serialize(new
            {
                connectionBinding = new
                {
                    connectivityType = "Automatic",
                    connectionDetails = new
                    {
                        type = "SQL",
                        path
                    }
                }
            });

            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.FabricAccessToken);

            _log.LogInformation(
                "[PBI creds] Binding semantic model {datasetId} to Fabric Automatic SQL cloud connection for Lakehouse server/database. Sanitized datasource entries: {entries}",
                datasetId, JsonSerializer.Serialize(sanitizedDatasourceEntries));

            var resp = await SendWithRetryAsync(
                () => http.PostAsync(bindUrl, new StringContent(payload, Encoding.UTF8, "application/json")),
                $"POST Fabric semantic model bindConnection {datasetId}");

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                _log.LogError(
                    "[PBI creds] Fabric semantic model bindConnection auth/authz failure for dataset {datasetId} (HTTP {status}); response body length={bytes}.",
                    datasetId, (int)resp.StatusCode, errBody.Length);
                return $"Failed to bind Fabric automatic cloud connection for dataset {datasetId} (HTTP {(int)resp.StatusCode}). " +
                       "The signed-in user must own the semantic model and the app must have SemanticModel.ReadWrite.All or Item.ReadWrite.All consent.";
            }

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                _log.LogError(
                    "[PBI creds] Fabric semantic model bindConnection rejected for dataset {datasetId} (HTTP {status}); response body length={bytes}.",
                    datasetId, (int)resp.StatusCode, errBody.Length);
                return $"Failed to bind Fabric automatic cloud connection for dataset {datasetId} (HTTP {(int)resp.StatusCode}). " +
                       "Check backend diagnostics and Fabric semantic model connection permissions.";
            }

            var okMsg = $"Bound semantic model '{datasetId}' to Fabric Automatic SQL cloud connection for Lakehouse SQL endpoint.";
            _log.LogInformation("[PBI creds] {msg}", okMsg);
            return okMsg;
        }
    }
}
