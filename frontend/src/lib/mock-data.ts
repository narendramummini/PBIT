// Mock data for the PowerBI Accelerator

export type Workspace = {
  id: string;
  name: string;
  description: string;
  reportCount: number;
};

export type Report = {
  id: string;
  name: string;
  workspaceId: string;
  workspaceName: string;
  lastModified: string;
  owner: string;
  size: string;
};

export const workspaces: Workspace[] = [
  { id: "ws-1", name: "Sales Analytics", description: "Revenue, pipeline & forecasting", reportCount: 12 },
  { id: "ws-2", name: "Marketing Insights", description: "Campaign performance & attribution", reportCount: 8 },
  { id: "ws-3", name: "Finance Reporting", description: "P&L, budget vs actuals", reportCount: 15 },
  { id: "ws-4", name: "Operations KPIs", description: "Supply chain & logistics", reportCount: 6 },
  { id: "ws-5", name: "HR Dashboards", description: "Headcount, attrition, hiring", reportCount: 4 },
  { id: "ws-6", name: "Product Analytics", description: "Usage, retention, feature adoption", reportCount: 9 },
];

export const reports: Report[] = [
  { id: "r-1", name: "Q4 Revenue Pipeline", workspaceId: "ws-1", workspaceName: "Sales Analytics", lastModified: "2025-06-20", owner: "Sarah Chen", size: "24.6 MB" },
  { id: "r-2", name: "Regional Sales Performance", workspaceId: "ws-1", workspaceName: "Sales Analytics", lastModified: "2025-06-18", owner: "James Park", size: "18.2 MB" },
  { id: "r-3", name: "Sales Forecast 2026", workspaceId: "ws-1", workspaceName: "Sales Analytics", lastModified: "2025-06-15", owner: "Sarah Chen", size: "31.7 MB" },
  { id: "r-4", name: "Account Executive Scorecard", workspaceId: "ws-1", workspaceName: "Sales Analytics", lastModified: "2025-06-10", owner: "Mike Torres", size: "12.4 MB" },
  { id: "r-5", name: "Campaign Attribution Model", workspaceId: "ws-2", workspaceName: "Marketing Insights", lastModified: "2025-06-22", owner: "Priya Patel", size: "28.9 MB" },
  { id: "r-6", name: "Web & Paid Funnel", workspaceId: "ws-2", workspaceName: "Marketing Insights", lastModified: "2025-06-19", owner: "Alex Kim", size: "16.1 MB" },
  { id: "r-7", name: "Monthly P&L Summary", workspaceId: "ws-3", workspaceName: "Finance Reporting", lastModified: "2025-06-23", owner: "David Liu", size: "42.3 MB" },
  { id: "r-8", name: "Budget vs Actuals", workspaceId: "ws-3", workspaceName: "Finance Reporting", lastModified: "2025-06-21", owner: "David Liu", size: "19.8 MB" },
  { id: "r-9", name: "Warehouse Throughput", workspaceId: "ws-4", workspaceName: "Operations KPIs", lastModified: "2025-06-17", owner: "Elena Rossi", size: "14.2 MB" },
  { id: "r-10", name: "Attrition Heatmap", workspaceId: "ws-5", workspaceName: "HR Dashboards", lastModified: "2025-06-12", owner: "Tom Becker", size: "9.6 MB" },
  { id: "r-11", name: "Feature Adoption Trends", workspaceId: "ws-6", workspaceName: "Product Analytics", lastModified: "2025-06-24", owner: "Nina Wong", size: "22.1 MB" },
];

export const pipelineSteps = [
  { id: "upload", label: "PBIX Uploaded", detail: "Reading binary file" },
  { id: "model", label: "Data Model Extracted", detail: "Parsing VertiPaq metadata" },
  { id: "mquery", label: "M Queries Parsed", detail: "Translating Power Query" },
  { id: "dax", label: "DAX Measures Parsed", detail: "Analyzing DAX expressions" },
  { id: "bronze", label: "Bronze Tables Retrieved", detail: "Materializing raw layer" },
  { id: "mapping", label: "Table Mapping Completed", detail: "Mapping PBI → Snowflake" },
  { id: "silver", label: "Silver Views Generated", detail: "Conforming dimensions" },
  { id: "gold", label: "Gold Views Generated", detail: "Translating measures to SQL" },
  { id: "deploy", label: "Snowflake Deployment Completed", detail: "Executing DDL/DML" },
  { id: "repoint", label: "Power BI Dataset Repointed", detail: "Source switched to Snowflake Gold" },
] as const;

export type PipelineStepId = (typeof pipelineSteps)[number]["id"];

