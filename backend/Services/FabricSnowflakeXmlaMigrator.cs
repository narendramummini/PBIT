using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AnalysisServices.Tabular;

namespace PbixMigrator.Services
{
    // ─────────────────────────────────────────────────────────────────────
    // CONFIG — every value is supplied by the caller at runtime. Nothing in
    // the migrator below references a specific table, report, or dataset
    // name, which is what makes it report-agnostic: one build of this class
    // runs unmodified against any dataset in any Premium/Fabric workspace.
    // ─────────────────────────────────────────────────────────────────────
    public sealed class XmlaMigrationConfig
    {
        // ── Azure AD Service Principal ──────────────────────────────────
        public string TenantId { get; init; } = default!;
        public string ClientId { get; init; } = default!;
        public string ClientSecret { get; init; } = default!;

        // ── XMLA target ──────────────────────────────────────────────────
        /// e.g. "powerbi://api.powerbi.com/v1.0/myorg/&lt;workspace-name&gt;"
        public string XmlaEndpoint { get; init; } = default!;

        /// The dataset (Database) NAME to connect to, resolved at runtime.
        /// NOTE: Database.Name isn't guaranteed unique across a workspace —
        /// if two datasets ever share a display name, FindByName below
        /// returns whichever the server lists first. That's a known
        /// tradeoff of name-based targeting (vs. Database.ID); flagged here
        /// rather than silently hidden.
        public string DatasetName { get; init; } = default!;

        // ── Snowflake target ─────────────────────────────────────────────
        public string SnowflakeServer { get; init; } = default!;
        public string SnowflakeWarehouse { get; init; } = default!;
        public string SnowflakeDatabase { get; init; } = default!;
        public string SnowflakeSchema { get; init; } = default!;

        /// Optional explicit table → Snowflake object mapping. Key = TOM
        /// table name as it exists in the model. Value = the Snowflake
        /// view/table name to bind it to (auto-uppercased). Any table not
        /// present in this map falls back to mapping its own name,
        /// uppercased, 1:1 — so passing null/empty still works for a
        /// straightforward rename-free migration.
        public IReadOnlyDictionary<string, string>? TableToSnowflakeViewMap { get; init; }

        /// Substrings that mark a partition expression as "legacy /
        /// file-based". Overridable so new source types can be flagged
        /// without a code change; ships with sensible defaults.
        public IReadOnlyList<string> LegacySourceMarkers { get; init; } = new[]
        {
            ".xlsx", ".xls", ".csv", ".accdb", ".mdb",
            "Microsoft.ACE.OLEDB", "Excel.Workbook", "Csv.Document",
            "File.Contents", "Folder.Files"
        };
    }

    public sealed class XmlaMigrationResult
    {
        public List<string> LegacyTablesRemoved { get; } = new();
        public List<string> TablesMappedToSnowflake { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool Success { get; set; }
    }

    /// <summary>
    /// Report-agnostic, two-phase XMLA migrator.
    ///   Phase 1 — detect and remove legacy file-backed tables (and every
    ///             Measure/Relationship/Variation that depends on them)
    ///             from whatever dataset is targeted at runtime, then
    ///             commit an intermediate clean-metadata state.
    ///   Phase 2 — bind the tables that remain (or an explicit mapping) to
    ///             Snowflake views via a dynamically generated M partition
    ///             expression, then commit the final state.
    /// </summary>
    public sealed class FabricSnowflakeXmlaMigrator
    {
        public async Task<XmlaMigrationResult> RunAsync(XmlaMigrationConfig cfg, CancellationToken ct = default)
        {
            Validate(cfg);

            var result = new XmlaMigrationResult();
            Log("INFO", $"Starting XMLA migration run — endpoint='{cfg.XmlaEndpoint}', dataset='{cfg.DatasetName}'");

            var server = new Server();
            try
            {
                Connect(server, cfg);

                var db = server.Databases.FindByName(cfg.DatasetName)
                    ?? throw new InvalidOperationException(
                        $"Dataset '{cfg.DatasetName}' was not found on '{cfg.XmlaEndpoint}'. " +
                        $"Databases visible on this server: {string.Join(", ", server.Databases.Cast<Database>().Select(d => d.Name))}");

                Log("INFO", $"Connected — dataset '{db.Name}' (id={db.ID}), {db.Model.Tables.Count} table(s) currently in the model");

                ct.ThrowIfCancellationRequested();
                Phase0_ScrubVariationTokens(db, result);

                ct.ThrowIfCancellationRequested();
                Phase1_OmitAndClearLegacyTables(db, cfg, result);

                ct.ThrowIfCancellationRequested();
                Phase2_MapRemainingTablesToSnowflake(db, cfg, result);

                result.Success = true;
                Log("INFO", $"Migration complete — {result.LegacyTablesRemoved.Count} legacy table(s) removed, " +
                             $"{result.TablesMappedToSnowflake.Count} table(s) mapped to Snowflake, " +
                             $"{result.Warnings.Count} warning(s).");
            }
            catch (Exception ex)
            {
                result.Success = false;
                Log("ERROR", $"Migration run FAILED: {ex.Message}");
                throw; // never swallow a hard failure — the caller/orchestrator must see it
            }
            finally
            {
                try { server.Disconnect(); } catch { /* best-effort cleanup */ }
                server.Dispose();
            }

            return await Task.FromResult(result);
        }

