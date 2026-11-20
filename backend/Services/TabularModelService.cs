// using System;
// using System.Collections.Generic;
// using System.Diagnostics;
// using System.Linq;
// using System.Text.RegularExpressions;
// using System.Threading.Tasks;
// using Microsoft.AnalysisServices.Tabular;
// using Microsoft.Extensions.Logging;
// using PbixMigrator.Models;

// namespace PbixMigrator.Services
// {
//     public class TabularModelService
//     {
//         private readonly ILogger<TabularModelService> _log;

//         // ─────────────────────────────────────────────────────────────────────
//         // Power BI's "Auto date/time" feature silently creates one hidden
//         // LocalDateTable_<guid> (or DateTableTemplate_<guid>) per date column,
//         // each with ShowAsVariationsOnly=1. That flag is only legal while the
//         // table remains the TARGET of a Variation on the source date column.
//         //
//         // These tables are 100% Power-BI-managed — they never map to a
//         // Snowflake view and must never be treated as "missing" or have their
//         // variation link severed. The safest, most robust behaviour is to
//         // never touch them at all: don't remove them, don't rebuild them,
//         // don't strip the variation that targets them, don't extract them as
//         // visual/column bindings. Leave them exactly as Power BI made them.
//         // ─────────────────────────────────────────────────────────────────────
//         private static readonly Regex AutoDateTablePattern =
//             new(@"^(LocalDateTable_|DateTableTemplate_)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

//         internal static bool IsAutoDateTable(string? tableName) =>
//             !string.IsNullOrEmpty(tableName) && AutoDateTablePattern.IsMatch(tableName);

//         public TabularModelService(ILogger<TabularModelService> log) => _log = log;

//         // Delegated user token for the XMLA connection. The MSOLAP provider
//         // accepts an already-acquired AAD bearer token directly as the
//         // connection-string Password when User ID is left blank — that's a
//         // DIFFERENT connection-string form than the app-only
//         // "User ID=app:{clientId}@{tenantId};Password={clientSecret}" one
//         // this used previously; the two must not be mixed. This method
//         // no longer touches TenantId/ClientId/ClientSecret at all — it just
//         // hands back cfg.UserAccessToken, the same delegated token
//         // PowerBIService.GetEffectiveTokenAsync uses for the REST API, so
//         // the XMLA connection runs as the signed-in user and is subject to
//         // their own workspace permissions (Contributor+ / Analysis Services
//         // API access), not the app registration's.
//         private static Task<string> GetAccessTokenAsync(PowerBIConfig cfg)
//         {
//             if (string.IsNullOrWhiteSpace(cfg.UserAccessToken))
//                 throw new InvalidOperationException(
//                     "No signed-in user token was provided. The XMLA connection now runs as the signed-in " +
//                     "user (delegated auth), not a service principal. Sign in with Microsoft on the " +
//                     "frontend (SignInGate) and make sure the acquired token is sent as " +
//                     "PowerBIConfig.UserAccessToken.");

//             return Task.FromResult(cfg.UserAccessToken);
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // Connect to published dataset via XMLA and extract FULL VISUAL METADATA.
//         //
//         // Unlike the previous flat (Table, Column) list, this returns:
//         //   - Which page each visual is on
//         //   - Visual name, title, type
//         //   - Each column the visual binds, with its data role
//         //
//         // This is the foundation for the per-visual column rebind pipeline.
//         // The TOM API does not expose visual-to-field bindings directly (those
//         // live in the Report/Layout JSON), so we extract all Table.Column pairs
//         // from the semantic model and cross-reference with the layout JSON when
//         // available. When the PBIX bytes are in session, PbixService provides
//         // the richer visual-level extraction; this method covers the XMLA path.
//         // ─────────────────────────────────────────────────────────────────────
//         public async Task<(Server server, Database db, List<VisualMetadata> visuals)>
//             ConnectAndExtractVisualMetadataAsync(PowerBIConfig cfg, string datasetId, string? datasetNameForLog = null)
//         {
//             if (string.IsNullOrWhiteSpace(cfg.XmlaEndpoint))
//                 throw new InvalidOperationException(
//                     "PowerBIConfig.XmlaEndpoint is required. " +
//                     "Set it to powerbi://api.powerbi.com/v1.0/myorg/<workspace name>.");

//             _log.LogInformation("[XMLA] Connecting to '{ep}' as the signed-in user (delegated token)…", cfg.XmlaEndpoint);
//             var accessToken = await GetAccessTokenAsync(cfg);
//             var server = new Server();
//             var connStr =
//                 $"Provider=MSOLAP;Data Source={cfg.XmlaEndpoint};" +
//                 $"Password={accessToken};" +
//                 "Persist Security Info=True;Impersonation Level=Impersonate";

//             try { server.Connect(connStr); _log.LogInformation("[XMLA] Connected OK to {ep} as signed-in user", cfg.XmlaEndpoint); }
//             catch (Exception ex)
//             {
//                 _log.LogError(ex, "[XMLA] Connect FAILED to {ep}", cfg.XmlaEndpoint);
//                 server.Dispose();
//                 throw new InvalidOperationException(
//                     $"Failed to connect to XMLA endpoint '{cfg.XmlaEndpoint}'. " +
//                     $"Ensure workspace is Premium/Fabric and XMLA read/write is enabled. Error: {ex.Message}", ex);
//             }

//             // ── Resolve by dataset ID, NOT by name ──────────────────────────
//             // Over XMLA, Database.ID on a Power BI workspace IS the dataset's
//             // GUID (the same ID the REST API and session.DatasetId use) —
//             // Database.Name is just the display name, which is NOT unique.
//             // Re-running this tool against the same PBIX file name, or any
//             // pre-existing dataset that happens to share that name, produces
//             // more than one Database with an identical Name. FindByName used
//             // to grab whichever one the server listed first, silently
//             // wiping/reading the WRONG dataset while the one the user was
//             // actually looking at in the Power BI Service sat untouched —
//             // this is why a "successful" run could still show the old
//             // dataset with no changes. Matching on ID makes this
//             // unambiguous.
//             if (string.IsNullOrWhiteSpace(datasetId))
//             {
//                 server.Dispose();
//                 throw new InvalidOperationException(
//                     "ConnectAndExtractVisualMetadataAsync requires a dataset ID (GUID), not a name.");
//             }

//             Database? db;
//             try
//             {
//                 db = server.Databases.Cast<Database>()
//                     .FirstOrDefault(d => string.Equals(d.ID, datasetId, StringComparison.OrdinalIgnoreCase));
//             }
//             catch (Exception ex)
//             {
//                 _log.LogError(ex, "[XMLA] Lookup by dataset ID '{id}' failed", datasetId);
//                 server.Dispose();
//                 throw new InvalidOperationException($"Lookup failed: {ex.Message}", ex);
//             }

//             if (db == null)
//             {
//                 var visible = server.Databases.Cast<Database>()
//                     .Select(d => $"{d.Name} (id={d.ID})");
//                 _log.LogError("[XMLA] Dataset with ID '{id}' (name '{name}') not found. Databases visible on this server: {list}",
//                     datasetId, datasetNameForLog, string.Join(", ", visible));
//                 server.Dispose();
//                 throw new InvalidOperationException(
//                     $"Dataset with ID '{datasetId}' not found on the XMLA endpoint.");
//             }
//             _log.LogInformation("[XMLA] Found dataset '{ds}' (id={id}) — {tables} table(s) in current model",
//                 db.Name, datasetId, db.Model.Tables.Count);

//             // Build visual metadata from the TOM model.
//             // TOM gives us tables/columns/measures but NOT visual layout.
//             // We synthesise one VisualMetadata entry per (Table, Column) pair,
//             // tagged as "model-level" so the frontend knows to do a full rebind.
//             // The PBIX layout fallback in PbixService.ExtractVisualMetadata()
//             // provides richer per-visual data when the PBIX bytes are available.
//             var visuals = new List<VisualMetadata>();

//             foreach (Table table in db.Model.Tables)
//             {
//                 if (IsAutoDateTable(table.Name))
//                 {
//                     _log.LogInformation("[XMLA] Skipping auto date table '{t}' in visual metadata extraction", table.Name);
//                     continue;
//                 }

//                 foreach (Column col in table.Columns)
//                 {
//                     if (col.Type == ColumnType.RowNumber) continue;

//                     visuals.Add(new VisualMetadata
//                     {
//                         PageName    = "(model)",
//                         VisualName  = $"model::{table.Name}",
//                         VisualTitle = table.Name,
//                         VisualType  = "table",
//                         BoundColumns = new List<VisualColumnBinding>
//                         {
//                             new VisualColumnBinding
//                             {
//                                 Table    = table.Name,
//                                 Column   = col.Name,
//                                 DataRole = "Values"
//                             }
//                         }
//                     });
//                 }
//             }

//             _log.LogInformation(
//                 "Extracted {count} column bindings from dataset '{ds}' via XMLA",
//                 visuals.Sum(v => v.BoundColumns.Count), db.Name);

//             return (server, db, visuals);
//         }

//         // ── Legacy: flat binding extraction (kept for backward compat) ─────────
//         public async Task<(Server server, Database db, List<VisualBinding> bindings)>
//             ConnectAndExtractBindingsAsync(PowerBIConfig cfg, string datasetId)
//         {
//             var (server, db, visuals) = await ConnectAndExtractVisualMetadataAsync(cfg, datasetId);
//             var bindings = visuals
//                 .SelectMany(v => v.BoundColumns)
//                 .Select(b => new VisualBinding { OriginalTable = b.Table, OriginalColumn = b.Column })
//                 .GroupBy(b => $"{b.OriginalTable}.{b.OriginalColumn}", StringComparer.OrdinalIgnoreCase)
//                 .Select(g => g.First())
//                 .ToList();
//             return (server, db, bindings);
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // CLEAN REPLACE: remove every existing table (and everything that
//         // hangs off it — columns, measures, hierarchies, relationships),
//         // then load every selected-schema Snowflake view in as a fresh
//         // table. No name-matching, no reuse of old table/column objects —
//         // the dataset ends this call containing ONLY the newly-loaded
//         // Snowflake tables. Visual bindings that pointed at the old tables
//         // will break; that's expected and is what /rebind-columns (the
//         // LLM-driven analysis step) exists to repair afterward.
//         // ─────────────────────────────────────────────────────────────────────
//         public List<UnmappedTable> WipeAndLoadSnowflakeViews(
//             Database db,
//             SnowflakeConfig sfCfg,
//             List<SnowflakeViewMeta> sfViews)
//         {
//             var unmapped = new List<UnmappedTable>();

//             var existingTableNames = db.Model.Tables.Cast<Table>().Select(t => t.Name).ToList();

//             var savedRelationships = db.Model.Relationships
//             .OfType<SingleColumnRelationship>()
//             .Select(r => new {
//             FromTable = r.FromTable.Name,
//             FromColumn = r.FromColumn.Name,
//             ToTable = r.ToTable.Name,
//             ToColumn = r.ToColumn.Name,
//             IsActive = r.IsActive
//             }).ToList();

//             foreach (var r in db.Model.Relationships.Cast<Relationship>().ToList())
//                 db.Model.Relationships.Remove(r);
//             foreach (var t in db.Model.Tables.Cast<Table>().ToList())
//                 db.Model.Tables.Remove(t);

//             _log.LogInformation(
//                 "[XMLA] Wiped {n} existing table(s) and all relationships — dataset is now empty: {list}",
//                 existingTableNames.Count, string.Join(", ", existingTableNames));

//             var duplicateViewNameCounts = sfViews
//                 .GroupBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase)
//                 .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

//             var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

//             foreach (var sfView in sfViews)
//             {
//                 // Two selected schemas can have a view with the same bare
//                 // name (e.g. BRONZE.CUSTOMERS and GOLD.CUSTOMERS) — TOM
//                 // requires unique table names within one dataset, so qualify
//                 // with the schema whenever that would collide. This is only
//                 // a naming safeguard, not a merge/match of any kind.
//                 var needsQualifier = duplicateViewNameCounts[sfView.ViewName] > 1;

//                 var tableName = needsQualifier
//                     ? $"{sfView.ViewName}_{sfView.Schema}"
//                     : sfView.ViewName;

//                 var suffix = 2;
//                 while (usedNames.Contains(tableName))
//                     tableName = $"{sfView.ViewName}_{sfView.Schema}_{suffix++}";

//                 usedNames.Add(tableName);

//                 if (needsQualifier)
//                     _log.LogInformation(
//                         "[XMLA] '{s}.{v}' renamed to table '{t}' to avoid a name clash with another selected schema",
//                         sfView.Schema, sfView.ViewName, tableName);

//                 var newTable = new Table { Name = tableName };
//                 var mExpr    = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);

//                 newTable.Partitions.Add(new Partition
//                 {
//                     Name   = tableName,
//                     Source = new MPartitionSource { Expression = mExpr }
//                 });

//                 foreach (var sfCol in sfView.Columns)
//                 {
//                     newTable.Columns.Add(new DataColumn
//                     {
//                         Name         = sfCol.Name,
//                         SourceColumn = sfCol.Name,
//                         DataType     = MapDataType(sfCol.DataType),
//                         SummarizeBy  = AggregateFunction.None
//                     });
//                 }

//                 db.Model.Tables.Add(newTable);
//                 _log.LogInformation(
//                     "[XMLA] Loaded Snowflake view '{s}.{v}' as new table '{t}'",
//                     sfView.Schema, sfView.ViewName, tableName);
//             }

//             foreach (var rel in savedRelationships)
//             {
//                 var fromTable = db.Model.Tables.FirstOrDefault(t => t.Name.Equals(rel.FromTable, StringComparison.OrdinalIgnoreCase));
//                 var toTable = db.Model.Tables.FirstOrDefault(t => t.Name.Equals(rel.ToTable, StringComparison.OrdinalIgnoreCase));

//                 if (fromTable != null && toTable != null)
//                 {
//                     var fromCol = fromTable.Columns.FirstOrDefault(c => c.Name.Equals(rel.FromColumn, StringComparison.OrdinalIgnoreCase));
//                     var toCol = toTable.Columns.FirstOrDefault(c => c.Name.Equals(rel.ToColumn, StringComparison.OrdinalIgnoreCase));

//                     if (fromCol != null && toCol != null)
//                     {
//                        var newRel = new SingleColumnRelationship
//                        {
//                             Name = $"{fromTable.Name}_{fromCol.Name}_{toTable.Name}_{toCol.Name}",
//                             FromColumn = fromCol,
//                             ToColumn = toCol,
//                             IsActive = rel.IsActive
//                         };
//                         db.Model.Relationships.Add(newRel);
//                     }
//                 }
//             }

//             _log.LogInformation("[XMLA] Re-created {count} relationship(s) in dataset.", db.Model.Relationships.Count);

//             DisableAutomaticModelBehaviors(db);

//             _log.LogInformation(
//                 "Dataset wiped and rebuilt from scratch: {n} Snowflake view(s) loaded, {removed} old table(s) removed. " +
//                 "Run /rebind-columns to repair visuals that referenced the old tables.",
//                 sfViews.Count, existingTableNames.Count);

//             return unmapped;
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // ADD the Snowflake views to the dataset as brand-new tables.
//         // Deliberately does NOT try to match a Snowflake view to an existing
//         // PBIX table by name, does NOT touch/rename/remove any existing
//         // table, column, measure, or relationship, and does NOT rewire any
//         // partition source on a pre-existing table. The previous dataset is
//         // left exactly as it was; the new Snowflake-backed tables sit
//         // alongside it. Reconciling old columns → new columns is handled
//         // entirely afterward by the separate LLM-driven analysis step
//         // (AzureOpenAIService.RebindVisualColumnsAsync via /rebind-columns),
//         // which compares the old dataset's visual bindings against these
//         // newly-loaded views and proposes the column mapping.
//         // ─────────────────────────────────────────────────────────────────────
//         public List<UnmappedTable> LoadSnowflakeViewsAlongsideExisting(
//             Database db,
//             SnowflakeConfig sfCfg,
//             List<SnowflakeViewMeta> sfViews)
//         {
//             var unmapped = new List<UnmappedTable>();

//             var existingTableNames = new HashSet<string>(
//                 db.Model.Tables.Cast<Table>().Select(t => t.Name),
//                 StringComparer.OrdinalIgnoreCase);

//             var duplicateViewNameCounts = sfViews
//                 .GroupBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase)
//                 .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

//             var usedNames = new HashSet<string>(existingTableNames, StringComparer.OrdinalIgnoreCase);

//             foreach (var sfView in sfViews)
//             {
//                 // Two Snowflake views selected together can share a bare name
//                 // (e.g. BRONZE.CUSTOMERS and GOLD.CUSTOMERS) or collide with
//                 // a table name already in the dataset — TOM requires unique
//                 // table names, so qualify with the schema whenever that would
//                 // happen. Nothing is matched/merged; this only avoids a
//                 // duplicate-name save error.
//                 var needsQualifier =
//                     duplicateViewNameCounts[sfView.ViewName] > 1 ||
//                     usedNames.Contains(sfView.ViewName);

//                 var tableName = (needsQualifier
//                 ? $"{sfView.ViewName}_{sfView.Schema}": sfView.ViewName).ToUpperInvariant();

//                 var suffix = 2;
//                 while (usedNames.Contains(tableName))
//                     tableName = $"{sfView.ViewName}_{sfView.Schema}_{suffix++}";

//                 usedNames.Add(tableName);

//                 if (needsQualifier)
//                     _log.LogInformation(
//                         "[XMLA] '{s}.{v}' renamed to table '{t}' to avoid a name clash while loading alongside the existing dataset",
//                         sfView.Schema, sfView.ViewName, tableName);

//                 var newTable = new Table { Name = tableName };
//                 var mExpr    = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);

//                 newTable.Partitions.Add(new Partition
//                 {
//                     Name   = tableName,
//                     Source = new MPartitionSource { Expression = mExpr }
//                 });

//                 foreach (var sfCol in sfView.Columns)
//                 {
//                     newTable.Columns.Add(new DataColumn
//                     {
//                         Name         = sfCol.Name,
//                         SourceColumn = sfCol.Name,
//                         DataType     = MapDataType(sfCol.DataType),
//                         SummarizeBy  = AggregateFunction.None
//                     });
//                 }

//                 db.Model.Tables.Add(newTable);
//                 _log.LogInformation(
//                     "[XMLA] Loaded Snowflake view '{s}.{v}' as new table '{t}' — existing dataset left untouched",
//                     sfView.Schema, sfView.ViewName, tableName);
//             }

//             // Turn off implicit measures / auto summarization / auto data
//             // categories / auto date-time tables, same as the replace path.
//             DisableAutomaticModelBehaviors(db);

//             _log.LogInformation(
//                 "Loaded {n} Snowflake view(s) as new tables. Existing dataset tables were not modified — " +
//                 "run /rebind-columns to analyze and map old columns to these new tables.",
//                 sfViews.Count);

//             return unmapped;
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // PURE ADDITIVE LOAD — no matching, no comparison, no removal. Every
//         // view in the selected Snowflake schemas is loaded in as its own new
//         // table. Existing tables in the dataset are never inspected, updated,
//         // or removed — this method doesn't look at them at all. The only
//         // thing it checks existing table/loaded-view names for is avoiding a
//         // duplicate TOM table name (TOM requires unique names within a
//         // model), which is a naming safeguard, not a match/compare.
//         //
//         // "Power BI automatic detection" — implicit measures, auto
//         // Sum/Count summarization, auto data categories, auto date/time
//         // tables — is turned OFF up front (before any table is added) AND
//         // again after loading.
//         // ─────────────────────────────────────────────────────────────────────
//         public List<UnmappedTable> UpdateSnowflakeViewsInPlace(
//             Database db,
//             SnowflakeConfig sfCfg,
//             List<SnowflakeViewMeta> sfViews)
//         {
//             var unmapped = new List<UnmappedTable>();

//             // Auto-detection OFF before a single table is added.
//             DisableAutomaticModelBehaviors(db);

//             var existingNames = new HashSet<string>(
//                 db.Model.Tables.Cast<Table>().Select(t => t.Name),
//                 StringComparer.OrdinalIgnoreCase);

