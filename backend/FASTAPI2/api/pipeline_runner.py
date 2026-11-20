"""
pipeline_runner.py
Thin adapter layer between the FastAPI routes and the existing src/ pipeline
modules. Each function here is synchronous (called via asyncio.to_thread by
jobs.JobManager.run) and accepts:
  - a progress_cb(str) callback for status updates
  - an optional sf_params dict with Snowflake connection details passed
    directly from the API request (no .env / environment variables needed)

sf_params mirrors the SnowflakeConnection schema fields:
    account, user, warehouse, database, role,
    schema_ (bronze schema), silver_schema, gold_schema,
    pat_token | password | private_key_path [+ private_key_passphrase]
"""

import json
import re
import sys
from dataclasses import asdict
from pathlib import Path
from typing import Optional

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))
from config import config
import pbix_parser
import table_matcher
import mquery_translator
import dax_translator
import deploy_sql
import validator
import migration_report
from snowflake_client import list_tables, list_columns
import pbix_extractor         



def _sf_schema(sf_params: Optional[dict], key: str, fallback: str) -> str:
    """Pull a schema name from sf_params or fall back to a string default."""
    if sf_params:
        return sf_params.get(key) or fallback
    return fallback

# ---------------------------------------------------------------------------
# Stage 0: Extract  (.pbix -> extracted metadata JSON; no Snowflake needed)
# ---------------------------------------------------------------------------

def run_extract(pbix_path: str, output_path: Optional[str] = None, progress_cb=None):
    progress_cb = progress_cb or (lambda m: None)
    progress_cb(f"Extracting metadata from {pbix_path}")

    raw_results = pbix_extractor.extract_all(pbix_path)
    payload = pbix_extractor.build_json_payload(raw_results, Path(pbix_path).name)

    out_path = Path(output_path) if output_path else (config.mapping_dir / "extracted_metadata.json")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2)

    s = payload["summary"]
    progress_cb(
        f"Extracted {s['table_count']} tables, {s['dax_measure_count']} measures, "
        f"{s['calculated_table_count']} calculated tables"
    )
    return {"output_file": str(out_path), "summary": s}


def run_extract_and_parse(pbix_path: str, progress_cb=None):
    """Chained stage: raw .pbix -> extracted JSON -> parsed_model.json, one job."""
    progress_cb = progress_cb or (lambda m: None)
    extract_result = run_extract(pbix_path, progress_cb=lambda m: progress_cb(f"[extract] {m}"))
    parse_result = run_parse(extract_result["output_file"], progress_cb=lambda m: progress_cb(f"[parse] {m}"))
    return {"extract": extract_result, "parse": parse_result}


# ---------------------------------------------------------------------------
# Stage 1: Parse  (no Snowflake needed)
# ---------------------------------------------------------------------------

def run_parse(input_path: str, output_path: Optional[str] = None, progress_cb=None):
    progress_cb = progress_cb or (lambda m: None)
    progress_cb(f"Reading PBIX metadata from {input_path}")

    model = pbix_parser.parse_pbix_metadata(input_path)

    out_path = Path(output_path) if output_path else (config.mapping_dir / "parsed_model.json")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(model.to_dict(), f, indent=2)

    progress_cb(f"Parsed {len(model.tables)} tables, {len(model.measures)} measures")
    return {
        "output_file": str(out_path),
        "tables": len(model.tables),
        "measures": len(model.measures),
        "calculated_columns": len(model.calculated_columns),
        "relationships": len(model.relationships),
        "table_names": [t.name for t in model.tables],
    }


# ---------------------------------------------------------------------------
# Stage 2: Table matching  (reads Snowflake Bronze schema)
# ---------------------------------------------------------------------------

