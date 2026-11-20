import shutil
import sys
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, UploadFile, File, HTTPException, Query
from fastapi.middleware.cors import CORSMiddleware

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from config import config
from logger import get_logger
from jobs import job_manager, JobStatus
import pipeline_runner as pr
from schemas import (
    JobResponse, ParseRequest, MatchTablesRequest, ManualMatchRequest,
    DeployRequest, ValidateRequest, ReportRequest, RunAllRequest,
    SnowflakeConnection,
)


from starlette.middleware.sessions import SessionMiddleware
from powerbi.auth import router as powerbi_auth_router
from powerbi.workspaces import router as powerbi_workspace_router
from powerbi.clone import router as powerbi_clone_router
log = get_logger("api.main", config.logs_dir, config.pipeline.log_level)

app = FastAPI(
    title="PBIX → Snowflake Migration API",
    description=(
    "REST API wrapping the PBIX metadata → Snowflake Bronze/Silver/Gold migration pipeline, "
    "plus Power BI SSO login and report-cloning endpoints.\n\n"
    "Snowflake credentials are passed per-request (no .env needed for that part). "
    "Power BI SSO requires CLIENT_ID/CLIENT_SECRET/TENANT_ID/SESSION_SECRET_KEY in .env.\n\n"
    "Every pipeline stage runs as an async job: POST to start, GET /jobs/{job_id} to poll."
),
    version="1.1.0",
)


