using System.Collections.Generic;

namespace PbixMigrator.Models
{
    // ── Snowflake PAT Config ──────────────────────────────────────────────────
    public class SnowflakeConfig
    {
        public string Account   { get; set; } = "";
        public string Username  { get; set; } = "";
        public string PatToken  { get; set; } = "";
        public string Warehouse { get; set; } = "";
        public string Database  { get; set; } = "";
        public string Role      { get; set; } = "";
        public List<string> SelectedSchemas { get; set; } = new();
    }

    // ── Power BI / Azure Config ───────────────────────────────────────────────
    public class PowerBIConfig
    {
        public string TenantId              { get; set; } = "";
        public string ClientId              { get; set; } = "";
        public string ClientSecret          { get; set; } = "";
        public string WorkspaceId           { get; set; } = "";
        public string AzureOpenAIEndpoint   { get; set; } = "";
        public string AzureOpenAIKey        { get; set; } = "";
        public string AzureOpenAIDeployment { get; set; } = "gpt-4o";
        public string XmlaEndpoint          { get; set; } = "";

        // ── Optional delegated-user auth (ROPC) fallback ────────────────────
        // Only needed for label-protected PBIX files, which Azure AD rejects
        // under service-principal (app-only) auth. Leave both blank to keep
        // using the service principal for everything else. This account MUST
        // NOT have MFA or Conditional Access applied, or ROPC will fail
        // outright — Microsoft has been deprecating this flow for that reason.
        // Store the password via user-secrets/environment/Key Vault, never
        // committed to source or appsettings.json in plaintext.
        public string UserAccountUpn        { get; set; } = "";
        public string UserAccountPassword   { get; set; } = "";

        // ── Delegated user token from MSAL popup sign-in (frontend) ────────
        // Acquired interactively in the browser (see msalAuth.js /
        // UploadStep's "Sign in with Microsoft" button) and sent up
        // per-request — never stored server-side beyond the current
        // operation. This app no longer uses service-principal (app-only)
        // auth anywhere — every Power BI / Fabric / OneLake call runs as
        // this signed-in user.
        public string UserAccessToken       { get; set; } = "";

        // AAD access tokens are audience-scoped — a token acquired for the
        // Power BI resource (UserAccessToken above) will NOT work against
        // Fabric's public API or OneLake, and vice versa. The frontend
        // acquires one token per resource (same signed-in account, three
        // silent acquireTokenSilent calls with different scopes) and sends
        // all three up. See FabricLakehouseService.GetTokenAsync.
        public string FabricAccessToken     { get; set; } = "";
        public string OneLakeAccessToken    { get; set; } = "";
        public string LakehouseSqlAccessToken { get; set; } = "";

        // ── Fabric Lakehouse staging target (v19 ingestion path) ────────────
        // Display name of the Lakehouse this migrator creates/reuses in
        // WorkspaceId to stage Snowflake data before Power BI binds to it.
        // Leave blank to fall back to a fixed default name.
        public string LakehouseName         { get; set; } = "pbix_migrator_lakehouse";

        // Populated at runtime (copied from MigrationSession.LakehouseSqlServer
        // right after GetSqlEndpointAsync resolves it) so that
        // PowerBIService.BindLakehouseDatasourceCredentialsAsync — which only
        // receives a PowerBIConfig, not the full session — can match the
        // Lakehouse's SQL Analytics Endpoint datasource by its connection
        // string. Without this being set, the match always fails and
        // credential binding silently no-ops every time.
        public string? LakehouseSqlServer   { get; set; }
        public string? LakehouseSqlDatabase { get; set; }
    }

    // ── Session state ─────────────────────────────────────────────────────────
    public class MigrationSession
    {
        public string SessionId                { get; init; } = System.Guid.NewGuid().ToString();
        public string OwnerId                  { get; init; } = "";
        public SnowflakeConfig SnowflakeConfig  { get; set; } = new();
        public PowerBIConfig PowerBIConfig      { get; set; } = new();
        public byte[]? PbixBytes               { get; set; }
        public string? PbixFileName            { get; set; }
        public List<string> AvailableSchemas   { get; set; } = new();