//             var duplicateViewNameCounts = sfViews
//                 .GroupBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase)
//                 .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

//             var usedNames = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

//             foreach (var sfView in sfViews)
//             {
//                 // Only a naming safeguard so TOM's "unique table name"
//                 // requirement isn't violated — not a match/merge of any
//                 // kind. Two selected schemas can have a view with the same
//                 // bare name (e.g. BRONZE.CUSTOMERS and GOLD.CUSTOMERS), or a
//                 // view name can collide with a pre-existing table name.
//                 var needsQualifier =
//                     duplicateViewNameCounts[sfView.ViewName] > 1 ||
//                     usedNames.Contains(sfView.ViewName) ||
//                     IsAutoDateTable(sfView.ViewName);

//                 var tableName = needsQualifier
//                     ? $"{sfView.ViewName}_{sfView.Schema}"
//                     : sfView.ViewName;

//                 var suffix = 2;
//                 while (usedNames.Contains(tableName))
//                     tableName = $"{sfView.ViewName}_{sfView.Schema}_{suffix++}";

//                 usedNames.Add(tableName);

//                 if (needsQualifier)
//                     _log.LogInformation(
//                         "[XMLA] '{s}.{v}' renamed to table '{t}' to avoid a name clash",
//                         sfView.Schema, sfView.ViewName, tableName);

//                 var newTable = new Table { Name = tableName };
//                 var mExpr    = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);

//                 newTable.Partitions.Add(new Partition
//                 {
//                     Name   = tableName,
//                     Source = new MPartitionSource { Expression = mExpr }
//                 });

//                 foreach (var sfCol in sfView.Columns)
//                 {
//                     newTable.Columns.Add(new DataColumn
//                     {
//                         Name         = sfCol.Name,
//                         SourceColumn = sfCol.Name,
//                         DataType     = MapDataType(sfCol.DataType),
//                         SummarizeBy  = AggregateFunction.None
//                     });
//                 }

//                 db.Model.Tables.Add(newTable);
//                 _log.LogInformation(
//                     "[XMLA] Loaded Snowflake view '{s}.{v}' as new table '{t}' — no matching/removal, existing dataset untouched",
//                     sfView.Schema, sfView.ViewName, tableName);
//             }

//             // Re-apply after loading too — cheap, idempotent, and covers
//             // anything adding the new tables above might have touched.
//             DisableAutomaticModelBehaviors(db);

//             _log.LogInformation(
//                 "[XMLA] Additive load complete: {n} Snowflake view(s) loaded as new tables, 0 removed, 0 compared",
//                 sfViews.Count);

//             return unmapped;
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // v19 Lakehouse path: same additive-load shape as
//         // UpdateSnowflakeViewsInPlace above, but the partition M expression
//         // points at the Fabric Lakehouse's SQL Analytics Endpoint instead of
//         // straight at Snowflake. Power BI never sees a Snowflake connection
//         // at all with this path — Snowflake auth (PAT) stays entirely inside
//         // this backend's ingestion step (FabricLakehouseService), and the
//         // dataset only ever talks to a Fabric-native SQL endpoint in the
//         // same tenant, which resolves via Microsoft Entra ID rather than a
//         // manually-bound data-source credential.
//         // ─────────────────────────────────────────────────────────────────────
//         public static string BuildLakehouseSqlMExpression(string sqlServer, string sqlDatabase, string tableName)
//         {
//             // Lakehouse tables surface under the "dbo" schema on the SQL
//             // Analytics Endpoint regardless of how they were loaded.
//             return
//                 $"let\n" +
//                 $"    Source = Sql.Database(\"{sqlServer}\", \"{sqlDatabase}\"),\n" +
//                 $"    Nav    = Source{{[Schema=\"dbo\",Item=\"{tableName}\"]}}[Data]\n" +
//                 $"in\n" +
//                 $"    Nav";
//         }

//         public List<UnmappedTable> UpdateLakehouseTablesInPlace(
//             Database db,
//             string sqlServer,
//             string sqlDatabase,
//             List<LakehouseTableMeta> lakehouseTables)
//         {
//             var unmapped = new List<UnmappedTable>();

//             DisableAutomaticModelBehaviors(db);

//             var existingNames = new HashSet<string>(
//                 db.Model.Tables.Cast<Table>().Select(t => t.Name),
//                 StringComparer.OrdinalIgnoreCase);

//             var usedNames = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

//             foreach (var lhTable in lakehouseTables)
//             {
//                 var tableName = lhTable.TableName;
//                 if (usedNames.Contains(tableName) || IsAutoDateTable(tableName))
//                 {
//                     var suffix = 2;
//                     var candidate = $"{tableName}_{suffix}";
//                     while (usedNames.Contains(candidate))
//                         candidate = $"{tableName}_{suffix++}";
//                     _log.LogInformation(
//                         "[Lakehouse] '{orig}' renamed to table '{t}' to avoid a name clash",
//                         tableName, candidate);
//                     tableName = candidate;
//                 }
//                 usedNames.Add(tableName);

//                 var newTable = new Table { Name = tableName };
//                 var mExpr = BuildLakehouseSqlMExpression(sqlServer, sqlDatabase, lhTable.TableName);

//                 newTable.Partitions.Add(new Partition
//                 {
//                     Name = tableName,
//                     // DirectQuery, not Import — the table's rows are never
//                     // pulled into the semantic model at save/refresh time;
//                     // every query hits the Lakehouse SQL Analytics Endpoint
//                     // live. This also means SaveChanges() never needs to
//                     // process/import this partition, so there's nothing here
//                     // for Transaction 3 (RequestFullRefreshAndSave) to fail
//                     // on for these tables specifically.
//                     Mode   = ModeType.DirectQuery,
//                     Source = new MPartitionSource { Expression = mExpr }
//                 });

//                 foreach (var col in lhTable.Columns)
//                 {
//                     // SourceColumn must be the PHYSICAL name Fabric actually
//                     // stored (col.SourceColumn, sanitized) — the CSV header
//                     // Load Table ingested was sanitized too, so pointing TOM
//                     // at the raw col.Name here would make the partition
//                     // query a column that doesn't exist. Name stays the
//                     // original Snowflake column name so Power BI still shows
//                     // familiar field names to report authors.
//                     var physicalName = string.IsNullOrEmpty(col.SourceColumn) ? col.Name : col.SourceColumn;
//                     newTable.Columns.Add(new DataColumn
//                     {
//                         Name = col.Name,
//                         SourceColumn = physicalName,
//                         DataType = MapDataType(col.DataType),
//                         SummarizeBy = AggregateFunction.None
//                     });
//                 }

//                 db.Model.Tables.Add(newTable);
//                 _log.LogInformation(
//                     "[Lakehouse] Bound table '{t}' → SQL Analytics Endpoint table 'dbo.{lh}' (source Snowflake view '{s}.{v}', {rows} row(s) loaded)",
//                     tableName, lhTable.TableName, lhTable.SourceSchema, lhTable.SourceView, lhTable.RowCount);
//             }

//             DisableAutomaticModelBehaviors(db);

//             _log.LogInformation(
//                 "[Lakehouse] Additive load complete: {n} Lakehouse table(s) bound, 0 removed, 0 compared",
//                 lakehouseTables.Count);

//             return unmapped;
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // REMOVE the original (pre-Snowflake) tables from the dataset. Call
//         // this only after the frontend's Visual Repair Layer has finished
//         // rebinding every visual to the new Snowflake-backed tables — at
//         // that point nothing should still reference these tables, so it's
//         // safe to drop them and leave the dataset containing only the new
//         // Snowflake tables (which is the end state of the whole migration).
//         // ─────────────────────────────────────────────────────────────────────
//         public void RemoveOriginalTables(Database db, List<string> originalTableNames)
//         {
//             if (originalTableNames == null || originalTableNames.Count == 0)
//             {
//                 _log.LogInformation("[XMLA] RemoveOriginalTables: no original table names supplied — nothing to remove");
//                 return;
//             }

//             var toRemoveNames = new HashSet<string>(originalTableNames, StringComparer.OrdinalIgnoreCase);
//             var toRemove = db.Model.Tables.Cast<Table>()
//                 .Where(t => toRemoveNames.Contains(t.Name))
//                 .ToList();

//             if (toRemove.Count == 0)
//             {
//                 _log.LogInformation("[XMLA] RemoveOriginalTables: none of the {n} original table name(s) were found in the current model", originalTableNames.Count);
//                 return;
//             }

//             // Strip variations that point at a table being removed, same
//             // reasoning as the auto-date-table cleanup — a dangling variation
//             // target throws at SaveChanges().
//             var removeNameSet = new HashSet<string>(toRemove.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
//             foreach (Table table in db.Model.Tables)
//             {
//                 foreach (Column col in table.Columns)
//                 {
//                     if (col is not DataColumn dc) continue;
//                     foreach (var v in dc.Variations.ToList())
//                     {
//                         var target = GetVariationTargetTableName(v);
//                         if (target != null && removeNameSet.Contains(target))
//                             dc.Variations.Remove(v);
//                     }
//                 }
//             }

//             foreach (var t in toRemove)
//                 db.Model.Tables.Remove(t);

//             // Drop any relationship that referenced a now-removed table.
//             PurgeDanglingRelationships(db);

//             // The tables just removed were the ONLY thing referencing the
//             // original PBIX's shared M expressions / legacy data sources
//             // (its Snowflake, on-prem, or local-file connection). Those
//             // objects don't disappear on their own — TOM's SaveChanges()
//             // still validates "Use" permission on every DataSource/shared
//             // Expression left in the model, even ones nothing references
//             // any more. Since this identity was never granted access to
//             // the *original* PBIX's connection (only to the new Lakehouse
//             // one being migrated to), leaving that orphaned object behind
//             // is exactly what turns a same-tenant structural save — one
//             // that isn't touching that connection's data at all — into
//             // the "insufficient permissions ... Use permissions on all
//             // data connections" error. Purge them here, in the same pass
//             // that drops the tables that used them, so SAVE 1/2 below
//             // never has anything left in the model this identity can't
//             // reach.
//             PurgeOrphanedDataSourcesAndExpressions(db);

//             // The tables just removed above were very likely the only thing
//             // still pointing a Variation at Power BI's auto-generated
//             // LocalDateTable_*/DateTableTemplate_* tables. Once that last
//             // variation is gone, AS rejects the next SaveChanges() with
//             // "Table 'LocalDateTable_...' with ShowAsVariationsOnly property
//             // set to '1' must be a target of a variation when variation
//             // notation is enabled" — because an orphaned auto-date table is
//             // an invalid model state, not just an unused one. Clear those out
//             // in the same pass so whatever save runs right after this call
//             // (e.g. the placeholder-only structural save) never hits it.
//             RemoveAutoDateTables(db);

//             _log.LogInformation("[XMLA] Removed {n} original table(s) now that visuals have been rebound: {list}",
//                 toRemove.Count, string.Join(", ", toRemove.Select(t => t.Name)));
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // Removes shared M expressions / legacy data sources that nothing in
//         // the model references any more. See the call site in
//         // RemoveOriginalTables for why this matters: TOM validates "Use"
//         // permission on every DataSource/Expression object still attached to
//         // the model on every SaveChanges(), whether or not any table still
//         // uses it. Left behind after the tables that used them are gone, an
//         // orphaned object from the *original* PBIX's connection (Snowflake,
//         // on-prem gateway, local file, …) — one this migration identity was
//         // never granted access to — is enough on its own to fail a purely
//         // structural save.
//         //
//         // Legacy DataSources (compat level < 1400) are matched by direct
//         // reference from a QueryPartitionSource partition. Shared Expressions
//         // (compat level 1400+, the normal case for Fabric/Power BI models)
//         // are matched conservatively by name: an expression is kept if its
//         // name appears as a whole-word identifier anywhere in another
//         // remaining partition's or expression's M text — the way a "Source =
//         // SharedQueryName" reference actually looks — and removed only when
//         // nothing else mentions it. Runs to a fixed point so a chain of
//         // expressions that only reference each other (A uses B, B is now
//         // unused once A is gone) is fully cleaned up in one call.
//         // ─────────────────────────────────────────────────────────────────────
//         private void PurgeOrphanedDataSourcesAndExpressions(Database db)
//         {
//             var referencedDataSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
//             foreach (Table table in db.Model.Tables)
//                 foreach (Partition p in table.Partitions)
//                     if (p.Source is QueryPartitionSource pps && pps.DataSource != null)
//                         referencedDataSourceNames.Add(pps.DataSource.Name);

//             foreach (var ds in db.Model.DataSources.ToList())
//             {
//                 if (!referencedDataSourceNames.Contains(ds.Name))
//                 {
//                     db.Model.DataSources.Remove(ds);
//                     _log.LogInformation(
//                         "[XMLA] Removed orphaned legacy data source '{d}' — no remaining table partition references it",
//                         ds.Name);
//                 }
//             }

//             bool changed = true;
//             while (changed)
//             {
//                 changed = false;

//                 foreach (var expr in db.Model.Expressions.ToList())
//                 {

//                     var escapedName = Regex.Escape(expr.Name);
//                     var pattern = $@"(#""{escapedName}""|\b{escapedName}\b)";

//                     var isReferenced =
//                         db.Model.Tables.SelectMany(t => t.Partitions)
//                             .Select(p => (p.Source as MPartitionSource)?.Expression)
//                             .Where(text => !string.IsNullOrEmpty(text))
//                             .Any(text => Regex.IsMatch(text!, $@"\b{Regex.Escape(expr.Name)}\b"))
//                         ||
//                         db.Model.Expressions
//                             .Where(e => e.Name != expr.Name)
//                             .Any(e => !string.IsNullOrEmpty(e.Expression) &&
//                                       Regex.IsMatch(e.Expression, $@"\b{Regex.Escape(expr.Name)}\b"));

//                     if (!isReferenced)
//                     {
//                         db.Model.Expressions.Remove(expr);
//                         _log.LogInformation(
//                             "[XMLA] Removed orphaned shared expression '{e}' — no remaining table or expression references it",
//                             expr.Name);
//                         changed = true;
//                     }
//                 }
//             }
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // PLACEHOLDER TABLE — keeps a save from ever committing a model with
//         // ZERO tables. A Tabular model saved empty mid-migration is exactly
//         // what produces "Couldn't load the model schema" if anything touches
//         // the dataset in that window.
//         //
//         // Unified with the Gold-measures table: rather than a throwaway
//         // table that exists only until real tables show up, step 1 creates
//         // "_Measures" (see GoldMeasuresTableName below) itself, with its
//         // temporary dummy column, and uses THAT as the never-zero-tables
//         // placeholder. If Gold KPI measures end up landing on it later, it
//         // simply stays in the model as the real, permanent "_Measures"
//         // table instead of being thrown away and rebuilt. If no Gold views
//         // were selected, it's removed at the end exactly like the old
//         // disposable placeholder was. The pattern:
//         //   1. AddPlaceholderTable()          — creates "_Measures" with a
//         //                                        temporary dummy column
//         //   2. RemoveOriginalTables()         — drop everything else
//         //   3. RequestPlaceholderRefresh()    — flag ONLY "_Measures" for
//         //                                        refresh (self-contained M, no
//         //                                        external connection, so this
//         //                                        can never fail on credentials)
//         //   4. SaveStructureOnly()            — model has exactly 1 table,
//         //                                        never 0, and it's now actual
//         //                                        committed data, not just schema
//         //   5. UpdateLakehouseTablesInPlace() — add the real (non-Gold) Lakehouse
//         //                                        tables; AddGoldMeasuresTable()
//         //                                        converts Gold views into
//         //                                        measures on "_Measures" and
//         //                                        drops its temporary dummy column
//         //   6. RemovePlaceholderTable()       — drop "_Measures" ONLY if it
//         //                                        never picked up any real
//         //                                        measures (no Gold views selected)
//         //   7. SaveStructureOnly()            — model has only the real tables
//         // ─────────────────────────────────────────────────────────────────────
//         private const string PlaceholderTableName = GoldMeasuresTableName;

//         public void AddPlaceholderTable(Database db)
//         {
//             if (db.Model.Tables.Find(PlaceholderTableName) != null)
//             {
//                 _log.LogInformation("[XMLA] AddPlaceholderTable: '{t}' already present — nothing to do", PlaceholderTableName);
//                 return;
//             }

//             var table = new Table { Name = PlaceholderTableName };

//             // Named __DummyColumn__ (not "DummyColumn") and left VISIBLE
//             // (IsHidden = false) on purpose. SaveStructureOnly() and
//             // RequestFullRefreshAndSave() both scan every table for one with
//             // no visible, non-hidden data column and inject their own
//             // "__DummyColumn__" safety-net column if they find one. If this
//             // column were hidden, that scan would treat the placeholder as
//             // columnless, inject a SECOND column also named __DummyColumn__
//             // whose SourceColumn doesn't exist in the M rowset below, and AS
//             // would reject the save with "The '__DummyColumn__' column does
//             // not exist in the rowset." Making this column visible AND
//             // giving it that exact name means (a) the safety-net scan skips
//             // this table entirely — hasDataColumn is true — and (b) even if
//             // some other path added a same-named column, Table.Columns is
//             // keyed by name so it would just no-op instead of colliding.
//             var column = new DataColumn
//             {
//                 Name         = "__DummyColumn__",
//                 SourceColumn = "__DummyColumn__",
//                 DataType     = DataType.String,
//                 IsHidden     = false
//             };
//             table.Columns.Add(column);

//             var partition = new Partition
//             {
//                 Name = PlaceholderTableName,
//                 Source = new MPartitionSource
//                 {
//                     // Self-contained, TYPED M — one literal row, no external
//                     // connection. Typing the #table() call (rather than the
//                     // untyped #table({"col"}, {{"val"}}) form) makes the
//                     // rowset's column name and type explicit up front, so AS
//                     // has no ambiguity to resolve when it validates this
//                     // partition's output against the DataColumn defined
//                     // above — the column name here must match "__DummyColumn__"
//                     // exactly.
//                     Expression =
//                         "let\n" +
//                         "    Source = #table(\n" +
//                         "        type table [__DummyColumn__ = Text.Type],\n" +
//                         "        {{\"placeholder\"}}\n" +
//                         "    )\n" +
//                         "in\n" +
//                         "    Source"
//                 }
//             };
//             table.Partitions.Add(partition);

//             db.Model.Tables.Add(table);
//             _log.LogInformation("[XMLA] Added placeholder table '{t}' — model will never be saved with zero tables from here on", PlaceholderTableName);
//         }

//         // Flags ONLY the placeholder table for a full refresh, so the next
//         // SaveChanges() actually processes its one literal row instead of
//         // leaving it schema-only/unprocessed. Deliberately scoped to just
//         // this table — every other table is left untouched here, since
//         // requesting a refresh on anything still bound to a real data
//         // connection is exactly what SaveStructureOnly is designed to avoid
//         // (that's Transaction 2/3's job, after credentials are confirmed).
//         // No-op if the placeholder isn't present.
//         public void RequestPlaceholderRefresh(Database db)
//         {
//             var t = db.Model.Tables.Find(PlaceholderTableName);
//             if (t == null)
//             {
//                 _log.LogInformation("[XMLA] RequestPlaceholderRefresh: '{t}' not present — nothing to refresh", PlaceholderTableName);
//                 return;
//             }
//             t.RequestRefresh(RefreshType.Full);
//             _log.LogInformation("[XMLA] Flagged placeholder table '{t}' for refresh — its one literal row will be committed on the next save", PlaceholderTableName);
//         }

//         public void RemovePlaceholderTable(Database db)
//         {
//             var t = db.Model.Tables.Find(PlaceholderTableName);
//             if (t == null)
//             {
//                 _log.LogInformation("[XMLA] RemovePlaceholderTable: '{t}' not present — nothing to remove", PlaceholderTableName);
//                 return;
//             }