app.add_middleware(
    SessionMiddleware,
    secret_key=config.powerbi.session_secret_key,
    same_site="lax",
    https_only=False,
    session_cookie="session",
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=[
        "https://1115fb10-6ea8-4052-8d1b-31238016c02e.lovableproject.com",
        "https://lovable.dev",
        "https://id-preview--1115fb10-6ea8-4052-8d1b-31238016c02e.lovable.app",
        "http://localhost:8080",
        "http://127.0.0.1:8080",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
    ],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(powerbi_auth_router)
app.include_router(powerbi_workspace_router)
app.include_router(powerbi_clone_router)

UPLOAD_DIR = Path(__file__).resolve().parent.parent / "uploads"
UPLOAD_DIR.mkdir(parents=True, exist_ok=True)


def _job_to_response(job) -> JobResponse:
    return JobResponse(**job.to_dict())

def _is_pbix(filename: str) -> bool:
    return filename.lower().endswith(".pbix")

def _sf_to_dict(sf: Optional[SnowflakeConnection]) -> Optional[dict]:
    """Convert a SnowflakeConnection Pydantic model to a plain dict for pipeline_runner."""
    if sf is None:
        return None
    return {
        "account": sf.account,
        "user": sf.user,
        "warehouse": sf.warehouse,
        "database": sf.database,
        "role": sf.role,
        "schema_": sf.schema_,
        "silver_schema": sf.silver_schema,
        "gold_schema": sf.gold_schema,
        "pat_token": sf.pat_token,
        "password": sf.password,
        "private_key_path": sf.private_key_path,
        "private_key_passphrase": sf.private_key_passphrase,
    }


# ---------------------------------------------------------------------------
# Health / info
# ---------------------------------------------------------------------------

@app.get("/health", tags=["meta"])
async def health():
    return {"status": "ok"}


@app.get("/", tags=["meta"])
async def root():
    return {
        "service": "PBIX -> Snowflake Migration API",
        "docs": "/docs",
        "auth": "Pass Snowflake credentials in the request body — no .env needed.",
        "stages": [
            "POST /parse or /parse/upload",
            "POST /tables/match",
            "GET /tables/bronze",
            "POST /tables/manual-match",
            "POST /translate/silver",
            "POST /translate/gold",
            "POST /deploy",
            "POST /validate",
            "POST /report",
            "POST /run-all",
        ],
    }


# ---------------------------------------------------------------------------
# Jobs
# ---------------------------------------------------------------------------

@app.get("/jobs/{job_id}", response_model=JobResponse, tags=["jobs"])
async def get_job(job_id: str):
    job = job_manager.get(job_id)
    if not job:
        raise HTTPException(404, f"No job found with id {job_id}")
    return _job_to_response(job)


@app.get("/jobs", response_model=list[JobResponse], tags=["jobs"])
async def list_jobs(stage: Optional[str] = None):
    return [_job_to_response(j) for j in job_manager.list(stage=stage)]

# ---------------------------------------------------------------------------
# Stage 0: Extract  (raw .pbix -> metadata JSON)
# ---------------------------------------------------------------------------

@app.post("/extract/upload", response_model=JobResponse, tags=["0. extract"])
async def extract_upload(file: UploadFile = File(...)):
    """Upload a raw .pbix file and extract its metadata to JSON (does not parse/match yet)."""
    if not _is_pbix(file.filename):
        raise HTTPException(400, "Expected a .pbix file")

    dest = UPLOAD_DIR / file.filename
    with open(dest, "wb") as f:
        shutil.copyfileobj(file.file, f)
    log.info(f"Uploaded PBIX file to {dest}")

    job = await job_manager.create("extract")
    import asyncio
    asyncio.create_task(job_manager.run(job, pr.run_extract, str(dest)))
    return _job_to_response(job)


@app.post("/pipeline/from-pbix", response_model=JobResponse, tags=["0. extract"])
async def pipeline_from_pbix(file: UploadFile = File(...)):
    """
    One-shot convenience: upload a raw .pbix file, extract metadata AND parse
    it into parsed_model.json in a single background job.
    Equivalent to /extract/upload followed by /parse, chained automatically.
    """
    if not _is_pbix(file.filename):
        raise HTTPException(400, "Expected a .pbix file")

    dest = UPLOAD_DIR / file.filename
    with open(dest, "wb") as f:
        shutil.copyfileobj(file.file, f)
    log.info(f"Uploaded PBIX file to {dest}")

    job = await job_manager.create("extract_and_parse")
    import asyncio
    asyncio.create_task(job_manager.run(job, pr.run_extract_and_parse, str(dest)))
    return _job_to_response(job)


# ---------------------------------------------------------------------------
# Stage 1: Parse  (no Snowflake needed)
# ---------------------------------------------------------------------------

@app.post("/parse", response_model=JobResponse, tags=["1. parse"])
async def parse(req: ParseRequest):
    """Parse a PBIX metadata JSON already present on the server filesystem."""
    if not req.input_path:
        raise HTTPException(422, "input_path is required")
    if not Path(req.input_path).exists():
        raise HTTPException(400, f"input_path does not exist on server: {req.input_path}")

    job = await job_manager.create("parse")
    import asyncio
    asyncio.create_task(job_manager.run(job, pr.run_parse, req.input_path))
    return _job_to_response(job)


@app.post("/parse/upload", response_model=JobResponse, tags=["1. parse"])
async def parse_upload(file: UploadFile = File(...)):
    """Upload a PBIX metadata JSON file directly and parse it."""
    if not file.filename.endswith(".json"):
        raise HTTPException(400, "Expected a .json file")

    dest = UPLOAD_DIR / file.filename
    with open(dest, "wb") as f:
        shutil.copyfileobj(file.file, f)
    log.info(f"Uploaded PBIX metadata to {dest}")

    job = await job_manager.create("parse")
    import asyncio
    asyncio.create_task(job_manager.run(job, pr.run_parse, str(dest)))
    return _job_to_response(job)


# ---------------------------------------------------------------------------
# Stage 2: Table matching  (requires Snowflake)
# ---------------------------------------------------------------------------

@app.post("/tables/match", response_model=JobResponse, tags=["2. table matching"])
async def match_tables(req: MatchTablesRequest):
    """
    Match parsed PBIX tables to Snowflake Bronze tables.
    Requires /parse to have run first.
    Pass your Snowflake credentials in the `snowflake` field.
    """
    model_path = config.mapping_dir / "parsed_model.json"
    if not model_path.exists():
        raise HTTPException(400, "No parsed model found. Run POST /parse first.")

    sf_params = _sf_to_dict(req.snowflake)
    job = await job_manager.create("match_tables")
    import asyncio
    asyncio.create_task(job_manager.run(
        job, pr.run_match_tables,
        bronze_schema=req.bronze_schema,
        manual_overrides=req.manual_overrides,
        sf_params=sf_params,
    ))
    return _job_to_response(job)


@app.get("/tables/bronze", tags=["2. table matching"])
async def list_bronze_tables(
    schema: Optional[str] = None,
    account: str = Query(..., description="Snowflake account"),
    user: str = Query(..., description="Snowflake user"),
    warehouse: str = Query(..., description="Snowflake warehouse"),
    database: str = Query(..., description="Snowflake database"),
    role: str = Query("SYSADMIN", description="Snowflake role"),
    pat_token: Optional[str] = Query(None, description="PAT token"),
    password: Optional[str] = Query(None, description="Password (if no PAT)"),
):
    """
    Synchronous helper — list available Bronze tables in Snowflake.
    Useful for building a manual-match picker UI.
    Credentials are passed as query parameters.
    """
    sf_params = {
        "account": account, "user": user, "warehouse": warehouse,
        "database": database, "role": role,
        "schema_": schema or "BRONZE",
        "pat_token": pat_token, "password": password,
    }
    try:
        return pr.run_list_bronze_tables(schema=schema, sf_params=sf_params)
    except Exception as e:
        raise HTTPException(500, f"Failed to list Bronze tables: {e}")


@app.post("/tables/manual-match", response_model=JobResponse, tags=["2. table matching"])
async def manual_match(req: ManualMatchRequest):
    """Re-run matching with manual overrides to resolve low-confidence entries."""
    model_path = config.mapping_dir / "parsed_model.json"
    if not model_path.exists():
        raise HTTPException(400, "No parsed model found. Run POST /parse first.")

    sf_params = _sf_to_dict(req.snowflake)
    job = await job_manager.create("manual_match")
    import asyncio
    asyncio.create_task(job_manager.run(
        job, pr.run_match_tables,
        manual_overrides=req.overrides,
        sf_params=sf_params,
    ))
    return _job_to_response(job)


# ---------------------------------------------------------------------------
# Stage 3: M query -> Silver SQL  (no live Snowflake connection needed)
# ---------------------------------------------------------------------------

@app.post("/translate/silver", response_model=JobResponse, tags=["3. silver layer"])
async def translate_silver():
    """
    Translate Power Query (M) -> Snowflake SQL, generate Silver layer DDL.
    Schema names are taken from the table_mapping produced by /tables/match.
    Requires /tables/match to have run first.
    """
    mapping_path = config.mapping_dir / "table_mapping.json"
    if not mapping_path.exists():
        raise HTTPException(400, "No table mapping found. Run POST /tables/match first.")

    job = await job_manager.create("translate_silver")
    import asyncio
    asyncio.create_task(job_manager.run(job, pr.run_translate_silver))
    return _job_to_response(job)


# ---------------------------------------------------------------------------
# Stage 4: DAX -> Gold SQL  (no live Snowflake connection needed)
# ---------------------------------------------------------------------------

@app.post("/translate/gold", response_model=JobResponse, tags=["4. gold layer"])
async def translate_gold():
    """
    Translate DAX measures/calculated columns/tables -> Snowflake SQL.
    Requires /parse to have run first.
    """
    model_path = config.mapping_dir / "parsed_model.json"
    if not model_path.exists():
        raise HTTPException(400, "No parsed model found. Run POST /parse first.")

    job = await job_manager.create("translate_gold")
    import asyncio
    asyncio.create_task(job_manager.run(job, pr.run_translate_gold))
    return _job_to_response(job)


# ---------------------------------------------------------------------------
# Stage 5: Deploy  (requires Snowflake)
# ---------------------------------------------------------------------------

@app.post("/deploy", response_model=JobResponse, tags=["5. deploy"])
async def deploy(req: DeployRequest):
    """
    Deploy generated Silver/Gold SQL to Snowflake.
    Pass your Snowflake credentials in the `snowflake` field.
    DRY_RUN by default — set force=true to actually execute.
    """
    sf_params = _sf_to_dict(req.snowflake)
    job = await job_manager.create("deploy")
    import asyncio
    asyncio.create_task(job_manager.run(
        job, pr.run_deploy,
        layer=req.layer, force=req.force, sf_params=sf_params,
    ))
    return _job_to_response(job)


# ---------------------------------------------------------------------------
# Stage 6: Validate  (requires Snowflake)
# ---------------------------------------------------------------------------

@app.post("/validate", response_model=JobResponse, tags=["6. validate"])
async def validate(req: ValidateRequest):
    """
    Validate deployed Silver/Gold objects against Bronze sources.
    Pass your Snowflake credentials in the `snowflake` field.
    """
    mapping_path = config.mapping_dir / "table_mapping.json"
    if not mapping_path.exists():
        raise HTTPException(400, "No table mapping found. Run POST /tables/match first.")

    sf_params = _sf_to_dict(req.snowflake)
    job = await job_manager.create("validate")
    import asyncio
    asyncio.create_task(job_manager.run(job, pr.run_validate, sf_params=sf_params))
    return _job_to_response(job)


# ---------------------------------------------------------------------------
# Stage 7: Report  (no Snowflake needed)
# ---------------------------------------------------------------------------

@app.post("/report", response_model=JobResponse, tags=["7. report"])
async def report(req: ReportRequest):
    """Generate the final Markdown migration report aggregating all prior stages."""
    job = await job_manager.create("report")
    import asyncio
    asyncio.create_task(job_manager.run(job, pr.run_report))
    return _job_to_response(job)


@app.get("/report/latest", tags=["7. report"])
async def get_latest_report():
    """Fetch the most recently generated report text directly (no job polling)."""
    report_path = config.reports_dir / "migration_report.md"
    if not report_path.exists():
        raise HTTPException(404, "No report generated yet. Run POST /report first.")
    return {"report_markdown": report_path.read_text(encoding="utf-8")}


# ---------------------------------------------------------------------------
# Orchestrator: run everything as one chained job
# ---------------------------------------------------------------------------

def _run_all_sync(input_path: str, bronze_schema: Optional[str], deploy_flag: bool,
                   force: bool, validate_flag: bool, sf_params: Optional[dict] = None,
                   progress_cb=None):
    progress_cb = progress_cb or (lambda m: None)

    if input_path.lower().endswith(".pbix"):
        progress_cb("Stage 0/7: Extracting PBIX metadata...")
        extract_result = pr.run_extract(input_path, progress_cb=lambda m: progress_cb(f"[extract] {m}"))
        parse_input_path = extract_result["output_file"]
    else:
        parse_input_path = input_path

    progress_cb("Stage 1/7: Parsing PBIX metadata...")
    parse_result = pr.run_parse(parse_input_path, progress_cb=lambda m: progress_cb(f"[parse] {m}"))

    progress_cb("Stage 2/7: Matching tables...")
    match_result = pr.run_match_tables(
        bronze_schema=bronze_schema, sf_params=sf_params,
        progress_cb=lambda m: progress_cb(f"[match] {m}"),
    )

    progress_cb("Stage 3/7: Translating Power Query -> Silver SQL...")
    silver_result = pr.run_translate_silver(
        sf_params=sf_params, progress_cb=lambda m: progress_cb(f"[silver] {m}")
    )

    progress_cb("Stage 4/7: Translating DAX -> Gold SQL...")
    gold_result = pr.run_translate_gold(
        sf_params=sf_params, progress_cb=lambda m: progress_cb(f"[gold] {m}")
    )

    deploy_result = None
    if deploy_flag:
        if not sf_params:
            raise ValueError("snowflake credentials are required when deploy=true")
        progress_cb("Stage 5/7: Deploying to Snowflake...")
        deploy_result = pr.run_deploy(
            layer="all", force=force, sf_params=sf_params,
            progress_cb=lambda m: progress_cb(f"[deploy] {m}"),
        )
    else:
        progress_cb("Stage 5/7: Skipped (deploy=false)")

    validate_result = None
    if validate_flag:
        if not sf_params:
            raise ValueError("snowflake credentials are required when run_validation=true")
        progress_cb("Stage 6/7: Validating...")
        validate_result = pr.run_validate(
            sf_params=sf_params, progress_cb=lambda m: progress_cb(f"[validate] {m}")
        )
    else:
        progress_cb("Stage 6/7: Skipped (run_validation=false)")

    progress_cb("Stage 7/7: Generating report...")
    report_result = pr.run_report(progress_cb=lambda m: progress_cb(f"[report] {m}"))

    return {
        "parse": parse_result,
        "match_tables": match_result,
        "translate_silver": silver_result,
        "translate_gold": gold_result,
        "deploy": deploy_result,
        "validate": validate_result,
        "report": report_result,
    }


@app.post("/run-all", response_model=JobResponse, tags=["orchestrator"])
async def run_all(req: RunAllRequest):
    """
    Run the entire pipeline end-to-end as a single background job.
    Pass `snowflake` credentials if deploy=true or run_validation=true.
    Poll GET /jobs/{job_id}; progress_message shows the current stage.
    """
    if not req.input_path:
        raise HTTPException(422, "input_path is required")
    if not Path(req.input_path).exists():
        raise HTTPException(400, f"input_path does not exist on server: {req.input_path}")

    input_path = req.input_path

    sf_params = _sf_to_dict(req.snowflake)
    job = await job_manager.create("run_all")
    import asyncio
    asyncio.create_task(job_manager.run(
        job, _run_all_sync,
        input_path=input_path,
        bronze_schema=req.bronze_schema,
        deploy_flag=req.deploy,
        force=req.force,
        validate_flag=req.run_validation,
        sf_params=sf_params,
    ))
    return _job_to_response(job)