def run_match_tables(model_path: Optional[str] = None, output_path: Optional[str] = None,
                      bronze_schema: Optional[str] = None, manual_overrides: Optional[dict] = None,
                      sf_params: Optional[dict] = None, progress_cb=None):
    """
    sf_params: Snowflake connection dict from the API request.
    manual_overrides: optional {pbix_table_name: bronze_table_name}.
    """
    progress_cb = progress_cb or (lambda m: None)

    model_path = model_path or str(config.mapping_dir / "parsed_model.json")
    with open(model_path, "r", encoding="utf-8") as f:
        parsed_model = json.load(f)

    progress_cb("Discovering Bronze tables and matching (may call Azure OpenAI for ambiguous tables)...")
    matches = table_matcher.match_tables(parsed_model, bronze_schema=bronze_schema,
                                          sf_params=sf_params)

    if manual_overrides:
        by_name = {m.pbix_table: m for m in matches}
        for pbix_name, bronze_name in manual_overrides.items():
            if pbix_name in by_name:
                by_name[pbix_name].bronze_table = bronze_name
                by_name[pbix_name].method = "manual"
                by_name[pbix_name].confidence = 1.0
                progress_cb(f"Manual override applied: {pbix_name} -> {bronze_name}")

    out_path = Path(output_path) if output_path else (config.mapping_dir / "table_mapping.json")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump([asdict(m) for m in matches], f, indent=2)

    needs_review = [asdict(m) for m in matches if m.method in ("llm_low_confidence", "unmatched")]
    matched_count = sum(1 for m in matches if m.bronze_table or m.method == "calculated_table")

    progress_cb(f"Matched {matched_count}/{len(matches)} tables; {len(needs_review)} need manual review")
    return {
        "output_file": str(out_path),
        "total_tables": len(matches),
        "matched": matched_count,
        "needs_review": needs_review,
        "all_matches": [asdict(m) for m in matches],
    }


def run_list_bronze_tables(schema: Optional[str] = None, sf_params: Optional[dict] = None,
                            progress_cb=None):
    """List available Bronze tables so a UI can offer a manual-match dropdown."""
    progress_cb = progress_cb or (lambda m: None)
    progress_cb("Querying INFORMATION_SCHEMA.TABLES...")
    tables = list_tables(schema=schema, sf_params=sf_params)
    target_schema = schema or _sf_schema(sf_params, "schema_", "BRONZE")
    return {"schema": target_schema, "tables": [t["TABLE_NAME"] for t in tables]}


# ---------------------------------------------------------------------------
# Stage 3: M query -> Silver SQL  (schema names from sf_params or config)
# ---------------------------------------------------------------------------