//             // "_Measures" is now shared between the placeholder role and the
//             // real Gold-measures table (see AddGoldMeasuresTable). If any
//             // Gold views were selected, real measures will already be sitting
//             // on this table — keep it in the model permanently instead of
//             // deleting the very content this migration was asked to produce.
//             // Only remove it when it's still exactly what step 1 left behind:
//             // no measures, nothing else using it.
//             if (t.Measures.Count > 0)
//             {
//                 _log.LogInformation(
//                     "[XMLA] RemovePlaceholderTable: '{t}' holds {n} real Gold measure(s) — keeping it as a permanent table instead of removing it",
//                     PlaceholderTableName, t.Measures.Count);
//                 return;
//             }

//             db.Model.Tables.Remove(t);
//             PurgeDanglingRelationships(db);
//             _log.LogInformation("[XMLA] Removed placeholder table '{t}' — no Gold measures were ever added to it", PlaceholderTableName);
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // GOLD KPI views → one shared "_Measures" table.
//         //
//         // Every Gold-schema view is a single-row business KPI (see
//         // MigrationController's Gold/non-Gold split before this is called —
//         // only Gold views ever reach here). Rather than one physical
//         // Lakehouse table per KPI, every (column name, row-1 value) pair
//         // across all Gold views becomes one constant DAX measure —
//         // MeasureName = ConstantValue — on a single semantic-model-only
//         // table called "_Measures". This table is NEVER backed by a
//         // Lakehouse/Delta table or a live partition of real data; its one
//         // partition is a self-contained single-row M literal, exactly like
//         // AddPlaceholderTable's, used only to keep the table object valid
//         // while its temporary column is in place.
//         //
//         // Steps, matching the "dummy column → measures → delete dummy
//         // column" request this was built for:
//         //   1. Create "_Measures" (if not already present) with one
//         //      temporary, visible dummy column — a table needs at least one
//         //      column to be a valid object before anything else can be
//         //      added to it.
//         //   2. Add one Measure per Gold (column, value) pair.
//         //   3. Remove the temporary dummy column, leaving a table with only
//         //      measures.
//         //
//         // Note step 3 doesn't produce a literal zero-column table: Analysis
//         // Services requires every table to have at least one column to be
//         // processable, so SaveStructureOnly's existing safety net (see
//         // that method) will silently re-add its own HIDDEN "__DummyColumn__"
//         // the next time the model is saved, bound to this same table's
//         // partition (which already outputs a "__DummyColumn__" field, so it
//         // binds cleanly). That column is hidden from the field list, so from
//         // a report author's perspective this table is exactly what was
//         // asked for — measures only, nothing to browse or aggregate.
//         private const string GoldMeasuresTableName = "_Measures";

//                 public class OriginalMeasureSnapshot
//         {
//             public string TableName { get; set; } = "";
//             public string MeasureName { get; set; } = "";
//             public string Expression { get; set; } = "";
//         }

//         /// <summary>
//         /// Call BEFORE wiping/replacing tables — captures live DAX from the published model.
//         /// </summary>
//         public List<OriginalMeasureSnapshot> CaptureOriginalMeasures(Database db)
//         {
//             var list = new List<OriginalMeasureSnapshot>();
//             foreach (var table in db.Model.Tables.Cast<Table>())
//             {
//                 if (IsAutoDateTable(table.Name)) continue;
//                 foreach (var m in table.Measures.Cast<Measure>())
//                 {
//                     if (string.IsNullOrWhiteSpace(m.Expression)) continue;
//                     list.Add(new OriginalMeasureSnapshot
//                     {
//                         TableName = table.Name,
//                         MeasureName = m.Name,
//                         Expression = m.Expression
//                     });
//                     _log.LogInformation("[XMLA] Captured original measure '{t}'[{m}]", table.Name, m.Name);
//                 }
//             }
//             _log.LogInformation("[XMLA] CaptureOriginalMeasures: {n} measure(s)", list.Count);
//             return list;
//         }

//         private static string NormName(string s) =>
//             (s ?? "").Trim().ToLowerInvariant().Replace("_", "").Replace(" ", "").Replace("-", "");

//         // private static bool IsFilterDependentMeasureName(string name)
//         // {
//         //     var n = NormName(name);
//         //     return n.Contains("total") || n.Contains("sum") || n.Contains("count")
//         //         || n.Contains("order") || n.Contains("sales") || n.Contains("profit")
//         //         || n.Contains("quantity") || n.Contains("qty") || n.Contains("cost")
//         //         || n.Contains("amount") || n.Contains("revenue");
//         // }

//                 private static bool IsFilterDependentMeasureName(string name)
//         {
//             var n = NormName(name);

//             // Margin / % / ratio can use Gold constant if we have no better DAX
//             if (n.Contains("margin") || n.Contains("pct") || n.Contains("percent") || n.Contains("ratio"))
//                 return false;

//             return n.Contains("total") || n.Contains("sum") || n.Contains("count")
//                 || n.Contains("order") || n.Contains("sales") || n.Contains("profit")
//                 || n.Contains("quantity") || n.Contains("qty") || n.Contains("cost")
//                 || n.Contains("amount") || n.Contains("revenue");
//         }

//         /// <summary>
//         /// Remap 'Table'[Col] references to names that exist on the rebuilt model.
//         /// Returns null if any dependency is missing.
//         /// </summary>
//         private string? TryRemapDaxToCurrentModel(Database db, string expression)
//         {
//             if (string.IsNullOrWhiteSpace(expression)) return null;

//             var tables = db.Model.Tables.Cast<Table>()
//                 .Where(t => !IsAutoDateTable(t.Name))
//                 .ToList();

//             var tableByNorm = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase);
//             foreach (var t in tables)
//             {
//                 var key = NormName(t.Name);
//                 if (!tableByNorm.ContainsKey(key))
//                     tableByNorm[key] = t;
//             }

//             var pattern = new Regex(@"('([^']+)'|([A-Za-z_][\w ]*))\[([^\]]+)\]", RegexOptions.Compiled);
//             var missing = new List<string>();

//             var result = pattern.Replace(expression, match =>
//             {
//                 var rawTable = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
//                 var col = match.Groups[4].Value.Trim();
//                 var normT = NormName(rawTable);

//                 Table? table = null;
//                 if (tableByNorm.TryGetValue(normT, out var exact))
//                     table = exact;
//                 else
//                     table = tables.FirstOrDefault(t =>
//                         NormName(t.Name) == normT
//                         || NormName(t.Name).Contains(normT)
//                         || normT.Contains(NormName(t.Name)));

//                 if (table == null)
//                 {
//                     missing.Add($"table:{rawTable}");
//                     return match.Value;
//                 }

//                 var column = table.Columns.Cast<Column>().FirstOrDefault(c =>
//                     c.Type != ColumnType.RowNumber
//                     && (string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase)
//                         || NormName(c.Name) == NormName(col)));

//                 if (column == null)
//                 {
//                     missing.Add($"{table.Name}[{col}]");
//                     return match.Value;
//                 }

//                 return $"'{table.Name}'[{column.Name}]";
//             });

//             if (missing.Count > 0)
//             {
//                 _log.LogWarning("[XMLA] DAX remap incomplete — missing: {deps}",
//                     string.Join(", ", missing.Distinct()));
//                 return null;
//             }

//             return result;
//         }

//         private string? TryBuildCountMeasureDax(Database db, string measureName)
//         {
//             var n = NormName(measureName);
//             if (!(n.Contains("order") || n.Contains("count") || n.Contains("rows")))
//                 return null;

//             var fact = db.Model.Tables.Cast<Table>()
//                 .Where(t => !IsAutoDateTable(t.Name)
//                     && !t.Name.Equals(GoldMeasuresTableName, StringComparison.OrdinalIgnoreCase))
//                 .FirstOrDefault(t => NormName(t.Name).Contains("fact"));

//             return fact == null ? null : $"COUNTROWS('{fact.Name}')";
//         }

//         public int ConfigureDateMonthSort(Database db)
//         {
//             int configured = 0;
//             foreach (var table in db.Model.Tables.Cast<Table>()
//                 .Where(t => NormName(t.Name).Contains("date")))
//             {
//                 var monthCol = table.Columns.Cast<Column>()
//                     .FirstOrDefault(c => NormName(c.Name) == "month");
//                 if (monthCol == null) continue;

//                 var sortCol = table.Columns.Cast<Column>().FirstOrDefault(c =>
//                 {
//                     var n = NormName(c.Name);
//                     return n is "monthnumber" or "monthnum" or "monthno" or "monthkey"
//                         || n == "datekey" || n == "date";
//                 });
//                 if (sortCol == null) continue;

//                 try
//                 {
//                     monthCol.SortByColumn = sortCol;
//                     configured++;
//                     _log.LogInformation("[XMLA] Month sort: '{t}'[{m}] by [{s}]",
//                         table.Name, monthCol.Name, sortCol.Name);
//                 }
//                 catch (Exception ex)
//                 {
//                     _log.LogWarning(ex, "[XMLA] Month sort failed on '{t}'", table.Name);
//                 }
//             }
//             return configured;
//         }

//       public List<UnmappedTable> AddGoldMeasuresTable(
//     Database db,
//     List<GoldMeasureDef> goldMeasures,
//     List<OriginalMeasureSnapshot>? originalMeasures = null)
//         {
//             var unmapped = new List<UnmappedTable>();
//             if (goldMeasures.Count == 0)
//             {
//                 _log.LogInformation("[XMLA] AddGoldMeasuresTable: no Gold measures to add — leaving '{t}' untouched", GoldMeasuresTableName);
//                 return unmapped;
//             }

//             var table = db.Model.Tables.Find(GoldMeasuresTableName);
//             bool isNewTable = table == null;
//             if (isNewTable)
//             {
//                 table = new Table { Name = GoldMeasuresTableName };

//                 // Same self-contained, typed single-row M pattern as
//                 // AddPlaceholderTable — see that method's comment for why
//                 // the column must be named exactly "__DummyColumn__" and
//                 // left visible for now (it's removed at the end of this
//                 // method; the visibility only matters while it's the sole
//                 // column keeping this table valid).
//                 var dummyColumn = new DataColumn
//                 {
//                     Name         = "__DummyColumn__",
//                     SourceColumn = "__DummyColumn__",
//                     DataType     = DataType.String,
//                     IsHidden     = false
//                 };
//                 table.Columns.Add(dummyColumn);

//                 var partition = new Partition
//                 {
//                     Name = GoldMeasuresTableName,
//                     Source = new MPartitionSource
//                     {
//                         Expression =
//                             "let\n" +
//                             "    Source = #table(\n" +
//                             "        type table [__DummyColumn__ = Text.Type],\n" +
//                             "        {{\"placeholder\"}}\n" +
//                             "    )\n" +
//                             "in\n" +
//                             "    Source"
//                     }
//                 };
//                 table.Partitions.Add(partition);

//                 db.Model.Tables.Add(table);
//                 _log.LogInformation("[XMLA] Created '{t}' with temporary dummy column — will hold {n} Gold KPI measure(s)",
//                     GoldMeasuresTableName, goldMeasures.Count);
//             }

//             var existingMeasureNames = new HashSet<string>(
//                 table!.Measures.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

//             foreach (var gm in goldMeasures)
//             {
//                 var measureName = gm.MeasureName;
//                 if (existingMeasureNames.Contains(measureName))
//                 {
//                     // Two Gold views defining the same column name (e.g. two
//                     // KPI views both returning a "TOTAL" column) — keep both
//                     // by disambiguating with the source view, rather than
//                     // silently overwriting one.
//                     var disambiguated = $"{measureName} ({gm.SourceView})";
//                     _log.LogWarning(
//                         "[XMLA] Measure name '{m}' already exists on '{t}' — '{s}.{v}' added as '{d}' instead",
//                         measureName, GoldMeasuresTableName, gm.SourceSchema, gm.SourceView, disambiguated);
//                     measureName = disambiguated;
//                 }

//                 // string daxExpression;
//                 // try
//                 // {
//                 //     var sumDax = TryBuildSumMeasureDax(db, measureName);
//                 //     if (!string.IsNullOrEmpty(sumDax))
//                 //     {
//                 //         daxExpression = sumDax;
//                 //         _log.LogInformation(
//                 //             "[XMLA] Measure '{m}' → filterable DAX {expr} (matched fact column; not Gold constant)",
//                 //             measureName, daxExpression);
//                 //             }
//                 //             else
//                 //             {
//                 //                 daxExpression = FormatGoldMeasureDaxLiteral(gm.RawValue, gm.SnowflakeDataType);
//                 //                 _log.LogInformation(
//                 //                     "[XMLA] Measure '{m}' → Gold constant DAX (no fact column match): {expr}",
//                 //                     measureName, daxExpression);
//                 //                      }
//                 //                      }
//                 //                      catch (Exception ex)
//                 //                      {
//                 //                         _log.LogWarning(ex, "[XMLA] Could not format value for measure '{m}' from '{s}.{v}' — skipping.",
//                 //                         measureName, gm.SourceSchema, gm.SourceView);
//                 //                         unmapped.Add(new UnmappedTable
//                 //                         {
//                 //                             TableName = $"{gm.SourceSchema}.{gm.SourceView}",
//                 //                             Reason    = $"Column '{gm.MeasureName}' value could not be turned into a DAX literal: {ex.Message}"
//                 //                             });
//                 //                             continue;
//                 //                         }

//                                 string? daxExpression = null;
//                 string strategy = "none";

//                 try
//                 {
//                     var orig = originalMeasures?.FirstOrDefault(o =>
//                         string.Equals(o.MeasureName, measureName, StringComparison.OrdinalIgnoreCase)
//                         || NormName(o.MeasureName) == NormName(measureName));

//                     if (orig != null)
//                     {
//                         var remapped = TryRemapDaxToCurrentModel(db, orig.Expression);
//                         if (!string.IsNullOrEmpty(remapped))
//                         {
//                             daxExpression = remapped;
//                             strategy = "preserved-original-dax";
//                             _log.LogInformation(
//                                 "[XMLA] Measure '{m}' → PRESERVED original DAX: {expr}",
//                                 measureName, daxExpression);
//                         }
//                     }

//                     if (daxExpression == null)
//                     {
//                         var sumDax = TryBuildSumMeasureDax(db, measureName);
//                         if (!string.IsNullOrEmpty(sumDax))
//                         {
//                             daxExpression = sumDax;
//                             strategy = "pattern-sum";
//                             _log.LogInformation("[XMLA] Measure '{m}' → pattern SUM: {expr}",
//                                 measureName, daxExpression);
//                         }
//                     }

//                     if (daxExpression == null)
//                     {
//                         var countDax = TryBuildCountMeasureDax(db, measureName);
//                         if (!string.IsNullOrEmpty(countDax))
//                         {
//                             daxExpression = countDax;
//                             strategy = "pattern-count";
//                             _log.LogInformation("[XMLA] Measure '{m}' → pattern COUNT: {expr}",
//                                 measureName, daxExpression);
//                         }
//                     }

//                     // if (daxExpression == null)
//                     // {
//                     //     if (IsFilterDependentMeasureName(measureName))
//                     //     {
//                     //         _log.LogWarning(
//                     //             "[XMLA] Measure '{m}' NOT migrated (no safe DAX). Will not use Gold constant.",
//                     //             measureName);
//                     //         unmapped.Add(new UnmappedTable
//                     //         {
//                     //             TableName = $"{gm.SourceSchema}.{gm.SourceView}",
//                     //             Reason = $"Measure '{measureName}' needs filterable DAX but could not be reconstructed."
//                     //         });
//                     //         continue;
//                     //     }

//                    if (daxExpression == null)
//                     {
//                         var sumDax = TryBuildSumMeasureDax(db, measureName);
//                         if (!string.IsNullOrEmpty(sumDax))
//                         {
//                             daxExpression = sumDax;
//                             strategy = "pattern-sum";
//                             _log.LogInformation(
//                                 "[XMLA] Measure '{m}' → pattern SUM: {expr}",
//                                 measureName, daxExpression);
//                         }
//                         else
//                         {
//                             daxExpression = FormatGoldMeasureDaxLiteral(gm.RawValue, gm.SnowflakeDataType);
//                             strategy = "gold-constant";
//                             _log.LogInformation(
//                                 "[XMLA] Measure '{m}' → Gold constant fallback: {expr}",
//                                 measureName, daxExpression);
//                         }
//                     }
//                 }
//                 catch (Exception ex)
//                 {
//                     _log.LogWarning(ex, "[XMLA] Measure '{m}' from '{s}.{v}' failed — skipping.",
//                         measureName, gm.SourceSchema, gm.SourceView);
//                     unmapped.Add(new UnmappedTable
//                     {
//                         TableName = $"{gm.SourceSchema}.{gm.SourceView}",
//                         Reason = $"Measure '{measureName}': {ex.Message}"
//                     });
//                     continue;
//                 }

//                 _log.LogInformation("[XMLA] Measure '{m}' strategy={strategy}", measureName, strategy);


//                 table.Measures.Add(new Measure
//                 {
//                     Name       = measureName,
//                     Expression = daxExpression!,
//                     IsHidden   = false
//                 });
//                 existingMeasureNames.Add(measureName);

//                 _log.LogInformation("[XMLA] Added measure '{m}' = {expr} (from '{s}.{v}')",
//                     measureName, daxExpression, gm.SourceSchema, gm.SourceView);
//             }

//             // Step 3: drop the temporary dummy column now that the table has
//             // real measures to stand on. "_Measures" is created up front by
//             // AddPlaceholderTable (step 1 of the migration), so by the time
//             // this runs the table already exists — isNewTable is normally
//             // false — and its visible "__DummyColumn__" from step 1 is still
//             // sitting there. Once at least one real measure has been added
//             // above, that column is no longer needed to keep the table
//             // valid, so remove it unconditionally here rather than gating on
//             // isNewTable.
//             var existingDummy = table.Columns.Find("__DummyColumn__");
//             if (existingDummy != null)
//             {
//                 table.Columns.Remove(existingDummy);
//                 _log.LogInformation(
//                     "[XMLA] Removed temporary dummy column from '{t}' — table now holds only measures " +
//                     "(a hidden technical column will be silently re-added on save if AS requires one; " +
//                     "it will not appear in the field list).", GoldMeasuresTableName);
//             }

//             return unmapped;
//         }

//         // Formats one Gold KPI's raw (string) Snowflake value as a DAX
//         // literal for a constant measure expression. Numeric Snowflake
//         // types become a bare numeric literal (so PROFIT_MARGIN_PCT =
//         // 0.3505148518, not a quoted string that DAX would have to
//         // implicitly convert); everything else — text, dates, booleans,
//         // variants — becomes an escaped quoted DAX string literal, which is
//         // always valid regardless of the underlying type.
//         private static readonly HashSet<string> NumericSnowflakeTypes = new(StringComparer.OrdinalIgnoreCase)
//         {
//             "NUMBER", "DECIMAL", "NUMERIC", "INT", "INTEGER", "BIGINT", "SMALLINT",
//             "TINYINT", "BYTEINT", "FLOAT", "FLOAT4", "FLOAT8", "DOUBLE", "DOUBLE PRECISION", "REAL"
//         };

//         internal static string FormatGoldMeasureDaxLiteral(string rawValue, string snowflakeDataType)
//         {
//             if (rawValue == null)
//                 return "BLANK()";

//             if (NumericSnowflakeTypes.Contains(snowflakeDataType) &&
//                 double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
//                     System.Globalization.CultureInfo.InvariantCulture, out var num))
//             {
//                 return num.ToString(System.Globalization.CultureInfo.InvariantCulture);
//             }

//             // Quoted DAX string literal — escape embedded double-quotes by
//             // doubling them, DAX's own escaping convention.
//             var escaped = rawValue.Replace("\"", "\"\"");
//             return $"\"{escaped}\"";
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // REPLACE the entire semantic model with Snowflake views as DirectQuery.
//         // ─────────────────────────────────────────────────────────────────────
//         public List<UnmappedTable> ReplaceWithSnowflakeViews(
//             Database db,
//             SnowflakeConfig sfCfg,
//             List<SnowflakeViewMeta> sfViews,
//             List<VisualBinding> originalBindings)
//         {
//             var unmapped    = new List<UnmappedTable>();
//             var loadedViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