        // ─────────────────────────────────────────────────────────────
        // Connection
        // ─────────────────────────────────────────────────────────────
        private static void Connect(Server server, XmlaMigrationConfig cfg)
        {
            // App-only (service principal) auth against the XMLA endpoint —
            // no interactive/token flow, suitable for unattended pipelines.
            var connStr =
                $"Provider=MSOLAP;Data Source={cfg.XmlaEndpoint};" +
                $"User ID=app:{cfg.ClientId}@{cfg.TenantId};Password={cfg.ClientSecret};" +
                "Persist Security Info=True;Impersonation Level=Impersonate;";

            Log("INFO", $"Connecting to XMLA endpoint '{cfg.XmlaEndpoint}' as app:{cfg.ClientId}@{cfg.TenantId}…");

            try
            {
                server.Connect(connStr);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to connect to '{cfg.XmlaEndpoint}'. Confirm the workspace is Premium/Fabric, " +
                    "XMLA read-write is enabled in the workspace's Premium settings, and the service " +
                    $"principal is a workspace member with 'Analysis Services API' access. Underlying error: {ex.Message}", ex);
            }

            Log("INFO", "XMLA connection established.");
        }

        private static void Validate(XmlaMigrationConfig cfg)
        {
            void Require(string? v, string name)
            {
                if (string.IsNullOrWhiteSpace(v))
                    throw new ArgumentException($"{name} is required.", name);
            }

            Require(cfg.TenantId, nameof(cfg.TenantId));
            Require(cfg.ClientId, nameof(cfg.ClientId));
            Require(cfg.ClientSecret, nameof(cfg.ClientSecret));
            Require(cfg.XmlaEndpoint, nameof(cfg.XmlaEndpoint));
            Require(cfg.DatasetName, nameof(cfg.DatasetName));
            Require(cfg.SnowflakeServer, nameof(cfg.SnowflakeServer));
            Require(cfg.SnowflakeWarehouse, nameof(cfg.SnowflakeWarehouse));
            Require(cfg.SnowflakeDatabase, nameof(cfg.SnowflakeDatabase));
            Require(cfg.SnowflakeSchema, nameof(cfg.SnowflakeSchema));
        }