def run_translate_silver(model_path: Optional[str] = None, mapping_path: Optional[str] = None,
                          output_dir: Optional[str] = None, sf_params: Optional[dict] = None,
                          progress_cb=None):
    progress_cb = progress_cb or (lambda m: None)

    model_path = model_path or str(config.mapping_dir / "parsed_model.json")
    mapping_path = mapping_path or str(config.mapping_dir / "table_mapping.json")
    out_dir = Path(output_dir) if output_dir else config.sql_silver_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    with open(model_path, "r", encoding="utf-8") as f:
        parsed_model = json.load(f)
    with open(mapping_path, "r", encoding="utf-8") as f:
        table_mapping = json.load(f)

    mapping_by_pbix = {m["pbix_table"]: m for m in table_mapping}

    # Resolve schema/database from request params or fall back to config
    if sf_params:
        db = sf_params.get("database", "")
        silver_schema = sf_params.get("silver_schema", "SILVER")
    else:
        sf = config.snowflake
        db = sf.database
        silver_schema = sf.silver_schema

    # Build Silver FQN map upfront for ALL tables so every translate_table call
    # knows where to find joined Silver views (e.g. MIGRATION_DD.SILVER.EMP)
    all_silver_fqns = {
        t["name"]: f"{db}.{silver_schema}.{re.sub(r'[^A-Za-z0-9_]', '_', t['name']).upper()}"
        for t in parsed_model["tables"]
    }

    # Group calculated columns by table upfront — baked into Silver, not Gold
    from collections import defaultdict
    calc_cols_by_table: dict = defaultdict(list)
    for col in parsed_model.get("calculated_columns", []):
        calc_cols_by_table[col["table"]].append(col)

    results = []
    bronze_cols_cache: dict = {}

    for table in parsed_model["tables"]:
        name = table["name"]
        progress_cb(f"Translating M query for table: {name}")
        match = mapping_by_pbix.get(name)
        bronze_fqn = None
        bronze_columns = None
        column_mapping = None

        if match and match.get("bronze_table"):
            bronze_fqn = f'{db}.{match["bronze_schema"]}.{match["bronze_table"]}'
            column_mapping = match.get("column_mapping") or None

            # Always fetch the REAL live Bronze column list for the matched
            # table and ground the LLM in it, rather than relying solely on
            # the fuzzy column_mapping from the match stage (which can be
            # empty — e.g. when the PBIX table has few/no native columns —
            # even though the actual Bronze table has plenty of columns).
            # Without this, the LLM has nothing to check identifiers
            # against and will hallucinate column names (e.g. "PERSON").
            cache_key = (match["bronze_schema"], match["bronze_table"])
            if cache_key not in bronze_cols_cache:
                try:
                    cols = list_columns(
                        table_name=match["bronze_table"],
                        schema=match["bronze_schema"],
                        sf_params=sf_params,
                    )
                    bronze_cols_cache[cache_key] = [c["COLUMN_NAME"] for c in cols]
                except Exception as e:
                    progress_cb(
                        f"Could not fetch live columns for "
                        f"{match['bronze_schema']}.{match['bronze_table']}: {e}"
                    )
                    bronze_cols_cache[cache_key] = []
            bronze_columns = bronze_cols_cache[cache_key]

        result = mquery_translator.translate_table(
            table, bronze_fqn,
            column_mapping=column_mapping,
            bronze_columns=bronze_columns,
            all_bronze_columns={
                f'{db}.{s}.{t}': cols
                for (s, t), cols in bronze_cols_cache.items()
            },
            all_silver_fqns=all_silver_fqns,
            calc_columns=calc_cols_by_table.get(name) or None,
        )
        results.append(result)

        if result["sql"]:
            ddl = mquery_translator.build_silver_ddl(name, result["sql"], silver_schema)
            safe_name = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
            out_file = out_dir / f"silver_{safe_name}.sql"
            with open(out_file, "w", encoding="utf-8") as f:
                f.write(ddl)
            result["output_file"] = str(out_file)
            result["ddl"] = ddl

    summary_path = out_dir / "_translation_summary.json"
    with open(summary_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)

    ok_count = sum(1 for r in results if r["sql"])
    warnings = [(r["table_name"], w) for r in results for w in r.get("warnings", [])]
    progress_cb(f"Generated {ok_count}/{len(results)} Silver SQL objects, {len(warnings)} warnings")

    return {
        "output_dir": str(out_dir),
        "summary_file": str(summary_path),
        "objects_generated": ok_count,
        "total_tables": len(results),
        "warnings": [{"table": t, "warning": w} for t, w in warnings],
        "results": results,
    }


# ---------------------------------------------------------------------------
# Stage 4: DAX -> Gold SQL  (schema names from sf_params or config)
# ---------------------------------------------------------------------------