        // Populated after publish step
        public string? ReportId    { get; set; }
        public string? DatasetId   { get; set; }
        public string? DatasetName { get; set; }
        public string? B2JobId { get; set; }

        // Visual-level bindings: each visual with its table→column bindings
        public List<VisualMetadata> VisualMetadata    { get; set; } = new();

        // Model-level bindings (fallback): every Table.Column in the semantic model
        public List<VisualBinding> VisualBindings     { get; set; } = new();

        // Captured before the first structural XMLA save removes original PBIX
        // tables. Reused on retry because the live dataset may already contain
        // only the placeholder table after a reconciliation failure.
        public List<PbixTableSchema> OriginalSemanticSchemas { get; set; } = new();
        public List<PbixMeasureSchema> OriginalSemanticMeasures { get; set; } = new();

        // All Snowflake views loaded from selected schemas
        public List<SnowflakeViewMeta> SnowflakeViews { get; set; } = new();

        // ── Fabric Lakehouse staging (v19 ingestion path) ────────────────
        public string? LakehouseId          { get; set; }
        public string? LakehouseSqlServer   { get; set; }
        public string? LakehouseSqlDatabase { get; set; }

        // Table names that existed in the dataset BEFORE the Snowflake views
        // were loaded alongside it. Kept so they can be safely removed once
        // the Visual Repair Layer has rebound every visual to the new
        // Snowflake tables (see /finalize).
        public List<string> OriginalTableNames { get; set; } = new();

        public string  Status        { get; set; } = "pending";
        public string? EmbedUrl      { get; set; }
        public string? EmbedToken    { get; set; }
        public string? ErrorMessage  { get; set; }

        public List<UnmappedTable> UnmappedTables { get; set; } = new();
        public int TablesLoaded  { get; set; }
        public int TablesTotal   { get; set; }

        // Populated once the frontend Visual Repair Layer finishes and calls /finalize
        public MigrationReport? Report { get; set; }

        public System.DateTime CreatedAt { get; set; } = System.DateTime.UtcNow;
    }

    // ── Visual-level metadata extracted from the PBIX / semantic model ────────
    // Each visual on each page, with the specific columns it currently binds.
    public class VisualMetadata
    {
        public string PageName    { get; set; } = "";
        public string VisualName  { get; set; } = "";   // internal GUID-like name
        public string VisualTitle { get; set; } = "";   // display title from config
        public string VisualType  { get; set; } = "";   // e.g. "barChart", "card", "slicer"

        // Each column the visual currently references: Table + Column + data role
        public List<VisualColumnBinding> BoundColumns { get; set; } = new();
    }

    // ── One column bound in a visual ─────────────────────────────────────────
    public class VisualColumnBinding
    {
        public string Table      { get; set; } = "";
        public string Column     { get; set; } = "";
        public string DataRole   { get; set; } = "";   // "Values", "Category", "Y", etc.

        // "" = no aggregation (plain column). Otherwise one of:
        // Sum, Average, Min, Max, Count, DistinctCount, Median,
        // StandardDeviation, Variance. Captured from the ORIGINAL PBIX visual.
        public string Aggregation { get; set; } = "";

        // true only when the original visual field was a real model measure
        // (as opposed to a column, with or without an aggregation applied).
        public bool   IsMeasure   { get; set; }
    }

    // ── A single model-level visual→field binding (legacy / fallback) ─────────
    public class VisualBinding
    {
        public string OriginalTable  { get; set; } = "";
        public string OriginalColumn { get; set; } = "";
    }

    // ── API shapes ────────────────────────────────────────────────────────────
    public class SchemaSelectionRequest
    {
        public string SessionId             { get; set; } = "";
        public List<string> SelectedSchemas { get; set; } = new();
    }

    public class MigrateRequest { public string SessionId { get; set; } = ""; }

