using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PbixMigrator.Models;

namespace PbixMigrator.Services
{
    /// <summary>
    /// Metadata for a Lakehouse table this service has loaded, enough for
    /// TabularModelService to bind a Power BI table's partition to it.
    /// </summary>
    public class LakehouseTableMeta
    {
        public string TableName { get; set; } = "";
        public string SourceSchema { get; set; } = "";
        public string SourceView { get; set; } = "";
        public List<ColumnMeta> Columns { get; set; } = new();
        public int RowCount { get; set; }
    }

    public class LakehouseSqlEndpoint
    {
        public string LakehouseId { get; set; } = "";
        public string Server      { get; set; } = "";
        public string Database    { get; set; } = "";
    }

    /// <summary>
    /// New ingestion path (v19): instead of Power BI's dataset connecting
    /// straight to Snowflake — which requires the Power BI Service to hold
    /// its own Snowflake credentials (the "unable to retrieve the
    /// credentials for the data source" failure) — this service pulls the
    /// data out of Snowflake itself (using the PAT already configured in
    /// SnowflakeConfig, entirely inside this backend) and lands it as
    /// managed Delta tables in a Fabric Lakehouse. Power BI then binds to
    /// the Lakehouse's SQL Analytics Endpoint, which — for a Fabric
    /// dataset in the same tenant/workspace — resolves via Microsoft
    /// Entra ID, so there is no separate "data source credentials" screen
    /// to fill in and no refresh-in-the-same-transaction failure at all.
    ///
    /// Flow per selected Snowflake view:
    ///   1. Ensure the target Lakehouse exists in the workspace (create once,
    ///      reuse afterwards).
    ///   2. Pull the view's rows via the Snowflake REST SQL API.
    ///   3. Upload them as a CSV file into the Lakehouse's OneLake "Files"
    ///      area via the ADLS Gen2 DFS protocol (create → append → flush).
    ///   4. Call Fabric's Lakehouse "Load Table" API to materialize that
    ///      file as a managed Delta table.
    ///   5. Resolve the Lakehouse's SQL Analytics Endpoint connection
    ///      string once, so the caller can build a Sql.Database(...) M
    ///      expression against it.
    /// </summary>
    public class FabricLakehouseService
    {
        private readonly ILogger<FabricLakehouseService> _log;
        private readonly IHttpClientFactory _httpFactory;

        private const string FabricScope = "https://api.fabric.microsoft.com/.default";
        private const string OneLakeScope = "https://storage.azure.com/.default";
        private const string FabricApiBase = "https://api.fabric.microsoft.com/v1";
        private const string OneLakeDfsBase = "https://onelake.dfs.fabric.microsoft.com";

        public FabricLakehouseService(ILogger<FabricLakehouseService> log, IHttpClientFactory httpFactory)
        {
            _log = log;
            _httpFactory = httpFactory;
        }

        // ── Token resolution ────────────────────────────────────────────────
        // Delegated user-token resolution, one per resource audience —
        // Fabric public API and OneLake/storage each need their own
        // audience-scoped token. Both are acquired interactively in the
        // browser (see msalAuth.js's signInAndAcquireAllTokens, invoked from
        // SignInGate) via silent acquireTokenSilent calls against the
        // signed-in user's MSAL session, and sent up per-request as
        // PowerBIConfig.FabricAccessToken / PowerBIConfig.OneLakeAccessToken.
        // Nothing is minted server-side and no client-secret/app-only
        // acquisition happens here — Lakehouse creation, table loads, and
        // every other Fabric/OneLake call this service makes run as the
        // signed-in user, matching GetEffectiveTokenAsync in
        // PowerBIService.cs. Because AAD tokens are audience+user scoped,
        // this does not attempt to refresh an expired token itself — that
        // has to happen back on the frontend and be resent; a 401 here means
        // the token expired mid-session.
        private Task<string> GetTokenAsync(PowerBIConfig cfg, string scope)
        {
            var (token, fieldName) = scope switch
            {
                FabricScope  => (cfg.FabricAccessToken, nameof(cfg.FabricAccessToken)),
                OneLakeScope => (cfg.OneLakeAccessToken, nameof(cfg.OneLakeAccessToken)),
                _ => throw new InvalidOperationException($"Unrecognized Fabric/OneLake scope '{scope}' — no delegated-token field is mapped for it.")
            };

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    $"No signed-in user token was provided for scope '{scope}'. Lakehouse creation, table " +
                    $"loads, and every other Fabric/OneLake call now run as the signed-in user (delegated " +
                    $"auth), not a service principal. Sign in with Microsoft on the frontend (SignInGate) and " +
                    $"make sure the acquired token is sent as PowerBIConfig.{fieldName}.");

