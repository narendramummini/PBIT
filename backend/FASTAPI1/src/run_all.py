"""
run_all.py
End-to-end orchestrator: runs every pipeline stage in order. Each stage is
also runnable independently (see README.md) — this just chains them for
convenience, stopping on the first hard failure.

Run:
    python src/run_all.py
    python src/run_all.py --skip-deploy        # generate SQL only, don't touch Snowflake writes
    python src/run_all.py --deploy --force      # also deploy + validate for real
"""

import argparse
import subprocess
import sys
from pathlib import Path

SRC_DIR = Path(__file__).resolve().parent


def run_step(label: str, args: list):
    print(f"\n{'='*70}\nSTEP: {label}\n{'='*70}")
    result = subprocess.run([sys.executable] + args, cwd=str(SRC_DIR))
    if result.returncode != 0:
        print(f"\n[FATAL] Step '{label}' failed with exit code {result.returncode}. Stopping pipeline.")
        sys.exit(result.returncode)


def main():
    ap = argparse.ArgumentParser(description="Run the full PBIX -> Snowflake migration pipeline")
    ap.add_argument("--input", default=None, help="Override PBIX metadata JSON path")
    ap.add_argument("--no-interactive", action="store_true", help="Skip manual table-match review prompts")
    ap.add_argument("--deploy", action="store_true", help="Also run deploy_sql.py after generating SQL")
    ap.add_argument("--force", action="store_true", help="Combined with --deploy: actually execute SQL (overrides DRY_RUN)")
    ap.add_argument("--validate", action="store_true", help="Also run validator.py (recommended after --deploy)")
    args = ap.parse_args()

    parse_args = ["pbix_parser.py"]
    if args.input:
        parse_args += ["--input", args.input]
    run_step("1/7 Parse PBIX metadata JSON", parse_args)

    match_args = ["table_matcher.py"]
    if args.no_interactive:
        match_args.append("--no-interactive")
    run_step("2/7 Match PBIX tables to Snowflake Bronze tables (Azure OpenAI-assisted)", match_args)

    run_step("3/7 Translate Power Query (M) -> Snowflake SQL (Silver layer)", ["mquery_translator.py"])

    run_step("4/7 Translate DAX (measures/calc columns/calc tables) -> Snowflake SQL (Gold layer)", ["dax_translator.py"])

    if args.deploy:
        deploy_args = ["deploy_sql.py", "--layer", "all"]
        if args.force:
            deploy_args.append("--force")
        run_step("5/7 Deploy Silver + Gold SQL to Snowflake", deploy_args)
    else:
        print("\n(Skipping deployment — pass --deploy [--force] to create objects in Snowflake.)")

    if args.validate:
        run_step("6/7 Validate migrated objects against Bronze sources", ["validator.py"])
    else:
        print("\n(Skipping validation — pass --validate after a real --deploy --force run.)")

    run_step("7/7 Generate migration report", ["migration_report.py"])

    print("\nPipeline complete. See output/reports/migration_report.md for the full summary.")


if __name__ == "__main__":
    main()