    // Returned after PBIX is published
    public class PublishResponse
    {
        public string SessionId                   { get; set; } = "";
        public string ReportId                    { get; set; } = "";
        public string DatasetId                   { get; set; } = "";
        public string DatasetName                 { get; set; } = "";
        public string? B2JobId                    { get; set; }
        public List<string> Schemas               { get; set; } = new();
        public List<VisualBinding> VisualBindings { get; set; } = new();
        public List<VisualMetadata> VisualMetadata{ get; set; } = new();
        public string Status                      { get; set; } = "";
    }

    // Returned after schema selection + auto-apply
    public class MigrationStatusResponse
    {
        public string SessionId        { get; set; } = "";
        public string Status           { get; set; } = "";
        public string? EmbedUrl        { get; set; }
        public string? EmbedToken      { get; set; }
        public string? ReportId        { get; set; }
        public string? DatasetId       { get; set; }
        public string? B2JobId         { get; set; }
        public string? ErrorMessage    { get; set; }
        public List<UnmappedTable> UnmappedTables { get; set; } = new();
        public int TablesLoaded  { get; set; }
        public int TablesTotal   { get; set; }

        // Original (pre-rebuild) per-visual bindings captured via XMLA in /init.
        // Ground truth for the frontend's Visual Repair Layer — it must use
        // THESE when a live visual's getDataFields() comes back empty after
        // the dataset rebuild breaks it, instead of concluding "unbound".
        public List<VisualMetadata> VisualMetadata { get; set; } = new();
    }

    // ── Gold KPI view → one constant measure ────────────────────────────────
    // Built from a single row of a Gold-schema Snowflake view (see
    // MigrationController's Gold-view handling and
    // TabularModelService.AddGoldMeasuresTable). MeasureName = ConstantValue.
    public class GoldMeasureDef
    {
        public string MeasureName      { get; set; } = "";
        public string RawValue         { get; set; } = "";
        public string SnowflakeDataType{ get; set; } = "";
        public string SourceSchema     { get; set; } = "";
        public string SourceView       { get; set; } = "";
    }

    // ── Snowflake metadata ────────────────────────────────────────────────────
    public class SnowflakeViewMeta
    {
        public string Schema   { get; set; } = "";
        public string ViewName { get; set; } = "";
        public List<ColumnMeta> Columns { get; set; } = new();
    }

    public class ColumnMeta
    {
        public string Name     { get; set; } = "";
        public string DataType { get; set; } = "";

        // The physical column name as it actually exists in the Lakehouse
        // Delta table / SQL Analytics Endpoint after header sanitization
        // (leading spaces, "··ROW_ID"-style special chars, etc. stripped —
        // Fabric's CSV ingestion rejects those outright). Name stays the
        // original Snowflake column name for display in Power BI; TOM's
        // DataColumn.SourceColumn must use SourceColumn (this field) so it
        // actually resolves against the Lakehouse table. Falls back to Name
        // when empty, so nothing outside the Lakehouse path has to change.
        public string SourceColumn { get; set; } = "";
    }

    // ── Visual field mapping (layout JSON rewrite) ────────────────────────────
    public class PbixTableSchema
    {
        public string TableName { get; set; } = "";
        public List<ColumnMeta> Columns { get; set; } = new();
        public int MeasuresExcluded { get; set; }
        public int CalculatedColumnsExcluded { get; set; }
    }

    public class PbixMeasureSchema
    {
        public string TableName { get; set; } = "";
        public string MeasureName { get; set; } = "";
        public string Expression { get; set; } = "";
        public string FormatString { get; set; } = "";
        public string DisplayFolder { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsHidden { get; set; }
    }

    public class VisualFieldMap
    {
        public string VisualName { get; set; } = "";
        public string VisualType { get; set; } = "";
        public List<FieldBinding> Bindings { get; set; } = new();
    }

    public class FieldBinding
    {
        public string OriginalTable    { get; set; } = "";
        public string OriginalColumn   { get; set; } = "";
        public string SnowflakeSchema  { get; set; } = "";
        public string SnowflakeView    { get; set; } = "";
        public string SnowflakeColumn  { get; set; } = "";
    }