//             // A view name that exists in MORE THAN ONE selected schema (very
//             // common in medallion setups where bronze/silver/gold all have a
//             // table called e.g. "CUSTOMERS") is resolved using a fixed layer
//             // priority — Gold wins over Silver, Silver wins over Bronze — so
//             // matching is deterministic instead of picking whichever schema
//             // happened to sort first alphabetically. If none of the
//             // conflicting schemas match a known layer name, it's still
//             // treated as a genuine, unresolvable ambiguity and reported.
//             var layerPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
//             {
//                 ["GOLD"]   = 0,
//                 ["SILVER"] = 1,
//                 ["BRONZE"] = 2
//             };

//             var viewGroups = sfViews
//                 .GroupBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase)
//                 .ToList();

//             var viewLookup = new Dictionary<string, SnowflakeViewMeta>(StringComparer.OrdinalIgnoreCase);

//             foreach (var g in viewGroups)
//             {
//                 var distinctSchemas = g.Select(v => v.Schema)
//                     .Distinct(StringComparer.OrdinalIgnoreCase)
//                     .ToList();

//                 if (distinctSchemas.Count <= 1)
//                 {
//                     viewLookup[g.Key] = g.First();
//                     continue;
//                 }

//                 var ranked = g
//                     .Where(v => layerPriority.ContainsKey(v.Schema))
//                     .OrderBy(v => layerPriority[v.Schema])
//                     .ToList();

//                 if (ranked.Count > 0)
//                 {
//                     var winner = ranked.First();
//                     var where  = string.Join(", ", g.Select(v => $"{v.Schema}.{v.ViewName}"));
//                     _log.LogInformation(
//                         "[XMLA] View name '{v}' exists in multiple selected schemas ({where}) — " +
//                         "resolved to '{winner}' by layer priority (Gold > Silver > Bronze)",
//                         g.Key, where, winner.Schema);
//                     viewLookup[g.Key] = winner;
//                     continue;
//                 }

//                 // None of the conflicting schemas are a recognized layer name
//                 // (e.g. two custom/staging schemas both have "CUSTOMERS") —
//                 // there's no safe automatic choice, so skip it and report it
//                 // rather than guessing.
//                 var whereUnresolved = string.Join(", ", g.Select(v => $"{v.Schema}.{v.ViewName}"));
//                 _log.LogWarning(
//                     "[XMLA] View name '{v}' exists in multiple selected schemas ({where}) with no " +
//                     "recognized Gold/Silver/Bronze layer among them — skipping automatic match",
//                     g.Key, whereUnresolved);
//                 unmapped.Add(new UnmappedTable
//                 {
//                     TableName = g.Key,
//                     Reason    = $"'{g.Key}' exists in more than one selected schema ({whereUnresolved}), " +
//                                 "and none of them is a recognized Gold/Silver/Bronze layer name, so it " +
//                                 "couldn't be resolved automatically. Select only the schema you want, or " +
//                                 "rename the views so they're unique, then retry."
//                 });
//             }

//             // ── Update or remove existing tables ──────────────────────────────
//             var tablesToRemove = new List<Table>();
//             foreach (Table table in db.Model.Tables)
//             {
//                 // Never remove, rebuild, or otherwise touch Power BI's own
//                 // auto-generated date tables — they don't come from the PBIX
//                 // author and have no Snowflake equivalent by design. Treating
//                 // them as "unmapped" and removing them (or stripping the
//                 // variation that targets them, see CleanTableMetadata) is
//                 // exactly what produces:
//                 //   "Table 'LocalDateTable_...' with ShowAsVariationsOnly
//                 //    property set to '1' must be a target of a variation"
//                 if (IsAutoDateTable(table.Name))
//                 {
//                     _log.LogInformation("[XMLA] Skipping auto date table '{t}' — left untouched", table.Name);
//                     continue;
//                 }

//                if (!viewLookup.TryGetValue(table.Name, out var sfView))
// {
//     // A table with no Snowflake counterpart but real measures on it
//     // (e.g. "_Measures") is a semantic-only table, not unmapped data —
//     // it was never supposed to have a matching Snowflake view. Deleting
//     // it silently destroys every DAX measure defined on it. Preserve it
//     // untouched instead.
//     if (table.Measures.Count > 0)
//     {
//         _log.LogInformation(
//             "[XMLA] Table '{t}' has no matching Snowflake view but holds {n} measure(s) — " +
//             "preserving as a measures-only table, not removing", table.Name, table.Measures.Count);
//         continue;
//     }

//     _log.LogWarning("[XMLA] Table '{t}' has no matching Snowflake view — marking for removal", table.Name);
//     tablesToRemove.Add(table);
//     unmapped.Add(new UnmappedTable
//     {
//         TableName = table.Name,
//         Reason    = "No Snowflake view with a matching name found — table removed. The per-visual column rebind step will remap visuals that referenced this table."
//     });
//     continue;
// }
//                 _log.LogInformation("[XMLA] Table '{t}' → matched Snowflake view '{v}' ({cols} column(s))",
//                     table.Name, sfView.ViewName, sfView.Columns.Count);

//                 var mExpr = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);
//                 foreach (Partition partition in table.Partitions)
//                     partition.Source = new MPartitionSource { Expression = mExpr };

//                 // foreach (var m in table.Measures.ToList())
//                 //     table.Measures.Remove(m);
//                 foreach (var c in table.Columns
//                     .Where(c => c.Type == ColumnType.Calculated || c.Type == ColumnType.CalculatedTableColumn)
//                     .ToList())
//                     table.Columns.Remove(c);

//                 CleanTableMetadata(table);

//                 var sfColLookup = sfView.Columns
//                     .ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
//                 var uncovered = new List<string>();
//                 foreach (Column col in table.Columns)
//                 {
//                     if (col.Type == ColumnType.RowNumber) continue;
//                     if (sfColLookup.TryGetValue(col.Name, out var sfCol) && col is DataColumn dc)
//                         dc.SourceColumn = sfCol.Name;
//                     else
//                         uncovered.Add(col.Name);
//                 }
//                 if (uncovered.Count > 0)
//                 {
//                     _log.LogWarning("[XMLA] Table '{t}' → {v}: {n} column(s) had no exact name match: {list}",
//                         table.Name, sfView.ViewName, uncovered.Count, string.Join(", ", uncovered));
//                     unmapped.Add(new UnmappedTable
//                     {
//                         TableName       = table.Name,
//                         Reason          = $"Mapped to {sfView.Schema}.{sfView.ViewName} but these columns had no exact name match. The per-visual column rebind step will resolve these via LLM.",
//                         UnmappedColumns = uncovered
//                     });
//                 }

//                 loadedViews.Add(sfView.ViewName);
//                 _log.LogInformation("Updated existing table '{t}' → {s}.{v}", table.Name, sfView.Schema, sfView.ViewName);
//             }

//             foreach (var t in tablesToRemove)
//                 db.Model.Tables.Remove(t);

//             PurgeDanglingRelationships(db);

//             // ── Add new tables for Snowflake views with no matching PBI table ──
//             foreach (var sfView in sfViews)
//             {
//                 if (loadedViews.Contains(sfView.ViewName)) continue;

//                 var newTable = new Table { Name = sfView.ViewName };
//                 var mExpr    = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);

//                 newTable.Partitions.Add(new Partition
//                 {
//                     Name   = sfView.ViewName,
//                     Source = new MPartitionSource { Expression = mExpr }
//                 });

//                 foreach (var sfCol in sfView.Columns)
//                 {
//                     newTable.Columns.Add(new DataColumn
//                     {
//                         Name         = sfCol.Name,
//                         SourceColumn = sfCol.Name,
//                         DataType     = MapDataType(sfCol.DataType),
//                         // Without this, TOM leaves SummarizeBy at its default,
//                         // which the AS engine resolves to Sum (numeric) or
//                         // Count (text) for every new column — showing up in
//                         // Power BI as an automatically-applied Sum/Count/etc.
//                         // aggregation on columns that were never configured
//                         // that way in the source PBIX.
//                         SummarizeBy  = AggregateFunction.None
//                     });
//                 }

//                 db.Model.Tables.Add(newTable);
//                 loadedViews.Add(sfView.ViewName);
//                 _log.LogInformation("Created new table '{v}' from {s}.{v2}", sfView.ViewName, sfView.Schema, sfView.ViewName);
//             }

//             // Strip out everything Power BI would otherwise do automatically
//             // on top of the raw data — implicit measures, default Sum/Count
//             // summarization, auto data categories, auto date/time tables —
//             // so the dataset reflects exactly what was pushed, nothing more.
//             DisableAutomaticModelBehaviors(db);

//             _log.LogInformation(
//                 "Dataset rebuilt: {loaded} Snowflake views, {unmapped} issues",
//                 loadedViews.Count, unmapped.Count);

//             return unmapped;
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // Turn off Power BI's "automatic model detection" behaviors so the
//         // dataset is exactly what was pushed — no implicit measures, no
//         // default Sum/Count aggregation, no auto-suggested data categories,
//         // no auto-generated Date/Time tables. Note: a few things on this list
//         // (relationship autodetection, Power Query type inference on load)
//         // are Power BI Desktop *file-open* behaviors that don't apply to an
//         // XMLA-based push at all — there's nothing to disable here for those,
//         // because this code path never triggers them in the first place.
//         // Column encoding (RLE/hash/etc.) is an internal Vertipaq storage
//         // decision made at processing time and isn't exposed as a model
//         // property, so it can't be turned off via TOM.
//         // ─────────────────────────────────────────────────────────────────────
//         private void DisableAutomaticModelBehaviors(Database db)
//         {
//             // Stops Power BI from creating an implicit SUM()/COUNT() measure
//             // the moment a column is dragged onto a visual — report authors
//             // must write an explicit DAX measure instead.
//             db.Model.DiscourageImplicitMeasures = true;

//             foreach (Table table in db.Model.Tables)
//             {
//                 if (IsAutoDateTable(table.Name)) continue;

//                 foreach (Column col in table.Columns)
//                 {
//                     if (col is not DataColumn dc) continue;

//                     // No automatic Sum/Count/Average aggregation icon.
//                     dc.SummarizeBy = AggregateFunction.None;
//                     // No auto-suggested Data Category (e.g. City/Country/Address).
//                     dc.DataCategory = null;
//                 }
//             }

//             RemoveAutoDateTables(db);
//         }

//         // Fully removes Power BI's auto-generated LocalDateTable_*/
//         // DateTableTemplate_* tables (instead of just leaving them alone),
//         // and sets the annotation Power BI Desktop checks before it
//         // regenerates them, so they don't come back on the next open/refresh.
//         private void RemoveAutoDateTables(Database db)
//         {
//             var autoDateTables = db.Model.Tables.Cast<Table>()
//                 .Where(t => IsAutoDateTable(t.Name))
//                 .ToList();

//             if (autoDateTables.Count > 0)
//             {
//                 var autoDateNames = new HashSet<string>(
//                     autoDateTables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

//                 // Strip variations pointing at an auto date table first —
//                 // deleting the table out from under a live variation throws
//                 // "... must be a target of a variation" at SaveChanges().
//                 foreach (Table table in db.Model.Tables)
//                 {
//                     if (IsAutoDateTable(table.Name)) continue;
//                     foreach (Column col in table.Columns)
//                     {
//                         if (col is not DataColumn dc) continue;
//                         foreach (var v in dc.Variations.ToList())
//                         {
//                             var target = GetVariationTargetTableName(v);
//                             if (target != null && autoDateNames.Contains(target))
//                                 dc.Variations.Remove(v);
//                         }
//                     }
//                 }

//                 foreach (var r in db.Model.Relationships.Cast<Relationship>().ToList())
//                 {
//                     if (r is SingleColumnRelationship scr &&
//                         ((scr.FromTable != null && autoDateNames.Contains(scr.FromTable.Name)) ||
//                          (scr.ToTable   != null && autoDateNames.Contains(scr.ToTable.Name))))
//                     {
//                         db.Model.Relationships.Remove(r);
//                     }
//                 }

//                 foreach (var t in autoDateTables)
//                     db.Model.Tables.Remove(t);

//                 _log.LogInformation("[XMLA] Removed {n} auto date/time table(s)", autoDateTables.Count);
//             }

//             // Tell Power BI Desktop not to regenerate auto date/time tables
//             // the next time this file/dataset is opened or refreshed.
//             var existing = db.Model.Annotations.Find("__PBI_TimeIntelligenceEnabled");
//             if (existing != null) db.Model.Annotations.Remove(existing);
//             db.Model.Annotations.Add(new Annotation
//             {
//                 Name  = "__PBI_TimeIntelligenceEnabled",
//                 Value = "0"
//             });
//         }
//         private void PurgeDanglingRelationships(Database db)
//         {
//             var tableNames = new HashSet<string>(
//                 db.Model.Tables.Cast<Table>().Select(t => t.Name),
//                 StringComparer.OrdinalIgnoreCase);

//             var toRemove = db.Model.Relationships
//                 .Cast<Relationship>()
//                 .Where(r =>
//                 {
//                     if (r is SingleColumnRelationship scr)
//                         return !tableNames.Contains(scr.FromTable?.Name ?? "") ||
//                                !tableNames.Contains(scr.ToTable?.Name ?? "");
//                     return false;
//                 })
//                 .ToList();

//             foreach (var r in toRemove)
//                 db.Model.Relationships.Remove(r);

//             if (toRemove.Count > 0)
//                 _log.LogInformation("Purged {n} dangling relationships", toRemove.Count);
//         }

//         private void CleanTableMetadata(Table table)
//         {
//             foreach (var h in table.Hierarchies.ToList())
//                 table.Hierarchies.Remove(h);

//             foreach (Column col in table.Columns)
//             {
//                 if (col is DataColumn dc)
//                 {
//                     foreach (var v in dc.Variations.ToList())
//                     {
//                         // A Variation whose target is an auto date table
//                         // (LocalDateTable_*/DateTableTemplate_*) MUST be left
//                         // in place. Those tables are never removed or
//                         // rebuilt (see IsAutoDateTable checks above), and
//                         // ShowAsVariationsOnly=1 on them is only valid while
//                         // some Variation still targets them. Stripping it
//                         // here — even though this method runs on the SOURCE
//                         // table, not the date table itself — is exactly what
//                         // caused: "Table 'LocalDateTable_...' with
//                         // ShowAsVariationsOnly property set to '1' must be a
//                         // target of a variation" at SaveChanges().
//                         var targetTableName = GetVariationTargetTableName(v);
//                         if (IsAutoDateTable(targetTableName))
//                         {
//                             _log.LogInformation(
//                                 "[XMLA] Preserving variation on '{t}.{c}' → auto date table '{dt}'",
//                                 table.Name, col.Name, targetTableName);
//                             continue;
//                         }

//                         dc.Variations.Remove(v);
//                     }
//                 }
//             }
//         }

//         // Variation exposes its target table either via the underlying
//         // Relationship (SingleColumnRelationship.ToTable) or via
//         // DefaultHierarchy.Table. Try both defensively — TOM's object model
//         // differs slightly across SDK versions.
//         private static string? GetVariationTargetTableName(Variation v)
//         {
//             try
//             {
//                 if (v.Relationship is SingleColumnRelationship scr && scr.ToTable != null)
//                     return scr.ToTable.Name;
//             }
//             catch { /* fall through to DefaultHierarchy */ }

//             try
//             {
//                 return v.DefaultHierarchy?.Table?.Name;
//             }
//             catch
//             {
//                 return null;
//             }
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // Roadblock 2 fix: Power BI's XMLA endpoint rejects any single
//         // transaction that both (a) adds/changes a data source and (b)
//         // refreshes it — "We are unable to retrieve the credentials for the
//         // data source... add new data sources in a separate transaction
//         // first." SaveChanges() below still requests a refresh in the same
//         // call it saves structure in, which is exactly that anti-pattern.
//         // These two methods are the deliberately-split replacement:
//         //
//         //   1. SaveStructureOnly(db)        — Transaction 1: metadata only.
//         //   2. (credential bind goes here)  — see PowerBIService.
//         //                                      BindLakehouseDatasourceCredentialsAsync
//         //   3. RequestFullRefreshAndSave(db)— Transaction 2: refresh + save.
//         //
//         // Call them in that order from the controller, with the credential
//         // step run in between. Kept alongside (not replacing) SaveChanges()
//         // so existing callers are unaffected.
//         // ─────────────────────────────────────────────────────────────────────

//         // ── Transaction 1: structure only, NO RequestRefresh anywhere ──────
//         public List<string> SaveStructureOnly(Database db)
//         {
//             var warnings = new List<string>();
//             const string DummyColName = "__DummyColumn__";

//             foreach (Table table in db.Model.Tables)
//             {
//                 if (IsAutoDateTable(table.Name)) continue;
//                 CleanTableMetadata(table);
//             }

//             // A table needs at least one column to be a valid, processable
//             // object even before its first refresh — inject a hidden dummy
//             // column just like the combined path does, but note that doing
//             // so does NOT itself trigger any data movement; SaveChanges()
//             // with no pending RequestRefresh() commits metadata only.
//             foreach (Table table in db.Model.Tables)
//             {
//                 if (IsAutoDateTable(table.Name)) continue;
//                 // No name-based skip for the placeholder/'_Measures' table here:
//                 // while its own visible "__DummyColumn__" is still in place,
//                 // Columns.Find(DummyColName) below already finds it and no-ops
//                 // correctly. Once that column has been removed (after real Gold
//                 // measures land on it — see AddGoldMeasuresTable), this same
//                 // check needs to fire and inject a hidden replacement, exactly
//                 // like it would for any other table.

//                 bool hasDataColumn = table.Columns
//                     .Any(c => c.Type == ColumnType.Data && !c.IsHidden);

//                 if (!hasDataColumn && table.Columns.Find(DummyColName) == null)
//                 {
//                     table.Columns.Add(new DataColumn
//                     {
//                         Name         = DummyColName,
//                         DataType     = DataType.String,
//                         IsHidden     = true,
//                         SourceColumn = DummyColName
//                     });
//                 }
//             }

//             try
//             {
//                 db.Model.SaveChanges();
//                 _log.LogInformation("[Transaction 1/Structure] SaveChanges() succeeded — no refresh requested, credentials untouched.");
//             }
//             catch (Exception ex)
//             {
//                 _log.LogWarning(ex, "[Transaction 1/Structure] Bulk save failed — falling back to per-table save.");
//                 foreach (Table table in db.Model.Tables)
//                 {
//                     if (IsAutoDateTable(table.Name)) continue;
//                     try { db.Model.SaveChanges(); }
//                     catch (Exception tex)
//                     {
//                         _log.LogWarning("[Transaction 1/Structure] SKIPPED '{t}': {err}", table.Name, tex.Message);
//                         warnings.Add($"'{table.Name}' failed to save structure: {tex.Message}");
//                     }
//                 }
//             }

//             return warnings;
//         }

//         // ── Partition-mode awareness ────────────────────────────────────────
//         // A table can be Import, DirectQuery, DirectLake, or a hybrid mix of
//         // partitions. RequestRefresh(Full) is meaningful for Import and
//         // DirectLake partitions (it triggers an actual data load / OneLake
//         // reframe) but is a no-op-at-best, throw-at-worst for pure DirectQuery
//         // partitions — there is no cached copy to refresh, the engine just
//         // validates connectivity + schema against the live source. Skipping
//         // the RequestRefresh call for DirectQuery-only tables avoids both the
//         // wasted round-trip and any engine version where that call errors
//         // instead of silently ignoring it.
//         private static bool IsDirectQueryOnlyTable(Table table)
//         {
//             var partitions = table.Partitions.Cast<Partition>().ToList();
//             return partitions.Count > 0 && partitions.All(p => p.Mode == ModeType.DirectQuery);
//         }

//         // ── Transaction 2: refresh + save, run only AFTER credentials are
//         //    bound (see PowerBIService.BindLakehouseDatasourceCredentialsAsync
//         //    for the gap in between). Never call this in the same method
//         //    invocation as SaveStructureOnly() above. ─────────────────────
//         public List<string> RequestFullRefreshAndSave(Database db)
//         {
//             var warnings = new List<string>();

//             foreach (Table table in db.Model.Tables)
//             {
//                 if (IsAutoDateTable(table.Name)) continue;