def run_translate_gold(model_path: Optional[str] = None, output_dir: Optional[str] = None,
                        sf_params: Optional[dict] = None, progress_cb=None):
    progress_cb = progress_cb or (lambda m: None)

    model_path = model_path or str(config.mapping_dir / "parsed_model.json")
    out_dir = Path(output_dir) if output_dir else config.sql_gold_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    with open(model_path, "r", encoding="utf-8") as f:
        parsed_model = json.load(f)

    if sf_params:
        db = sf_params.get("database", "")
        silver_schema = sf_params.get("silver_schema", "SILVER")
        gold_schema = sf_params.get("gold_schema", "GOLD")
    else:
        sf = config.snowflake
        db = sf.database
        silver_schema = sf.silver_schema
        gold_schema = sf.gold_schema

    all_table_names = [t["name"] for t in parsed_model["tables"]]
    # Build exact Silver FQN map so the LLM cannot hallucinate table names like "SALES"
    silver_table_map = dax_translator._build_silver_table_map(all_table_names, db, silver_schema)

    # Build a map of {table_name: [exact_col_name, ...]} by reading the already-generated
    # Silver SQL files.  This gives the Gold LLM the precise quoted column aliases
    # (e.g. '"City"', '"Sub-Category"') so it never guesses unquoted uppercase names
    # or uses table-prefix syntax inside a view.
    silver_columns_map: dict = {}
    silver_sql_dir = config.sql_silver_dir
    for table_name in all_table_names:
        safe = re.sub(r"[^A-Za-z0-9_]", "_", table_name).upper()
        silver_file = silver_sql_dir / f"silver_{safe}.sql"
        if silver_file.exists():
            ddl_text = silver_file.read_text(encoding="utf-8")
            # Extract column aliases from lines like:  CITY::VARCHAR AS "City",
            # or  Person::VARCHAR AS Person  (unquoted)
            cols = re.findall(r'\bAS\s+("[\w\s\-&%./]+"|\w+)', ddl_text, re.IGNORECASE)
            # Filter out schema-level aliases like the view name itself
            cols = [c for c in cols if c.upper() not in (safe, "SILVER", "GOLD", "BRONZE")]
            if cols:
                silver_columns_map[table_name] = cols

    results = []

    for measure in parsed_model.get("measures", []):
        progress_cb(f"Translating measure: {measure['name']}")
        try:
            sql = dax_translator._llm_translate_measure(
                measure, all_table_names, silver_schema, db,
                silver_table_map=silver_table_map,
                silver_columns_map=silver_columns_map,
            )
            method = "llm"
        except Exception as e:
            sql = f'-- TODO: manual review: LLM translation failed ({e})\nSELECT NULL AS "{measure["name"]}"'
            method = "llm_failed_fallback"

        ddl = dax_translator.build_gold_ddl(measure["name"], "measure", sql, gold_schema)
        safe_name = re.sub(r"[^A-Za-z0-9_]", "_", measure["name"]).upper()
        out_file = out_dir / f"gold_kpi_{safe_name}.sql"
        with open(out_file, "w", encoding="utf-8") as f:
            f.write(ddl)
        results.append({"name": measure["name"], "kind": "measure", "method": method,
                         "output_file": str(out_file), "ddl": ddl})

    # NOTE: Calculated columns are no longer processed here.
    # They are baked directly into the Silver layer SQL during run_translate_silver.
    # Gold only contains measures (KPI views) and calculated tables.

    for table in parsed_model["tables"]:
        if not table.get("is_calculated_table"):
            continue
        progress_cb(f"Translating calculated table: {table['name']}")
        try:
            sql = dax_translator._llm_translate_calc_table(table, all_table_names, silver_schema, db, silver_table_map=silver_table_map)
            method = "llm"
        except Exception as e:
            sql = "-- TODO: manual review: LLM translation failed ({e})\nSELECT 1 AS placeholder"
            method = "llm_failed_fallback"

        ddl = dax_translator.build_gold_ddl(table["name"], "calculated_table", sql, gold_schema)
        safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table["name"]).upper()
        out_file = out_dir / f"gold_table_{safe_name}.sql"
        with open(out_file, "w", encoding="utf-8") as f:
            f.write(ddl)
        results.append({"name": table["name"], "kind": "calculated_table", "method": method,
                         "output_file": str(out_file), "ddl": ddl})

    summary_path = out_dir / "_translation_summary.json"
    with open(summary_path, "w", encoding="utf-8") as f:
        json.dump([{k: v for k, v in r.items() if k != "ddl"} for r in results], f, indent=2)

    progress_cb(f"Generated {len(results)} Gold SQL objects")
    return {
        "output_dir": str(out_dir),
        "summary_file": str(summary_path),
        "objects_generated": len(results),
        "results": results,
    }


# ---------------------------------------------------------------------------
# Stage 5: Deploy  (writes to Snowflake — sf_params required unless dry-run)
# ---------------------------------------------------------------------------