export const sampleLogs: { step: PipelineStepId; message: string }[] = [
  { step: "upload", message: "Received Q4_Revenue_Pipeline.pbix (24.6 MB)" },
  { step: "upload", message: "Computed SHA-256 checksum: 9f3a...c712" },
  { step: "model", message: "Opened DataModel stream from PBIX package" },
  { step: "model", message: "Detected VertiPaq engine, model schema v1.2" },
  { step: "model", message: "Found 14 tables, 187 columns, 42 measures, 9 calculated columns" },
  { step: "mquery", message: "Parsing M expression: Source = Sql.Database(...)" },
  { step: "mquery", message: "Translated 18 Power Query steps to Snowflake SQL" },
  { step: "mquery", message: "Resolved 7 parameters and 3 shared queries" },
  { step: "dax", message: "Parsing DAX: Total Revenue := SUMX(Sales, Sales[Qty]*Sales[Price])" },
  { step: "dax", message: "Translated 42 DAX measures to SQL equivalents" },
  { step: "dax", message: "3 measures flagged for manual review (time-intelligence)" },
  { step: "bronze", message: "CREATE TABLE BRONZE.SALES_RAW (47 columns)" },
  { step: "bronze", message: "CREATE TABLE BRONZE.CUSTOMER_RAW (23 columns)" },
  { step: "bronze", message: "Loaded 14 bronze tables, 8.2M rows total" },
  { step: "mapping", message: "Mapped 14/14 PBI tables to Snowflake objects" },
  { step: "mapping", message: "Detected 9 star-schema relationships" },
  { step: "silver", message: "CREATE OR REPLACE VIEW SILVER.DIM_CUSTOMER" },
  { step: "silver", message: "CREATE OR REPLACE VIEW SILVER.DIM_PRODUCT" },
  { step: "silver", message: "CREATE OR REPLACE VIEW SILVER.FACT_SALES" },
  { step: "silver", message: "Generated 14 silver views with SCD2 support" },
  { step: "gold", message: "CREATE OR REPLACE VIEW GOLD.VW_REVENUE_SUMMARY" },
  { step: "gold", message: "CREATE OR REPLACE VIEW GOLD.VW_PIPELINE_FORECAST" },
  { step: "gold", message: "Generated 9 gold views, materialized 3 as tables" },
  { step: "deploy", message: "Connecting to xyz12345.snowflakecomputing.com" },
  { step: "deploy", message: "Using warehouse ANALYTICS_WH, role TRANSFORMER" },
  { step: "deploy", message: "Executed 67 DDL statements, 0 errors" },
  { step: "repoint", message: "Updated PBIX connection string to Snowflake GOLD schema" },
  { step: "repoint", message: "Rebound 9 gold views to existing Power BI tables" },
  { step: "repoint", message: "Power BI dataset ready — open in Power BI Service to refresh" },
];

export const conversionStats = {
  tables: 14,
  columns: 187,
  measures: 42,
  calculatedColumns: 9,
  bronzeObjects: 14,
  silverObjects: 14,
  goldObjects: 9,
  sqlStatements: 67,
};

export const generatedSql = [
  {
    name: "BRONZE.SALES_RAW",
    layer: "bronze",
    sql: `CREATE OR REPLACE TABLE BRONZE.SALES_RAW (
  SALE_ID         NUMBER(38,0),
  CUSTOMER_ID     NUMBER(38,0),
  PRODUCT_ID      NUMBER(38,0),
  ORDER_DATE      DATE,
  QUANTITY        NUMBER(18,2),
  UNIT_PRICE      NUMBER(18,4),
  DISCOUNT        NUMBER(5,4),
  _INGESTED_AT    TIMESTAMP_NTZ DEFAULT CURRENT_TIMESTAMP()
);`,
  },
  {
    name: "SILVER.FACT_SALES",
    layer: "silver",
    sql: `CREATE OR REPLACE VIEW SILVER.FACT_SALES AS
SELECT
  s.SALE_ID,
  s.CUSTOMER_ID,
  s.PRODUCT_ID,
  s.ORDER_DATE,
  s.QUANTITY,
  s.UNIT_PRICE,
  s.QUANTITY * s.UNIT_PRICE * (1 - COALESCE(s.DISCOUNT,0)) AS NET_REVENUE
FROM BRONZE.SALES_RAW s
WHERE s.SALE_ID IS NOT NULL;`,
  },
  {
    name: "GOLD.VW_REVENUE_SUMMARY",
    layer: "gold",
    sql: `CREATE OR REPLACE VIEW GOLD.VW_REVENUE_SUMMARY AS
SELECT
  DATE_TRUNC('MONTH', f.ORDER_DATE)   AS REVENUE_MONTH,
  c.REGION,
  p.CATEGORY,
  SUM(f.NET_REVENUE)                   AS TOTAL_REVENUE,
  COUNT(DISTINCT f.SALE_ID)            AS ORDER_COUNT,
  SUM(f.NET_REVENUE) / NULLIF(COUNT(DISTINCT f.CUSTOMER_ID),0) AS REVENUE_PER_CUSTOMER
FROM SILVER.FACT_SALES f
JOIN SILVER.DIM_CUSTOMER c USING (CUSTOMER_ID)
JOIN SILVER.DIM_PRODUCT  p USING (PRODUCT_ID)
GROUP BY 1,2,3;`,
  },
];