//                 if (IsDirectQueryOnlyTable(table))
//                 {
//                     _log.LogInformation(
//                         "[Transaction 2/Refresh] Skipping RequestRefresh for '{t}' — DirectQuery-only partition(s), " +
//                         "nothing to import/cache; the engine validates it at save time instead.", table.Name);
//                     continue;
//                 }

//                 // Import and DirectLake (and any hybrid table with at least
//                 // one non-DirectQuery partition) still need an explicit
//                 // refresh request — for DirectLake this is what triggers the
//                 // OneLake reframe.
//                 table.RequestRefresh(RefreshType.Full);
//             }

//             try
//             {
//                 db.Model.SaveChanges();
//                 _log.LogInformation("[Transaction 2/Refresh] SaveChanges() succeeded — all refreshable tables refreshed.");
//             }
//             catch (Exception ex)
//             {
//                 var isCredentialError =
//                     ex.Message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
//                     ex.Message.Contains("Access Error", StringComparison.OrdinalIgnoreCase) ||
//                     ex.Message.Contains("(401)", StringComparison.OrdinalIgnoreCase) ||
//                     ex.Message.Contains("not accessible", StringComparison.OrdinalIgnoreCase) ||
//                     ex.Message.Contains("OLE DB or ODBC error", StringComparison.OrdinalIgnoreCase);

//                 _log.LogWarning(ex, "[Transaction 2/Refresh] Bulk refresh failed{hint} — falling back to per-table refresh.",
//                     isCredentialError ? " (looks like a credentials problem — confirm Transaction 1 and the credential bind step both completed first)" : "");

//                 foreach (Table table in db.Model.Tables)
//                 {
//                     if (IsAutoDateTable(table.Name)) continue;
//                     if (IsDirectQueryOnlyTable(table)) continue; // see note above — nothing to refresh

//                     try
//                     {
//                         table.RequestRefresh(RefreshType.Full);
//                         db.Model.SaveChanges();
//                     }
//                     catch (Exception tex)
//                     {
//                         _log.LogWarning("[Transaction 2/Refresh] SKIPPED '{t}': {err}", table.Name, tex.Message);
//                         warnings.Add($"'{table.Name}' failed to refresh: {tex.Message}");
//                     }
//                 }
//             }

//             return warnings;
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // Poll for a fully-processed, valid schema BEFORE the caller is
//         // allowed to move on to embed-token generation.
//         //
//         // Why this exists: db.Model.SaveChanges() with RequestRefresh() (see
//         // RequestFullRefreshAndSave above) is synchronous from the XMLA
//         // client's point of view — it blocks until the *command* completes —
//         // but that is not the same guarantee as "every table is Ready".
//         // Large/partition-heavy refreshes, background-processing capacities,
//         // and transient CalculationNeeded states after a structural change
//         // can all leave SaveChanges() returning success while one or more
//         // tables/measures are still mid-recalculation. If the embed-token
//         // request lands in that window, the AS engine can refuse to load the
//         // model for the embed session and the Power BI service surfaces that
//         // as Premium_ASWL_Error (HTTP 500) — an engine-side workload error,
//         // not an auth/config problem.
//         //
//         // This method re-reads object state directly from the server (not
//         // from the in-memory TOM object graph, which goes stale the instant
//         // SaveChanges() returns) and only returns once every table reports
//         // ObjectState.Ready. It throws immediately on a genuine error state
//         // (fail fast — retrying a genuinely broken schema just burns the
//         // timeout budget) and throws TimeoutException if the poll window
//         // expires with tables still not ready, so the caller can decide
//         // whether to retry the whole rebuild or surface the error.
//         //
//         // Call this AFTER RequestFullRefreshAndSave(db) and BEFORE
//         // Disconnect(server) — it needs the live connection to refresh
//         // state from the server, not the cached client-side object graph.
//         //
//         // Direct Lake note: this poll works unchanged for DirectLake
//         // partitions — RequestRefresh(Full) on a DirectLake partition
//         // triggers an OneLake reframe, and the resulting Ready/NoData vs.
//         // SemanticError/EvaluationError/DependencyError states are reported
//         // through the same Partition.State property as Import/DirectQuery.
//         // The one practical difference is timing: reframing large Delta
//         // tables can take noticeably longer than an Import refresh of the
//         // same row count, so give DirectLake-heavy models a longer `timeout`
//         // than the 5-minute default.
//         // ─────────────────────────────────────────────────────────────────────
//         public async Task WaitForModelReadyAsync(
//             Server server,
//             Database db,
//             TimeSpan? timeout = null,
//             TimeSpan? pollInterval = null)
//         {
//             var effectiveTimeout  = timeout ?? TimeSpan.FromMinutes(5);
//             var effectivePollGap  = pollInterval ?? TimeSpan.FromSeconds(3);
//             var sw = Stopwatch.StartNew();
//             var attempt = 0;

//             while (true)
//             {
//                 attempt++;

//                 // Re-pull live object state from the server — do NOT rely on
//                 // the in-memory db.Model.Tables states from before SaveChanges,
//                 // they do not update themselves.
//                 server.Refresh();
//                 db.Refresh();

//                 var tables = db.Model.Tables
//                     .Cast<Table>()
//                     .Where(t => !IsAutoDateTable(t.Name))
//                     .ToList();

//                 // Table itself exposes no aggregate State in this TOM package
//                 // — processing state lives on its Partitions (actual data
//                 // load) and on any Calculated/CalculatedTableColumn columns
//                 // (DAX expression evaluation). Data columns mirror their
//                 // partition's state, so partitions + calculated columns
//                 // together are the full picture of "is this table truly
//                 // queryable".
//                 var partitionEntries = tables
//                     .SelectMany(t => t.Partitions.Cast<Partition>().Select(p => (Table: t, Partition: p)))
//                     .ToList();

//                 var calcColumnEntries = tables
//                     .SelectMany(t => t.Columns.Cast<Column>()
//                         .Where(c => c.Type == ColumnType.Calculated || c.Type == ColumnType.CalculatedTableColumn)
//                         .Select(c => (Table: t, Column: c)))
//                     .ToList();

//                 // TOM's ObjectState has no plain "Error" member — processing
//                 // failures surface as one of three specific states instead.
//                 // NoData (queryable, just empty) is NOT an error — a
//                 // zero-row table/partition is still a valid, loadable schema.
//                 var erroredPartitions = partitionEntries
//                     .Where(x => x.Partition.State == ObjectState.SemanticError
//                              || x.Partition.State == ObjectState.EvaluationError
//                              || x.Partition.State == ObjectState.DependencyError)
//                     .Select(x => $"{x.Table.Name}[partition:{x.Partition.Name},mode:{x.Partition.Mode}]={x.Partition.State}")
//                     .ToList();

//                 var erroredColumns = calcColumnEntries
//                     .Where(x => x.Column.State == ObjectState.SemanticError
//                              || x.Column.State == ObjectState.EvaluationError
//                              || x.Column.State == ObjectState.DependencyError)
//                     .Select(x => $"{x.Table.Name}[column:{x.Column.Name}]={x.Column.State}")
//                     .ToList();

//                 var errored = erroredPartitions.Concat(erroredColumns).ToList();
//                 if (errored.Count > 0)
//                 {
//                     var msg = $"Model rebuild left {errored.Count} object(s) in an error state after processing: " +
//                               string.Join(", ", errored) + ". Aborting before embed-token generation — " +
//                               "retrying the token call against a broken schema would just reproduce " +
//                               "Premium_ASWL_Error.";
//                     _log.LogError("[WaitForModelReady] {msg}", msg);
//                     throw new InvalidOperationException(msg);
//                 }

//                 // Ready and NoData are both queryable/loadable — only
//                 // CalculationNeeded means "still processing, keep polling".
//                 var notReadyPartitions = partitionEntries
//                     .Where(x => x.Partition.State != ObjectState.Ready && x.Partition.State != ObjectState.NoData)
//                     .Select(x => $"{x.Table.Name}[partition:{x.Partition.Name},mode:{x.Partition.Mode}]={x.Partition.State}")
//                     .ToList();

//                 var notReadyColumns = calcColumnEntries
//                     .Where(x => x.Column.State != ObjectState.Ready && x.Column.State != ObjectState.NoData)
//                     .Select(x => $"{x.Table.Name}[column:{x.Column.Name}]={x.Column.State}")
//                     .ToList();

//                 var notReady = notReadyPartitions.Concat(notReadyColumns).ToList();

//                 if (notReady.Count == 0)
//                 {
//                     _log.LogInformation(
//                         "[WaitForModelReady] All {n} table(s) Ready after {attempt} poll(s), {elapsed}ms — safe to request embed token.",
//                         tables.Count, attempt, sw.ElapsedMilliseconds);
//                     return;
//                 }

//                 if (sw.Elapsed >= effectiveTimeout)
//                 {
//                     var msg = $"Model still not Ready after {effectiveTimeout.TotalSeconds:0}s ({attempt} poll(s)). " +
//                               $"Table(s) still not ready: {string.Join(", ", notReady)}.";
//                     _log.LogError("[WaitForModelReady] {msg}", msg);
//                     throw new TimeoutException(msg);
//                 }

//                 _log.LogInformation(
//                     "[WaitForModelReady] Poll {attempt}: {count} table(s) not ready yet — {list}. Waiting {gap}s before re-checking.",
//                     attempt, notReady.Count, string.Join(", ", notReady), effectivePollGap.TotalSeconds);

//                 await Task.Delay(effectivePollGap);
//             }
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // FinalizeAndProcessModelAsync — the single entry point callers
//         // (MigrationController) should use to guarantee "the model is Ready
//         // before the API returns success", instead of manually sequencing
//         // SaveStructureOnly / RequestFullRefreshAndSave / WaitForModelReadyAsync
//         // themselves.
//         //
//         // Sequence:
//         //   1. SaveStructureOnly(db)                — Transaction 1: metadata only.
//         //   2. betweenStructureAndRefresh() if given — e.g. bind Lakehouse/
//         //      datasource credentials. Must run strictly between steps 1 and
//         //      3 — never before (nothing to bind credentials to yet) and
//         //      never combined with step 3 in one SaveChanges (that's the
//         //      "add new data sources in a separate transaction first"
//         //      anti-pattern this whole split was built to avoid).
//         //   3. RequestFullRefreshAndSave(db) if requestFullRefresh          — Transaction 2.
//         //   4. WaitForModelReadyAsync(server, db, ...)                      — poll to Ready.
//         //
//         // Unlike WaitForModelReadyAsync (which throws), this method NEVER
//         // throws for a business-logic failure (error state, timeout) — it
//         // catches those and reports them on the returned ModelFinalizeResult
//         // so the caller can decide what to do (retry, surface to the user,
//         // roll the session back) without a try/catch around every call site.
//         // Only truly unexpected/infrastructure exceptions (e.g. the XMLA
//         // connection itself dying) are allowed to propagate, since there is
//         // nothing meaningful to report in the result object for those.
//         // ─────────────────────────────────────────────────────────────────────
//         public async Task<ModelFinalizeResult> FinalizeAndProcessModelAsync(
//             Server server,
//             Database db,
//             Func<Task>? betweenStructureAndRefresh = null,
//             bool requestFullRefresh = true,
//             TimeSpan? readyTimeout = null,
//             TimeSpan? readyPollInterval = null)
//         {
//             var result = new ModelFinalizeResult();
//             var sw = Stopwatch.StartNew();

//             try
//             {
//                 _log.LogInformation("[FinalizeAndProcessModel] Transaction 1/3: saving structure only…");
//                 result.StructureWarnings.AddRange(SaveStructureOnly(db));
//                 result.Status = ModelFinalizeStatus.StructureSaved;

//                 if (betweenStructureAndRefresh != null)
//                 {
//                     _log.LogInformation(
//                         "[FinalizeAndProcessModel] Running caller-supplied between-phase step " +
//                         "(e.g. datasource credential binding)…");
//                     await betweenStructureAndRefresh();
//                 }

//                 if (requestFullRefresh)
//                 {
//                     _log.LogInformation("[FinalizeAndProcessModel] Transaction 2/3: requesting full refresh + save…");
//                     result.RefreshWarnings.AddRange(RequestFullRefreshAndSave(db));
//                     result.Status = ModelFinalizeStatus.Processing;
//                 }
//                 else
//                 {
//                     _log.LogInformation("[FinalizeAndProcessModel] requestFullRefresh=false — skipping Transaction 2.");
//                 }

//                 _log.LogInformation("[FinalizeAndProcessModel] Transaction 3/3: polling engine state until Ready…");
//                 await WaitForModelReadyAsync(server, db, readyTimeout, readyPollInterval);

//                 result.Success = true;
//                 result.Status  = ModelFinalizeStatus.Ready;
//                 _log.LogInformation(
//                     "[FinalizeAndProcessModel] Model is Ready in {elapsed}ms — safe to request an embed token.",
//                     sw.ElapsedMilliseconds);
//             }
//             catch (TimeoutException ex)
//             {
//                 result.Success      = false;
//                 result.Status       = ModelFinalizeStatus.TimedOut;
//                 result.ErrorMessage = ex.Message;
//                 _log.LogError(ex, "[FinalizeAndProcessModel] Timed out waiting for the model to become Ready.");
//             }
//             catch (InvalidOperationException ex)
//             {
//                 result.Success      = false;
//                 result.Status       = ModelFinalizeStatus.Failed;
//                 result.ErrorMessage = ex.Message;
//                 _log.LogError(ex, "[FinalizeAndProcessModel] Model finalize failed with a business-logic error.");
//             }
//             finally
//             {
//                 result.Elapsed = sw.Elapsed;
//             }

//             return result;
//         }

//         // ── Two-phase save (Phase1: dummy cols → save; Phase2: remove dummies) ─
//         // Returns any table-level processing warnings so the caller can
//         // surface them (previously these were only written to the log and
//         // the caller had no way to know a table came back with zero rows).
//         public List<string> SaveChanges(Database db)
//         {
//             var warnings = new List<string>();
//             const string DummyColName = "__DummyColumn__";

//             foreach (Table table in db.Model.Tables)
//             {
//                 // Don't strip hierarchies (Year/Quarter/Month/Day) off Power
//                 // BI's own auto date tables — those are part of what makes
//                 // them valid variation targets in the first place.
//                 if (IsAutoDateTable(table.Name)) continue;
//                 CleanTableMetadata(table);
//             }

//             foreach (Table table in db.Model.Tables)
//             {
//                 // Auto date tables always have their own Date column and are
//                 // fully managed by Power BI — never inject/remove synthetic
//                 // columns on them.
//                 if (IsAutoDateTable(table.Name)) continue;
//                 // See SaveStructureOnly()'s matching comment: no name-based
//                 // skip for the placeholder/'_Measures' table — the check
//                 // below already no-ops while its own dummy column is intact,
//                 // and needs to fire once that column is gone.

//                 bool hasDataColumn = table.Columns
//                     .Any(c => c.Type == ColumnType.Data && !c.IsHidden);

//                 if (!hasDataColumn && table.Columns.Find(DummyColName) == null)
//                 {
//                     table.Columns.Add(new DataColumn
//                     {
//                         Name         = DummyColName,
//                         DataType     = DataType.String,
//                         IsHidden     = true,
//                         SourceColumn = DummyColName
//                     });
//                 }
//             }

//             // Without this, a bulk save that succeeds on the FIRST try (the
//             // common case) commits metadata only — Model.SaveChanges() alone
//             // never loads any data. Every previous RequestRefresh() call in
//             // this method lived inside the per-table FALLBACK path below,
//             // which only runs when the bulk save throws. That left the
//             // normal success path creating tables/columns with zero rows
//             // behind them — exactly "tables show up but every visual is
//             // blank".
//             foreach (Table table in db.Model.Tables)
//             {
//                 if (IsAutoDateTable(table.Name)) continue;
//                 table.RequestRefresh(RefreshType.Full);
//             }

//             try
//             {
//                 db.Model.SaveChanges();
//                 _log.LogInformation("Phase-1 SaveChanges() succeeded (including full refresh of all tables).");
//             }
//             catch (Exception ex)
//             {
//                 // Specific, named detection for the auto-date-table variation
//                 // error, even though IsAutoDateTable()/CleanTableMetadata()
//                 // above should already prevent it from ever reaching here. If
//                 // it DOES show up, the log should say exactly what happened
//                 // instead of a generic bulk-save failure. Either way we fall
//                 // through to the per-table retry so one bad table never
//                 // blocks every other table from saving.
//                 bool isAutoDateVariationError =
//                     ex.Message.Contains("ShowAsVariationsOnly", StringComparison.OrdinalIgnoreCase) ||
//                     ex.Message.Contains("must be a target of a variation", StringComparison.OrdinalIgnoreCase);

//                 if (isAutoDateVariationError)
//                 {
//                     _log.LogWarning(ex,
//                         "Phase-1 bulk save failed on an auto-date-table variation error " +
//                         "(ShowAsVariationsOnly) — this should be prevented by IsAutoDateTable() " +
//                         "skip logic; falling back to per-table save so unrelated tables still commit.");
//                 }
//                 else
//                 {
//                     _log.LogWarning(ex, "Phase-1 bulk save failed — switching to per-table save.");
//                 }

//                 foreach (Table table in db.Model.Tables)
//                 {
//                     if (IsAutoDateTable(table.Name))
//                     {
//                         _log.LogInformation("Per-table save: skipping auto date table '{t}' entirely.", table.Name);
//                         continue;
//                     }
//                     try
//                     {
//                         table.RequestRefresh(RefreshType.Full);
//                         db.Model.SaveChanges();
//                         _log.LogInformation("Per-table save succeeded: '{t}'", table.Name);
//                     }
//                     catch (Exception tex)
//                     {
//                         var tableIsAutoDateError =
//                             tex.Message.Contains("ShowAsVariationsOnly", StringComparison.OrdinalIgnoreCase) ||
//                             tex.Message.Contains("must be a target of a variation", StringComparison.OrdinalIgnoreCase);

//                         // The single most common reason a brand-new Snowflake-
//                         // backed table saves its metadata fine but never gets
//                         // any rows: Power BI Service has no stored credentials
//                         // for that data source yet. Pushing the M expression
//                         // via TOM only defines the query — it never
//                         // authenticates the service to Snowflake. That refresh
//                         // failure was previously only ever written to the log
//                         // at Warning level with no way for the caller/frontend
//                         // to know about it, which is exactly why a table can
//                         // show correct columns with silently zero rows.
//                         var isCredentialError =
//                             tex.Message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
//                             tex.Message.Contains("Access Error", StringComparison.OrdinalIgnoreCase) ||
//                             tex.Message.Contains("(401)", StringComparison.OrdinalIgnoreCase) ||
//                             tex.Message.Contains("user name and", StringComparison.OrdinalIgnoreCase) ||
//                             tex.Message.Contains("not accessible", StringComparison.OrdinalIgnoreCase) ||
//                             tex.Message.Contains("OLE DB or ODBC error", StringComparison.OrdinalIgnoreCase);

//                         var hint = tableIsAutoDateError
//                             ? " (auto-date-table variation error — check for a stray LocalDateTable_*/DateTableTemplate_* reference)"
//                             : isCredentialError
//                                 ? " (looks like a data-source CREDENTIALS problem — go to the dataset's Settings → Data source credentials in the Power BI Service and sign in to the Snowflake connection, then retry)"
//                                 : "";

//                         _log.LogWarning("Per-table save SKIPPED '{t}': {err}{hint}", table.Name, tex.Message, hint);
//                         warnings.Add($"'{table.Name}' failed to refresh: {tex.Message}{hint}");
//                     }
//                 }
//             }

//             bool needsSecondSave = false;
//             foreach (Table table in db.Model.Tables)
//             {
//                 if (IsAutoDateTable(table.Name)) continue;
//                 // No name-based skip here either: the hasMeasures/
//                 // hasOtherDataColumn gate just below already keeps the
//                 // placeholder/'_Measures' table's dummy column in place while
//                 // it has nothing else to stand on, and correctly drops it
//                 // once real Gold measures exist — exactly the outcome we want.

//                 var dummy = table.Columns.Find(DummyColName);
//                 if (dummy == null) continue;