        // ─────────────────────────────────────────────────────────────
        // PHASE 0 — Dynamic Pre-Teardown Scan (variation-token scrub)
        //
        // Root cause this fixes: a Variation lets a column be browsed
        // through a hierarchy (e.g. an auto date/time table) using DAX of
        // the form <Column>.[Date] / .[Year] / .[Month] / .[Quarter]. That
        // syntax is only valid while the Variation — and the table it
        // points at — still exists. Phase 1 below deletes exactly those
        // tables/variations, so if we don't also rewrite every Measure and
        // calculated Column expression that still contains one of these
        // suffix tokens, SaveChanges() throws a "variation does not exist"
        // validation crash the moment the table is torn down, regardless
        // of which report/dataset is targeted. Running this scan FIRST —
        // before anything is deleted — makes the fix report-agnostic: no
        // table/column name is ever hardcoded, only the DAX suffix syntax
        // itself is matched and stripped.
        // ─────────────────────────────────────────────────────────────
        private static readonly Regex VariationTokenRegex = new Regex(
            @"\.\s*\[\s*(?:Date|Year|Month|Quarter)\s*\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private void Phase0_ScrubVariationTokens(Database db, XmlaMigrationResult result)
        {
            Log("INFO", "── Phase 0: pre-teardown scan — stripping time-intelligence variation tokens from DAX ──");

            int measuresTouched = 0;
            int columnsTouched = 0;

            foreach (Table table in db.Model.Tables)
            {
                // Measures: iterate defensively with ToList() since we
                // mutate Expression in place while enumerating.
                foreach (Measure measure in table.Measures.ToList())
                {
                    try
                    {
                        if (string.IsNullOrEmpty(measure.Expression)) continue;

                        var original = measure.Expression;
                        var cleaned = VariationTokenRegex.Replace(original, string.Empty);

                        if (!string.Equals(cleaned, original, StringComparison.Ordinal))
                        {
                            measure.Expression = cleaned;
                            measuresTouched++;
                            Log("INFO", $"Stripped variation token(s) from measure '{table.Name}[{measure.Name}]'");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Could not scrub measure '{table.Name}[{measure.Name}]': {ex.Message}");
                        Log("WARN", $"Skipped measure '{table.Name}[{measure.Name}]' during variation scrub: {ex.Message}");
                    }
                }

                // Calculated columns carry a DAX Expression too; regular
                // DataColumns don't, so the type check below is what keeps
                // this loop generic across arbitrary, unknown schemas.
                foreach (Column column in table.Columns)
                {
                    if (column is not CalculatedColumn calcColumn) continue;

                    try
                    {
                        if (string.IsNullOrEmpty(calcColumn.Expression)) continue;

                        var original = calcColumn.Expression;
                        var cleaned = VariationTokenRegex.Replace(original, string.Empty);

                        if (!string.Equals(cleaned, original, StringComparison.Ordinal))
                        {
                            calcColumn.Expression = cleaned;
                            columnsTouched++;
                            Log("INFO", $"Stripped variation token(s) from calculated column '{table.Name}[{column.Name}]'");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Could not scrub calculated column '{table.Name}[{column.Name}]': {ex.Message}");
                        Log("WARN", $"Skipped calculated column '{table.Name}[{column.Name}]' during variation scrub: {ex.Message}");
                    }
                }
            }

            if (measuresTouched == 0 && columnsTouched == 0)
            {
                Log("INFO", "No variation-suffix tokens found in any measure or calculated column — Phase 0 is a no-op for this dataset.");
                return;
            }

            // Commit the DAX rewrite on its own, before Phase 1 deletes
            // anything — if this fails, we bail out with the model still
            // fully intact instead of leaving it half-scrubbed.
            try
            {
                db.Model.SaveChanges();
                Log("INFO", $"Phase 0 SaveChanges() committed — {measuresTouched} measure(s), {columnsTouched} calculated column(s) rewritten.");
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Phase 0 SaveChanges() failed: {ex.Message}");
                throw new InvalidOperationException(
                    "Phase 0 (variation-token scrub) failed to commit. No table removal or Snowflake mapping was attempted. " +
                    $"Underlying error: {ex.Message}", ex);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // PHASE 1 — Dynamic Omit & Clear
        // ─────────────────────────────────────────────────────────────
        private void Phase1_OmitAndClearLegacyTables(Database db, XmlaMigrationConfig cfg, XmlaMigrationResult result)
        {
            Log("INFO", "── Phase 1: scanning for legacy file-backed tables ──");

            var legacyTables = new List<Table>();

            foreach (Table table in db.Model.Tables)
            {
                if (IsLegacyFileBackedTable(table, cfg.LegacySourceMarkers, out var matchedMarker))
                {
                    Log("INFO", $"Flagged '{table.Name}' as LEGACY — partition expression contains '{matchedMarker}'");
                    legacyTables.Add(table);
                }
            }

            if (legacyTables.Count == 0)
            {
                Log("INFO", "No legacy file-backed tables detected — Phase 1 is a no-op for this dataset.");
                return;
            }

            var legacyNames = new HashSet<string>(legacyTables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

            // Clear every Measure living ON a legacy table first — a Measure
            // can't survive its parent table being removed, and clearing it
            // explicitly (rather than relying on cascade) keeps
            // SaveChanges() from throwing a dependency-validation error.
            foreach (var table in legacyTables)
            {
                var measureCount = table.Measures.Count;
                foreach (var m in table.Measures.ToList())
                    table.Measures.Remove(m);
                if (measureCount > 0)
                    Log("INFO", $"Cleared {measureCount} measure(s) from legacy table '{table.Name}'");
            }

            // Clear every Relationship referencing a legacy table on EITHER
            // side — a dangling FromTable/ToTable reference is the single
            // most common SaveChanges() failure right after a table delete.
            var relationshipsToRemove = db.Model.Relationships
                .Cast<Relationship>()
                .Where(r => r is SingleColumnRelationship scr &&
                            (legacyNames.Contains(scr.FromTable?.Name ?? string.Empty) ||
                             legacyNames.Contains(scr.ToTable?.Name ?? string.Empty)))
                .ToList();

            foreach (var r in relationshipsToRemove)
            {
                Log("INFO", $"Removing relationship '{r.Name}' (references a legacy table)");
                db.Model.Relationships.Remove(r);
            }

            // Strip any Variation on a SURVIVING column that targets a
            // legacy table being removed — same class of dangling reference
            // as a relationship, just surfaced via Variations instead (e.g.
            // a custom date-table variation pointing at a legacy table).
            foreach (Table table in db.Model.Tables)
            {
                if (legacyNames.Contains(table.Name)) continue;
                foreach (Column col in table.Columns)
                {
                    if (col is not DataColumn dc) continue;
                    foreach (var v in dc.Variations.ToList())
                    {
                        var targetName = TryGetVariationTargetTableName(v);
                        if (targetName != null && legacyNames.Contains(targetName))
                        {
                            dc.Variations.Remove(v);
                            Log("INFO", $"Removed variation on '{table.Name}.{col.Name}' targeting legacy table '{targetName}'");
                        }
                    }
                }
            }

            // Finally, delete the legacy tables themselves.
            foreach (var table in legacyTables)
            {
                db.Model.Tables.Remove(table);
                result.LegacyTablesRemoved.Add(table.Name);
                Log("INFO", $"Deleted legacy table '{table.Name}' from the model");
            }

            // Commit the intermediate clean-metadata state BEFORE Phase 2
            // touches anything — if this fails, Phase 2 never runs against
            // a half-cleared model.
            try
            {
                db.Model.SaveChanges();
                Log("INFO", $"Phase 1 SaveChanges() committed — {legacyTables.Count} legacy table(s), " +
                             $"{relationshipsToRemove.Count} relationship(s) removed.");
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Phase 1 SaveChanges() failed: {ex.Message}");
                throw new InvalidOperationException(
                    "Phase 1 (legacy table removal) failed to commit. No Snowflake mapping was attempted. " +
                    $"Underlying error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// A table is "legacy" if ANY of its partitions is an M expression
        /// whose text references a local-file provider/extension. This is a
        /// text-level scan of the partition Expression, not a parse of the
        /// M AST — deliberately simple and dependency-free, and sufficient
        /// because Power Query always names its file/provider functions and
        /// extensions literally in the expression text.
        /// </summary>
        private static bool IsLegacyFileBackedTable(Table table, IReadOnlyList<string> markers, out string matchedMarker)
        {
            matchedMarker = string.Empty;

            foreach (Partition partition in table.Partitions)
            {
                if (partition.SourceType != PartitionSourceType.M) continue;
                if (partition.Source is not MPartitionSource mSource) continue;

                var expr = mSource.Expression ?? string.Empty;

                foreach (var marker in markers)
                {
                    if (expr.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matchedMarker = marker;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string? TryGetVariationTargetTableName(Variation v)
        {
            try
            {
                if (v.Relationship is SingleColumnRelationship scr && scr.ToTable != null)
                    return scr.ToTable.Name;
            }
            catch { /* SDK-version defensive — fall through */ }

            try { return v.DefaultHierarchy?.Table?.Name; }
            catch { return null; }
        }

        // ─────────────────────────────────────────────────────────────
        // PHASE 2 — Dynamic Snowflake View Mapping & Target Ingestion
        // ─────────────────────────────────────────────────────────────
        private void Phase2_MapRemainingTablesToSnowflake(Database db, XmlaMigrationConfig cfg, XmlaMigrationResult result)
        {
            Log("INFO", "── Phase 2: mapping remaining tables to Snowflake ──");

            var remainingTables = db.Model.Tables.Cast<Table>().ToList();

            if (remainingTables.Count == 0)
            {
                Log("WARN", "No tables remain in the model after Phase 1 — nothing to map in Phase 2.");
                result.Warnings.Add("Phase 2 ran with zero remaining tables.");
                return;
            }

            foreach (var table in remainingTables)
            {
                // Explicit override wins; otherwise fall back to the
                // table's own name. Snowflake identifiers are normalized to
                // UPPER CASE unless the caller already cased them
                // deliberately in the map.
                var snowflakeObjectName =
                    (cfg.TableToSnowflakeViewMap != null &&
                     cfg.TableToSnowflakeViewMap.TryGetValue(table.Name, out var mapped))
                        ? mapped
                        : table.Name.ToUpperInvariant();

                var mExpression = BuildSnowflakeMExpression(cfg, snowflakeObjectName);

                var partition = table.Partitions.FirstOrDefault();
                if (partition == null)
                {
                    partition = new Partition { Name = table.Name };
                    table.Partitions.Add(partition);
                    Log("INFO", $"Table '{table.Name}' had no partition — created one for the Snowflake binding.");
                }

                partition.Source = new MPartitionSource { Expression = mExpression };

                Log("INFO", $"Mapped table '{table.Name}' → Snowflake object " +
                             $"'{cfg.SnowflakeDatabase.ToUpperInvariant()}.{cfg.SnowflakeSchema.ToUpperInvariant()}.{snowflakeObjectName.ToUpperInvariant()}'");

                result.TablesMappedToSnowflake.Add(table.Name);
            }

            try
            {
                db.Model.SaveChanges();
                Log("INFO", $"Phase 2 SaveChanges() committed — {result.TablesMappedToSnowflake.Count} table(s) now Snowflake-backed.");
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Phase 2 SaveChanges() failed: {ex.Message}");
                throw new InvalidOperationException(
                    "Phase 2 (Snowflake mapping) failed to commit. NOTE: Phase 1's legacy-table removal was " +
                    $"already committed and is NOT rolled back by this failure. Underlying error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Builds a native-Snowflake-connector M expression via
        /// Snowflake.Databases(...), navigating Database → Schema → View by
        /// name. The native connector (rather than a raw SQL.Database
        /// statement) resolves query folding and credentials the same way
        /// Power BI Desktop's own "Get Data → Snowflake" wizard would, and
        /// is what the Power BI Service's credential-binding UI expects.
        /// Step names are fixed literals (not derived from the dynamic
        /// values) so the generated M is always a syntactically valid
        /// identifier regardless of what characters appear in the
        /// database/schema/object names.
        /// </summary>
        private static string BuildSnowflakeMExpression(XmlaMigrationConfig cfg, string snowflakeObjectName)
        {
            // Snowflake object identifiers are case-normalized to UPPER by
            // Snowflake itself unless quoted at creation time — matching
            // that here means the M navigation step resolves first-try
            // instead of silently returning zero rows on a case mismatch.
            var database = cfg.SnowflakeDatabase.ToUpperInvariant();
            var schema   = cfg.SnowflakeSchema.ToUpperInvariant();
            var obj      = snowflakeObjectName.ToUpperInvariant();

            return
$@"let
    Source = Snowflake.Databases(""{cfg.SnowflakeServer}"", ""{cfg.SnowflakeWarehouse}"", [Role=null]),
    DatabaseNav = Source{{[Name=""{database}"",Kind=""Database""]}}[Data],
    SchemaNav = DatabaseNav{{[Name=""{schema}"",Kind=""Schema""]}}[Data],
    ObjectNav = SchemaNav{{[Name=""{obj}"",Kind=""View""]}}[Data]
in
    ObjectNav";
        }

        // ─────────────────────────────────────────────────────────────
        // Logging — verbose, structured console output for pipeline
        // observability. Swap for ILogger in a hosted/DI context if
        // desired; kept as Console here so this class runs standalone in a
        // CI job, Azure Function, or console app with zero DI wiring.
        // ─────────────────────────────────────────────────────────────
        private static void Log(string level, string message) =>
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] [{level,-5}] {message}");
    }
}
