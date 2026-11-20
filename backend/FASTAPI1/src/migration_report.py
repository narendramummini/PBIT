"""
migration_report.py
Aggregates outputs from every pipeline stage (parsed model, table mapping,
Silver/Gold translation summaries, validation results) into a single
human-readable Markdown migration report.

Run:
    python src/migration_report.py
"""

import argparse
import json
import sys
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from config import config
from logger import get_logger

log = get_logger("migration_report", config.logs_dir, config.pipeline.log_level)


def _load_json(path: Path, default=None):
    if not path.exists():
        return default
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def generate_report() -> str:
    parsed_model = _load_json(config.mapping_dir / "parsed_model.json", {})
    table_mapping = _load_json(config.mapping_dir / "table_mapping.json", [])
    silver_summary = _load_json(config.sql_silver_dir / "_translation_summary.json", [])
    gold_summary = _load_json(config.sql_gold_dir / "_translation_summary.json", [])
    validation = _load_json(config.reports_dir / "validation_results.json", {})
    deployment = _load_json(config.reports_dir / "deployment_results.json", {})

    tables_total = len(parsed_model.get("tables", []))
    measures_total = len(parsed_model.get("measures", []))
    calc_cols_total = len(parsed_model.get("calculated_columns", []))
    rels_total = len(parsed_model.get("relationships", []))

    matched = sum(1 for m in table_mapping if m.get("bronze_table") or m.get("method") == "calculated_table")
    unmatched = [m for m in table_mapping if not m.get("bronze_table") and m.get("method") != "calculated_table"]

    silver_ok = sum(1 for s in silver_summary if s.get("sql"))
    silver_warnings = [(s["table_name"], w) for s in silver_summary for w in s.get("warnings", [])]

    gold_by_kind = {}
    for g in gold_summary:
        gold_by_kind.setdefault(g["kind"], []).append(g)

    lines = []
    lines.append(f"# PBIX → Snowflake Migration Report")
    lines.append(f"\nGenerated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    lines.append(f"\nMode: {'DRY RUN (no SQL executed)' if config.pipeline.dry_run else 'LIVE DEPLOYMENT'}")

    lines.append("\n## 1. Source Model Summary")
    lines.append(f"- Tables: **{tables_total}**")
    lines.append(f"- Measures: **{measures_total}**")
    lines.append(f"- Calculated columns: **{calc_cols_total}**")
    lines.append(f"- Relationships: **{rels_total}**")

    lines.append("\n## 2. Table Matching (PBIX ↔ Bronze)")
    lines.append(f"- Matched: **{matched}/{len(table_mapping)}**")
    if unmatched:
        lines.append(f"- ⚠️ Unmatched tables requiring attention:")
        for m in unmatched:
            lines.append(f"  - `{m['pbix_table']}` (method={m.get('method')}, confidence={m.get('confidence')})")
    method_counts = {}
    for m in table_mapping:
        method_counts[m.get("method")] = method_counts.get(m.get("method"), 0) + 1
    lines.append(f"- Match methods breakdown: {dict(method_counts)}")

    lines.append("\n## 3. Silver Layer (Power Query → SQL)")
    lines.append(f"- Objects generated: **{silver_ok}/{len(silver_summary)}**")
    method_counts_s = {}
    for s in silver_summary:
        method_counts_s[s.get("method")] = method_counts_s.get(s.get("method"), 0) + 1
    lines.append(f"- Translation method breakdown: {dict(method_counts_s)}")
    if silver_warnings:
        lines.append(f"- ⚠️ Warnings ({len(silver_warnings)}):")
        for tname, w in silver_warnings:
            lines.append(f"  - `{tname}`: {w}")

    lines.append("\n## 4. Gold Layer (DAX → SQL)")
    for kind, items in gold_by_kind.items():
        lines.append(f"- {kind}: **{len(items)}** objects generated")
    if not gold_summary:
        lines.append("- No Gold-layer objects generated yet (run dax_translator.py).")

    lines.append("\n## 5. Validation Results")
    silver_val = validation.get("silver_validation", [])
    gold_val = validation.get("gold_validation", [])
    if silver_val:
        fail_count = 0
        warn_count = 0
        for entry in silver_val:
            statuses = [c["status"] for c in entry["checks"]]
            if "error" in statuses:
                fail_count += 1
            elif "warn" in statuses:
                warn_count += 1
        lines.append(f"- Silver: {len(silver_val)} tables checked — "
                      f"{len(silver_val) - fail_count - warn_count} OK, {warn_count} warnings, {fail_count} errors")
        for entry in silver_val:
            statuses = [c["status"] for c in entry["checks"]]
            if "error" in statuses or "warn" in statuses:
                lines.append(f"  - `{entry['pbix_table']}`:")
                for c in entry["checks"]:
                    if c["status"] in ("error", "warn"):
                        lines.append(f"    - [{c['status'].upper()}] {c['check']}: {c['detail']}")
    else:
        lines.append("- No Silver validation results yet (run validator.py).")

    if gold_val:
        gold_fail = sum(1 for g in gold_val if g["status"] == "error")
        lines.append(f"- Gold: {len(gold_val)} objects checked — {len(gold_val) - gold_fail} OK, {gold_fail} errors")
        for g in gold_val:
            if g["status"] == "error":
                lines.append(f"  - [ERROR] `{g['object']}`: {g['detail']}")
    else:
        lines.append("- No Gold validation results yet.")

    lines.append("\n## 6. Deployment Results")
    if deployment:
        for layer, results in deployment.items():
            ok = sum(1 for r in results if r["status"] == "success")
            lines.append(f"- {layer.capitalize()}: {ok}/{len(results)} objects deployed successfully")
            for r in results:
                if r["status"] == "failed":
                    lines.append(f"  - [FAILED] `{r['file']}`: {r.get('error')}")
    else:
        lines.append("- No deployment has been run yet (run deploy_sql.py --force to deploy).")

    lines.append("\n## 7. Recommended Next Steps")
    next_steps = []
    if unmatched:
        next_steps.append("Manually review and resolve unmatched PBIX→Bronze table mappings.")
    if silver_warnings:
        next_steps.append("Review flagged Silver SQL files for 'TODO: manual review' comments.")
    if config.pipeline.dry_run:
        next_steps.append("Re-run with DRY_RUN=false (or `--force`) to actually deploy Silver/Gold objects to Snowflake.")
    if not validation:
        next_steps.append("Run validator.py after deployment to confirm row/column/type parity.")
    if not next_steps:
        next_steps.append("No outstanding issues detected — migration looks complete. Spot-check KPI values in Gold views against the original Power BI report.")
    for s in next_steps:
        lines.append(f"- {s}")

    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description="Generate final migration report")
    ap.add_argument("--output", default=str(config.reports_dir / "migration_report.md"))
    args = ap.parse_args()

    report = generate_report()
    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(report)

    log.info(f"Migration report written to {out_path}")
    print(f"\nReport written to {out_path}\n")
    print(report)


if __name__ == "__main__":
    main()
