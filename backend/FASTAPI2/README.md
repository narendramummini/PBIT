# PBIX → Snowflake Migration Pipeline

End-to-end pipeline that migrates a Power BI model (tables, Power Query/M,
DAX measures, calculated columns/tables, relationships) into a Snowflake
Bronze → Silver → Gold architecture, using Azure OpenAI to assist with
table matching and complex M/DAX → SQL translation.

```
PBIX Metadata JSON
      │
      ▼
Metadata Parser ─────────────────────────────  pbix_parser.py
      │
      ▼
Snowflake Table Discovery (INFORMATION_SCHEMA)  snowflake_client.py
      │
      ▼
Table Matching Engine (fuzzy + Azure OpenAI) ─  table_matcher.py
      │
      ▼
Power Query → SQL Translation ───────────────  mquery_translator.py
      │
      ▼
SILVER layer SQL + objects
      │
      ▼
DAX → SQL Translation ────────────────────────  dax_translator.py
      │
      ▼
GOLD layer SQL + objects
      │
      ▼
Deploy to Snowflake (optional) ──────────────  deploy_sql.py
      │
      ▼
Validation Framework ─────────────────────────  validator.py
      │
      ▼
Migration Report ─────────────────────────────  migration_report.py
```

Each stage is a standalone CLI script that reads/writes JSON or SQL files
under `output/`, so you can run the pipeline step-by-step, inspect/edit any
intermediate artifact, and re-run just the steps you need.

---

## 1. Setup

```bash
cd pbix-snowflake-migration
python3 -m venv venv
source venv/bin/activate        # Windows: venv\Scripts\activate

pip install -r requirements.txt
```

Copy the environment template and fill in your real credentials:

```bash
cp .env.example .env
```

Edit `.env`:

- **Snowflake**: `SNOWFLAKE_ACCOUNT`, `SNOWFLAKE_USER`, and one of:
  - `SNOWFLAKE_PAT_TOKEN` (recommended — generate in Snowsight under
    **User menu → Settings → Authentication → Programmatic access tokens**)
  - `SNOWFLAKE_PASSWORD` (legacy)
  - `SNOWFLAKE_PRIVATE_KEY_PATH` (key-pair auth)

  Plus `SNOWFLAKE_WAREHOUSE`, `SNOWFLAKE_DATABASE`, `SNOWFLAKE_SCHEMA` (your
  Bronze schema), `SNOWFLAKE_SILVER_SCHEMA`, `SNOWFLAKE_GOLD_SCHEMA`.
- **Azure OpenAI**: `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`,
  `AZURE_OPENAI_DEPLOYMENT` (your GPT-4o or similar deployment name).