//                 // Only safe to drop the dummy once the table has something else
//                 // to stand on — measures (matches the reference XMLA deployer's
//                 // pattern) OR another real, visible data column. Removing it
//                 // unconditionally — which is what this used to do — can leave a
//                 // table with ZERO columns. Analysis Services can't process a
//                 // columnless table, and that is a direct, reproducible cause of
//                 // Power BI's "FailedToRefreshPackageAfterImportError" the next
//                 // time anything (including the service's own post-import
//                 // refresh) tries to refresh it.
//                 bool hasMeasures        = table.Measures.Any();
//                 bool hasOtherDataColumn = table.Columns
//                     .Any(c => c != dummy && c.Type == ColumnType.Data && !c.IsHidden);

//                 if (hasMeasures || hasOtherDataColumn)
//                 {
//                     table.Columns.Remove(dummy);
//                     needsSecondSave = true;
//                     _log.LogInformation(
//                         "Removed dummy column from '{t}' (measures={m}, otherDataCol={c})",
//                         table.Name, hasMeasures, hasOtherDataColumn);
//                 }
//                 else
//                 {
//                     _log.LogInformation(
//                         "Keeping dummy column on '{t}' — it has no measures or other columns " +
//                         "to stand on without it, and a columnless table can't refresh.",
//                         table.Name);
//                 }
//             }

//             if (needsSecondSave)
//             {
//                 try { db.Model.SaveChanges(); _log.LogInformation("Phase-2 SaveChanges() succeeded."); }
//                 catch (Exception ex)
//                 {
//                     _log.LogWarning(ex, "Phase-2 bulk save failed — per-table save.");
//                     foreach (Table table in db.Model.Tables)
//                     {
//                         try { db.Model.SaveChanges(); }
//                         catch (Exception tex)
//                         {
//                             _log.LogWarning("Phase-2 per-table SKIPPED '{t}': {err}", table.Name, tex.Message);
//                             warnings.Add($"'{table.Name}' failed on phase-2 save: {tex.Message}");
//                         }
//                     }
//                 }
//             }

//             return warnings;
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // Post-import repair. Power BI's Import API triggers an automatic
//         // refresh right after import, and that refresh commonly fails
//         // (FailedToRefreshPackageAfterImportError) for models with
//         // measure-only or otherwise columnless tables — Analysis Services has
//         // nothing to actually pull for them. Power BI usually still creates
//         // the dataset+report despite reporting the import as "Failed", so
//         // PowerBIService recovers those IDs and we connect via XMLA here to
//         // put every table into a refreshable state (reusing the SaveChanges
//         // dummy-column pattern above) BEFORE anything else touches the model.
//         // ─────────────────────────────────────────────────────────────────────
//         public async Task RepairAfterImportAsync(PowerBIConfig cfg, string datasetId, string? datasetNameForLog = null)
//         {
//             if (string.IsNullOrWhiteSpace(cfg.XmlaEndpoint))
//             {
//                 _log.LogWarning("[XMLA repair] No XmlaEndpoint configured — skipping post-import repair for dataset id '{id}'.", datasetId);
//                 return;
//             }

//             var accessToken = await GetAccessTokenAsync(cfg);
//             var server = new Server();
//             var connStr =
//                 $"Provider=MSOLAP;Data Source={cfg.XmlaEndpoint};" +
//                 $"Password={accessToken};" +
//                 "Persist Security Info=True;Impersonation Level=Impersonate";

//             try
//             {
//                 server.Connect(connStr);
//                 // Match on ID, not Name — same reasoning as
//                 // ConnectAndExtractVisualMetadataAsync above: Name isn't
//                 // unique, so a name-based lookup here could just as easily
//                 // repair a stale same-named dataset instead of the one that
//                 // was just imported.
//                 var db = server.Databases.Cast<Database>()
//                     .FirstOrDefault(d => string.Equals(d.ID, datasetId, StringComparison.OrdinalIgnoreCase));
//                 if (db == null)
//                 {
//                     _log.LogWarning("[XMLA repair] Dataset id '{id}' (name '{name}') not found yet — skipping post-import repair.", datasetId, datasetNameForLog);
//                     return;
//                 }

//                 _log.LogInformation("[XMLA repair] Connected to '{ds}' (id={id}) — repairing any tables that can't refresh…", db.Name, datasetId);
//                 SaveChanges(db);
//                 _log.LogInformation("[XMLA repair] '{ds}' (id={id}) repaired and saved.", db.Name, datasetId);
//             }
//             catch (Exception ex)
//             {
//                 // Non-fatal: log and let the caller carry on. Later steps
//                 // (ReplaceWithSnowflakeViews → SaveChanges) get another shot
//                 // at fixing things, and the caller's own error handling still
//                 // applies if this dataset genuinely can't be reached.
//                 _log.LogWarning(ex, "[XMLA repair] Failed to repair dataset id '{id}' — a later step may still fail.", datasetId);
//             }
//             finally
//             {
//                 try { server.Disconnect(); } catch { }
//                 server.Dispose();
//             }
//         }

//         public void Disconnect(Server server)
//         {
//             try { server.Disconnect(); } catch { }
//             server.Dispose();
//         }

//         public int EnsureRelationshipsByKeyColumns(Database db)
// {
//     static string Norm(string s) =>
//         (s ?? "").Trim().ToLowerInvariant()
//             .Replace("_", "").Replace(" ", "").Replace("-", "");

//     var tables = db.Model.Tables.Cast<Table>()
//         .Where(t => !IsAutoDateTable(t.Name))
//         .Where(t => !t.Name.Equals("_Measures", StringComparison.OrdinalIgnoreCase))
//         .ToList();

//     var factTables = tables.Where(t => Norm(t.Name).Contains("fact")).ToList();
//     var dimTables = tables.Where(t => Norm(t.Name).Contains("dim")).ToList();

//     if (factTables.Count == 0 || dimTables.Count == 0)
//     {
//         _log.LogWarning("[XMLA] EnsureRelationshipsByKeyColumns: no fact/dim tables — skipped");
//         return 0;
//     }

//     var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
//     foreach (var r in db.Model.Relationships.OfType<SingleColumnRelationship>())
//     {
//         existing.Add($"{r.FromTable.Name}.{r.FromColumn.Name}->{r.ToTable.Name}.{r.ToColumn.Name}");
//         existing.Add($"{r.ToTable.Name}.{r.ToColumn.Name}->{r.FromTable.Name}.{r.FromColumn.Name}");
//     }

//     int created = 0;

//     foreach (var fact in factTables)
//     {
//         foreach (var factCol in fact.Columns.Cast<Column>())
//         {
//             if (factCol.Type == ColumnType.RowNumber) continue;

//             var normFactCol = Norm(factCol.Name);
//             bool looksLikeKey =
//                 normFactCol.EndsWith("key") ||
//                 (normFactCol.EndsWith("id") && normFactCol.Length > 2);
//             if (!looksLikeKey) continue;

//             foreach (var dim in dimTables)
//             {
//                 var dimCol = dim.Columns.Cast<Column>()
//                     .FirstOrDefault(c =>
//                         c.Type != ColumnType.RowNumber &&
//                         Norm(c.Name) == normFactCol);

//                 if (dimCol == null) continue;

//                 var key = $"{fact.Name}.{factCol.Name}->{dim.Name}.{dimCol.Name}";
//                 if (existing.Contains(key)) continue;

//                 try
//                 {
//                     var rel = new SingleColumnRelationship
//                     {
//                         Name = $"{dim.Name}_{dimCol.Name}_{fact.Name}_{factCol.Name}",
//                         FromColumn = factCol,
//                         ToColumn = dimCol,
//                         FromCardinality = RelationshipEndCardinality.Many,
//                         ToCardinality = RelationshipEndCardinality.One,
//                         CrossFilteringBehavior = CrossFilteringBehavior.OneDirection,
//                         IsActive = true
//                     };
//                     db.Model.Relationships.Add(rel);
//                     existing.Add(key);
//                     created++;
//                     _log.LogInformation(
//                         "[XMLA] Relationship created: {fact}[{fc}] * → 1 {dim}[{dc}]",
//                         fact.Name, factCol.Name, dim.Name, dimCol.Name);
//                 }
//                 catch (Exception ex)
//                 {
//                     _log.LogWarning(ex,
//                         "[XMLA] Could not create relationship {fact}[{fc}] → {dim}[{dc}]",
//                         fact.Name, factCol.Name, dim.Name, dimCol.Name);
//                 }
//             }
//         }
//     }

//     _log.LogInformation(
//         "[XMLA] EnsureRelationshipsByKeyColumns: created {n}; total now {t}",
//         created, db.Model.Relationships.Count);
//     return created;
// }


//         private string? TryBuildSumMeasureDax(Database db, string measureName)
// {
//     static string Norm(string s) =>
//         (s ?? "").Trim().ToLowerInvariant()
//             .Replace("_", "").Replace(" ", "").Replace("-", "");

//     var normMeasure = Norm(measureName);
//     if (string.IsNullOrEmpty(normMeasure)) return null;

//     foreach (var prefix in new[] { "total", "sum", "avg", "average", "count", "kpi" })
//     {
//         if (normMeasure.StartsWith(prefix) && normMeasure.Length > prefix.Length)
//         {
//             normMeasure = normMeasure[prefix.Length..];
//             break;
//         }
//     }

//     if (string.IsNullOrEmpty(normMeasure)) return null;

//     var tables = db.Model.Tables.Cast<Table>()
//         .Where(t => !IsAutoDateTable(t.Name))
//         .Where(t => !t.Name.Equals(GoldMeasuresTableName, StringComparison.OrdinalIgnoreCase))
//         .OrderByDescending(t =>
//         {
//             var n = Norm(t.Name);
//             var score = 0;
//             if (n.Contains("fact")) score += 5;
//             if (n.Contains("dim")) score -= 5;
//             return score;
//         })
//         .ToList();

//     foreach (var table in tables)
//     {
//         foreach (var col in table.Columns.Cast<Column>())
//         {
//             if (col.Type == ColumnType.RowNumber) continue;

//             var normCol = Norm(col.Name);
//             if (string.IsNullOrEmpty(normCol)) continue;

//             // bool match =
//             //     normCol == normMeasure ||
//             //     normCol.Contains(normMeasure) ||
//             //     normMeasure.Contains(normCol);

//             bool match = normCol == normMeasure;

//             if (!match) continue;

//            if (col.DataType != DataType.Double &&
//            col.DataType != DataType.Decimal &&
//            col.DataType != DataType.Int64)
//            {
//             continue;
//           }

//             return $"SUM('{table.Name}'[{col.Name}])";
//         }
//     }

//     return null;
// }

        

//         private static DataType MapDataType(string sfType) => sfType?.ToUpperInvariant() switch
//         {
//             "NUMBER" or "DECIMAL" or "NUMERIC" or "FLOAT" or "DOUBLE" or "REAL" => DataType.Double,
//             "INTEGER" or "INT" or "BIGINT" or "SMALLINT" or "TINYINT"            => DataType.Int64,
//             "BOOLEAN"                                                              => DataType.Boolean,
//             "DATE" or "DATETIME" or "TIMESTAMP" or "TIMESTAMP_NTZ"
//                 or "TIMESTAMP_LTZ" or "TIMESTAMP_TZ"                              => DataType.DateTime,
//             _ => DataType.String
//         };
//     }

//     // ─────────────────────────────────────────────────────────────────────
//     // Rich result returned by TabularModelService.FinalizeAndProcessModelAsync
//     // so callers (MigrationController) know definitively whether the model
//     // is safe to hand off for embed-token generation, without having to
//     // wrap the call in their own try/catch to find out.
//     // ─────────────────────────────────────────────────────────────────────
//     public enum ModelFinalizeStatus
//     {
//         NotStarted,
//         StructureSaved,
//         Processing,
//         Ready,
//         Failed,
//         TimedOut
//     }

//     public class ModelFinalizeResult
//     {
//         public bool Success { get; set; }
//         public ModelFinalizeStatus Status { get; set; } = ModelFinalizeStatus.NotStarted;
//         public string? ErrorMessage { get; set; }
//         public TimeSpan Elapsed { get; set; }
//         public List<string> StructureWarnings { get; set; } = new();
//         public List<string> RefreshWarnings { get; set; } = new();

//         /// <summary>All non-fatal warnings from both transactions, for callers that just want one flat list.</summary>
//         public List<string> AllWarnings =>
//             StructureWarnings.Concat(RefreshWarnings).ToList();
//     }

//     // Kept for Models.cs compatibility
//     public class PbixTableSchema
//     {
//         public string TableName { get; set; } = "";
//         public List<ColumnMeta> Columns { get; set; } = new();
//         public int MeasuresExcluded          { get; set; }
//         public int CalculatedColumnsExcluded { get; set; }
//     }
// }




using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging;
using PbixMigrator.Models;

namespace PbixMigrator.Services
{
    public class TabularModelService
    {
        private readonly ILogger<TabularModelService> _log;

        private static readonly Regex AutoDateTablePattern =
            new(@"^(LocalDateTable_|DateTableTemplate_)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool IsAutoDateTable(string? tableName) =>
            !string.IsNullOrEmpty(tableName) && AutoDateTablePattern.IsMatch(tableName);

        public TabularModelService(ILogger<TabularModelService> log) => _log = log;

        private static Task<string> GetAccessTokenAsync(PowerBIConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.UserAccessToken))
                throw new InvalidOperationException(
                    "No signed-in user token was provided. The XMLA connection requires cfg.UserAccessToken.");

            return Task.FromResult(cfg.UserAccessToken);
        }