def run_deploy(layer: str = "all", force: bool = False,
               sf_params: Optional[dict] = None, progress_cb=None):
    progress_cb = progress_cb or (lambda m: None)

    original_dry_run = config.pipeline.dry_run
    if force:
        config.pipeline.dry_run = False
        progress_cb("force=true: SQL will execute for real.")
    else:
        progress_cb("DRY_RUN mode — SQL logged but not executed. Pass force=true to deploy.")

    if sf_params:
        silver_schema = sf_params.get("silver_schema", "SILVER")
        gold_schema = sf_params.get("gold_schema", "GOLD")
    else:
        sf = config.snowflake
        silver_schema = sf.silver_schema
        gold_schema = sf.gold_schema

    all_results = {}
    try:
        if layer in ("silver", "all"):
            progress_cb("Deploying SILVER layer...")
            all_results["silver"] = deploy_sql.deploy_layer(
                config.sql_silver_dir, silver_schema, sf_params=sf_params,
                progress_cb=progress_cb,
            )

        silver_had_unresolved_failure = any(
            r["status"] == "failed"          # hard failed (exhausted retries)
            for r in all_results.get("silver", [])
        )
        silver_had_skipped = any(
            r["status"] == "skipped"
            for r in all_results.get("silver", [])
        )
        if layer == "all" and silver_had_unresolved_failure:
            progress_cb(
                "Skipping GOLD layer — SILVER has hard failures that "
                "Gold objects depend on. Fix and re-run deploy."
            )
        elif layer in ("gold", "all"):
            progress_cb("Deploying GOLD layer...")
            all_results["gold"] = deploy_sql.deploy_layer(
                config.sql_gold_dir, gold_schema, sf_params=sf_params,
                progress_cb=progress_cb,
            )
    finally:
        config.pipeline.dry_run = original_dry_run

    out_path = config.reports_dir / "deployment_results.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(all_results, f, indent=2)

    summary = {
        layer_name: {
            "succeeded": sum(1 for r in results if r["status"] == "success"),
            "skipped":   sum(1 for r in results if r["status"] == "skipped"),
            "failed":    sum(1 for r in results if r["status"] == "failed"),
            "total":     len(results),
            "skipped_files": [
                {"file": Path(r["file"]).name, "error": (r.get("error") or "")[:300]}
                for r in results if r["status"] == "skipped"
            ],
            "failed_files": [
                {"file": Path(r["file"]).name, "error": (r.get("error") or "")[:300]}
                for r in results if r["status"] == "failed"
            ],
        }
        for layer_name, results in all_results.items()
    }
    progress_cb(f"Deployment complete: {summary}")
    return {"output_file": str(out_path), "summary": summary, "results": all_results, "dry_run": not force}


# ---------------------------------------------------------------------------
# Stage 6: Validate
# ---------------------------------------------------------------------------

def run_validate(mapping_path: Optional[str] = None, gold_summary_path: Optional[str] = None,
                  output_path: Optional[str] = None, sf_params: Optional[dict] = None,
                  progress_cb=None):
    progress_cb = progress_cb or (lambda m: None)

    mapping_path = mapping_path or str(config.mapping_dir / "table_mapping.json")
    gold_summary_path = gold_summary_path or str(config.sql_gold_dir / "_translation_summary.json")

    with open(mapping_path, "r", encoding="utf-8") as f:
        table_mapping = json.load(f)

    progress_cb("Validating Silver layer objects against Bronze sources...")
    silver_results = validator.validate_table_mapping(table_mapping, sf_params=sf_params)

    progress_cb("Validating Gold layer objects...")
    gold_results = validator.validate_gold_objects(Path(gold_summary_path), sf_params=sf_params)

    output = {"silver_validation": silver_results, "gold_validation": gold_results}
    out_path = Path(output_path) if output_path else (config.reports_dir / "validation_results.json")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2)

    silver_fail = sum(1 for e in silver_results if any(c["status"] == "error" for c in e["checks"]))
    gold_fail = sum(1 for g in gold_results if g["status"] == "error")
    progress_cb(
        f"Validation complete: silver {len(silver_results) - silver_fail}/{len(silver_results)} OK, "
        f"gold {len(gold_results) - gold_fail}/{len(gold_results)} OK"
    )
    return {"output_file": str(out_path), "silver_validation": silver_results, "gold_validation": gold_results}


# ---------------------------------------------------------------------------
# Stage 7: Report  (no Snowflake needed)
# ---------------------------------------------------------------------------

def run_report(output_path: Optional[str] = None, progress_cb=None):
    progress_cb = progress_cb or (lambda m: None)
    progress_cb("Aggregating pipeline outputs into report...")

    report_text = migration_report.generate_report()
    out_path = Path(output_path) if output_path else (config.reports_dir / "migration_report.md")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(report_text)

    progress_cb("Report generated.")
    return {"output_file": str(out_path), "report_markdown": report_text}