    public class UnmappedTable
    {
        public string TableName              { get; set; } = "";
        public string Reason                 { get; set; } = "";
        public List<string> UnmappedColumns  { get; set; } = new();
        public string FailedMethod           { get; set; } = "";
        public string OriginalTable          { get; set; } = "";
        public string SourceTable            { get; set; } = "";
        public string PhysicalTable          { get; set; } = "";
        public List<string> ExpectedColumns  { get; set; } = new();
        public List<string> ActualColumns    { get; set; } = new();
        public string ExceptionType          { get; set; } = "";
        public string ExceptionMessage       { get; set; } = "";
    }

    public class WorkspaceInfo
    {
        public string Id   { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }

    // ── AI-generated table-level mapping (data model rewrite) ─────────────────
    public class TableMapping
    {
        public string OriginalTable    { get; set; } = "";
        public string SnowflakeSchema  { get; set; } = "";
        public string SnowflakeView    { get; set; } = "";
        public Dictionary<string, string> ColumnMap { get; set; } = new();
        public double Confidence       { get; set; }
    }

    // ── LLM visual binding request/response ───────────────────────────────────
    public class LlmBindRequest
    {
        public List<LlmVisualInfo> Visuals { get; set; } = new();
    }

    public class LlmVisualInfo
    {
        public string VisualName  { get; set; } = "";
        public string VisualTitle { get; set; } = "";
        public string VisualType  { get; set; } = "";
    }

    public class LlmRoleBinding
    {
        public string Column { get; set; } = "";
        public string Role   { get; set; } = "Values";
    }

    public class LlmVisualBinding
    {
        public string VisualName                  { get; set; } = "";
        public string SnowflakeView               { get; set; } = "";
        public string SnowflakeSchema             { get; set; } = "";
        public List<string> Columns               { get; set; } = new();
        public List<LlmRoleBinding> RoleBindings  { get; set; } = new();
        public double Confidence                   { get; set; }
    }

    // ── Per-visual column rebind: request from frontend after report renders ──
    // The frontend sends the live visual state; the backend asks the LLM to map
    // each existing column binding to the best matching column in the new dataset.
    public class VisualColumnRebindRequest
    {
        // One entry per visual on screen
        public List<VisualRebindTarget> Visuals { get; set; } = new();
    }

    public class VisualRebindTarget
    {
        public string VisualName  { get; set; } = "";
        public string VisualTitle { get; set; } = "";
        public string VisualType  { get; set; } = "";

        // The columns the visual is currently bound to (from the OLD dataset)
        public List<VisualColumnBinding> CurrentBindings { get; set; } = new();
    }

    // ── Response: per-visual, per-column replacement instructions ─────────────
    public class VisualColumnRebindResponse
    {
        public List<VisualRebindResult> Results { get; set; } = new();
        public int TotalVisuals    { get; set; }
        public int MappedVisuals   { get; set; }
        public int ErrorVisuals    { get; set; }
    }

    public class VisualRebindResult
    {
        public string VisualName   { get; set; } = "";
        public string VisualTitle  { get; set; } = "";
        public bool   HasError     { get; set; }
        public string? ErrorReason { get; set; }

        // For each old binding: what to replace it with
        public List<ColumnReplacement> Replacements { get; set; } = new();
    }

    public class ColumnReplacement
    {
        // Original binding to REMOVE
        public string OldTable    { get; set; } = "";
        public string OldColumn   { get; set; } = "";
        public string OldRole     { get; set; } = "";

        // New binding to ADD (empty SnowflakeColumn = could not match → mark error)
        public string NewTable    { get; set; } = "";
        public string NewColumn   { get; set; } = "";
        public string NewRole     { get; set; } = "";
        public double Confidence  { get; set; }
        public bool   Matched     { get; set; }
        public string? MatchNote  { get; set; }

