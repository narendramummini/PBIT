"""
main.py
FastAPI application exposing the PBIX -> Snowflake migration pipeline as a
REST API. Snowflake credentials are passed directly in each request body —
no .env file or environment variables are required.

Run:
    uvicorn api.main:app --reload --host 0.0.0.0 --port 8000

Then open http://localhost:8000/docs for interactive Swagger UI.
"""

import shutil
import sys
import uuid
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, UploadFile, File, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from starlette.responses import JSONResponse

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from config import config
from logger import get_logger
from jobs import job_manager, JobStatus
import pipeline_runner as pr
from schemas import (
    JobResponse, ParseRequest, MatchTablesRequest, BronzeTablesRequest, ManualMatchRequest,
    DeployRequest, ValidateRequest, ReportRequest, RunAllRequest,
    SnowflakeConnection,
)
from security import authenticate_migration, current_claims, workspace_root

log = get_logger("api.main", config.logs_dir, config.pipeline.log_level)

app = FastAPI(
    title="PBIX → Snowflake Migration API",
    description=(
        "REST API wrapping the PBIX metadata → Snowflake Bronze/Silver/Gold migration pipeline.\n\n"
        "**No .env or environment variables needed** — pass your Snowflake credentials directly "
        "in the request body of any endpoint that touches Snowflake.\n\n"
        "Every stage runs as an async job: POST to start, GET /jobs/{job_id} to poll."
    ),
    version="1.1.0",
)
app.middleware("http")(authenticate_migration)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

def _upload_path(filename: str, job_id: str) -> Path:
    suffix = Path(filename or "").suffix.lower()
    return workspace_root() / "uploads" / job_id / f"{uuid.uuid4().hex}{suffix}"


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
            "POST /tables/bronze",
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
    claims = current_claims()
    job = job_manager.get(job_id, claims["migration_id"], claims["owner_id"])
    if not job:
        raise HTTPException(404, f"No job found with id {job_id}")
    return _job_to_response(job)


@app.get("/jobs", response_model=list[JobResponse], tags=["jobs"])
async def list_jobs(stage: Optional[str] = None):
    claims = current_claims()
    return [_job_to_response(j) for j in job_manager.list(
        claims["migration_id"], claims["owner_id"], stage=stage)]

# ---------------------------------------------------------------------------
# Stage 0: Extract  (raw .pbix -> metadata JSON)
# ---------------------------------------------------------------------------

@app.post("/extract/upload", response_model=JobResponse, tags=["0. extract"])
async def extract_upload(file: UploadFile = File(...)):
    """Upload a raw .pbix file and extract its metadata to JSON (does not parse/match yet)."""
    if not _is_pbix(file.filename):
        raise HTTPException(400, "Expected a .pbix file")

    job = await job_manager.create("extract")
    dest = _upload_path(file.filename, job.id)
    dest.parent.mkdir(parents=True, exist_ok=True)
    with open(dest, "wb") as f:
        shutil.copyfileobj(file.file, f)
    log.info("Uploaded PBIX file for migration")

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

    job = await job_manager.create("extract_and_parse")
    dest = _upload_path(file.filename, job.id)
    dest.parent.mkdir(parents=True, exist_ok=True)
    with open(dest, "wb") as f:
        shutil.copyfileobj(file.file, f)
    log.info("Uploaded PBIX file for migration")

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
    input_path = Path(req.input_path).resolve()
    root = workspace_root().resolve()
    if root not in input_path.parents or not input_path.is_file():
        raise HTTPException(400, "input_path must reference a file in this migration workspace")

    job = await job_manager.create("parse")
    import asyncio
    asyncio.create_task(job_manager.run(job, pr.run_parse, str(input_path)))
    return _job_to_response(job)


@app.post("/parse/upload", response_model=JobResponse, tags=["1. parse"])
async def parse_upload(file: UploadFile = File(...)):
    """Upload a PBIX metadata JSON file directly and parse it."""
    if not file.filename.endswith(".json"):
        raise HTTPException(400, "Expected a .json file")

    job = await job_manager.create("parse")
    dest = _upload_path(file.filename, job.id)
    dest.parent.mkdir(parents=True, exist_ok=True)
    with open(dest, "wb") as f:
        shutil.copyfileobj(file.file, f)
    log.info("Uploaded PBIX metadata for migration")

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


@app.post("/tables/bronze", tags=["2. table matching"])
async def list_bronze_tables(req: BronzeTablesRequest):
    """
    Synchronous helper — list available Bronze tables in Snowflake.
    Useful for building a manual-match picker UI.
    Credentials are accepted only in an authenticated request body, never in a URL.
    """
    sf_params = _sf_to_dict(req.snowflake)
    try:
        return pr.run_list_bronze_tables(schema=req.schema_name, sf_params=sf_params)
    except Exception:
        raise HTTPException(500, "Failed to list Bronze tables") from None


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
    import os
    if req.force and os.getenv("ENABLE_SNOWFLAKE_DDL", "").lower() != "true":
        raise HTTPException(403, "Snowflake DDL is disabled by deployment policy")
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
    import os
    if req.deploy and (not req.force or os.getenv("ENABLE_SNOWFLAKE_DDL", "").lower() != "true"):
        raise HTTPException(403, "Snowflake DDL requires explicit force approval and enabled deployment policy")
    input_path = Path(req.input_path).resolve()
    root = workspace_root().resolve()
    if root not in input_path.parents or not input_path.is_file():
        raise HTTPException(400, "input_path must reference a file in this migration workspace")

    input_path = str(input_path)

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
