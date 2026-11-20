using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PbixMigrator.Models;

namespace PbixMigrator.Services
{
    /// <summary>
    /// Result of a defensive schema-validation pass: everything that parsed
    /// cleanly, and everything that was skipped (with why), so the caller
    /// can surface partial success instead of a hard failure.
    /// </summary>
    public sealed class SchemaValidationResult
    {
        public List<SnowflakeViewMeta> ValidViews { get; } = new();
        public List<UnmappedTable> SkippedViews { get; } = new();
    }

    /// <summary>
    /// Connects to Snowflake using the REST SQL API with a PAT Bearer token.
    /// No Snowflake.Data driver quirks — pure HTTPS calls.
    /// </summary>
    public class SnowflakeService
    {
        private readonly ILogger<SnowflakeService> _log;
        private readonly IHttpClientFactory _httpFactory;

        public SnowflakeService(ILogger<SnowflakeService> log, IHttpClientFactory httpFactory)
        {
            _log = log;
            _httpFactory = httpFactory;
        }

        // ── Build the Snowflake REST SQL API base URL ─────────────────────────
        // Account format: QPXJSGB-RY62199  →  https://QPXJSGB-RY62199.snowflakecomputing.com
        private string BaseUrl(SnowflakeConfig cfg)
            => $"https://{cfg.Account.Trim()}.snowflakecomputing.com";

        // ── Snowflake SQL API error → InvalidOperationException ───────────────
        // Same message format the original inline code produced.
        private static void ThrowSnowflakeError(System.Net.HttpStatusCode status, string raw)
        {
            // Extract Snowflake error message from JSON if possible
            string detail = raw;
            try
            {
                var err = JsonNode.Parse(raw);
                detail  = err?["message"]?.GetValue<string>()
                       ?? err?["error"]?.GetValue<string>()
                       ?? raw;
            }
            catch { /* use raw */ }
            throw new InvalidOperationException(
                $"Snowflake REST API error ({(int)status}): {detail}");
        }

        // Reads a response body, transparently un-gzipping it if Snowflake
        // compressed it (result partitions can be returned gzip-encoded).
        private static async Task<string> ReadBodyAsync(HttpResponseMessage resp)
        {
            if (resp.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase))
            {
                await using var stream = await resp.Content.ReadAsStreamAsync();
                await using var gz     = new GZipStream(stream, CompressionMode.Decompress);
                using var reader       = new StreamReader(gz, Encoding.UTF8);
                return await reader.ReadToEndAsync();
            }
            return await resp.Content.ReadAsStringAsync();
        }