        public async Task<(Server server, Database db, List<VisualMetadata> visuals)>
            ConnectAndExtractVisualMetadataAsync(PowerBIConfig cfg, string datasetId, string? datasetNameForLog = null)
        {
            if (string.IsNullOrWhiteSpace(cfg.XmlaEndpoint))
                throw new InvalidOperationException("PowerBIConfig.XmlaEndpoint is required.");

            _log.LogInformation("[XMLA] Connecting to '{ep}' as signed-in user…", cfg.XmlaEndpoint);
            var accessToken = await GetAccessTokenAsync(cfg);
            var server = new Server();
            var connStr =
                $"Provider=MSOLAP;Data Source={cfg.XmlaEndpoint};" +
                $"Password={accessToken};" +
                "Persist Security Info=True;Impersonation Level=Impersonate";

            try
            {
                server.Connect(connStr);
                _log.LogInformation("[XMLA] Connected OK to {ep}", cfg.XmlaEndpoint);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[XMLA] Connect FAILED to {ep}", cfg.XmlaEndpoint);
                server.Dispose();
                throw new InvalidOperationException($"Failed to connect to XMLA endpoint: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(datasetId))
            {
                server.Dispose();
                throw new InvalidOperationException("ConnectAndExtractVisualMetadataAsync requires a dataset ID (GUID).");
            }

            Database? db;
            try
            {
                db = server.Databases.Cast<Database>()
                    .FirstOrDefault(d => string.Equals(d.ID, datasetId, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[XMLA] Lookup by dataset ID '{id}' failed", datasetId);
                server.Dispose();
                throw new InvalidOperationException($"Lookup failed: {ex.Message}", ex);
            }

            if (db == null)
            {
                server.Dispose();
                throw new InvalidOperationException($"Dataset with ID '{datasetId}' not found on the XMLA endpoint.");
            }

            var visuals = new List<VisualMetadata>();
            foreach (Table table in db.Model.Tables)
            {
                if (IsAutoDateTable(table.Name)) continue;

                foreach (Column col in table.Columns)
                {
                    if (col.Type == ColumnType.RowNumber) continue;

                    visuals.Add(new VisualMetadata
                    {
                        PageName     = "(model)",
                        VisualName   = $"model::{table.Name}",
                        VisualTitle  = table.Name,
                        VisualType   = "table",
                        BoundColumns = new List<VisualColumnBinding>
                        {
                            new VisualColumnBinding
                            {
                                Table    = table.Name,
                                Column   = col.Name,
                                DataRole = "Values"
                            }
                        }
                    });
                }
            }

            return (server, db, visuals);
        }

        public async Task<(Server server, Database db, List<VisualBinding> bindings)>
            ConnectAndExtractBindingsAsync(PowerBIConfig cfg, string datasetId)
        {
            var (server, db, visuals) = await ConnectAndExtractVisualMetadataAsync(cfg, datasetId);
            var bindings = visuals
                .SelectMany(v => v.BoundColumns)
                .Select(b => new VisualBinding { OriginalTable = b.Table, OriginalColumn = b.Column })
                .GroupBy(b => $"{b.OriginalTable}.{b.OriginalColumn}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            return (server, db, bindings);
        }

        public List<UnmappedTable> WipeAndLoadSnowflakeViews(
            Database db,
            SnowflakeConfig sfCfg,
            List<SnowflakeViewMeta> sfViews)
        {
            var unmapped = new List<UnmappedTable>();
            var existingTableNames = db.Model.Tables.Cast<Table>().Select(t => t.Name).ToList();

            var savedRelationships = db.Model.Relationships
                .OfType<SingleColumnRelationship>()
                .Select(r => new {
                    FromTable = r.FromTable.Name,
                    FromColumn = r.FromColumn.Name,
                    ToTable = r.ToTable.Name,
                    ToColumn = r.ToColumn.Name,
                    IsActive = r.IsActive
                }).ToList();

            foreach (var r in db.Model.Relationships.Cast<Relationship>().ToList())
                db.Model.Relationships.Remove(r);

            foreach (var t in db.Model.Tables.Cast<Table>().ToList())
                db.Model.Tables.Remove(t);

            var duplicateViewNameCounts = sfViews
                .GroupBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sfView in sfViews)
            {
                var needsQualifier = duplicateViewNameCounts[sfView.ViewName] > 1;
                var tableName = needsQualifier
                    ? $"{sfView.ViewName}_{sfView.Schema}"
                    : sfView.ViewName;

                var suffix = 2;
                while (usedNames.Contains(tableName))
                    tableName = $"{sfView.ViewName}_{sfView.Schema}_{suffix++}";

                usedNames.Add(tableName);

                var newTable = new Table { Name = tableName };
                var mExpr    = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);

                newTable.Partitions.Add(new Partition
                {
                    Name   = tableName,
                    Mode   = ModeType.DirectQuery,
                    Source = new MPartitionSource { Expression = mExpr }
                });

                foreach (var sfCol in sfView.Columns)
                {
                    newTable.Columns.Add(new DataColumn
                    {
                        Name         = sfCol.Name,
                        SourceColumn = sfCol.Name,
                        DataType     = MapDataType(sfCol.DataType),
                        SummarizeBy  = AggregateFunction.None
                    });
                }

                db.Model.Tables.Add(newTable);
            }

            foreach (var rel in savedRelationships)
            {
                var fromTable = db.Model.Tables.FirstOrDefault(t => t.Name.Equals(rel.FromTable, StringComparison.OrdinalIgnoreCase));
                var toTable = db.Model.Tables.FirstOrDefault(t => t.Name.Equals(rel.ToTable, StringComparison.OrdinalIgnoreCase));

                if (fromTable != null && toTable != null)
                {
                    var fromCol = fromTable.Columns.FirstOrDefault(c => c.Name.Equals(rel.FromColumn, StringComparison.OrdinalIgnoreCase));
                    var toCol = toTable.Columns.FirstOrDefault(c => c.Name.Equals(rel.ToColumn, StringComparison.OrdinalIgnoreCase));

                    if (fromCol != null && toCol != null)
                    {
                        var newRel = new SingleColumnRelationship
                        {
                            Name                   = $"{fromTable.Name}_{fromCol.Name}_{toTable.Name}_{toCol.Name}",
                            FromColumn             = fromCol,
                            ToColumn               = toCol,
                            FromCardinality        = RelationshipEndCardinality.Many,
                            ToCardinality          = RelationshipEndCardinality.One,
                            CrossFilteringBehavior = CrossFilteringBehavior.OneDirection,
                            IsActive               = rel.IsActive
                        };
                        db.Model.Relationships.Add(newRel);
                    }
                }
            }

            DisableAutomaticModelBehaviors(db);
            return unmapped;
        }

        public List<UnmappedTable> LoadSnowflakeViewsAlongsideExisting(
            Database db,
            SnowflakeConfig sfCfg,
            List<SnowflakeViewMeta> sfViews)
        {
            var unmapped = new List<UnmappedTable>();
            var existingTableNames = new HashSet<string>(
                db.Model.Tables.Cast<Table>().Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            var duplicateViewNameCounts = sfViews
                .GroupBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var usedNames = new HashSet<string>(existingTableNames, StringComparer.OrdinalIgnoreCase);

            foreach (var sfView in sfViews)
            {
                var needsQualifier =
                    duplicateViewNameCounts[sfView.ViewName] > 1 ||
                    usedNames.Contains(sfView.ViewName);

                var tableName = (needsQualifier
                    ? $"{sfView.ViewName}_{sfView.Schema}" : sfView.ViewName).ToUpperInvariant();

                var suffix = 2;
                while (usedNames.Contains(tableName))
                    tableName = $"{sfView.ViewName}_{sfView.Schema}_{suffix++}";

                usedNames.Add(tableName);

                var newTable = new Table { Name = tableName };
                var mExpr    = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);

                newTable.Partitions.Add(new Partition
                {
                    Name   = tableName,
                    Mode   = ModeType.DirectQuery,
                    Source = new MPartitionSource { Expression = mExpr }
                });

                foreach (var sfCol in sfView.Columns)
                {
                    newTable.Columns.Add(new DataColumn
                    {
                        Name         = sfCol.Name,
                        SourceColumn = sfCol.Name,
                        DataType     = MapDataType(sfCol.DataType),
                        SummarizeBy  = AggregateFunction.None
                    });
                }

                db.Model.Tables.Add(newTable);
            }

            DisableAutomaticModelBehaviors(db);
            return unmapped;
        }

        public List<UnmappedTable> UpdateSnowflakeViewsInPlace(
            Database db,
            SnowflakeConfig sfCfg,
            List<SnowflakeViewMeta> sfViews)
        {
            var unmapped = new List<UnmappedTable>();
            DisableAutomaticModelBehaviors(db);

            var existingNames = new HashSet<string>(
                db.Model.Tables.Cast<Table>().Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            var duplicateViewNameCounts = sfViews
                .GroupBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var usedNames = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

            foreach (var sfView in sfViews)
            {
                var needsQualifier =
                    duplicateViewNameCounts[sfView.ViewName] > 1 ||
                    usedNames.Contains(sfView.ViewName) ||
                    IsAutoDateTable(sfView.ViewName);

                var tableName = needsQualifier
                    ? $"{sfView.ViewName}_{sfView.Schema}"
                    : sfView.ViewName;

                var suffix = 2;
                while (usedNames.Contains(tableName))
                    tableName = $"{sfView.ViewName}_{sfView.Schema}_{suffix++}";

                usedNames.Add(tableName);

                var newTable = new Table { Name = tableName };
                var mExpr    = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);

                newTable.Partitions.Add(new Partition
                {
                    Name   = tableName,
                    Mode   = ModeType.DirectQuery,
                    Source = new MPartitionSource { Expression = mExpr }
                });

                foreach (var sfCol in sfView.Columns)
                {
                    newTable.Columns.Add(new DataColumn
                    {
                        Name         = sfCol.Name,
                        SourceColumn = sfCol.Name,
                        DataType     = MapDataType(sfCol.DataType),
                        SummarizeBy  = AggregateFunction.None
                    });
                }

                db.Model.Tables.Add(newTable);
            }

            DisableAutomaticModelBehaviors(db);
            return unmapped;
        }

        public static string BuildLakehouseSqlMExpression(string sqlServer, string sqlDatabase, string tableName)
        {
            return
                $"let\n" +
                $"    Source = Sql.Database(\"{sqlServer}\", \"{sqlDatabase}\"),\n" +
                $"    Nav    = Source{{[Schema=\"dbo\",Item=\"{tableName}\"]}}[Data]\n" +
                $"in\n" +
                $"    Nav";
        }

        public List<UnmappedTable> UpdateLakehouseTablesInPlace(
            Database db,
            string sqlServer,
            string sqlDatabase,
            List<LakehouseTableMeta> lakehouseTables)
        {
            var unmapped = new List<UnmappedTable>();
            DisableAutomaticModelBehaviors(db);

            var existingNames = new HashSet<string>(
                db.Model.Tables.Cast<Table>().Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            var usedNames = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

            foreach (var lhTable in lakehouseTables)
            {
                var tableName = lhTable.TableName;
                if (usedNames.Contains(tableName) || IsAutoDateTable(tableName))
                {
                    var suffix = 2;
                    var candidate = $"{tableName}_{suffix}";
                    while (usedNames.Contains(candidate))
                        candidate = $"{tableName}_{suffix++}";
                    tableName = candidate;
                }
                usedNames.Add(tableName);

                var newTable = new Table { Name = tableName };
                var mExpr = BuildLakehouseSqlMExpression(sqlServer, sqlDatabase, lhTable.TableName);

                newTable.Partitions.Add(new Partition
                {
                    Name   = tableName,
                    Mode   = ModeType.DirectQuery,
                    Source = new MPartitionSource { Expression = mExpr }
                });

                foreach (var col in lhTable.Columns)
                {
                    var physicalName = string.IsNullOrEmpty(col.SourceColumn) ? col.Name : col.SourceColumn;
                    newTable.Columns.Add(new DataColumn
                    {
                        Name         = col.Name,
                        SourceColumn = physicalName,
                        DataType     = MapDataType(col.DataType),
                        SummarizeBy  = AggregateFunction.None
                    });
                }

                db.Model.Tables.Add(newTable);
            }

            DisableAutomaticModelBehaviors(db);
            return unmapped;
        }

        public void RemoveOriginalTables(Database db, List<string> originalTableNames)
        {
            if (originalTableNames == null || originalTableNames.Count == 0) return;

            var toRemoveNames = new HashSet<string>(originalTableNames, StringComparer.OrdinalIgnoreCase);
            var toRemove = db.Model.Tables.Cast<Table>()
                .Where(t => toRemoveNames.Contains(t.Name))
                .ToList();

            if (toRemove.Count == 0) return;

            // Strip variations pointing to tables being removed
            var removeNameSet = new HashSet<string>(toRemove.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            foreach (Table table in db.Model.Tables)
            {
                foreach (Column col in table.Columns)
                {
                    if (col is not DataColumn dc) continue;
                    foreach (var v in dc.Variations.ToList())
                    {
                        var target = GetVariationTargetTableName(v);
                        if (target != null && removeNameSet.Contains(target))
                            dc.Variations.Remove(v);
                    }
                }
            }

            foreach (var t in toRemove)
                db.Model.Tables.Remove(t);

            PurgeDanglingRelationships(db);
            PurgeOrphanedDataSourcesAndExpressions(db);
            RemoveAutoDateTables(db);
        }

        private void PurgeOrphanedDataSourcesAndExpressions(Database db)
        {
            var referencedDataSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Table table in db.Model.Tables)
                foreach (Partition p in table.Partitions)
                    if (p.Source is QueryPartitionSource pps && pps.DataSource != null)
                        referencedDataSourceNames.Add(pps.DataSource.Name);

            foreach (var ds in db.Model.DataSources.ToList())
            {
                if (!referencedDataSourceNames.Contains(ds.Name))
                    db.Model.DataSources.Remove(ds);
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var expr in db.Model.Expressions.ToList())
                {
                    var isReferenced =
                        db.Model.Tables.SelectMany(t => t.Partitions)
                            .Select(p => (p.Source as MPartitionSource)?.Expression)
                            .Where(text => !string.IsNullOrEmpty(text))
                            .Any(text => Regex.IsMatch(text!, $@"\b{Regex.Escape(expr.Name)}\b"))
                        ||
                        db.Model.Expressions
                            .Where(e => e.Name != expr.Name)
                            .Any(e => !string.IsNullOrEmpty(e.Expression) &&
                                      Regex.IsMatch(e.Expression, $@"\b{Regex.Escape(expr.Name)}\b"));

                    if (!isReferenced)
                    {
                        db.Model.Expressions.Remove(expr);
                        changed = true;
                    }
                }
            }
        }

        private const string GoldMeasuresTableName = "_Measures";
        private const string PlaceholderTableName = GoldMeasuresTableName;

        public void AddPlaceholderTable(Database db)
        {
            if (db.Model.Tables.Find(PlaceholderTableName) != null) return;

            var table = new Table { Name = PlaceholderTableName };
            var column = new DataColumn
            {
                Name         = "__DummyColumn__",
                SourceColumn = "__DummyColumn__",
                DataType     = DataType.String,
                IsHidden     = false
            };
            table.Columns.Add(column);

            var partition = new Partition
            {
                Name   = PlaceholderTableName,
                Source = new MPartitionSource
                {
                    Expression =
                        "let\n" +
                        "    Source = #table(\n" +
                        "        type table [__DummyColumn__ = Text.Type],\n" +
                        "        {{\"placeholder\"}}\n" +
                        "    )\n" +
                        "in\n" +
                        "    Source"
                }
            };
            table.Partitions.Add(partition);
            db.Model.Tables.Add(table);
        }

        public void RequestPlaceholderRefresh(Database db)
        {
            var t = db.Model.Tables.Find(PlaceholderTableName);
            t?.RequestRefresh(RefreshType.Full);
        }

        public void RemovePlaceholderTable(Database db)
        {
            var t = db.Model.Tables.Find(PlaceholderTableName);
            if (t == null) return;

            if (t.Measures.Count > 0) return;

            db.Model.Tables.Remove(t);
            PurgeDanglingRelationships(db);
        }

        public class OriginalMeasureSnapshot
        {
            public string TableName { get; set; } = "";
            public string MeasureName { get; set; } = "";
            public string Expression { get; set; } = "";
        }

        public List<OriginalMeasureSnapshot> CaptureOriginalMeasures(Database db)
        {
            var list = new List<OriginalMeasureSnapshot>();
            foreach (var table in db.Model.Tables.Cast<Table>())
            {
                if (IsAutoDateTable(table.Name)) continue;
                foreach (var m in table.Measures.Cast<Measure>())
                {
                    if (string.IsNullOrWhiteSpace(m.Expression)) continue;
                    list.Add(new OriginalMeasureSnapshot
                    {
                        TableName   = table.Name,
                        MeasureName = m.Name,
                        Expression  = m.Expression
                    });
                }
            }
            return list;
        }

        private static string NormName(string s) =>
            (s ?? "").Trim().ToLowerInvariant().Replace("_", "").Replace(" ", "").Replace("-", "");

        private string? TryRemapDaxToCurrentModel(Database db, string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;

            var tables = db.Model.Tables.Cast<Table>()
                .Where(t => !IsAutoDateTable(t.Name))
                .ToList();

            var tableByNorm = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables)
            {
                var key = NormName(t.Name);
                if (!tableByNorm.ContainsKey(key))
                    tableByNorm[key] = t;
            }

            var pattern = new Regex(@"('([^']+)'|([A-Za-z_][\w ]*))\[([^\]]+)\]", RegexOptions.Compiled);
            var missing = new List<string>();

            var result = pattern.Replace(expression, match =>
            {
                var rawTable = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                var col      = match.Groups[4].Value.Trim();
                var normT    = NormName(rawTable);

                Table? table = null;
                if (tableByNorm.TryGetValue(normT, out var exact))
                    table = exact;
                else
                    table = tables.FirstOrDefault(t =>
                        NormName(t.Name) == normT ||
                        NormName(t.Name).Contains(normT) ||
                        normT.Contains(NormName(t.Name)));

                if (table == null)
                {
                    missing.Add($"table:{rawTable}");
                    return match.Value;
                }

                var column = table.Columns.Cast<Column>().FirstOrDefault(c =>
                    c.Type != ColumnType.RowNumber &&
                    (string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase) || NormName(c.Name) == NormName(col)));

                if (column == null)
                {
                    missing.Add($"{table.Name}[{col}]");
                    return match.Value;
                }

                return $"'{table.Name}'[{column.Name}]";
            });

            return missing.Count > 0 ? null : result;
        }

        private string? TryBuildCountMeasureDax(Database db, string measureName)
        {
            var n = NormName(measureName);
            if (!(n.Contains("order") || n.Contains("count") || n.Contains("rows")))
                return null;

            var fact = db.Model.Tables.Cast<Table>()
                .Where(t => !IsAutoDateTable(t.Name) && !t.Name.Equals(GoldMeasuresTableName, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(t => NormName(t.Name).Contains("fact"));

            return fact == null ? null : $"COUNTROWS('{fact.Name}')";
        }

       public int ConfigureDateMonthSort(Database db)
{
    int configured = 0;
    foreach (var table in db.Model.Tables.Cast<Table>().Where(t => NormName(t.Name).Contains("date")))
    {
        var monthCol = table.Columns.Cast<Column>()
            .FirstOrDefault(c => NormName(c.Name) == "month");
        if (monthCol == null) continue;

        // ONLY sort by a true 1-12 month number, NEVER by daily datekey or date
        var sortCol = table.Columns.Cast<Column>().FirstOrDefault(c =>
        {
            var n = NormName(c.Name);
            return n is "monthnumber" or "monthnum" or "monthno" or "monthkey";
        });
        if (sortCol == null) continue;

        try
        {
            monthCol.SortByColumn = sortCol;
            configured++;
            _log.LogInformation("[XMLA] Month sort: '{t}'[{m}] by [{s}]",
                table.Name, monthCol.Name, sortCol.Name);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[XMLA] Month sort failed on '{t}'", table.Name);
        }
    }
    return configured;
}

public int ApplyDateSortHints(Database db, IEnumerable<SnowflakeService.DateSortHint> hints){
    int applied = 0;
    foreach (var h in hints)
    {
        var table = db.Model.Tables.Cast<Table>()
            .FirstOrDefault(t => string.Equals(t.Name, h.View, StringComparison.OrdinalIgnoreCase));
        if (table == null) { _log.LogWarning("[XMLA] Sort hint: table '{v}' not found", h.View); continue; }

        var textCol = table.Columns.Cast<Column>()
            .FirstOrDefault(c => string.Equals(c.Name, h.TextColumn, StringComparison.OrdinalIgnoreCase));
        var dateCol = table.Columns.Cast<Column>()
            .FirstOrDefault(c => string.Equals(c.Name, h.DateColumn, StringComparison.OrdinalIgnoreCase));
        if (textCol == null || dateCol == null) continue;

        var dax = h.Part switch
        {
            "MONTH"     => $"MONTH('{table.Name}'[{dateCol.Name}])",
            "QUARTER"   => $"QUARTER('{table.Name}'[{dateCol.Name}])",
            "DAYOFWEEK" => $"WEEKDAY('{table.Name}'[{dateCol.Name}], 2)",
            _ => null
        };
        if (dax == null) continue;

        var keyName = $"{textCol.Name}_SortKey";
        var key = table.Columns.Find(keyName);
        if (key == null)
        {
            key = new CalculatedColumn
            {
                Name = keyName, DataType = DataType.Int64, Expression = dax,
                SummarizeBy = AggregateFunction.None, IsHidden = true
            };
            table.Columns.Add(key);
        }
        try
        {
            textCol.SortByColumn = key;
            applied++;
            _log.LogInformation("[XMLA] '{t}'[{c}] sorted by hidden [{k}] = {dax}", table.Name, textCol.Name, keyName, dax);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[XMLA] Sort hint failed on '{t}'[{c}]", table.Name, textCol.Name); }
    }
    return applied;
}

        public List<UnmappedTable> AddGoldMeasuresTable(
            Database db,
            List<GoldMeasureDef> goldMeasures,
            List<OriginalMeasureSnapshot>? originalMeasures = null)
        {
            var unmapped = new List<UnmappedTable>();
            if (goldMeasures.Count == 0) return unmapped;

            var table = db.Model.Tables.Find(GoldMeasuresTableName);
            if (table == null)
            {
                table = new Table { Name = GoldMeasuresTableName };
                table.Columns.Add(new DataColumn
                {
                    Name         = "__DummyColumn__",
                    SourceColumn = "__DummyColumn__",
                    DataType     = DataType.String,
                    IsHidden     = false
                });

                table.Partitions.Add(new Partition
                {
                    Name   = GoldMeasuresTableName,
                    Source = new MPartitionSource
                    {
                        Expression = "let Source = #table(type table [__DummyColumn__ = Text.Type], {{\"placeholder\"}}) in Source"
                    }
                });

                db.Model.Tables.Add(table);
            }

            var existingMeasureNames = new HashSet<string>(table.Measures.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var gm in goldMeasures)
            {
                var measureName = gm.MeasureName;
                if (existingMeasureNames.Contains(measureName))
                    measureName = $"{measureName} ({gm.SourceView})";

                string? daxExpression = null;
                var orig = originalMeasures?.FirstOrDefault(o =>
                    string.Equals(o.MeasureName, measureName, StringComparison.OrdinalIgnoreCase) ||
                    NormName(o.MeasureName) == NormName(measureName));

                if (orig != null)
                    daxExpression = TryRemapDaxToCurrentModel(db, orig.Expression);

                if (daxExpression == null)
                    daxExpression = TryBuildSumMeasureDax(db, measureName);

                if (daxExpression == null)
                    daxExpression = TryBuildCountMeasureDax(db, measureName);

                if (daxExpression == null)
                    daxExpression = FormatGoldMeasureDaxLiteral(gm.RawValue, gm.SnowflakeDataType);

                table.Measures.Add(new Measure
                {
                    Name       = measureName,
                    Expression = daxExpression!,
                    IsHidden   = false
                });
                existingMeasureNames.Add(measureName);
            }

            var existingDummy = table.Columns.Find("__DummyColumn__");
            if (existingDummy != null)
                table.Columns.Remove(existingDummy);

            return unmapped;
        }

        private static readonly HashSet<string> NumericSnowflakeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "NUMBER", "DECIMAL", "NUMERIC", "INT", "INTEGER", "BIGINT", "SMALLINT",
            "TINYINT", "BYTEINT", "FLOAT", "FLOAT4", "FLOAT8", "DOUBLE", "DOUBLE PRECISION", "REAL"
        };

        internal static string FormatGoldMeasureDaxLiteral(string rawValue, string snowflakeDataType)
        {
            if (rawValue == null) return "BLANK()";

            if (NumericSnowflakeTypes.Contains(snowflakeDataType) &&
                double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var num))
            {
                return num.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return $"\"{rawValue.Replace("\"", "\"\"")}\"";
        }

        public List<UnmappedTable> ReplaceWithSnowflakeViews(
            Database db,
            SnowflakeConfig sfCfg,
            List<SnowflakeViewMeta> sfViews,
            List<VisualBinding> originalBindings)
        {
            var unmapped    = new List<UnmappedTable>();
            var loadedViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var layerPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["GOLD"]   = 0,
                ["SILVER"] = 1,
                ["BRONZE"] = 2
            };

            var viewGroups = sfViews.GroupBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase).ToList();
            var viewLookup = new Dictionary<string, SnowflakeViewMeta>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in viewGroups)
            {
                var distinctSchemas = g.Select(v => v.Schema).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinctSchemas.Count <= 1)
                {
                    viewLookup[g.Key] = g.First();
                    continue;
                }

                var ranked = g.Where(v => layerPriority.ContainsKey(v.Schema))
                              .OrderBy(v => layerPriority[v.Schema])
                              .ToList();

                if (ranked.Count > 0)
                {
                    viewLookup[g.Key] = ranked.First();
                    continue;
                }

                unmapped.Add(new UnmappedTable
                {
                    TableName = g.Key,
                    Reason    = $"'{g.Key}' exists in multiple schemas with no priority winner."
                });
            }

            var tablesToRemove = new List<Table>();
            foreach (Table table in db.Model.Tables)
            {
                if (IsAutoDateTable(table.Name)) continue;

                if (!viewLookup.TryGetValue(table.Name, out var sfView))
                {
                    if (table.Measures.Count > 0) continue;
                    tablesToRemove.Add(table);
                    unmapped.Add(new UnmappedTable { TableName = table.Name, Reason = "No matching Snowflake view." });
                    continue;
                }

                var mExpr = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);
                foreach (Partition partition in table.Partitions)
                {
                    partition.Mode = ModeType.DirectQuery;
                    partition.Source = new MPartitionSource { Expression = mExpr };
                }

                foreach (var c in table.Columns
                    .Where(c => c.Type == ColumnType.Calculated || c.Type == ColumnType.CalculatedTableColumn)
                    .ToList())
                {
                    table.Columns.Remove(c);
                }

                CleanTableMetadata(table);

                var sfColLookup = sfView.Columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
                foreach (Column col in table.Columns)
                {
                    if (col.Type == ColumnType.RowNumber) continue;
                    if (sfColLookup.TryGetValue(col.Name, out var sfCol) && col is DataColumn dc)
                    {
                        dc.SourceColumn = sfCol.Name;
                        dc.SummarizeBy  = AggregateFunction.None;
                    }
                }

                loadedViews.Add(sfView.ViewName);
            }

            foreach (var t in tablesToRemove) db.Model.Tables.Remove(t);
            PurgeDanglingRelationships(db);

            foreach (var sfView in sfViews)
            {
                if (loadedViews.Contains(sfView.ViewName)) continue;

                var newTable = new Table { Name = sfView.ViewName };
                var mExpr    = SnowflakeService.BuildMExpression(sfCfg, sfView.Schema, sfView.ViewName);

                newTable.Partitions.Add(new Partition
                {
                    Name   = sfView.ViewName,
                    Mode   = ModeType.DirectQuery,
                    Source = new MPartitionSource { Expression = mExpr }
                });

                foreach (var sfCol in sfView.Columns)
                {
                    newTable.Columns.Add(new DataColumn
                    {
                        Name         = sfCol.Name,
                        SourceColumn = sfCol.Name,
                        DataType     = MapDataType(sfCol.DataType),
                        SummarizeBy  = AggregateFunction.None
                    });
                }

                db.Model.Tables.Add(newTable);
                loadedViews.Add(sfView.ViewName);
            }

            DisableAutomaticModelBehaviors(db);
            return unmapped;
        }

        private void DisableAutomaticModelBehaviors(Database db)
        {
            db.Model.DiscourageImplicitMeasures = false;

            foreach (Table table in db.Model.Tables)
            {
                if (IsAutoDateTable(table.Name)) continue;
                foreach (Column col in table.Columns)
                {
                    if (col is not DataColumn dc) continue;
                    dc.DataCategory = null;
                }
            }

            RemoveAutoDateTables(db);
        }

        // ─────────────────────────────────────────────────────────────────────
        // AUTO-DATE TABLE PURGE: Fixes ShowAsVariationsOnly error on SaveChanges
        // ─────────────────────────────────────────────────────────────────────
        private void RemoveAutoDateTables(Database db)
        {
            var autoDateTables = db.Model.Tables.Cast<Table>()
                .Where(t => IsAutoDateTable(t.Name))
                .ToList();

            if (autoDateTables.Count == 0) return;

            var autoDateNames = new HashSet<string>(
                autoDateTables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

            // 1. Disable ShowAsVariationsOnly so AS stops enforcing the variation constraint[cite: 8]
            foreach (var t in autoDateTables)
            {
                try { t.ShowAsVariationsOnly = false; } catch { }
            }

            // 2. Strip variations targeting auto date tables[cite: 8]
            foreach (Table table in db.Model.Tables)
            {
                if (IsAutoDateTable(table.Name)) continue;
                foreach (Column col in table.Columns)
                {
                    if (col is not DataColumn dc) continue;
                    foreach (var v in dc.Variations.ToList())
                    {
                        var target = GetVariationTargetTableName(v);
                        if (target != null && autoDateNames.Contains(target))
                            dc.Variations.Remove(v);
                    }
                }
            }

            // 3. Remove relationships involving auto date tables[cite: 8]
            foreach (var r in db.Model.Relationships.Cast<Relationship>().ToList())
            {
                if (r is SingleColumnRelationship scr &&
                    ((scr.FromTable != null && autoDateNames.Contains(scr.FromTable.Name)) ||
                     (scr.ToTable   != null && autoDateNames.Contains(scr.ToTable.Name))))
                {
                    db.Model.Relationships.Remove(r);
                }
            }

            // 4. Drop the auto-date tables[cite: 8]
            foreach (var t in autoDateTables)
                db.Model.Tables.Remove(t);

            _log.LogInformation("[XMLA] Purged {n} auto date/time table(s)", autoDateTables.Count);

            // 5. Turn off automatic time intelligence so Power BI doesn't recreate them
            var existing = db.Model.Annotations.Find("__PBI_TimeIntelligenceEnabled");
            if (existing != null) db.Model.Annotations.Remove(existing);
            db.Model.Annotations.Add(new Annotation
            {
                Name  = "__PBI_TimeIntelligenceEnabled",
                Value = "0"
            });
        }

        private void PurgeDanglingRelationships(Database db)
        {
            var tableNames = new HashSet<string>(
                db.Model.Tables.Cast<Table>().Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            var toRemove = db.Model.Relationships
                .Cast<Relationship>()
                .Where(r =>
                {
                    if (r is SingleColumnRelationship scr)
                        return !tableNames.Contains(scr.FromTable?.Name ?? "") ||
                               !tableNames.Contains(scr.ToTable?.Name ?? "");
                    return false;
                })
                .ToList();

            foreach (var r in toRemove)
                db.Model.Relationships.Remove(r);
        }

        private void CleanTableMetadata(Table table)
        {
            foreach (var h in table.Hierarchies.ToList())
                table.Hierarchies.Remove(h);

            foreach (Column col in table.Columns)
            {
                if (col is DataColumn dc)
                {
                    foreach (var v in dc.Variations.ToList())
                    {
                        var targetTableName = GetVariationTargetTableName(v);
                        if (IsAutoDateTable(targetTableName)) continue;
                        dc.Variations.Remove(v);
                    }
                }
            }
        }

        private static string? GetVariationTargetTableName(Variation v)
        {
            try
            {
                if (v.Relationship is SingleColumnRelationship scr && scr.ToTable != null)
                    return scr.ToTable.Name;
            }
            catch { }

            try
            {
                return v.DefaultHierarchy?.Table?.Name;
            }
            catch
            {
                return null;
            }
        }

        public List<string> SaveStructureOnly(Database db)
        {
            var warnings = new List<string>();
            const string DummyColName = "__DummyColumn__";

            // Purge auto-date tables prior to save so no dangling variation targets trigger an error[cite: 8]
            RemoveAutoDateTables(db);

            foreach (Table table in db.Model.Tables)
            {
                if (IsAutoDateTable(table.Name)) continue;
                CleanTableMetadata(table);
            }

            foreach (Table table in db.Model.Tables)
            {
                if (IsAutoDateTable(table.Name)) continue;

                bool hasDataColumn = table.Columns
                    .Any(c => c.Type == ColumnType.Data && !c.IsHidden);

                if (!hasDataColumn && table.Columns.Find(DummyColName) == null)
                {
                    table.Columns.Add(new DataColumn
                    {
                        Name         = DummyColName,
                        DataType     = DataType.String,
                        IsHidden     = true,
                        SourceColumn = DummyColName
                    });
                }
            }

            try
            {
                db.Model.SaveChanges();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Transaction 1/Structure] Bulk save failed — falling back to per-table save.");
                foreach (Table table in db.Model.Tables)
                {
                    if (IsAutoDateTable(table.Name)) continue;
                    try { db.Model.SaveChanges(); }
                    catch (Exception tex)
                    {
                        warnings.Add($"'{table.Name}' failed to save structure: {tex.Message}");
                    }
                }
            }

            return warnings;
        }

        private static bool IsDirectQueryOnlyTable(Table table)
        {
            var partitions = table.Partitions.Cast<Partition>().ToList();
            return partitions.Count > 0 && partitions.All(p => p.Mode == ModeType.DirectQuery);
        }

        public List<string> RequestFullRefreshAndSave(Database db)
        {
            var warnings = new List<string>();

            foreach (Table table in db.Model.Tables)
            {
                if (IsAutoDateTable(table.Name)) continue;
                if (IsDirectQueryOnlyTable(table)) continue;

                table.RequestRefresh(RefreshType.Full);
            }

            try
            {
                db.Model.SaveChanges();
            }
            catch (Exception ex)
            {
                foreach (Table table in db.Model.Tables)
                {
                    if (IsAutoDateTable(table.Name)) continue;
                    if (IsDirectQueryOnlyTable(table)) continue;

                    try
                    {
                        table.RequestRefresh(RefreshType.Full);
                        db.Model.SaveChanges();
                    }
                    catch (Exception tex)
                    {
                        warnings.Add($"'{table.Name}' failed to refresh: {tex.Message}");
                    }
                }
            }

            return warnings;
        }

        // ─────────────────────────────────────────────────────────────────────
        // READY WAIT: Skips DirectQuery tables so polling does not time out[cite: 9, 11, 12]
        // ─────────────────────────────────────────────────────────────────────
        public async Task WaitForModelReadyAsync(
            Server server,
            Database db,
            TimeSpan? timeout = null,
            TimeSpan? pollInterval = null)
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
            var effectivePollGap = pollInterval ?? TimeSpan.FromSeconds(2);
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < effectiveTimeout)
            {
                server.Refresh();
                db.Refresh();

                var tables = db.Model.Tables
                    .Cast<Table>()
                    .Where(t => !IsAutoDateTable(t.Name))
                    .ToList();

                var partitionEntries = tables
                    .SelectMany(t => t.Partitions.Cast<Partition>().Select(p => (Table: t, Partition: p)))
                    .ToList();

                // DirectQuery partitions query live and remain Unprocessed locally — skip them[cite: 9]
                var nonDqPartitions = partitionEntries
                    .Where(x => x.Partition.Mode != ModeType.DirectQuery)
                    .ToList();

                // If all partitions are DirectQuery, the model is queryable immediately[cite: 9]
                if (nonDqPartitions.Count == 0)
                {
                    _log.LogInformation("[WaitForModelReady] All tables are DirectQuery — proceeding immediately.");
                    return;
                }

                var erroredPartitions = nonDqPartitions
                    .Where(x => x.Partition.State == ObjectState.SemanticError
                             || x.Partition.State == ObjectState.EvaluationError
                             || x.Partition.State == ObjectState.DependencyError)
                    .Select(x => $"{x.Table.Name}={x.Partition.State}")
                    .ToList();

                if (erroredPartitions.Count > 0)
                {
                    throw new InvalidOperationException($"Model rebuild error: {string.Join(", ", erroredPartitions)}");
                }

                var notReadyPartitions = nonDqPartitions
                    .Where(x => x.Partition.State != ObjectState.Ready && x.Partition.State != ObjectState.NoData)
                    .ToList();

                if (notReadyPartitions.Count == 0)
                {
                    _log.LogInformation("[WaitForModelReady] Non-DQ partitions Ready after {elapsed}ms.", sw.ElapsedMilliseconds);
                    return;
                }

                await Task.Delay(effectivePollGap);
            }

            _log.LogInformation("[WaitForModelReady] DirectQuery model ready window elapsed — proceeding.");
        }

        public async Task<ModelFinalizeResult> FinalizeAndProcessModelAsync(
            Server server,
            Database db,
            Func<Task>? betweenStructureAndRefresh = null,
            bool requestFullRefresh = true,
            TimeSpan? readyTimeout = null,
            TimeSpan? readyPollInterval = null)
        {
            var result = new ModelFinalizeResult();
            var sw = Stopwatch.StartNew();

            try
            {
                result.StructureWarnings.AddRange(SaveStructureOnly(db));
                result.Status = ModelFinalizeStatus.StructureSaved;

                if (betweenStructureAndRefresh != null)
                {
                    await betweenStructureAndRefresh();
                }

                if (requestFullRefresh)
                {
                    result.RefreshWarnings.AddRange(RequestFullRefreshAndSave(db));
                    result.Status = ModelFinalizeStatus.Processing;
                }

                await WaitForModelReadyAsync(server, db, readyTimeout, readyPollInterval);

                result.Success = true;
                result.Status  = ModelFinalizeStatus.Ready;
            }
            catch (TimeoutException ex)
            {
                result.Success      = false;
                result.Status       = ModelFinalizeStatus.TimedOut;
                result.ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                result.Success      = false;
                result.Status       = ModelFinalizeStatus.Failed;
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                result.Elapsed = sw.Elapsed;
            }

            return result;
        }

        public List<string> SaveChanges(Database db)
        {
            var warnings = new List<string>();
            const string DummyColName = "__DummyColumn__";

            RemoveAutoDateTables(db);

            foreach (Table table in db.Model.Tables)
            {
                if (IsAutoDateTable(table.Name)) continue;
                CleanTableMetadata(table);

                bool hasDataColumn = table.Columns.Any(c => c.Type == ColumnType.Data && !c.IsHidden);
                if (!hasDataColumn && table.Columns.Find(DummyColName) == null)
                {
                    table.Columns.Add(new DataColumn
                    {
                        Name         = DummyColName,
                        DataType     = DataType.String,
                        IsHidden     = true,
                        SourceColumn = DummyColName
                    });
                }
            }

            foreach (Table table in db.Model.Tables)
            {
                if (IsAutoDateTable(table.Name)) continue;
                if (IsDirectQueryOnlyTable(table)) continue;
                table.RequestRefresh(RefreshType.Full);
            }

            try
            {
                db.Model.SaveChanges();
            }
            catch (Exception ex)
            {
                foreach (Table table in db.Model.Tables)
                {
                    if (IsAutoDateTable(table.Name)) continue;
                    if (IsDirectQueryOnlyTable(table)) continue;
                    try
                    {
                        table.RequestRefresh(RefreshType.Full);
                        db.Model.SaveChanges();
                    }
                    catch (Exception tex)
                    {
                        warnings.Add($"'{table.Name}' failed to refresh: {tex.Message}");
                    }
                }
            }

            return warnings;
        }

        public async Task RepairAfterImportAsync(PowerBIConfig cfg, string datasetId, string? datasetNameForLog = null)
        {
            if (string.IsNullOrWhiteSpace(cfg.XmlaEndpoint)) return;

            var accessToken = await GetAccessTokenAsync(cfg);
            var server = new Server();
            var connStr =
                $"Provider=MSOLAP;Data Source={cfg.XmlaEndpoint};" +
                $"Password={accessToken};" +
                "Persist Security Info=True;Impersonation Level=Impersonate";

            try
            {
                server.Connect(connStr);
                var db = server.Databases.Cast<Database>()
                    .FirstOrDefault(d => string.Equals(d.ID, datasetId, StringComparison.OrdinalIgnoreCase));
                if (db != null)
                {
                    SaveChanges(db);
                }
            }
            catch { }
            finally
            {
                try { server.Disconnect(); } catch { }
                server.Dispose();
            }
        }

        public void Disconnect(Server server)
        {
            try { server.Disconnect(); } catch { }
            server.Dispose();
        }

        public int EnsureRelationshipsByKeyColumns(Database db)
        {
            var tables = db.Model.Tables.Cast<Table>()
                .Where(t => !IsAutoDateTable(t.Name))
                .Where(t => !t.Name.Equals("_Measures", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var factTables = tables.Where(t => NormName(t.Name).Contains("fact")).ToList();
            var dimTables  = tables.Where(t => NormName(t.Name).Contains("dim")).ToList();

            if (factTables.Count == 0 || dimTables.Count == 0) return 0;

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in db.Model.Relationships.OfType<SingleColumnRelationship>())
            {
                existing.Add($"{r.FromTable.Name}.{r.FromColumn.Name}->{r.ToTable.Name}.{r.ToColumn.Name}");
                existing.Add($"{r.ToTable.Name}.{r.ToColumn.Name}->{r.FromTable.Name}.{r.FromColumn.Name}");
            }

            int created = 0;

            foreach (var fact in factTables)
            {
                foreach (var factCol in fact.Columns.Cast<Column>())
                {
                    if (factCol.Type == ColumnType.RowNumber) continue;

                    var normFactCol = NormName(factCol.Name);
                    bool looksLikeKey =
                        normFactCol.EndsWith("key") ||
                        (normFactCol.EndsWith("id") && normFactCol.Length > 2);
                    if (!looksLikeKey) continue;

                    foreach (var dim in dimTables)
                    {
                        var dimCol = dim.Columns.Cast<Column>()
                            .FirstOrDefault(c =>
                                c.Type != ColumnType.RowNumber &&
                                NormName(c.Name) == normFactCol);

                        if (dimCol == null) continue;

                        var key = $"{fact.Name}.{factCol.Name}->{dim.Name}.{dimCol.Name}";
                        if (existing.Contains(key)) continue;

                        try
                        {
                            var rel = new SingleColumnRelationship
                            {
                                Name                   = $"{dim.Name}_{dimCol.Name}_{fact.Name}_{factCol.Name}",
                                FromColumn             = factCol,
                                ToColumn               = dimCol,
                                FromCardinality        = RelationshipEndCardinality.Many,
                                ToCardinality          = RelationshipEndCardinality.One,
                                CrossFilteringBehavior = CrossFilteringBehavior.OneDirection,
                                IsActive               = true
                            };
                            db.Model.Relationships.Add(rel);
                            existing.Add(key);
                            created++;
                        }
                        catch { }
                    }
                }
            }

            return created;
        }

        private string? TryBuildSumMeasureDax(Database db, string measureName)
        {
            var normMeasure = NormName(measureName);
            if (string.IsNullOrEmpty(normMeasure)) return null;

            foreach (var prefix in new[] { "total", "sum", "avg", "average", "count", "kpi" })
            {
                if (normMeasure.StartsWith(prefix) && normMeasure.Length > prefix.Length)
                {
                    normMeasure = normMeasure[prefix.Length..];
                    break;
                }
            }

            if (string.IsNullOrEmpty(normMeasure)) return null;

            var tables = db.Model.Tables.Cast<Table>()
                .Where(t => !IsAutoDateTable(t.Name) && !t.Name.Equals(GoldMeasuresTableName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => NormName(t.Name).Contains("fact") ? 5 : (NormName(t.Name).Contains("dim") ? -5 : 0))
                .ToList();

            foreach (var table in tables)
            {
                foreach (var col in table.Columns.Cast<Column>())
                {
                    if (col.Type == ColumnType.RowNumber) continue;

                    var normCol = NormName(col.Name);
                    if (normCol != normMeasure) continue;

                    if (col.DataType != DataType.Double &&
                        col.DataType != DataType.Decimal &&
                        col.DataType != DataType.Int64)
                    {
                        continue;
                    }

                    return $"SUM('{table.Name}'[{col.Name}])";
                }
            }

            return null;
        }

        private static DataType MapDataType(string sfType) => sfType?.ToUpperInvariant() switch
        {
            "NUMBER" or "DECIMAL" or "NUMERIC" or "FLOAT" or "DOUBLE" or "REAL" => DataType.Double,
            "INTEGER" or "INT" or "BIGINT" or "SMALLINT" or "TINYINT"             => DataType.Int64,
            "BOOLEAN"                                                             => DataType.Boolean,
            "DATE" or "DATETIME" or "TIMESTAMP" or "TIMESTAMP_NTZ"
                or "TIMESTAMP_LTZ" or "TIMESTAMP_TZ"                             => DataType.DateTime,
            _ => DataType.String
        };
    }

    public enum ModelFinalizeStatus
    {
        NotStarted,
        StructureSaved,
        Processing,
        Ready,
        Failed,
        TimedOut
    }

    public class ModelFinalizeResult
    {
        public bool Success { get; set; }
        public ModelFinalizeStatus Status { get; set; } = ModelFinalizeStatus.NotStarted;
        public string? ErrorMessage { get; set; }
        public TimeSpan Elapsed { get; set; }
        public List<string> StructureWarnings { get; set; } = new();
        public List<string> RefreshWarnings { get; set; } = new();
        public List<string> AllWarnings => StructureWarnings.Concat(RefreshWarnings).ToList();
    }

    public class PbixTableSchema
    {
        public string TableName { get; set; } = "";
        public List<ColumnMeta> Columns { get; set; } = new();
        public int MeasuresExcluded { get; set; }
        public int CalculatedColumnsExcluded { get; set; }
    }
}