- **Pipeline**: `PBIX_METADATA_PATH` (path to your real PBIX metadata JSON),
  `DRY_RUN=true` to start (generates SQL without executing anything against
  Snowflake — flip to `false` or pass `--force` when you're ready to deploy).

Test your Snowflake connection before running anything else:

```bash
python3 src/snowflake_client.py --test
```

---

## 2. Getting PBIX metadata as JSON

This pipeline consumes a **PBIX metadata JSON export**, not the raw `.pbix`
file itself. You need to extract metadata first using one of:

- **[Tabular Editor](https://tabulareditor.com/)** (free) — open your PBIX's
  data model, use its scripting/macro feature or the `TOM` (Tabular Object
  Model) to dump tables, columns, measures, and relationships to JSON.
- **[pbi-tools](https://github.com/pbi-tools/pbi-tools)** — `pbi-tools extract`
  on the `.pbix` file produces a folder of JSON/text files describing the
  model; combine the relevant parts into the shape below.
- **DAX Studio** / **SQL Server Management Studio (model.bim)** — if your
  PBIX is connected to Power BI Premium/XMLA endpoint, you can query the
  Tabular Object Model directly and export to JSON.
- A **custom script** against the PBIX's `DataModelSchema` (inside the PBIX
  zip, in Power BI's JSON-based model format).

The parser (`src/pbix_parser.py`) is intentionally tolerant of varying key
names (`name`/`Name`, `mQuery`/`m_query`/`expression`, etc.) so most exports
will work directly. See `sample_data/pbix_metadata.json` for the exact shape
it expects:

```json
{
  "tables": [
    {
      "name": "Tickets",
      "mQuery": "let ... in ...",
      "columns": [{"name": "Ticket ID", "dataType": "Int64.Type", "sourceColumn": "TICKET_ID"}]
    }
  ],
  "measures": [
    {"name": "Open Ticket Count", "table": "Tickets", "expression": "CALCULATE(...)"}
  ],
  "relationships": [
    {"fromTable": "Tickets", "fromColumn": "AssignedTeam", "toTable": "Teams", "toColumn": "TEAM_NAME"}
  ]
}
```

If your real export uses different key names entirely, either rename keys
to match, or extend `_first_present(...)` calls in `pbix_parser.py` — they're
designed to be easy to add aliases to.

---

## 3. Running the pipeline

### Option A — step by step (recommended on first run)

```bash
# 1. Parse PBIX metadata JSON into a normalized model
python3 src/pbix_parser.py --input sample_data/pbix_metadata.json

# 2. Match PBIX tables to Snowflake Bronze tables (exact -> fuzzy -> Azure OpenAI)
#    Will pause for manual review on low-confidence matches (INTERACTIVE_REVIEW=true)
python3 src/table_matcher.py

# 3. Translate Power Query (M) -> Snowflake SQL, generate Silver layer DDL
python3 src/mquery_translator.py

# 4. Translate DAX measures/calculated columns/calculated tables -> SQL, generate Gold layer DDL
python3 src/dax_translator.py

# --- Inspect output/sql/silver/*.sql and output/sql/gold/*.sql before deploying ---

# 5. Deploy to Snowflake (DRY_RUN=true by default just logs the SQL; --force executes it)
python3 src/deploy_sql.py --layer all --force

# 6. Validate Silver/Gold objects against Bronze sources
python3 src/validator.py

# 7. Generate the final migration report
python3 src/migration_report.py
```

### Option B — orchestrated end-to-end

```bash
# Generate all SQL only (no Snowflake writes), good for first pass / review
python3 src/run_all.py --no-interactive

# Full run: generate + deploy for real + validate + report
python3 src/run_all.py --deploy --force --validate
```

Output locations:

| Artifact | Path |
|---|---|
| Normalized PBIX model | `output/mapping/parsed_model.json` |
| Table mapping (PBIX ↔ Bronze) | `output/mapping/table_mapping.json` |
| Silver SQL files | `output/sql/silver/*.sql` |
| Gold SQL files | `output/sql/gold/*.sql` |
| Validation results | `output/reports/validation_results.json` |
| Deployment results | `output/reports/deployment_results.json` |
| **Final report** | `output/reports/migration_report.md` |
| Per-run logs | `output/logs/*.log` |

---

## 4. REST API (alternative to the CLI)

Every CLI stage is also exposed as a FastAPI REST endpoint under `api/`.
The CLI scripts in `src/` and the API in `api/` share the exact same
business logic and read/write the exact same `output/` files — use
whichever fits your workflow, or mix both (e.g. run `/parse` via API, then
inspect output with the CLI).

### Start the server

```bash
python3 serve.py
# or directly:
uvicorn api.main:app --reload --host 0.0.0.0 --port 8000
```

Open **http://localhost:8000/docs** for interactive Swagger UI — every
endpoint below can be tried directly from the browser.

### How it works: async jobs

Every pipeline stage is **long-running** (LLM calls, Snowflake I/O), so
every `POST` endpoint returns immediately with a `job_id` and runs the work
in the background. Poll `GET /jobs/{job_id}` until `status` is `succeeded`
or `failed`.

```bash
# 1. Kick off a stage
curl -X POST http://localhost:8000/parse \
  -H "Content-Type: application/json" \
  -d '{"input_path": "sample_data/pbix_metadata.json"}'
# -> {"job_id": "abc123...", "status": "pending", ...}

# 2. Poll until done
curl http://localhost:8000/jobs/abc123...
# -> {"status": "succeeded", "result": {...}, ...}
```

### Endpoints (mirrors the 7 CLI stages)

| Stage | Endpoint | Notes |
|---|---|---|
| 1. Parse | `POST /parse` | Parse a PBIX metadata JSON already on the server (`input_path`, defaults to `PBIX_METADATA_PATH`) |
| 1. Parse (upload) | `POST /parse/upload` | Multipart file upload instead of a server path |
| 2. Match tables | `POST /tables/match` | Exact → fuzzy → Azure OpenAI matching. Result includes `needs_review` for low-confidence matches |
| 2. List Bronze tables | `GET /tables/bronze` | Synchronous helper (not a job) — lists Snowflake tables for building a manual-match picker |
| 2. Manual match | `POST /tables/manual-match` | Apply `{pbix_table: bronze_table}` overrides for tables flagged in `needs_review` |
| 3. Silver SQL | `POST /translate/silver` | Power Query (M) → Snowflake SQL, generates Silver DDL files |
| 4. Gold SQL | `POST /translate/gold` | DAX → Snowflake SQL, generates Gold DDL files |
| 5. Deploy | `POST /deploy` | `{"layer": "silver"\|"gold"\|"all", "force": true\|false}`. `force=false` (default) is a dry run |
| 6. Validate | `POST /validate` | Row/column/type checks, Bronze vs Silver, plus Gold object existence |
| 7. Report | `POST /report` | Generates `output/reports/migration_report.md` |
| 7. Report (fetch) | `GET /report/latest` | Fetch the latest generated report text directly, no job polling |
| Orchestrator | `POST /run-all` | Runs all 7 stages as one background job. `{"deploy": false, "force": false, "run_validation": false}` to opt into the later stages |
| Jobs | `GET /jobs/{job_id}` | Poll a specific job |
| Jobs | `GET /jobs?stage=parse` | List jobs, optionally filtered by stage |

### Example: full walkthrough with curl

```bash
# Parse
curl -s -X POST http://localhost:8000/parse \
  -d '{"input_path": "sample_data/pbix_metadata.json"}' -H "Content-Type: application/json"

# (poll job until succeeded, then:)

# Match tables to Snowflake Bronze
curl -s -X POST http://localhost:8000/tables/match -d '{}' -H "Content-Type: application/json"

# (if needs_review is non-empty, resolve manually:)
curl -s -X POST http://localhost:8000/tables/manual-match \
  -d '{"overrides": {"Tickets": "RAW_TICKETS_V2"}}' -H "Content-Type: application/json"

# Generate Silver + Gold SQL
curl -s -X POST http://localhost:8000/translate/silver -d '{}' -H "Content-Type: application/json"
curl -s -X POST http://localhost:8000/translate/gold -d '{}' -H "Content-Type: application/json"

# Deploy for real
curl -s -X POST http://localhost:8000/deploy \
  -d '{"layer": "all", "force": true}' -H "Content-Type: application/json"

# Validate + report
curl -s -X POST http://localhost:8000/validate -d '{}' -H "Content-Type: application/json"
curl -s -X POST http://localhost:8000/report -d '{}' -H "Content-Type: application/json"
curl -s http://localhost:8000/report/latest
```

### Notes

- **In-memory jobs**: job state lives in the API process's memory. Restarting
  the server clears job history (the underlying `output/` files are
  unaffected — they're the source of truth). Fine for single-instance use;
  swap `api/jobs.py` for Celery/RQ + Redis if you need multi-worker support.
- **CORS** is wide open (`allow_origins=["*"]`) for local development —
  tighten this in `api/main.py` before exposing the API beyond localhost.
- **No auth** is implemented. Don't expose this API on the open internet
  as-is — put it behind a reverse proxy with auth, or add a FastAPI
  dependency (`Depends(...)`) checking an API key/JWT before deploying it
  anywhere multi-user or internet-facing.

---

## 5. Project layout

- **Table matching** (`table_matcher.py`): exact name match → fuzzy string
  match (≥0.92 similarity auto-accepted) → ambiguous cases sent to Azure
  OpenAI with the PBIX table's columns + shortlisted Bronze candidates and
  their columns → if confidence is still below `MATCH_CONFIDENCE_THRESHOLD`
  (default 0.75), you're prompted interactively in the console (unless
  `--no-interactive` / `INTERACTIVE_REVIEW=false`).

- **M → SQL** (`mquery_translator.py`): a small rule engine handles the two
  most common single-purpose M patterns deterministically — pure
  `Table.RenameColumns` and pure `Table.SelectColumns` — without calling the
  LLM. Anything with filters, type conversions, joins, or multiple chained
  steps is sent to Azure OpenAI with the full M expression and source table
  name. The model is instructed to insert `-- TODO: manual review: <reason>`
  SQL comments wherever it isn't fully confident — **search the generated
  `.sql` files for `TODO`** before deploying.

- **DAX → SQL** (`dax_translator.py`): always LLM-driven (DAX's filter-context
  semantics — `CALCULATE`, `ALL`, time intelligence, iterators like `SUMX` —
  don't map cleanly to deterministic rules). Same `TODO: manual review`
  convention applies. Review these more carefully than Silver SQL, since DAX
  semantics are the most likely source of subtle behavior differences.

---

## 6. Validation

`validator.py` compares, for every matched table:

- **Row counts** — Bronze vs. Silver (mismatches are expected if your M query
  applies filters; investigate if unexpected).
- **Column counts & data types** — flags type mismatches on columns that
  share a name between Bronze and Silver.
- **Gold object existence** — confirms every generated KPI view / calculated
  table is actually queryable.

It does **not** automatically validate that KPI *values* match the original
Power BI report — DAX measures often have evaluation-context behavior that's
genuinely different from a SQL aggregate. Treat the Gold layer SQL as a
strong first draft and spot-check actual numbers against the Power BI report
for your most business-critical measures.

---

## 7. Project layout

```
pbix-snowflake-migration/
├── .env.example              # copy to .env and fill in credentials
├── requirements.txt
├── serve.py                   # convenience launcher for the API server
├── sample_data/
│   └── pbix_metadata.json    # example input — replace with your real export
├── src/                        # core pipeline logic (used by both CLI and API)
│   ├── config.py              # loads .env into typed config objects
│   ├── logger.py               # shared console + file logging
│   ├── pbix_parser.py          # Stage 1
│   ├── snowflake_client.py     # Snowflake connection + INFORMATION_SCHEMA helpers
│   ├── azure_openai_client.py  # Azure OpenAI wrapper (JSON-mode + text-mode)
│   ├── table_matcher.py        # Stage 2
│   ├── mquery_translator.py    # Stage 3 (Silver)
│   ├── dax_translator.py       # Stage 4 (Gold)
│   ├── deploy_sql.py           # Stage 5 (optional execution against Snowflake)
│   ├── validator.py            # Stage 6
│   ├── migration_report.py     # Stage 7
│   └── run_all.py              # CLI orchestrator chaining all stages
├── api/                        # REST API layer (FastAPI)
│   ├── main.py                 # route definitions (POST /parse, /tables/match, etc.)
│   ├── pipeline_runner.py      # adapts src/ functions for job-based async execution
│   ├── jobs.py                 # in-memory async job manager (status/progress/result)
│   └── schemas.py              # Pydantic request/response models
├── uploads/                    # PBIX JSON files uploaded via /parse/upload (gitignored)
└── output/                    # generated at runtime (gitignored)
    ├── mapping/
    ├── sql/silver/
    ├── sql/gold/
    ├── reports/
    └── logs/
```

---

## 8. Safety notes

- **`DRY_RUN=true` by default.** `deploy_sql.py` will only *log* the SQL it
  would run until you pass `--force` or set `DRY_RUN=false`. Review every
  file in `output/sql/silver/` and `output/sql/gold/` first.
- The pipeline only ever issues `CREATE OR REPLACE VIEW` (and
  `CREATE SCHEMA IF NOT EXISTS`) — it never drops or truncates Bronze data.
- Your `.env` contains real secrets — it's listed in `.gitignore`; never
  commit it.
