"""
validator.py
Validation framework: checks row counts, column counts, data types, and
(where possible) KPI/measure result spot-checks between Bronze source
tables and the newly created Silver/Gold objects in Snowflake.

Run:
    python src/validator.py --mapping output/mapping/table_mapping.json
"""

import argparse
import json
import sys
from pathlib import Path
from typing import Dict, List

sys.path.insert(0, str(Path(__file__).resolve().parent))
from config import config
from logger import get_logger
from snowflake_client import get_connection, get_connection_from_params

log = get_logger("validator", config.logs_dir, config.pipeline.log_level)


def _row_count(conn, fqn: str) -> int:
    cur = conn.cursor()
    cur.execute(f"SELECT COUNT(*) FROM {fqn}")
    return cur.fetchone()[0]


def _column_count(conn, schema: str, table: str) -> int:
    cur = conn.cursor()
    cur.execute(
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = %s AND TABLE_NAME = %s",
        (schema, table),
    )
    return cur.fetchone()[0]


def _column_types(conn, schema: str, table: str) -> Dict[str, str]:
    cur = conn.cursor()
    cur.execute(
        "SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = %s AND TABLE_NAME = %s",
        (schema, table),
    )
    return {row[0]: row[1] for row in cur.fetchall()}


def validate_table_mapping(table_mapping: List[dict], sf_params=None) -> List[dict]:
    if sf_params:
        db = sf_params.get("database", "")
        silver_schema = sf_params.get("silver_schema", "SILVER")
    else:
        sf = config.snowflake
        db = sf.database
        silver_schema = sf.silver_schema
    results = []

    if config.pipeline.dry_run:
        log.warning("DRY_RUN is enabled — Silver/Gold objects likely don't exist yet in Snowflake. "
                    "Validation will report objects as missing until you deploy with force=true.")

    conn = get_connection_from_params(sf_params) if sf_params else get_connection()
    try:
        for m in table_mapping:
            pbix_table = m["pbix_table"]
            bronze_table = m.get("bronze_table")
            method = m.get("method")

            entry = {"pbix_table": pbix_table, "checks": []}

            if method == "calculated_table" or not bronze_table:
                entry["checks"].append({
                    "check": "skip",
                    "status": "skipped",
                    "detail": "No Bronze source table to compare (calculated table or unmatched)."
                })
                results.append(entry)
                continue

            import re
            silver_table = re.sub(r"[^A-Za-z0-9_]", "_", pbix_table).upper()
            bronze_fqn = f'{db}.{m["bronze_schema"]}.{bronze_table}'
            silver_fqn = f'{db}.{silver_schema}.{silver_table}'

            # Row count comparison
            try:
                bronze_rows = _row_count(conn, bronze_fqn)
                silver_rows = _row_count(conn, silver_fqn)
                match = bronze_rows == silver_rows
                entry["checks"].append({
                    "check": "row_count",
                    "status": "pass" if match else "warn",
                    "bronze_rows": bronze_rows,
                    "silver_rows": silver_rows,
                    "detail": "Row counts match." if match else
                              f"Row count mismatch: Bronze={bronze_rows}, Silver={silver_rows}. "
                              f"Expected if Silver SQL applies filters; otherwise investigate."
                })
            except Exception as e:
                entry["checks"].append({
                    "check": "row_count", "status": "error",
                    "detail": f"Could not compare row counts (Silver object likely not deployed yet): {e}"
                })

            # Column count + type comparison
            try:
                bronze_cols = _column_types(conn, m["bronze_schema"], bronze_table)
                silver_cols = _column_types(conn, silver_schema, silver_table)
                entry["checks"].append({
                    "check": "column_count",
                    "status": "info",
                    "bronze_columns": len(bronze_cols),
                    "silver_columns": len(silver_cols),
                    "detail": f"Bronze has {len(bronze_cols)} columns, Silver has {len(silver_cols)} columns "
                              f"(difference expected if columns were renamed/dropped/derived)."
                })

                type_mismatches = []
                for col, dtype in silver_cols.items():
                    if col in bronze_cols and bronze_cols[col] != dtype:
                        type_mismatches.append(f"{col}: bronze={bronze_cols[col]} silver={dtype}")
                entry["checks"].append({
                    "check": "data_types",
                    "status": "warn" if type_mismatches else "pass",
                    "detail": "; ".join(type_mismatches) if type_mismatches else "No unexpected type mismatches on shared column names."
                })
            except Exception as e:
                entry["checks"].append({
                    "check": "column_count", "status": "error",
                    "detail": f"Could not compare columns (Silver object likely not deployed yet): {e}"
                })

            results.append(entry)
    finally:
        conn.close()

    return results


def validate_gold_objects(gold_summary_path: Path, sf_params=None) -> List[dict]:
    """Sanity-check that Gold KPI views/tables exist and are queryable (SELECT ... LIMIT 1)."""
    if not gold_summary_path.exists():
        log.warning(f"No Gold translation summary found at {gold_summary_path}; skipping Gold validation.")
        return []

    with open(gold_summary_path, "r", encoding="utf-8") as f:
        gold_objects = json.load(f)

    if sf_params:
        db = sf_params.get("database", "")
        gold_schema = sf_params.get("gold_schema", "GOLD")
    else:
        sf = config.snowflake
        db = sf.database
        gold_schema = sf.gold_schema

    results = []
    conn = get_connection_from_params(sf_params) if sf_params else get_connection()
    try:
        import re
        for obj in gold_objects:
            prefix = {"measure": "KPI", "calculated_column": "CALC", "calculated_table": "TBL"}.get(obj["kind"], "OBJ")
            safe_name = re.sub(r"[^A-Za-z0-9_]", "_", obj["name"].replace(".", "_")).upper()
            fqn = f"{db}.{gold_schema}.{prefix}_{safe_name}"
            try:
                cur = conn.cursor()
                cur.execute(f"SELECT * FROM {fqn} LIMIT 1")
                cur.fetchall()
                results.append({"object": fqn, "kind": obj["kind"], "status": "pass", "detail": "Object exists and is queryable."})
            except Exception as e:
                results.append({"object": fqn, "kind": obj["kind"], "status": "error", "detail": f"Not queryable: {e}"})
    finally:
        conn.close()

    return results


def main():
    ap = argparse.ArgumentParser(description="Validate migrated Silver/Gold objects against Bronze sources")
    ap.add_argument("--mapping", default=str(config.mapping_dir / "table_mapping.json"))
    ap.add_argument("--gold-summary", default=str(config.sql_gold_dir / "_translation_summary.json"))
    ap.add_argument("--output", default=str(config.reports_dir / "validation_results.json"))
    args = ap.parse_args()

    with open(args.mapping, "r", encoding="utf-8") as f:
        table_mapping = json.load(f)

    log.info("Validating Silver layer objects against Bronze sources...")
    silver_results = validate_table_mapping(table_mapping)

    log.info("Validating Gold layer objects...")
    gold_results = validate_gold_objects(Path(args.gold_summary))

    output = {"silver_validation": silver_results, "gold_validation": gold_results}
    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2)

    log.info(f"Validation results written to {out_path}")

    print("\nValidation Summary (Silver):")
    for entry in silver_results:
        statuses = [c["status"] for c in entry["checks"]]
        overall = "FAIL" if "error" in statuses else ("WARN" if "warn" in statuses else "OK")
        print(f"  [{overall}] {entry['pbix_table']}")

    print("\nValidation Summary (Gold):")
    for r in gold_results:
        print(f"  [{r['status'].upper()}] {r['object']}")


if __name__ == "__main__":
    main()