        // Appends one result partition's "data" array to the combined rows.
        private static void AppendRows(JsonNode? data, List<string[]> rows)
        {
            var arrData = data as JsonArray;
            if (arrData == null) return;

            foreach (var row in arrData)
            {
                var arr  = row?.AsArray();
                if (arr == null) continue;
                var cols = new string[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                    cols[i] = arr[i]?.GetValue<string>() ?? "";
                rows.Add(cols);
            }
        }

        // Snowflake reports numRows as a JSON number (occasionally a string).
        private static long? AsLong(JsonNode? node)
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
            }
            return null;
        }

        // ── Execute a SQL statement via REST and return rows as string[][] ────
        // Handles the full Snowflake SQL API result lifecycle:
        //   1. POST /api/v2/statements — 200 (result ready) or 202 (still running).
        //   2. 202 → poll GET statementStatusUrl until the statement completes.
        //   3. The completed response carries partition 0 in "data", plus
        //      statementHandle and resultSetMetaData { numRows, partitionInfo[] }.
        //   4. Every further partition is fetched with
        //      GET /api/v2/statements/{statementHandle}?partition=N (N = 1..count-1).
        //   5. The combined row count is validated against numRows.
        private async Task<List<string[]>> ExecSqlAsync(SnowflakeConfig cfg, string sql)
        {
            var url = $"{BaseUrl(cfg)}/api/v2/statements";

            var body = new
            {
                statement  = sql,
                // Snowflake's REST SQL API requires an integer number of
                // seconds here (0 is rejected as "must be a positive
                // integer"), so true "unlimited" isn't an option — use the
                // max Snowflake accepts so no view/query gets cut off while
                // loading all tables from selected schemas.
                timeout    = 604800, // 7 days
                database   = cfg.Database.Trim().ToUpper(),
                warehouse  = cfg.Warehouse.Trim().ToUpper(),
                role       = string.IsNullOrWhiteSpace(cfg.Role) ? null : cfg.Role.Trim().ToUpper(),
                parameters = new { MULTI_STATEMENT_COUNT = "0" }
            };

            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.PatToken.Trim());
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            // Both headers are mandatory for Snowflake REST API
            http.DefaultRequestHeaders.Add("X-Snowflake-Authorization-Token-Type", "PROGRAMMATIC_ACCESS_TOKEN");
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PbixMigrator/1.0");

            // .NET sends "Expect: 100-continue" by default on POSTs with a body.
            // Snowflake's REST endpoint sits behind a proxy/load balancer that
            // doesn't answer that handshake — instead of replying "100 Continue"
            // it just resets the TCP connection, which surfaces on the client as
            // "IOException: ...forcibly closed by the remote host... →
            // SocketException". Disabling Expect100Continue sends the request
            // body immediately instead of waiting for a 100 Continue response.
            http.DefaultRequestHeaders.ExpectContinue = false;

            var payload  = JsonSerializer.Serialize(body);
            var content  = new StringContent(payload, Encoding.UTF8, "application/json");

            _log.LogDebug("Snowflake REST → POST {url}\n{body}", url, payload);

            var resp = await http.PostAsync(url, content);
            var raw  = await ReadBodyAsync(resp);

            _log.LogDebug("Snowflake REST ← {status}\n{body}", resp.StatusCode, raw);

            if (!resp.IsSuccessStatusCode)
                ThrowSnowflakeError(resp.StatusCode, raw);

            // ── 202 Accepted: the statement is still executing. Poll its
            // status URL until Snowflake returns the finished result. Any
            // failure surfaces as a non-2xx (handled by ThrowSnowflakeError).
            if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                var pending   = JsonNode.Parse(raw);
                var pendingId = pending?["statementHandle"]?.GetValue<string>();
                var statusUrl = pending?["statementStatusUrl"]?.GetValue<string>();

                if (string.IsNullOrWhiteSpace(statusUrl) && string.IsNullOrWhiteSpace(pendingId))
                    throw new InvalidOperationException(
                        "Snowflake returned 202 (statement still running) with no statementHandle/statementStatusUrl to poll.");

                statusUrl = string.IsNullOrWhiteSpace(statusUrl)
                    ? $"/api/v2/statements/{pendingId}"
                    : statusUrl;
                var pollUrl = statusUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? statusUrl
                    : $"{BaseUrl(cfg)}{statusUrl}";

                var delayMs = 500;
                while (true)
                {
                    await Task.Delay(delayMs);
                    delayMs = Math.Min(delayMs * 2, 5000);

                    resp = await http.GetAsync(pollUrl);
                    raw  = await ReadBodyAsync(resp);

                    if (!resp.IsSuccessStatusCode)
                        ThrowSnowflakeError(resp.StatusCode, raw);
                    if (resp.StatusCode != System.Net.HttpStatusCode.Accepted)
                        break;
                }
            }

            // ── Parse partition 0 (the response body itself) ──────────────────
            var doc  = JsonNode.Parse(raw)!;
            var rows = new List<string[]>();
            AppendRows(doc["data"], rows);

            // ── Read result-set metadata: expected row count + partitions ─────
            var meta           = doc["resultSetMetaData"];
            var expectedRows   = AsLong(meta?["numRows"]);
            var partitionCount = (meta?["partitionInfo"] as JsonArray)?.Count ?? 1;
            if (partitionCount < 1) partitionCount = 1;

            // ── Fetch every remaining partition (1..count-1), in order ────────
            if (partitionCount > 1)
            {
                var statementHandle = doc["statementHandle"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(statementHandle))
                    throw new InvalidOperationException(
                        $"Snowflake reported {partitionCount} result partitions but returned no statementHandle to fetch partitions 1..{partitionCount - 1}.");

                for (int partition = 1; partition < partitionCount; partition++)
                {
                    var partitionUrl = $"{url}/{Uri.EscapeDataString(statementHandle)}?partition={partition}";
                    var partitionResp = await http.GetAsync(partitionUrl);
                    var partitionRaw  = await ReadBodyAsync(partitionResp);

                    if (!partitionResp.IsSuccessStatusCode)
                        ThrowSnowflakeError(partitionResp.StatusCode, partitionRaw);

                    var partitionDoc = JsonNode.Parse(partitionRaw)!;
                    AppendRows(partitionDoc["data"], rows);

                    _log.LogDebug("Snowflake REST ← partition {p}/{n}: {bytes} bytes, {rows} row(s) so far",
                        partition + 1, partitionCount, partitionRaw.Length, rows.Count);
                }
            }

            // ── Validate completeness against what Snowflake says it returned ─
            if (expectedRows.HasValue && rows.Count != expectedRows.Value)
                throw new InvalidOperationException(
                    $"Snowflake result incomplete: expected {expectedRows.Value} row(s) (resultSetMetaData.numRows) " +
                    $"across {partitionCount} partition(s) but collected {rows.Count}.");

            _log.LogInformation("[Snowflake] Statement complete — {rows} row(s) across {parts} partition(s)",
                rows.Count, partitionCount);

            return rows;
        }

        // ── Validate PAT + return all schemas ─────────────────────────────────
        public async Task<List<string>> GetSchemasAsync(SnowflakeConfig cfg)
        {
            var sql =
                $"SELECT SCHEMA_NAME " +
                $"FROM \"{cfg.Database.Trim().ToUpper()}\".INFORMATION_SCHEMA.SCHEMATA " +
                $"WHERE SCHEMA_NAME NOT IN ('INFORMATION_SCHEMA','PUBLIC') " +
                $"ORDER BY SCHEMA_NAME";

            List<string[]> rows;
            try
            {
                rows = await ExecSqlAsync(cfg, sql);
            }
            catch (InvalidOperationException)
            {
                throw; // already has a clean message
            }
            catch (Exception ex)
            {
                // ex.Message alone is often just the generic HttpRequestException
                // wrapper text ("An error occurred while sending the request.") —
                // the actual cause (DNS failure, TLS handshake error, connection
                // refused, proxy block, timeout, etc.) lives in ex.InnerException.
                // Keep exception detail in restricted backend diagnostics while
                // returning a stable, sanitized message to the caller.
                _log.LogError("[Snowflake] Connection failed reaching configured account '{acct}' ({type})",
                    cfg.Account, ex.GetType().Name);

                throw new InvalidOperationException(
                    "Snowflake connection failed. Check the account, credentials, network access, and backend diagnostics.", ex);
            }

            var schemas = new List<string>();
            foreach (var r in rows)
                if (r.Length > 0 && !string.IsNullOrWhiteSpace(r[0]))
                    schemas.Add(r[0]);

            _log.LogInformation("[Snowflake] GetSchemasAsync → {n} schema(s): {list}", schemas.Count, string.Join(", ", schemas));
            return schemas;
        }

        // ── Fetch all views + columns from selected schemas ───────────────────
        // Returns only the views that survived validation, plus a report of
        // anything skipped (broken/invalid/malformed metadata) so the caller
        // can surface that to the user instead of the whole request 500-ing.
        public async Task<(List<SnowflakeViewMeta> Views, List<UnmappedTable> Skipped)> GetViewMetadataAsync(
            SnowflakeConfig cfg, List<string> schemas)
        {
            if (schemas.Count == 0) return (new List<SnowflakeViewMeta>(), new List<UnmappedTable>());

            var schemaList = string.Join(",", schemas.ConvertAll(s => $"'{s.ToUpper()}'"));
            var db         = cfg.Database.Trim().ToUpper();

            // Fetch views
            var viewRows = await ExecSqlAsync(cfg,
                $"SELECT TABLE_SCHEMA, TABLE_NAME " +
                $"FROM \"{db}\".INFORMATION_SCHEMA.VIEWS " +
                $"WHERE TABLE_SCHEMA IN ({schemaList}) " +
                $"ORDER BY TABLE_SCHEMA, TABLE_NAME");

            // Preserve schema/name pairs in order, keyed the same way columns
            // below will be keyed, so a view with zero matching column rows
            // (a real Snowflake failure mode for broken/invalid views) still
            // shows up as an entry with an empty column list — and gets
            // caught and skipped by ProcessAndFilterSchemas below, rather
            // than silently vanishing or crashing later when the model
            // builder hits a table with no columns.
            var viewOrder = new List<(string Schema, string Name)>();
            foreach (var r in viewRows)
            {
                if (r.Length < 2) continue;
                viewOrder.Add((r[0], r[1]));
            }

            if (viewOrder.Count == 0)
                return (new List<SnowflakeViewMeta>(), new List<UnmappedTable>());

            // Fetch columns
            var colRows = await ExecSqlAsync(cfg,
                $"SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE " +
                $"FROM \"{db}\".INFORMATION_SCHEMA.COLUMNS " +
                $"WHERE TABLE_SCHEMA IN ({schemaList}) " +
                $"ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION");

            var columnsByKey = new Dictionary<string, List<(string Name, string DataType)>>();
            foreach (var r in colRows)
            {
                if (r.Length < 4) continue;
                var key = $"{r[0]}.{r[1]}";
                if (!columnsByKey.TryGetValue(key, out var list))
                    columnsByKey[key] = list = new List<(string, string)>();
                list.Add((r[2], r[3]));
            }

            // ── Assemble a raw JSON document exactly the shape a
            // schema-discovery payload would take, INCLUDING the chance of
            // nulls/malformed nesting for individual tables — this is what
            // ProcessAndFilterSchemas defends against, whether it came from
            // this in-memory build or a future raw-JSON schema endpoint. ──
            using var rawDoc = BuildRawSchemaDocument(viewOrder, columnsByKey);
            var validation = ProcessAndFilterSchemas(rawDoc.RootElement);

            _log.LogInformation(
                "[Snowflake] GetViewMetadataAsync → {valid} valid view(s), {skipped} skipped, across schema(s) {schemas}: {list}",
                validation.ValidViews.Count, validation.SkippedViews.Count, string.Join(", ", schemas),
                string.Join(", ", validation.ValidViews.ConvertAll(v => $"{v.Schema}.{v.ViewName}({v.Columns.Count} cols)")));

            if (validation.SkippedViews.Count > 0)
                _log.LogWarning("[Snowflake] Skipped {n} broken/invalid view(s): {list}",
                    validation.SkippedViews.Count,
                    string.Join(", ", validation.SkippedViews.ConvertAll(s => $"{s.TableName} ({s.Reason})")));

            return (validation.ValidViews, validation.SkippedViews);
        }

        // ── Build the in-memory JSON document ProcessAndFilterSchemas validates ──
        private static JsonDocument BuildRawSchemaDocument(
            List<(string Schema, string Name)> viewOrder,
            Dictionary<string, List<(string Name, string DataType)>> columnsByKey)
        {
            var root = new JsonArray();
            foreach (var (schema, name) in viewOrder)
            {
                var key = $"{schema}.{name}";
                var tableObj = new JsonObject
                {
                    ["schema"] = schema,
                    ["name"]   = name
                };

                if (columnsByKey.TryGetValue(key, out var cols) && cols.Count > 0)
                {
                    var colsArr = new JsonArray();
                    foreach (var c in cols)
                        colsArr.Add(new JsonObject { ["name"] = c.Name, ["dataType"] = c.DataType });
                    tableObj["columns"] = colsArr;
                }
                else
                {
                    // Deliberately explicit JSON null (not "missing") to
                    // exercise the exact failure mode this defends against:
                    // a broken/invalid Snowflake view returns zero column
                    // metadata rows, which without this guard is exactly
                    // what produces "requires an element of type 'Object',
                    // but the target element has type 'Null'" downstream.
                    tableObj["columns"] = null;
                }

                root.Add(tableObj);
            }

            return JsonDocument.Parse(root.ToJsonString());
        }

        // ─────────────────────────────────────────────────────────────────────
        // Defensive, report-agnostic validation/filtering pass over raw
        // per-table schema JSON. Loops each table individually; a broken or
        // malformed entry is logged and skipped (never throws out of this
        // method), so one bad Snowflake view can never take down the whole
        // schema-selection phase. Only tables that pass every ValueKind
        // check make it into ValidViews, which is what the TOM-building
        // step (UpdateSnowflakeViewsInPlace / UpdateLakehouseTablesInPlace)
        // ultimately consumes.
        // ─────────────────────────────────────────────────────────────────────
        public SchemaValidationResult ProcessAndFilterSchemas(JsonElement rawSchemas)
        {
            var result = new SchemaValidationResult();

            if (rawSchemas.ValueKind != JsonValueKind.Array)
            {
                _log.LogWarning("[Snowflake] ProcessAndFilterSchemas: root payload was '{kind}', expected an Array — nothing to process.",
                    rawSchemas.ValueKind);
                return result;
            }

            foreach (var rawTable in rawSchemas.EnumerateArray())
            {
                var identity = "(unidentified table)";
                try
                {
                    // ── 1. The table entry itself must be an Object ──────────
                    if (rawTable.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException($"Table entry was '{rawTable.ValueKind}', expected an Object.");

                    var schema = GetOptionalString(rawTable, "schema");
                    var name   = GetOptionalString(rawTable, "name");
                    identity = $"{schema ?? "?"}.{name ?? "?"}";

                    if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name))
                        throw new InvalidOperationException("'schema'/'name' is missing, null, or not a string — cannot identify this view.");

                    // ── 2. 'source', if present, must be Null/absent or an Object — never a bare scalar ──
                    if (rawTable.TryGetProperty("source", out var sourceEl) && IsPresent(sourceEl))
                    {
                        if (sourceEl.ValueKind != JsonValueKind.Object)
                            throw new InvalidOperationException($"'source' was '{sourceEl.ValueKind}', expected an Object.");

                        // ── 2a. Nested 'address', same rule, one level down ──
                        if (sourceEl.TryGetProperty("address", out var addressEl) && IsPresent(addressEl) &&
                            addressEl.ValueKind != JsonValueKind.Object)
                            throw new InvalidOperationException($"'source.address' was '{addressEl.ValueKind}', expected an Object.");
                    }

                    // ── 3. 'expression', if present, must be Null/absent or a String ──
                    if (rawTable.TryGetProperty("expression", out var exprEl) && IsPresent(exprEl) &&
                        exprEl.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException($"'expression' was '{exprEl.ValueKind}', expected a String.");

                    // ── 4. 'columns' is REQUIRED and must be a non-null Array ──
                    if (!rawTable.TryGetProperty("columns", out var columnsEl) || !IsPresent(columnsEl))
                        throw new InvalidOperationException("'columns' is missing or null — a view with no discoverable columns can't be modeled.");

                    if (columnsEl.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException($"'columns' was '{columnsEl.ValueKind}', expected an Array.");

                    var viewMeta = new SnowflakeViewMeta { Schema = schema!, ViewName = name! };

                    foreach (var colEl in columnsEl.EnumerateArray())
                    {
                        // A single malformed column doesn't sink the whole
                        // view — skip just that column and keep going.
                        if (colEl.ValueKind != JsonValueKind.Object) continue;

                        var colName = GetOptionalString(colEl, "name");
                        var colType = GetOptionalString(colEl, "dataType");
                        if (string.IsNullOrWhiteSpace(colName)) continue;

                        viewMeta.Columns.Add(new ColumnMeta { Name = colName!, DataType = colType ?? "TEXT" });
                    }

                    if (viewMeta.Columns.Count == 0)
                        throw new InvalidOperationException("Every column entry was malformed/unnamed after validation — nothing usable to model.");

                    result.ValidViews.Add(viewMeta);
                }
                catch (Exception ex)
                {
                    // Graceful skip: record a safe failure category and move
                    // to the next table. Never let one broken view abort batch.
                    _log.LogWarning("[Snowflake] Skipping broken/invalid view '{id}' ({type})", identity, ex.GetType().Name);
                    result.SkippedViews.Add(new UnmappedTable
                    {
                        TableName = identity,
                        Reason    = "Skipped during schema validation. Check backend diagnostics."
                    });
                    continue;
                }
            }

            return result;
        }

            // 'present' = key exists AND its value is neither JSON null nor the
            // .NET-only 'missing key' sentinel Undefined.
            private static bool IsPresent(JsonElement el) =>
                el.ValueKind != JsonValueKind.Null && el.ValueKind != JsonValueKind.Undefined;

            private static string? GetOptionalString(JsonElement obj, string propertyName)
            {
                if (!obj.TryGetProperty(propertyName, out var el)) return null;
                if (!IsPresent(el)) return null;
                return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            }

            // ── Fetch ALL rows for a single view (for Lakehouse ingestion) ────────
            // Reuses ExecSqlAsync's REST plumbing, which now collects EVERY result
            // partition Snowflake returns for the statement (not just the first)
            // and validates the combined row count against resultSetMetaData.numRows.
            public async Task<(List<ColumnMeta> Columns, List<string[]> Rows)> GetViewRowsAsync(
                SnowflakeConfig cfg, string schema, string viewName)
            {
                var db = cfg.Database.Trim().ToUpper();

                var colRows = await ExecSqlAsync(cfg,
                    $"SELECT COLUMN_NAME, DATA_TYPE " +
                    $"FROM \"{db}\".INFORMATION_SCHEMA.COLUMNS " +
                    $"WHERE TABLE_SCHEMA = '{schema.ToUpper()}' AND TABLE_NAME = '{viewName.ToUpper()}' " +
                    $"ORDER BY ORDINAL_POSITION");

                var columns = colRows
                    .Where(r => r.Length >= 2)
                    .Select(r => new ColumnMeta { Name = r[0], DataType = r[1] })
                    .ToList();

                if (columns.Count == 0)
                    throw new InvalidOperationException(
                        $"'{schema}.{viewName}' has no columns per INFORMATION_SCHEMA — it may have been dropped since the schema scan.");

                var dataRows = await ExecSqlAsync(cfg,
                    $"SELECT * FROM \"{db}\".\"{schema.ToUpper()}\".\"{viewName.ToUpper()}\"");

                _log.LogInformation("[Snowflake] GetViewRowsAsync '{schema}.{view}' → {cols} column(s), {rows} row(s)",
                    schema, viewName, columns.Count, dataRows.Count);

                return (columns, dataRows);
            }

            public sealed record DateSortHint(string Schema, string View, string TextColumn, string DateColumn, string Part);

    public async Task<List<DateSortHint>> DetectDateSortHintsAsync(
        SnowflakeConfig cfg, List<SnowflakeViewMeta> views)
    {
        var hints = new List<DateSortHint>();
        var db = cfg.Database.Trim().ToUpper();
        var parts = new[] { "MONTH", "QUARTER", "DAYOFWEEK" };

        static bool IsDate(string t) => t.StartsWith("DATE", StringComparison.OrdinalIgnoreCase)
                                        || t.StartsWith("TIMESTAMP", StringComparison.OrdinalIgnoreCase);
        static bool IsText(string t) => t.Equals("TEXT", StringComparison.OrdinalIgnoreCase)
                                        || t.StartsWith("VARCHAR", StringComparison.OrdinalIgnoreCase)
                                        || t.StartsWith("CHAR", StringComparison.OrdinalIgnoreCase);

        foreach (var v in views)
        {
            var dateCol = v.Columns.FirstOrDefault(c => IsDate(c.DataType));
            if (dateCol == null) continue;

            var fq = $"\"{db}\".\"{v.Schema.ToUpper()}\".\"{v.ViewName.ToUpper()}\"";
            try
            {
                // cost guard: skip very large views
                var cnt = await ExecSqlAsync(cfg, $"SELECT COUNT(*) FROM {fq}");
                if (cnt.Count > 0 && long.TryParse(cnt[0][0], out var n) && n > 5_000_000) continue;

                foreach (var tc in v.Columns.Where(c => IsText(c.DataType)))
                foreach (var part in parts)
                {
                    var sql =
                        $"SELECT COUNT(DISTINCT \"{tc.Name}\"), COUNT(DISTINCT {part}(\"{dateCol.Name}\")), " +
                        $"COUNT(DISTINCT \"{tc.Name}\" || '|' || {part}(\"{dateCol.Name}\")::STRING) " +
                        $"FROM {fq} WHERE \"{tc.Name}\" IS NOT NULL AND \"{dateCol.Name}\" IS NOT NULL";
                    var r = await ExecSqlAsync(cfg, sql);
                    if (r.Count == 0 || r[0].Length < 3) continue;
                    if (!long.TryParse(r[0][0], out var a) || !long.TryParse(r[0][1], out var b)
                        || !long.TryParse(r[0][2], out var ab)) continue;

                    // 1-to-1: every text value <-> exactly one part value
                    if (a > 1 && a <= 400 && a == b && a == ab)
                    {
                        hints.Add(new DateSortHint(v.Schema, v.ViewName, tc.Name, dateCol.Name, part));
                        _log.LogInformation("[Snowflake] Date-sort hint: {v}.{t} <-> {p}({d})",
                            v.ViewName, tc.Name, part, dateCol.Name);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Snowflake] Date-sort detection skipped for {v} ({type})", v.ViewName, ex.GetType().Name);
            }
        }
        return hints;
    }

            // ── Build Power Query M expression for DirectQuery ────────────────────
            public static string BuildMExpression(
                SnowflakeConfig cfg, string schema, string viewName)
            {
                return
                    $"let\n" +
                    $"    Source = Snowflake.Databases(\"{cfg.Account.Trim()}.snowflakecomputing.com\",\n" +
                    $"        \"{cfg.Warehouse.Trim()}\",\n" +
                    $"        [CreateNavigationProperties=false]),\n" +
                    $"    Database = Source{{[Name=\"{cfg.Database.Trim()}\",Kind=\"Database\"]}}[Data],\n" +
                    $"    Schema   = Database{{[Name=\"{schema}\",Kind=\"Schema\"]}}[Data],\n" +
                    $"    View     = Schema{{[Name=\"{viewName}\",Kind=\"View\"]}}[Data]\n" +
                    $"in\n" +
                    $"    View";
            }
        }
    }
