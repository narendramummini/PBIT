using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging;
using PbixMigrator.Models;
using PbixMigrator.Services;

namespace PbixMigrator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MigrationController : ControllerBase
    {
        private readonly ILogger<MigrationController> _log;
        private readonly SessionStore        _sessions;
        private readonly SnowflakeService    _snowflake;
        private readonly PbixService         _pbix;
        private readonly PowerBIService      _powerbi;
        private readonly TabularModelService _tabular;
        private readonly FabricSnowflakeXmlaMigrator _legacyMigrator;
        private readonly FabricLakehouseService _lakehouse;

        public MigrationController(
            ILogger<MigrationController> log,
            SessionStore sessions, SnowflakeService snowflake,
            PbixService pbix, PowerBIService powerbi, TabularModelService tabular,
            FabricSnowflakeXmlaMigrator legacyMigrator, FabricLakehouseService lakehouse)
        {
            _log = log; _sessions = sessions; _snowflake = snowflake;
            _pbix = pbix; _powerbi = powerbi; _tabular = tabular;
            _legacyMigrator = legacyMigrator; _lakehouse = lakehouse;
        }

        // Dumps any object as compact JSON for logging. Never throws — falls
        // back to ToString() if serialization fails, so a logging call can
        // never itself crash a request.
        private static string Dump(object? o)
        {
            if (o == null) return "null";
            try
            {
                return System.Text.Json.JsonSerializer.Serialize(o,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            }
            catch { return o.ToString() ?? "?"; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // STEP 1: Upload PBIX → publish to Power BI → extract FULL visual
        //         metadata (which visual is on which page, which table.column
        //         is bound to which data role in each visual) → return all of
        //         that alongside reportId + schemas.
        // ─────────────────────────────────────────────────────────────────────

    
        [HttpPost("init")]
        [RequestSizeLimit(500_000_000)]
        public async Task<IActionResult> InitSession(
            [FromForm] string snowflakeConfigJson,
            [FromForm] string powerBIConfigJson,
            [FromForm] IFormFile? pbixFile)
        {
            _log.LogInformation("[/init] ▶ request received — file={file} ({size} bytes)",
                pbixFile?.FileName, pbixFile?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(snowflakeConfigJson))
                return BadRequest(new { error = "snowflakeConfigJson is required" });
            if (string.IsNullOrWhiteSpace(powerBIConfigJson))
                return BadRequest(new { error = "powerBIConfigJson is required" });
            if (pbixFile == null || pbixFile.Length == 0)
                return BadRequest(new { error = "PBIX file is required" });

            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            SnowflakeConfig sfCfg;
            PowerBIConfig   pbiCfg;
            try
            {
                sfCfg  = System.Text.Json.JsonSerializer.Deserialize<SnowflakeConfig>(snowflakeConfigJson, opts)
                         ?? throw new Exception("null");
                pbiCfg = System.Text.Json.JsonSerializer.Deserialize<PowerBIConfig>(powerBIConfigJson, opts)
                         ?? throw new Exception("null");
            }
            catch (Exception ex) { _log.LogError(ex, "[/init] ✘ config parse failed"); return BadRequest(new { error = "Invalid config JSON: " + ex.Message }); }

            _log.LogInformation("[/init] Configs parsed OK — Snowflake account={acct} db={db} | PBI tenant={tenant} workspace={ws}",
                sfCfg.Account, sfCfg.Database, pbiCfg.TenantId, pbiCfg.WorkspaceId);

            byte[] pbixBytes;
            try
            {
                using var ms = new MemoryStream();
                await pbixFile.CopyToAsync(ms);
                pbixBytes = ms.ToArray();
                _log.LogInformation("[/init] PBIX read into memory — {bytes} bytes", pbixBytes.Length);
            }
            catch (Exception ex) { _log.LogError(ex, "[/init] ✘ Failed to read PBIX bytes"); return StatusCode(500, new { error = "Failed to read PBIX: " + ex.Message }); }

            // Validate Snowflake PAT and fetch schemas
            List<string> schemas;
            try
            {
                schemas = await _snowflake.GetSchemasAsync(sfCfg);
                _log.LogInformation("[/init] Snowflake schemas fetched — {n}: {list}", schemas.Count, Dump(schemas));
            }
            catch (InvalidOperationException ex) { _log.LogWarning(ex, "[/init] ✘ Snowflake auth/config rejected"); return StatusCode(400, new { error = ex.Message }); }
            catch (Exception ex)                 { _log.LogError(ex, "[/init] ✘ Snowflake error"); return StatusCode(500, new { error = "Snowflake error: " + ex.Message }); }

            var fileName = Path.GetFileNameWithoutExtension(pbixFile.FileName);

            // Publish raw PBIX to Power BI workspace
            string reportId, datasetId;
            try
            {
                _log.LogInformation("[/init] Publishing PBIX '{file}' to Power BI workspace {ws}…", fileName, pbiCfg.WorkspaceId);
                (reportId, datasetId) = await _powerbi.ImportPbixAsync(pbiCfg, pbixBytes, fileName);
                _log.LogInformation("[/init] Power BI publish OK — reportId={r} datasetId={d}", reportId, datasetId);
            }
            catch (PowerBiLabelBlockedException ex)
            {
                // Distinct from a generic publish failure: this is a known,
                // actionable condition (protected sensitivity label + SP auth),
                // not an unexplained backend error — 422 signals "fix the input",
                // not "the service is broken".
                _log.LogWarning(ex, "[/init] Power BI publish blocked by sensitivity label");
                return StatusCode(422, new { error = ex.Message, reason = "sensitivity_label_blocked" });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Power BI publish failed");
                return StatusCode(502, new { error = "Power BI publish failed: " + ex.Message });
            }

            // Immediately repair the model via XMLA — regardless of whether the
            // import completed cleanly or was recovered after a reported
            // "Failed" status (see PowerBIService.ImportPbixAsync), the model
            // may still contain measure-only/columnless tables that can't be
            // refreshed. Fixing them now means everything downstream (XMLA
            // extraction, Snowflake rebuild) starts from a healthy model.
            try
            {
                _log.LogInformation("[/init] Running post-import XMLA repair pass on dataset id '{id}' ('{ds}')…", datasetId, fileName);
                await _tabular.RepairAfterImportAsync(pbiCfg, datasetId, fileName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[/init] Post-import XMLA repair pass failed — continuing anyway.");
            }

            // ── Extract full visual-level metadata ────────────────────────────
            // PRIMARY path: PBIX layout JSON. This is the ONLY source that gives
            // us real visual identifiers (e.g. "VisualContainer3") grouped with
            // ALL of their bound columns/roles together — because that's how
            // Power BI actually names and structures visuals in the report layout.
            //
            // SECONDARY/fallback path: XMLA. The TOM API has no concept of
            // "visuals" at all — it only knows tables/columns. So this path
            // synthesizes ONE fake VisualMetadata entry per (Table, Column) pair
            // named "model::<TableName>". Those names will NEVER match a real
            // visual name like "VisualContainer3" sent by the frontend from the
            // live embedded report — so this path only exists as a last resort
            // (e.g. layout JSON is missing/corrupt) and is knowingly weaker.
            List<VisualMetadata> visualMetadata = new();
            List<VisualBinding>  visualBindings = new();   // fallback flat list

            try
            {
                _log.LogInformation("[/init] Parsing PBIX layout JSON to extract real per-visual metadata…");
                var layout = await _pbix.ExtractLayoutAsync(pbixBytes);
                var refs   = _pbix.ExtractFieldReferences(layout);

                visualMetadata = _pbix.ExtractVisualMetadata(layout);
                visualBindings = refs
                    .Select(r => new VisualBinding { OriginalTable = r.Table, OriginalColumn = r.Column })
                    .ToList();

                _log.LogInformation(
                    "[/init] Layout-JSON extraction OK — {vm} visuals (real VisualContainer names), {vb} bindings: {list}",
                    visualMetadata.Count, visualBindings.Count, Dump(visualBindings));

                if (visualMetadata.Count == 0)
                    _log.LogWarning("[/init] Layout JSON parsed but yielded ZERO visuals — will try XMLA fallback next.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[/init] PBIX layout extraction failed — falling back to XMLA");
            }

            if (visualMetadata.Count == 0)
            {
                try
                {
                    _log.LogInformation("[/init] Connecting via XMLA to extract model-level metadata (fallback — " +
                        "these VisualNames are 'model::<Table>' synthetic IDs, NOT real visual names)…");
                    var (server, db, extracted) =
                        await _tabular.ConnectAndExtractVisualMetadataAsync(pbiCfg, datasetId, fileName);
                    visualMetadata = extracted;
                    _tabular.Disconnect(server);

                    visualBindings = visualMetadata
                        .SelectMany(v => v.BoundColumns)
                        .Select(b => new VisualBinding { OriginalTable = b.Table, OriginalColumn = b.Column })
                        .Distinct(new VisualBindingComparer())
                        .ToList();

                    _log.LogInformation(
                        "[/init] XMLA fallback OK — {vm} synthetic visuals, {vb} distinct bindings: {list}",
                        visualMetadata.Count, visualBindings.Count, Dump(visualBindings));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[/init] XMLA fallback ALSO failed — visual metadata will be EMPTY. " +
                        "Every visual will subsequently be classified 'unbound' by the frontend triage.");
                }
            }

            var session = _sessions.Create();
            session.SnowflakeConfig  = sfCfg;
            session.PowerBIConfig    = pbiCfg;
            session.PbixBytes        = pbixBytes;
            session.PbixFileName     = fileName;
            session.AvailableSchemas = schemas;
            session.ReportId         = reportId;
            session.DatasetId        = datasetId;
            session.DatasetName      = fileName;
            session.VisualMetadata   = visualMetadata;
            session.VisualBindings   = visualBindings;
            session.Status           = "awaiting_schema_selection";
            _sessions.Update(session);

            _log.LogInformation(
                "Session {id}: published → reportId={r} datasetId={d} visuals={v} bindings={b}",
                session.SessionId, reportId, datasetId, visualMetadata.Count, visualBindings.Count);

            return Ok(new PublishResponse
            {
                SessionId      = session.SessionId,
                ReportId       = reportId,
                DatasetId      = datasetId,
                DatasetName    = fileName,
                Schemas        = schemas,
                VisualBindings = visualBindings,
                VisualMetadata = visualMetadata,
                Status         = session.Status
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // STEP 2: Schema selection → load Snowflake views → replace dataset
        //         via XMLA → generate embed token.
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost("select-schemas")]
        public async Task<IActionResult> SelectSchemas([FromBody] SchemaSelectionRequest req)
        {
            _log.LogInformation("[/select-schemas] ▶ request — sessionId={id} schemas={list}",
                req.SessionId, Dump(req.SelectedSchemas));

            if (string.IsNullOrWhiteSpace(req.SessionId))
                return BadRequest(new { error = "sessionId is required" });

            MigrationSession session;
            try   { session = _sessions.GetOrThrow(req.SessionId); }
            catch { return NotFound(new { error = "Session not found" }); }

            if (req.SelectedSchemas == null || req.SelectedSchemas.Count == 0)
                return BadRequest(new { error = "Select at least one schema" });

            session.SnowflakeConfig.SelectedSchemas = req.SelectedSchemas;
            session.Status = "loading_views";
            _sessions.Update(session);

            // Collected across the whole request — schema-validation skips,
            // dataset rebuild issues, and SaveChanges warnings all land here
            // so the frontend gets one consistent "here's what didn't make
            // it, and why" list instead of a hard failure on the first problem.
            List<UnmappedTable> unmapped = new();

            // 1. Fetch all views from selected Snowflake schemas — broken/
            // invalid views are skipped (not thrown) by ProcessAndFilterSchemas
            // inside GetViewMetadataAsync; only the ones that pass validation
            // continue on to the dataset rebuild below.
            List<SnowflakeViewMeta> sfViews;
            try
            {
                var (validViews, skippedViews) = await _snowflake.GetViewMetadataAsync(session.SnowflakeConfig, req.SelectedSchemas);
                sfViews = validViews;

                foreach (var skipped in skippedViews)
                    unmapped.Add(skipped);

                _log.LogInformation("[/select-schemas][{id}] Fetched {n} valid Snowflake view(s), {skipped} skipped: {list}",
                    session.SessionId, sfViews.Count, skippedViews.Count,
                    Dump(sfViews.Select(v => new { v.ViewName, v.Schema, ColumnCount = v.Columns.Count })));
            }
            catch (Exception ex) { _log.LogError(ex, "[/select-schemas][{id}] Snowflake metadata fetch failed", session.SessionId); return StatusCode(500, new { error = "Snowflake metadata error: " + ex.Message }); }

            session.SnowflakeViews = sfViews;
            session.Status = "rebuilding_dataset";
            _sessions.Update(session);

            // 2. Replace dataset via XMLA with all Snowflake views
            try
            {
                _log.LogInformation("[/select-schemas][{id}] Reconnecting via XMLA to rebuild dataset id '{did}' ('{ds}')…",
                    session.SessionId, session.DatasetId, session.DatasetName);
                // var (server, db, _) = await _tabular.ConnectAndExtractVisualMetadataAsync(
                //     session.PowerBIConfig, session.DatasetId!, session.DatasetName);
                // try
                // {
                //     // Capture the pre-existing (original PBIX) table names
                //     // before we touch anything.
                //     var originalTableNames = db.Model.Tables
                //         .Cast<Table>()
                //         .Select(t => t.Name)
                //         .Where(n => !TabularModelService.IsAutoDateTable(n))
                //         .ToList();

                var (server, db, _) = await _tabular.ConnectAndExtractVisualMetadataAsync(
    session.PowerBIConfig, session.DatasetId!, session.DatasetName);
try
{
    // ★ Capture original DAX BEFORE tables/measures are removed
    var originalMeasures = _tabular.CaptureOriginalMeasures(db);
    _log.LogInformation(
        "[/select-schemas][{id}] Captured {n} original measure(s) before rebuild",
        session.SessionId, originalMeasures.Count);

    // Capture the pre-existing (original PBIX) table names
    // before we touch anything.
    var originalTableNames = db.Model.Tables
        .Cast<Table>()
        .Select(t => t.Name)
        .Where(n => !TabularModelService.IsAutoDateTable(n))
        .ToList();

                    var saveWarnings = new List<string>();

                    // ── Step 1: add a placeholder table (dummy column) so the
                    // model is NEVER saved with zero tables. Step 2: wipe every
                    // original PBIX table except the placeholder. Step 3: save
                    // — the live dataset now has exactly one (harmless, self-
                    // contained) table instead of none.
                    // This is SAVE 1 of 2 for this rebuild — the second is
                    // after the real Lakehouse tables are loaded, below.
                    _log.LogInformation("[/select-schemas][{id}] Adding placeholder table, then removing {n} original table(s)…",
                        session.SessionId, originalTableNames.Count);
                    _tabular.AddPlaceholderTable(db);
                    _tabular.RemoveOriginalTables(db, originalTableNames);
                    _tabular.RequestPlaceholderRefresh(db);
                    session.OriginalTableNames = new List<string>();
                    var save1Warnings = _tabular.SaveStructureOnly(db);
                    saveWarnings.AddRange(save1Warnings);
                    _log.LogInformation("[/select-schemas][{id}] SAVE 1/2 done — dataset now holds only the placeholder table",
                        session.SessionId);

                    // If the original tables didn't actually come off the
                    // server here, don't go on to load the Lakehouse tables
                    // and request a full refresh — that just cascades into
                    // "failed to refresh"/DMTS_DatasourceHasNoCredentialError
                    // noise for tables that are still bound to the original
                    // PBIX's (often local-file) data source, which a service
                    // principal usually can't ever get credentials for
                    // anyway. Surface the real structural error instead.
                    if (save1Warnings.Count > 0)
                    {
                        var msg = "Failed to remove the original table(s) before loading the Lakehouse tables — " +
                                   "aborting before touching data. Structural error(s): " + string.Join(" | ", save1Warnings);
                        _log.LogError("[/select-schemas][{id}] {msg}", session.SessionId, msg);
                        session.Status       = "awaiting_schema_selection";
                        session.ErrorMessage = msg;
                        _sessions.Update(session);
                        return StatusCode(500, new { error = msg });
                    }

                    // ── Land each selected Snowflake view in the Fabric
                    // Lakehouse, then bind Power BI to the Lakehouse's SQL
                    // Analytics Endpoint — not to Snowflake directly.
                    // Snowflake auth (PAT) is only ever used here, inside
                    // this backend; Power BI never sees a Snowflake
                    // connection at all with this path.
                    _log.LogInformation("[/select-schemas][{id}] Ensuring Lakehouse '{lh}' in workspace {ws}…",
                        session.SessionId, session.PowerBIConfig.LakehouseName, session.PowerBIConfig.WorkspaceId);
                    session.LakehouseId = await _lakehouse.EnsureLakehouseAsync(
                        session.PowerBIConfig, session.PowerBIConfig.WorkspaceId, session.PowerBIConfig.LakehouseName);

                    var sqlEndpoint = await _lakehouse.GetSqlEndpointAsync(
                        session.PowerBIConfig, session.PowerBIConfig.WorkspaceId, session.LakehouseId,
                        session.PowerBIConfig.LakehouseName);
                    session.LakehouseSqlServer   = sqlEndpoint.Server;
                    session.LakehouseSqlDatabase = sqlEndpoint.Database;
                    // BindLakehouseDatasourceCredentialsAsync below only takes
                    // a PowerBIConfig, not the session — mirror the resolved
                    // server onto cfg so its datasource-matching filter has
                    // something to match against (see PowerBIService.
                    // BindLakehouseDatasourceCredentialsAsync / LooksLikeLakehouseSql).
                    session.PowerBIConfig.LakehouseSqlServer = sqlEndpoint.Server;
                    _log.LogInformation("[/select-schemas][{id}] Lakehouse SQL Analytics Endpoint ready — server={server} database={db}",
                        session.SessionId, sqlEndpoint.Server, sqlEndpoint.Database);

                    // Loads each selected Snowflake view straight into the
                    // Lakehouse via the CSV/"Load Table" REST path (no Spark
                    // job involved) — one view at a time, so a failure on
                    // any single view is caught and skipped without stopping
                    // the rest. Every view that loads successfully is added
                    // to lakehouseTables below and pushed into the dataset.
                    //
                    // GOLD is the one exception: those views are business
                    // KPIs, each returning a single scalar row (see the
                    // POWERBI_ACCELERATOR_DB.GOLD.KPI_* views), not row-level
                    // data worth a physical Lakehouse table of their own.
                    // They're pulled out here and never sent to
                    // LoadViewIntoLakehouseAsync — instead every Gold view's
                    // single column/row becomes one constant DAX measure on
                    // a single shared "_Measures" table, built further down
                    // via AddGoldMeasuresTable. Every other selected schema
                    // (Bronze, Silver, custom) is completely unaffected and
                    // still lands as its own physical Lakehouse table below.
                    var goldViews    = sfViews.Where(v => string.Equals(v.Schema, "GOLD", StringComparison.OrdinalIgnoreCase)).ToList();
                    var nonGoldViews = sfViews.Where(v => !string.Equals(v.Schema, "GOLD", StringComparison.OrdinalIgnoreCase)).ToList();

                    _log.LogInformation("[/select-schemas][{id}] Loading {n} non-Gold Snowflake view(s) into the Lakehouse ({g} Gold view(s) routed to the measures table instead)…",
                        session.SessionId, nonGoldViews.Count, goldViews.Count);
                    var lakehouseTables = new List<LakehouseTableMeta>();
                    foreach (var sfView in nonGoldViews)
                    {
                        try
                        {
                            var loaded = await _lakehouse.LoadViewIntoLakehouseAsync(
                                session.PowerBIConfig, session.SnowflakeConfig, _snowflake,
                                session.PowerBIConfig.WorkspaceId, session.LakehouseId, sfView);
                            lakehouseTables.Add(loaded);
                            _log.LogInformation("[/select-schemas][{id}] Loaded '{s}.{v}' → table '{t}' ({rows} row(s))",
                                session.SessionId, sfView.Schema, sfView.ViewName, loaded.TableName, loaded.RowCount);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "[/select-schemas][{id}] Failed to load '{s}.{v}' into the Lakehouse — skipping this view.",
                                session.SessionId, sfView.Schema, sfView.ViewName);
                            unmapped.Add(new UnmappedTable
                            {
                                TableName = $"{sfView.Schema}.{sfView.ViewName}",
                                Reason    = "Failed to load into Lakehouse: " + ex.Message
                            });
                        }
                    }
                    _log.LogInformation("[/select-schemas][{id}] Lakehouse load done — {n} view(s) loaded, {skipped} skipped.",
                        session.SessionId, lakehouseTables.Count, nonGoldViews.Count - lakehouseTables.Count);

                    var boundUnmapped = _tabular.UpdateLakehouseTablesInPlace(
                        db, session.LakehouseSqlServer!, session.LakehouseSqlDatabase!, lakehouseTables);
                    foreach (var u in boundUnmapped) unmapped.Add(u);
                   

                    // ── Gold KPIs → one shared "_Measures" table ─────────────
                    // Every Gold view is expected to return exactly one row
                    // (one KPI). Its column name becomes the measure name and
                    // its row-1 value becomes that measure's constant DAX
                    // expression — MeasureName = ConstantValue. A Gold view
                    // that comes back with 0 rows or more than 1 row can't be
                    // turned into a constant this way, so it's skipped and
                    // reported rather than guessed at.
                    if (goldViews.Count > 0)
                    {
                        _log.LogInformation("[/select-schemas][{id}] Building '_Measures' table from {n} Gold view(s)…",
                            session.SessionId, goldViews.Count);
                        var goldMeasures = new List<GoldMeasureDef>();
                        foreach (var sfView in goldViews)
                        {
                            try
                            {
                                var (cols, rows) = await _snowflake.GetViewRowsAsync(
                                    session.SnowflakeConfig, sfView.Schema, sfView.ViewName);

                                if (rows.Count != 1)
                                {
                                    _log.LogWarning("[/select-schemas][{id}] Gold view '{s}.{v}' returned {n} row(s), expected exactly 1 — skipping.",
                                        session.SessionId, sfView.Schema, sfView.ViewName, rows.Count);
                                    unmapped.Add(new UnmappedTable
                                    {
                                        TableName = $"{sfView.Schema}.{sfView.ViewName}",
                                        Reason    = $"Gold view returned {rows.Count} row(s) — a Gold KPI view must return exactly one row to become a constant measure. No measure was created for this view."
                                    });
                                    continue;
                                }

                                for (int i = 0; i < cols.Count && i < rows[0].Length; i++)
                                {
                                    goldMeasures.Add(new GoldMeasureDef
                                    {
                                        MeasureName        = cols[i].Name,
                                        RawValue            = rows[0][i],
                                        SnowflakeDataType   = cols[i].DataType,
                                        SourceSchema        = sfView.Schema,
                                        SourceView          = sfView.ViewName
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.LogError(ex, "[/select-schemas][{id}] Failed to read Gold view '{s}.{v}' for measures — skipping.",
                                    session.SessionId, sfView.Schema, sfView.ViewName);
                                unmapped.Add(new UnmappedTable
                                {
                                    TableName = $"{sfView.Schema}.{sfView.ViewName}",
                                    Reason    = "Failed to read Gold view for measure conversion: " + ex.Message
                                });
                            }
                        }

                       var measureUnmapped = _tabular.AddGoldMeasuresTable(db, goldMeasures, originalMeasures);
                        foreach (var u in measureUnmapped) unmapped.Add(u);

                        _log.LogInformation("[/select-schemas][{id}] '_Measures' table built — {n} measure(s) added from {v} Gold view(s).",
                            session.SessionId, goldMeasures.Count, goldViews.Count);
                    }
                    
                    var relCount = _tabular.EnsureRelationshipsByKeyColumns(db);
                    _log.LogInformation(
                      "[/select-schemas][{id}] Key-based relationships created: {n}",
                         session.SessionId, relCount);

                    var monthSort = _tabular.ConfigureDateMonthSort(db);
                    _log.LogInformation(
                        "[/select-schemas][{id}] Month sort configured on {n} table(s)",
                             session.SessionId, monthSort);

                             try
{
    var sortHints = await _snowflake.DetectDateSortHintsAsync(session.SnowflakeConfig, nonGoldViews);
    var hintsApplied = _tabular.ApplyDateSortHints(db, sortHints);
    _log.LogInformation(
        "[/select-schemas][{id}] Date sort hints: {found} detected, {applied} applied",
        session.SessionId, sortHints.Count, hintsApplied);
}
catch (Exception ex)
{
    _log.LogWarning(ex, "[/select-schemas][{id}] Date sort detection failed - continuing without it", session.SessionId);
}

                    // ── Step 5: remove the placeholder table now that the
                    // real Lakehouse tables are in the model.
                    _tabular.RemovePlaceholderTable(db);

                    // ── Transactions 1–3 + readiness poll, as one call ──────
                    // FinalizeAndProcessModelAsync runs, in order:
                    //   1. SaveStructureOnly(db)                — metadata only.
                    //   2. betweenStructureAndRefresh() — binds Lakehouse
                    //      datasource credentials, strictly between the
                    //      structural save and the refresh (see
                    //      PowerBIService.BindLakehouseDatasourceCredentialsAsync
                    //      for why this step is a no-op for the Lakehouse SQL
                    //      endpoint path specifically, and where to plug in a
                    //      real Update Datasource call for sources that do
                    //      need one).
                    //   3. RequestFullRefreshAndSave(db)        — refresh + save.
                    //   4. WaitForModelReadyAsync(server, db)   — poll to Ready.
                    // It never throws for a business-logic failure — check
                    // finalizeResult.Success instead.
                    var finalizeResult = await _tabular.FinalizeAndProcessModelAsync(
                        server, db,
                        betweenStructureAndRefresh: async () =>
                        {
                            var credentialNotes = await _powerbi.BindLakehouseDatasourceCredentialsAsync(
                                session.PowerBIConfig, session.PowerBIConfig.WorkspaceId, session.DatasetId!);
                            saveWarnings.AddRange(credentialNotes.Where(n => n.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)));
                        });

                    saveWarnings.AddRange(finalizeResult.AllWarnings);
                    foreach (var w in saveWarnings)
                        unmapped.Add(new UnmappedTable { TableName = "(refresh)", Reason = w });

                    _log.LogInformation(
                        "[/select-schemas][{id}] Clean-out + Lakehouse load + finalize status={status} ({elapsed}ms) — {n} issue(s): {list}",
                        session.SessionId, finalizeResult.Status, finalizeResult.Elapsed.TotalMilliseconds, unmapped.Count, Dump(unmapped));

                    if (!finalizeResult.Success)
                    {
                        var msg = $"Model did not reach a Ready state after the Lakehouse rebuild " +
                                  $"(status={finalizeResult.Status}): {finalizeResult.ErrorMessage}";
                        _log.LogError("[/select-schemas][{id}] {msg}", session.SessionId, msg);
                        session.Status       = "awaiting_schema_selection";
                        session.ErrorMessage = msg;
                        _sessions.Update(session);
                        return StatusCode(500, new { error = msg });
                    }

                    _log.LogInformation("[/select-schemas][{id}] Engine-side model state is Ready — safe to request the embed token.",
                        session.SessionId);
                }
                finally
                {
                    _tabular.Disconnect(server);
                }

                // Intentionally NOT binding Snowflake credentials or
                // triggering a refresh here. This step only pushes the
                // table/partition metadata (SaveChanges above). Registering
                // a brand-new data source and refreshing it in the same
                // transaction is what the service rejects with "We are
                // unable to retrieve the credentials for the data source…
                // add new data sources in a separate transaction first" —
                // so credential binding + refresh is left as a deliberate,
                // separate step (manual, via the dataset's Settings → Data
                // source credentials screen, or a later dedicated call) run
                // only AFTER this metadata push has landed on its own.
                _log.LogInformation(
                    "[/select-schemas][{id}] Table metadata pushed — credential binding/refresh " +
                    "intentionally skipped here; bind Snowflake credentials separately before refreshing.",
                    session.SessionId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[{id}] Dataset rebuild failed", session.SessionId);
                session.Status       = "awaiting_schema_selection";
                session.ErrorMessage = ex.Message;
                _sessions.Update(session);
                return StatusCode(500, new { error = ex.Message });
            }

            // 3. Check visual binding coverage
            var loadedTableNames  = sfViews.Select(v => v.ViewName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var uncoveredBindings = session.VisualBindings
                .Where(b => !loadedTableNames.Contains(b.OriginalTable))
                .Select(b => b.OriginalTable)
                .Distinct()
                .ToList();

            if (uncoveredBindings.Count > 0)
            {
                _log.LogWarning(
                    "[/select-schemas][{id}] {n} original table(s) referenced by visuals have NO matching Snowflake view — " +
                    "these visuals WILL show Missing_References until /rebind-columns resolves them: {list}",
                    session.SessionId, uncoveredBindings.Count, Dump(uncoveredBindings));
                unmapped.Add(new UnmappedTable
                {
                    TableName       = "(visual bindings)",
                    Reason          = "Original table names referenced by visuals have no matching Snowflake view. Visuals using them will show errors until the column rebind step resolves them.",
                    UnmappedColumns = uncoveredBindings
                });
            }

            // 4. Wait for the service-side (control-plane) view of the dataset
            //    to catch up, then generate the embed token.
            //
            //    Sequence enforced here:
            //      [Rebuild schema]  → done above (Transactions 1–3)
            //      [Process/Validate] → RequestFullRefreshAndSave + engine-side
            //                            WaitForModelReadyAsync (already ran
            //                            above, before Disconnect)
            //      [Poll for ready state] → WaitForDatasetRefreshCompleteAsync
            //                                (service/REST-side, below)
            //      [Generate embed token] → GenerateEmbedTokenWithRetryAsync
            //                                (retries transient ASWL errors)
            string embedUrl, embedToken;
            try
            {
                _log.LogInformation("[/select-schemas][{id}] Waiting for dataset {ds} service-side refresh state to settle…",
                    session.SessionId, session.DatasetId);
                await _powerbi.WaitForDatasetRefreshCompleteAsync(
                    session.PowerBIConfig, session.PowerBIConfig.WorkspaceId, session.DatasetId!);

                _log.LogInformation("[/select-schemas][{id}] Generating embed token for report {rid}…", session.SessionId, session.ReportId);
                (embedUrl, embedToken) = await _powerbi.GenerateEmbedTokenWithRetryAsync(
                    session.PowerBIConfig, session.ReportId!);
                _log.LogInformation("[/select-schemas][{id}] Embed token generated — embedUrl={url} tokenLength={len}",
                    session.SessionId, embedUrl, embedToken?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[{id}] Embed token generation failed", session.SessionId);
                session.Status       = "rebuilding_dataset";
                session.ErrorMessage = ex.Message;
                _sessions.Update(session);
                return StatusCode(500, new { error = "Embed token failed: " + ex.Message });
            }

            session.UnmappedTables = unmapped;
            session.TablesLoaded   = sfViews.Count;
            session.TablesTotal    = loadedTableNames.Count;
            session.EmbedUrl       = embedUrl;
            session.EmbedToken     = embedToken;
            session.Status         = "completed";
            _sessions.Update(session);

            _log.LogInformation(
                "Session {id}: {loaded} Snowflake views loaded, {unmapped} issues. Embed ready.",
                session.SessionId, sfViews.Count, unmapped.Count);

            return Ok(new MigrationStatusResponse
            {
                SessionId      = session.SessionId,
                Status         = "completed",
                EmbedUrl       = embedUrl,
                EmbedToken     = embedToken,
                ReportId       = session.ReportId,
                DatasetId      = session.DatasetId,
                UnmappedTables = unmapped,
                TablesLoaded   = sfViews.Count,
                TablesTotal    = loadedTableNames.Count,
                VisualMetadata = session.VisualMetadata
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // STEP 3 (NEW): Per-visual column rebind
        //
        // Called by the frontend AFTER the report is embedded and the JS API
        // has read the live visual state (what columns are currently bound
        // in each visual against the old dataset).
        //
        // Flow:
        //   Frontend → sends live visual list with their current column bindings
        //   Backend  → iterates each visual, asks LLM to map old column → new column
        //   LLM      → returns ColumnReplacement list per visual
        //   Backend  → marks visuals with no match as HasError=true (never stops)
        //   Frontend → for each visual:
        //                removeDataField(old) for each binding
        //                addDataField(new)    for each matched replacement
        //                mark as error        if HasError
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost("rebind-columns/{sessionId}")]
        public async Task<IActionResult> RebindColumns(
            string sessionId,
            [FromBody] VisualColumnRebindRequest req,
            [FromServices] AzureOpenAIService openAI)
        {
            MigrationSession session;
            try   { session = _sessions.GetOrThrow(sessionId); }
            catch { return NotFound(new { error = "Session not found" }); }

            if (session.SnowflakeViews == null || session.SnowflakeViews.Count == 0)
                return BadRequest(new { error = "No Snowflake views in session — run schema selection first." });

            if (req.Visuals == null || req.Visuals.Count == 0)
                return BadRequest(new { error = "No visuals supplied." });

           _log.LogInformation(
    "[/rebind-columns][{id}] ▶ REQUEST BODY — {count} visual(s): {payload}",
    sessionId, req.Visuals.Count, Dump(req.Visuals));

// Fetch the model's current measures (e.g. from "_Measures") so the
// AI can correctly rebind KPI-style bindings to real measures instead
// of guessing at a raw column.
List<AzureOpenAIService.ExistingMeasureMeta> existingMeasures = new();
try
{
    var (server, db, _) = await _tabular.ConnectAndExtractVisualMetadataAsync(
        session.PowerBIConfig, session.DatasetId!, session.DatasetName);
    try
    {
        existingMeasures = db.Model.Tables
            .Cast<Table>()
            .SelectMany(t => t.Measures.Cast<Measure>()
                .Select(m => new AzureOpenAIService.ExistingMeasureMeta
                {
                    TableName   = t.Name,
                    MeasureName = m.Name
                }))
            .ToList();
        _log.LogInformation("[/rebind-columns][{id}] Found {n} existing measure(s) in the model: {list}",
            sessionId, existingMeasures.Count, Dump(existingMeasures));
    }
    finally
    {
        _tabular.Disconnect(server);
    }
}
catch (Exception ex)
{
    _log.LogWarning(ex, "[/rebind-columns][{id}] Failed to read existing measures — proceeding with none.", sessionId);
}

            // Process all visuals — never throws; per-visual errors are marked in result
            VisualColumnRebindResponse response;
            try
            {
                response = await openAI.RebindAllVisualsAsync(
                    session.PowerBIConfig, req.Visuals, session.SnowflakeViews, existingMeasures);
                    }
                    
                    catch (Exception ex)
            {
                _log.LogError(ex, "Session {id}: Batch column rebind failed", sessionId);
                return StatusCode(500, new { error = "Column rebind failed: " + ex.Message });
            }

            _log.LogInformation(
                "[/rebind-columns][{id}] ◀ RESPONSE BODY — {mapped}/{total} mapped, {errors} error(s): {payload}",
                sessionId, response.MappedVisuals, response.TotalVisuals, response.ErrorVisuals, Dump(response));

            // Sanity check the thing the analysis doc flagged: does every
            // visualName the frontend sent actually have a matching entry
            // in the response the frontend will index by visualName?
            var sentNames  = req.Visuals.Select(v => v.VisualName).ToHashSet(StringComparer.Ordinal);
            var returnedNames = response.Results.Select(r => r.VisualName).ToHashSet(StringComparer.Ordinal);
            var missing = sentNames.Except(returnedNames).ToList();
            if (missing.Count > 0)
            {
                _log.LogWarning(
                    "[/rebind-columns][{id}] ⚠ {n} visualName(s) sent by the frontend have NO matching result " +
                    "in the response — the frontend's resultMap[visualName] lookup will silently miss these: {list}",
                    sessionId, missing.Count, Dump(missing));
            }

            return Ok(response);
        }

        // ─────────────────────────────────────────────────────────────────────
        // STEP 4 (NEW): Validation & Migration Report
        //
        // Called by the frontend once the Visual Repair Layer has finished
        // walking the live report (compare bindings → detect broken →
        // remove/apply AI mapping → refresh → save). The frontend sends the
        // final per-visual outcome it observed; the backend assembles this
        // into a single authoritative report and stores it on the session so
        // it survives a page reload / can be re-fetched later.
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost("finalize/{sessionId}")]
        public async Task<IActionResult> FinalizeMigration(string sessionId, [FromBody] FinalizeMigrationRequest req)
        {
            MigrationSession session;
            try   { session = _sessions.GetOrThrow(sessionId); }
            catch { return NotFound(new { error = "Session not found" }); }

            var visuals = req.Visuals ?? new List<VisualRepairRecord>();
            _log.LogInformation(
                "[/finalize][{id}] ▶ REQUEST BODY — {count} visual repair record(s): {payload}",
                sessionId, visuals.Count, Dump(visuals));

            var report = new MigrationReport
            {
                SessionId      = sessionId,
                ReportId       = session.ReportId ?? "",
                DatasetId      = session.DatasetId ?? "",
                DatasetName    = session.DatasetName ?? "",
                TablesLoaded   = session.TablesLoaded,
                TablesTotal    = session.TablesTotal,
                UnmappedTables = session.UnmappedTables,
                TotalVisuals            = visuals.Count,
                HealthyVisuals          = visuals.Count(v => v.Status == "healthy"),
                RepairedVisuals         = visuals.Count(v => v.Status == "repaired"),
                PartiallyRepairedVisuals= visuals.Count(v => v.Status == "partial"),
                StillBrokenVisuals      = visuals.Count(v => v.Status == "still_broken"),
                VisualDetails           = visuals
            };

            report.OverallResult =
                report.StillBrokenVisuals > 0                              ? "needs_attention" :
                (report.PartiallyRepairedVisuals > 0 || session.UnmappedTables.Count > 0) ? "success_with_warnings" :
                "success";

            // Only actually drop the original tables once every visual is
            // confirmed healthy/repaired against the new Snowflake tables —
            // if anything is still broken, deleting the old tables now could
            // take a visual that could still fall back on them down with it.
            if (session.OriginalTableNames.Count == 0)
            {
                report.OriginalTablesRemoved = false;
                report.OriginalTablesNote    = "No original tables were recorded for this session (nothing to remove).";
            }
            else if (report.StillBrokenVisuals > 0)
            {
                report.OriginalTablesRemoved = false;
                report.OriginalTablesNote    =
                    $"{report.StillBrokenVisuals} visual(s) are still broken — original tables were left in place. " +
                    "Resolve those and call /finalize again to remove them.";
                _log.LogWarning("[/finalize][{id}] {n} visual(s) still broken — leaving {t} original table(s) in place",
                    sessionId, report.StillBrokenVisuals, session.OriginalTableNames.Count);
            }
            else
            {
                try
                {
                    _log.LogInformation("[/finalize][{id}] All visuals healthy/repaired — removing {n} original table(s): {list}",
                        sessionId, session.OriginalTableNames.Count, Dump(session.OriginalTableNames));

                    var (server, db, _) = await _tabular.ConnectAndExtractVisualMetadataAsync(
                        session.PowerBIConfig, session.DatasetId!, session.DatasetName);
                    try
                    {
                        _tabular.RemoveOriginalTables(db, session.OriginalTableNames);
                        var saveWarnings = _tabular.SaveChanges(db);
                        if (saveWarnings.Count > 0)
                            _log.LogWarning("[/finalize][{id}] SaveChanges warning(s) after table removal: {w}",
                                sessionId, string.Join(" | ", saveWarnings));
                    }
                    finally
                    {
                        _tabular.Disconnect(server);
                    }

                    report.OriginalTablesRemoved = true;
                    report.OriginalTablesNote    = $"Removed {session.OriginalTableNames.Count} original table(s).";
                    session.OriginalTableNames   = new List<string>();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[/finalize][{id}] Failed to remove original tables", sessionId);
                    report.OriginalTablesRemoved = false;
                    report.OriginalTablesNote    = "Failed to remove original tables: " + ex.Message;
                    if (report.OverallResult == "success") report.OverallResult = "success_with_warnings";
                }
            }

            session.Report = report;
            session.Status = "validated";
            _sessions.Update(session);

            _log.LogInformation(
                "[/finalize][{id}] ◀ RESPONSE — {healthy} healthy, {repaired} repaired, " +
                "{partial} partial, {broken} still broken. Overall: {result}. Full report: {payload}",
                sessionId, report.HealthyVisuals, report.RepairedVisuals,
                report.PartiallyRepairedVisuals, report.StillBrokenVisuals, report.OverallResult, Dump(report));

            return Ok(report);
        }

        [HttpGet("report/{sessionId}")]
        public IActionResult GetReport(string sessionId)
        {
            MigrationSession session;
            try   { session = _sessions.GetOrThrow(sessionId); }
            catch { return NotFound(new { error = "Session not found" }); }

            if (session.Report == null)
                return NotFound(new { error = "No report generated yet for this session." });

            return Ok(session.Report);
        }

        // ── List workspaces ───────────────────────────────────────────────────
        [HttpPost("workspaces")]
        public async Task<IActionResult> ListWorkspaces([FromBody] PowerBIConfig cfg)
        {
            try   { return Ok(await _powerbi.ListWorkspacesAsync(cfg)); }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ── View→columns map for frontend ────────────────────────────────────
        [HttpGet("viewtables/{sessionId}")]
        public IActionResult GetViewTables(string sessionId)
        {
            MigrationSession session;
            try   { session = _sessions.GetOrThrow(sessionId); }
            catch { return NotFound(new { error = "Session not found" }); }

            var viewMap = session.SnowflakeViews
                .ToDictionary(
                    v => v.ViewName,
                    v => v.Columns.Select(c => c.Name).ToList()
                );

            return Ok(new
            {
                views          = viewMap,
                reportId       = session.ReportId,
                datasetId      = session.DatasetId,
                embedUrl       = session.EmbedUrl,
                embedToken     = session.EmbedToken,
                visualMetadata = session.VisualMetadata
            });
        }

        // ── Legacy: LLM visual→view binding ──────────────────────────────────
        [HttpPost("llm-bind/{sessionId}")]
        public async Task<IActionResult> LlmBind(
            string sessionId,
            [FromBody] LlmBindRequest req,
            [FromServices] AzureOpenAIService openAI)
        {
            MigrationSession session;
            try   { session = _sessions.GetOrThrow(sessionId); }
            catch { return NotFound(new { error = "Session not found" }); }

            if (session.SnowflakeViews == null || session.SnowflakeViews.Count == 0)
                return BadRequest(new { error = "No Snowflake views in session — run schema selection first." });

            if (req.Visuals == null || req.Visuals.Count == 0)
                return BadRequest(new { error = "No visuals supplied." });

            var viewSummaries = session.SnowflakeViews.Select(v => new
            {
                viewName = v.ViewName,
                schema   = v.Schema,
                columns  = v.Columns.Select(c => c.Name).ToList()
            }).ToList();

            var systemPrompt =
                "You are a Power BI to Snowflake migration assistant.\n" +
                "For EVERY visual, decide which Snowflake view it maps to and which columns " +
                "should be bound to which Power BI data roles.\n" +
                "Return ONLY a JSON array, no markdown, no commentary:\n" +
                "[\n" +
                "  {\n" +
                "    \"visualName\": \"<exact visualName from input>\",\n" +
                "    \"snowflakeView\": \"<viewName or empty string>\",\n" +
                "    \"snowflakeSchema\": \"<schema or empty string>\",\n" +
                "    \"columns\": [\"col1\", \"col2\"],\n" +
                "    \"roleBindings\": [\n" +
                "      { \"column\": \"col1\", \"role\": \"Values\" }\n" +
                "    ],\n" +
                "    \"confidence\": 0.95\n" +
                "  }\n" +
                "]";

            var userPrompt =
                "Power BI visuals:\n" +
                System.Text.Json.JsonSerializer.Serialize(req.Visuals) +
                "\n\nSnowflake views:\n" +
                System.Text.Json.JsonSerializer.Serialize(viewSummaries);

            List<LlmVisualBinding> result;
            try
            {
                result = await openAI.LlmBindVisualsAsync(
                    session.PowerBIConfig, systemPrompt, userPrompt);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[{id}] LLM visual binding failed", session.SessionId);
                return StatusCode(500, new { error = "LLM binding failed: " + ex.Message });
            }

            return Ok(new { bindings = result });
        }

        // ── Status poll ───────────────────────────────────────────────────────
        [HttpGet("status/{sessionId}")]
        public IActionResult GetStatus(string sessionId)
        {
            MigrationSession session;
            try   { session = _sessions.GetOrThrow(sessionId); }
            catch { return NotFound(new { error = "Session not found" }); }

            return Ok(new MigrationStatusResponse
            {
                SessionId      = session.SessionId,
                Status         = session.Status,
                EmbedUrl       = session.EmbedUrl,
                EmbedToken     = session.EmbedToken,
                ReportId       = session.ReportId,
                DatasetId      = session.DatasetId,
                ErrorMessage   = session.ErrorMessage,
                UnmappedTables = session.UnmappedTables,
                TablesLoaded   = session.TablesLoaded,
                TablesTotal    = session.TablesTotal
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // STANDALONE UTILITY: report-agnostic two-phase XMLA migration.
        // Not part of the session-based /init → /select-schemas → /finalize
        // pipeline above — this targets any dataset BY NAME at runtime and
        // runs independently. Phase 1 strips legacy file-backed tables
        // (Excel/CSV/Access partitions) and everything that depends on
        // them; Phase 2 maps whatever tables remain to Snowflake views.
        // See FabricSnowflakeXmlaMigrator for the full implementation.
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost("strip-legacy-and-map-snowflake")]
        public async Task<IActionResult> StripLegacyAndMapSnowflake([FromBody] LegacyStripRequest req)
        {
            _log.LogInformation("[/strip-legacy-and-map-snowflake] ▶ request — dataset='{ds}' endpoint='{ep}'",
                req.DatasetName, req.XmlaEndpoint);

            var cfg = new XmlaMigrationConfig
            {
                TenantId     = req.TenantId,
                ClientId     = req.ClientId,
                ClientSecret = req.ClientSecret,

                XmlaEndpoint = req.XmlaEndpoint,
                DatasetName  = req.DatasetName,

                SnowflakeServer    = req.SnowflakeServer,
                SnowflakeWarehouse = req.SnowflakeWarehouse,
                SnowflakeDatabase  = req.SnowflakeDatabase,
                SnowflakeSchema    = req.SnowflakeSchema,

                TableToSnowflakeViewMap = req.TableToSnowflakeViewMap
            };

            try
            {
                var result = await _legacyMigrator.RunAsync(cfg);
                _log.LogInformation(
                    "[/strip-legacy-and-map-snowflake] ✔ dataset='{ds}' removed={removed} mapped={mapped} warnings={warn}",
                    req.DatasetName, result.LegacyTablesRemoved.Count, result.TablesMappedToSnowflake.Count, result.Warnings.Count);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[/strip-legacy-and-map-snowflake] ✘ FAILED for dataset='{ds}'", req.DatasetName);
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    // Helper: distinct VisualBinding by Table+Column
    internal class VisualBindingComparer : System.Collections.Generic.IEqualityComparer<VisualBinding>
    {
        public bool Equals(VisualBinding? x, VisualBinding? y) =>
            string.Equals(x?.OriginalTable, y?.OriginalTable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x?.OriginalColumn, y?.OriginalColumn, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(VisualBinding obj) =>
            HashCode.Combine(obj.OriginalTable?.ToLowerInvariant(), obj.OriginalColumn?.ToLowerInvariant());
    }
}