        // Aggregation copied from the ORIGINAL binding (never from the LLM) so
        // the frontend can re-apply it when binding the target column.
        // Always "" when the target is a measure — a measure carries its own
        // aggregation — or when the original field was itself a measure.
        public string Aggregation  { get; set; } = "";

        // Whether the original field / the chosen target is a model measure.
        // Lets the frontend emit the right field schema (measure vs column).
        public bool   OldIsMeasure { get; set; }
        public bool   NewIsMeasure { get; set; }
    }

    // ── Visual Repair Layer: final per-visual outcome, sent by the frontend ───
    // after it has walked the live report via the JS Authoring API (compare
    // bindings → detect broken → remove/apply → refresh → save).
    public class VisualRepairRecord
    {
        public string VisualName   { get; set; } = "";
        public string VisualTitle  { get; set; } = "";
        public string VisualType   { get; set; } = "";
        public string PageName     { get; set; } = "";

        // "healthy"      → bindings already resolved against the new dataset, skipped
        // "repaired"     → was broken, AI mapping found + applied a full replacement
        // "partial"      → was broken, some columns repaired, some still unmatched
        // "still_broken" → was broken, no usable replacement found
        public string Status       { get; set; } = "healthy";

        public List<VisualColumnBinding> OriginalBindings { get; set; } = new();
        public List<ColumnReplacement>   Replacements     { get; set; } = new();
        public string? Notes       { get; set; }
    }

    public class FinalizeMigrationRequest
    {
        public List<VisualRepairRecord> Visuals { get; set; } = new();
    }

    // ── Final artifact: the "Validation & Migration Report" ───────────────────
    public class MigrationReport
    {
        public string SessionId            { get; set; } = "";
        public System.DateTime GeneratedAt { get; set; } = System.DateTime.UtcNow;
        public string ReportId             { get; set; } = "";
        public string DatasetId            { get; set; } = "";
        public string DatasetName          { get; set; } = "";

        public int TablesLoaded            { get; set; }
        public int TablesTotal             { get; set; }
        public List<UnmappedTable> UnmappedTables { get; set; } = new();

        public int TotalVisuals               { get; set; }
        public int HealthyVisuals             { get; set; }
        public int RepairedVisuals            { get; set; }
        public int PartiallyRepairedVisuals   { get; set; }
        public int StillBrokenVisuals         { get; set; }

        public List<VisualRepairRecord> VisualDetails { get; set; } = new();

        // Overall: "success" | "success_with_warnings" | "needs_attention"
        public string OverallResult        { get; set; } = "success";

        // Whether the original (pre-Snowflake) tables were removed as part
        // of this finalize call, and why not if they weren't.
        public bool OriginalTablesRemoved  { get; set; }
        public string? OriginalTablesNote  { get; set; }
    }

    // ── Request body for the standalone legacy-strip / Snowflake-mapping
    //    utility (FabricSnowflakeXmlaMigrator). Deliberately flat and
    //    self-contained rather than reusing PowerBIConfig/SnowflakeConfig —
    //    this endpoint targets an arbitrary dataset by name at runtime and
    //    isn't tied to a MigrationSession. ─────────────────────────────────
    public class LegacyStripRequest
    {
        // ── Azure AD Service Principal ──────────────────────────────────
        public string TenantId     { get; set; } = "";
        public string ClientId     { get; set; } = "";
        public string ClientSecret { get; set; } = "";

        // ── XMLA target ──────────────────────────────────────────────────
        public string XmlaEndpoint { get; set; } = "";
        public string DatasetName  { get; set; } = "";

        // ── Snowflake target ─────────────────────────────────────────────
        public string SnowflakeServer    { get; set; } = "";
        public string SnowflakeWarehouse { get; set; } = "";
        public string SnowflakeDatabase  { get; set; } = "";
        public string SnowflakeSchema    { get; set; } = "";

        // Optional: TOM table name → Snowflake view/table name override.
        // Omit to map every remaining table 1:1 by name (uppercased).
        public Dictionary<string, string>? TableToSnowflakeViewMap { get; set; }
    }
}