            _log.LogInformation(
                "[Fabric auth] Using delegated user-signed-in token ({len} chars) for scope '{scope}' — " +
                "no service-principal acquisition performed.", token.Length, scope);
            return Task.FromResult(token);
        }

        private HttpClient NewClient(string bearerToken)
        {
            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return http;
        }

        // ── Fabric long-running-operation poll ──────────────────────────────
        // Fabric item/table operations return 202 Accepted with a
        // Location/Retry-After header pair; the caller polls Location until
        // status is Succeeded/Failed. 201/200 (no polling needed) is handled
        // by the caller before this is ever invoked.
        private async Task PollLroAsync(HttpClient http, HttpResponseMessage initial, string description)
        {
            if (initial.StatusCode != System.Net.HttpStatusCode.Accepted) return;

            if (!initial.Headers.TryGetValues("Location", out var locVals))
            {
                _log.LogWarning("[Fabric LRO] {desc} returned 202 with no Location header — assuming it completed.", description);
                return;
            }
            var location = locVals.First();

            // No attempt cap — a large "load all views" run must be able to
            // keep polling for as long as Fabric takes, instead of the
            // operation getting abandoned (and reported as a failure) after
            // an arbitrary 40 polls while Fabric is still legitimately
            // working through it.
            for (int attempt = 0; ; attempt++)
            {
                var retryAfterSec = initial.Headers.RetryAfter?.Delta?.TotalSeconds ?? 2;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, retryAfterSec)));

                var pollResp = await http.GetAsync(location);
                var pollBody = await pollResp.Content.ReadAsStringAsync();

                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"{description}: polling {location} returned {(int)pollResp.StatusCode}: {pollBody}");

                using var doc = JsonDocument.Parse(pollBody);
                var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "Unknown";

                if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("[Fabric LRO] {desc} — Succeeded after {n} poll(s).", description, attempt + 1);
                    return;
                }
                if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{description} failed: {pollBody}");

                // else "Running"/"NotStarted" — keep polling
                if (pollResp.Headers.RetryAfter?.Delta != null)
                    initial = pollResp; // pick up an updated Retry-After if provided
            }
        }

        // ── 1. Ensure the Lakehouse exists (idempotent) ─────────────────────
        public async Task<string> EnsureLakehouseAsync(PowerBIConfig cfg, string workspaceId, string lakehouseName)
        {
            var token = await GetTokenAsync(cfg, FabricScope);
            using var http = NewClient(token);

            // Look for an existing Lakehouse with this display name first —
            // re-running the migration against the same workspace should
            // reuse it rather than erroring on a duplicate-name conflict.
            var listUrl = $"{FabricApiBase}/workspaces/{workspaceId}/lakehouses";
            var listResp = await http.GetAsync(listUrl);
            var listBody = await listResp.Content.ReadAsStringAsync();

            if (!listResp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Failed to list Lakehouses in workspace '{workspaceId}' ({(int)listResp.StatusCode}): {listBody}. " +
                    "Confirm the service principal has Contributor+ access on this Fabric workspace and that " +
                    "'Service principals can call Fabric public APIs' is enabled in the Fabric admin portal.");

            using (var listDoc = JsonDocument.Parse(listBody))
            {
                if (listDoc.RootElement.TryGetProperty("value", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var name = item.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                        if (string.Equals(name, lakehouseName, StringComparison.OrdinalIgnoreCase))
                        {
                            var existingId = item.GetProperty("id").GetString()!;
                            _log.LogInformation("[Fabric] Reusing existing Lakehouse '{name}' ({id}) in workspace {ws}.",
                                lakehouseName, existingId, workspaceId);
                            return existingId;
                        }
                    }
                }
            }

            // Not found — create it.
            var createUrl = $"{FabricApiBase}/workspaces/{workspaceId}/lakehouses";
            var createPayload = JsonSerializer.Serialize(new
            {
                displayName = lakehouseName,
                description = "Created by PbixMigrator — staging area for Snowflake views before Power BI binds to them via the SQL Analytics Endpoint."
            });
            var createResp = await http.PostAsync(createUrl,
                new StringContent(createPayload, Encoding.UTF8, "application/json"));
            var createBody = await createResp.Content.ReadAsStringAsync();

            if (createResp.StatusCode != System.Net.HttpStatusCode.Created &&
                createResp.StatusCode != System.Net.HttpStatusCode.Accepted)
                throw new InvalidOperationException(
                    $"Failed to create Lakehouse '{lakehouseName}' in workspace '{workspaceId}' ({(int)createResp.StatusCode}): {createBody}");

            string lakehouseId;
            if (createResp.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                // 202 — item creation itself is async; poll, then re-list to get the ID.
                await PollLroAsync(http, createResp, $"Create Lakehouse '{lakehouseName}'");
                var relist = await http.GetAsync(listUrl);
                using var relistDoc = JsonDocument.Parse(await relist.Content.ReadAsStringAsync());
                var match = relistDoc.RootElement.GetProperty("value").EnumerateArray()
                    .First(i => string.Equals(i.GetProperty("displayName").GetString(), lakehouseName, StringComparison.OrdinalIgnoreCase));
                lakehouseId = match.GetProperty("id").GetString()!;
            }
            else
            {
                using var createDoc = JsonDocument.Parse(createBody);
                lakehouseId = createDoc.RootElement.GetProperty("id").GetString()!;
            }

            _log.LogInformation("[Fabric] Created Lakehouse '{name}' ({id}) in workspace {ws}.",
                lakehouseName, lakehouseId, workspaceId);
            return lakehouseId;
        }

        // ── 2. Resolve the SQL Analytics Endpoint (with provisioning wait) ──
        // The endpoint isn't immediately queryable the instant a Lakehouse
        // is created — Fabric provisions it in the background. Poll the
        // Lakehouse's own properties until sqlEndpointProperties shows a
        // connectionString, rather than failing on the first empty read.
        public async Task<LakehouseSqlEndpoint> GetSqlEndpointAsync(PowerBIConfig cfg, string workspaceId, string lakehouseId, string lakehouseName)
        {
            var token = await GetTokenAsync(cfg, FabricScope);
            using var http = NewClient(token);

            var url = $"{FabricApiBase}/workspaces/{workspaceId}/lakehouses/{lakehouseId}";

            // No attempt cap (previously 30 × 3s ≈ 90s) — keep waiting for
            // the endpoint instead of failing the whole "load all tables"
            // run out from under an otherwise-healthy provisioning process.
            for (int attempt = 0; ; attempt++)
            {
                var resp = await http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Failed to read Lakehouse '{lakehouseId}' ({(int)resp.StatusCode}): {body}");

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("sqlEndpointProperties", out var sqlProps) &&
                    sqlProps.TryGetProperty("connectionString", out var csEl) &&
                    csEl.GetString() is string cs && !string.IsNullOrWhiteSpace(cs))
                {
                    // BUG FIX: `sqlEndpointProperties.id` is the GUID of the
                    // SQL-endpoint *item itself* — it is not a T-SQL database
                    // name and was never queryable. The database you actually
                    // connect to on a Lakehouse's SQL Analytics Endpoint (in
                    // SSMS, or via Sql.Database(server, database) in an M
                    // expression) is the Lakehouse's own displayName. Using
                    // the item GUID here silently pointed every DirectQuery
                    // M expression at a database that doesn't exist, which
                    // surfaces later as a schema/model load failure — exactly
                    // the symptom reported (Premium_ASWL_Error / SSO
                    // credential errors at embed time were a red herring;
                    // the query never had a valid database to resolve against
                    // in the first place, SSO or not).
                    var database = lakehouseName;
                    _log.LogInformation("[Fabric] SQL Analytics Endpoint ready for Lakehouse '{id}' — server={server} database={db}",
                        lakehouseId, cs, database);
                    return new LakehouseSqlEndpoint { LakehouseId = lakehouseId, Server = cs, Database = database };
                }

                _log.LogInformation("[Fabric] SQL Analytics Endpoint not provisioned yet for Lakehouse '{id}' (attempt {n}) — waiting…",
                    lakehouseId, attempt + 1);
                await Task.Delay(3000);
            }
        }

        // ── 3+4. Upload one Snowflake view's rows into the Lakehouse as a table ──
        public async Task<LakehouseTableMeta> LoadViewIntoLakehouseAsync(
            PowerBIConfig cfg, SnowflakeConfig sfCfg, SnowflakeService snowflake,
            string workspaceId, string lakehouseId, SnowflakeViewMeta view)
        {
            var tableName = SanitizeTableName(view.ViewName);

            _log.LogInformation("[Lakehouse ingest] {schema}.{view} → table '{table}' — fetching rows from Snowflake…",
                view.Schema, view.ViewName, tableName);

            var (columns, rows) = await snowflake.GetViewRowsAsync(sfCfg, view.Schema, view.ViewName);

            // Roadblock 1 fix: Fabric's CSV ingestion rejects headers with
            // leading/trailing spaces or non-word characters (Snowflake views
            // built on legacy sources routinely have both, e.g. "··ROW_ID")
            // with "Invalid column name(s)... no space characters are
            // allowed." Sanitize every header into a Delta-safe physical
            // name here, once, before the CSV is ever written — that keeps
            // the fix in one place instead of every downstream consumer
            // having to defend against illegal characters.
            var sanitizedColumns = SanitizeColumnNames(columns);
            // NormalizeTemporalValues(sanitizedColumns, rows);

            var csv = BuildCsv(sanitizedColumns, rows);
            var csvBytes = Encoding.UTF8.GetBytes(csv);
            var relativePath = $"Files/staging/{tableName}.csv";

            await UploadToOneLakeAsync(cfg, workspaceId, lakehouseId, relativePath, csvBytes);
            _log.LogInformation("[Lakehouse ingest] Uploaded {bytes} byte(s) / {rows} row(s) to OneLake '{path}'.",
                csvBytes.Length, rows.Count, relativePath);

            await LoadTableAsync(cfg, workspaceId, lakehouseId, tableName, relativePath);
            _log.LogInformation("[Lakehouse ingest] Loaded Delta table '{table}' in Lakehouse '{lh}'.", tableName, lakehouseId);

            return new LakehouseTableMeta
            {
                TableName = tableName,
                SourceSchema = view.Schema,
                SourceView = view.ViewName,
                Columns = sanitizedColumns,
                RowCount = rows.Count
            };
        }

        // ── Header sanitizer (Roadblock 1) ──────────────────────────────────
        // Fabric requires column identifiers to be UTF-8 word characters
        // only — no spaces, no punctuation like "·" or "#". This produces a
        // Delta-safe physical name (SourceColumn) for the CSV header/table
        // DDL while keeping the original Snowflake column name (Name) around
        // for display in Power BI. Collisions that sanitization can create
        // (e.g. "Order #" and "Order-" both becoming "Order_") are
        // disambiguated with a numeric suffix so no two physical columns in
        // the same table end up identical.
        private static readonly Regex IllegalColumnChars = new(@"[^\w]+", RegexOptions.Compiled);

        internal static List<ColumnMeta> SanitizeColumnNames(List<ColumnMeta> columns)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ColumnMeta>(columns.Count);

            foreach (var col in columns)
            {
                var physical = SanitizeSingleColumnName(col.Name, used);
                used.Add(physical);

                result.Add(new ColumnMeta
                {
                    Name         = col.Name,       // original, for display in Power BI
                    DataType     = col.DataType,
                    SourceColumn = physical         // actual Delta/CSV header
                });
            }

            return result;
        }

        private static string SanitizeSingleColumnName(string original, HashSet<string> used)
        {
            var trimmed  = (original ?? "").Trim();
            var cleaned  = IllegalColumnChars.Replace(trimmed, "_").Trim('_');

            if (string.IsNullOrEmpty(cleaned)) cleaned = "col";
            if (char.IsDigit(cleaned[0])) cleaned = "c_" + cleaned;

            var candidate = cleaned;
            var suffix    = 2;
            while (used.Contains(candidate))
                candidate = $"{cleaned}_{suffix++}";

            return candidate;
        }

        // ── OneLake upload (ADLS Gen2 DFS: create → append → flush) ────────
        private async Task UploadToOneLakeAsync(
            PowerBIConfig cfg, string workspaceId, string lakehouseId, string relativePath, byte[] content)
        {
            var token = await GetTokenAsync(cfg, OneLakeScope);
            using var http = NewClient(token);

            var fileUrl = $"{OneLakeDfsBase}/{workspaceId}/{lakehouseId}/{relativePath}";

            // 1. Create the file (empty, resource=file). Overwrites any
            // previous staged copy of the same table so re-runs are safe.
            var createResp = await http.PutAsync($"{fileUrl}?resource=file", new ByteArrayContent(Array.Empty<byte>()));
            if (!createResp.IsSuccessStatusCode)
            {
                var body = await createResp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"OneLake create-file failed for '{relativePath}' ({(int)createResp.StatusCode}): {body}");
            }

            // 2. Append the bytes at position 0.
            var appendContent = new ByteArrayContent(content);
            appendContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var appendResp = await http.PatchAsync($"{fileUrl}?action=append&position=0", appendContent);
            if (!appendResp.IsSuccessStatusCode)
            {
                var body = await appendResp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"OneLake append failed for '{relativePath}' ({(int)appendResp.StatusCode}): {body}");
            }

            // 3. Flush — commits the appended bytes as the file's contents.
            var flushResp = await http.PatchAsync($"{fileUrl}?action=flush&position={content.Length}", new StringContent(""));
            if (!flushResp.IsSuccessStatusCode)
            {
                var body = await flushResp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"OneLake flush failed for '{relativePath}' ({(int)flushResp.StatusCode}): {body}");
            }
        }

        // ── Fabric "Load Table" API: materializes a Files/ path as a managed Delta table ──
        // Note on the alternative approach: Delta's `columnMapping.mode =
        // "name"` lets a table carry column names Delta's default mode can't
        // (including ones with spaces), decoupling the logical name from the
        // physical Parquet field. It's a real option — but it's a *table
        // property* set at table-creation time via Spark/a Fabric notebook,
        // not a `formatOptions` flag this CSV-based Load Table REST call
        // exposes, and even with it enabled the CSV header being uploaded
        // still has to be valid to parse in the first place. Sanitizing the
        // header (SanitizeColumnNames, above) is the fix that actually
        // applies to this ingestion path; if a future path lands data via a
        // Spark job instead of this REST API, columnMapping.mode="name" is
        // the better lever to reach for there.
        private async Task LoadTableAsync(PowerBIConfig cfg, string workspaceId, string lakehouseId, string tableName, string relativePath)
        {
            var token = await GetTokenAsync(cfg, FabricScope);
            using var http = NewClient(token);

            var url = $"{FabricApiBase}/workspaces/{workspaceId}/lakehouses/{lakehouseId}/tables/{tableName}/load";
            var payload = JsonSerializer.Serialize(new
            {
                relativePath,
                pathType = "File",
                mode = "Overwrite",
                formatOptions = new
                {
                    format = "Csv",
                    header = true,
                    delimiter = ","
                }
            });

            var resp = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
            if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                await PollLroAsync(http, resp, $"Load table '{tableName}'");
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Load-table API rejected '{tableName}' ({(int)resp.StatusCode}): {body}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static string SanitizeTableName(string viewName)
        {
            // Delta/Lakehouse table identifiers: letters, digits, underscore;
            // must not start with a digit. Report-agnostic — no hardcoded
            // view names, just a generic identifier-safety transform.
            var cleaned = new string(viewName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            if (cleaned.Length == 0 || char.IsDigit(cleaned[0])) cleaned = "t_" + cleaned;
            return cleaned.ToLowerInvariant();
        }

        // Snowflake's REST SQL API returns DATE as days-since-epoch and
// TIMESTAMP_* as epoch seconds. Convert them to ISO text so Fabric
// doesn't ingest them as plain numbers.
private static void NormalizeTemporalValues(List<ColumnMeta> columns, List<string[]> rows)
{
    var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    for (int c = 0; c < columns.Count; c++)
    {
        var t = (columns[c].DataType ?? "").ToUpperInvariant();
        bool isDate = t == "DATE";
        bool isTs = t.StartsWith("TIMESTAMP");
        if (!isDate && !isTs) continue;

        foreach (var row in rows)
        {
            if (c >= row.Length || string.IsNullOrWhiteSpace(row[c])) continue;
            var token = row[c].Trim().Split(' ')[0];
            if (!double.TryParse(token, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var num)) continue;
            try
            {
                row[c] = isDate
                    ? epoch.AddDays(num).ToString("yyyy-MM-dd")
                    : epoch.AddSeconds(num).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { /* leave the value as-is */ }
        }
    }
}

        private static string BuildCsv(List<ColumnMeta> columns, List<string[]> rows)
        {
            var sb = new StringBuilder();
            // Header row MUST be the sanitized physical name (SourceColumn),
            // not the raw Snowflake column name — this is the row that
            // Fabric's Load Table API validates against "UTF-8 Unicode word
            // characters, no spaces" and rejects with a 400 otherwise.
            sb.AppendLine(string.Join(",", columns.Select(c => EscapeCsv(c.SourceColumn))));
            foreach (var row in rows)
                sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
            return sb.ToString();
        }

        private static string EscapeCsv(string? value)
        {
            value ??= "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}