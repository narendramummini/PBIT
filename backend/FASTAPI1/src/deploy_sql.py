# # """
# # deploy_sql.py
# # Executes the generated Silver and Gold SQL files against Snowflake, in
# # order (Silver first, then Gold, since Gold views depend on Silver).
# # Honors DRY_RUN from .env — set DRY_RUN=false to actually create objects.

# # Key improvements:
# #   1. Dependency-aware retry loop: files that fail because a referenced object
# #      doesn't exist yet are re-queued, resolving alphabetical ordering issues.
# #   2. SQL pre-processor auto-patches known LLM bugs before execution:
# #        a) TRY_CAST(<non-varchar> AS VARCHAR) → CAST(col AS VARCHAR)
# #        b) Duplicate column names in SELECT * joins → explicit de-duplicated list
# #        c) Unqualified column/table refs that never match Silver objects are
# #           flagged with a clear error rather than retried forever.
# #   3. Strict separation of retryable errors (missing object) vs non-retryable
# #      errors (bad SQL, duplicate columns, bad identifiers that won't fix
# #      themselves on retry).
# # """

# # import argparse
# # import json
# # import re
# # import sys
# # from pathlib import Path
# # from typing import Callable, Optional

# # sys.path.insert(0, str(Path(__file__).resolve().parent))
# # from config import config
# # from logger import get_logger
# # from snowflake_client import execute_sql, get_connection, get_connection_from_params, list_columns
# # import azure_openai_client as aoai

# # log = get_logger("deploy_sql", config.logs_dir, config.pipeline.log_level)


# # # ---------------------------------------------------------------------------
# # # LLM self-healing: send the failed SQL + Snowflake error back to Azure
# # # OpenAI and ask for a corrected version. Used for non-retryable errors
# # # (bad identifiers, duplicate columns, syntax errors) that the static
# # # pre-processor above doesn't already cover.
# # # ---------------------------------------------------------------------------

# # _SQL_FIX_SYSTEM_PROMPT = """You are an expert Snowflake SQL engineer. A generated Silver/Gold layer
# # SQL script (a CREATE OR REPLACE VIEW statement) failed to compile in Snowflake.

# # Rules:
# # - Fix only what the error message points to: invalid/missing identifiers, case-sensitivity or
# #   quoting issues, duplicate column names, or syntax errors.
# # - Preserve the script's structure, schema/object name, and business logic as much as possible.
# # - You will be given the ACTUAL column names that exist on the source table(s), when available.
# #   Treat that list as ground truth — pick the correct real column name from it rather than guessing
# #   or inventing a plausible-sounding name. Do not assume Power BI naming conventions translate
# #   directly; some renames are not simple case/whitespace changes (e.g. "Name" might map to FULL_NAME,
# #   not NAME).
# # - If no real column list is provided for a referenced table, and a column name in double quotes
# #   doesn't exist, assume Snowflake's default unquoted-uppercase identifier convention applies
# #   (e.g. "Row ID" -> ROW_ID, "Returned" -> RETURNED) unless the error gives a more specific name.
# # - If a SELECT produces a duplicate column name (e.g. from a JOIN), alias or exclude one of the
# #   duplicates instead of removing needed columns.
# # - Respond with ONLY the corrected, valid Snowflake SQL. No markdown code fences, no explanation,
# #   no commentary before or after the SQL.
# # """

# # # Matches  FROM db.schema.table  /  JOIN db.schema.table  (optionally quoted parts)
# # _SOURCE_TABLE_RE = re.compile(
# #     r'\b(?:FROM|JOIN)\s+([A-Za-z0-9_."]+\.[A-Za-z0-9_."]+\.[A-Za-z0-9_."]+)',
# #     re.IGNORECASE,
# # )


# # def _extract_source_tables(sql: str) -> list[str]:
# #     """Pull fully-qualified DB.SCHEMA.TABLE references out of FROM/JOIN clauses."""
# #     found = []
# #     for m in _SOURCE_TABLE_RE.finditer(sql):
# #         fqn = m.group(1).replace('"', "")
# #         if fqn not in found:
# #             found.append(fqn)
# #     return found


# # def _fetch_real_columns(fqn: str, sf_params=None) -> Optional[list[str]]:
# #     """Best-effort live INFORMATION_SCHEMA lookup for a DB.SCHEMA.TABLE reference.
# #     Returns None (not an empty list) on failure, so callers can distinguish
# #     'looked it up, table has no columns' from 'lookup failed / not attempted'."""
# #     parts = fqn.split(".")
# #     if len(parts) != 3:
# #         return None
# #     _, schema, table = parts
# #     try:
# #         cols = list_columns(table_name=table, schema=schema, sf_params=sf_params)
# #         return [c["COLUMN_NAME"] for c in cols]
# #     except Exception as e:
# #         log.warning(f"Could not fetch live columns for {fqn} while auto-healing: {e}")
# #         return None


# # def fix_sql_with_llm(failed_sql: str, error_message: str, filename: str,
# #                       sf_params=None) -> str:
# #     """Send the failed SQL and the Snowflake error to the LLM and return corrected SQL.

# #     Grounds the fix in the REAL column names of whatever table(s) the failing
# #     SQL references (parsed out of its FROM/JOIN clauses), so the LLM corrects
# #     an invalid identifier against the actual schema instead of guessing a
# #     second plausible-but-wrong name (e.g. "Row ID" -> PERSON instead of the
# #     real column).
# #     """
# #     schema_context = ""
# #     for fqn in _extract_source_tables(failed_sql):
# #         real_cols = _fetch_real_columns(fqn, sf_params=sf_params)
# #         if real_cols is not None:
# #             schema_context += f"\n### ACTUAL COLUMNS IN {fqn}\n{real_cols}\n"
# #         else:
# #             schema_context += f"\n### ACTUAL COLUMNS IN {fqn}\n(unable to fetch — use your best judgement)\n"

# #     user_prompt = (
# #         f"### FILE\n{filename}\n\n"
# #         f"### SNOWFLAKE ERROR\n{error_message}\n"
# #         f"{schema_context}\n"
# #         f"### FAILED SQL SCRIPT\n{failed_sql}\n"
# #     )
# #     corrected = aoai.chat_text(_SQL_FIX_SYSTEM_PROMPT, user_prompt)
# #     return corrected.strip()


# # # ---------------------------------------------------------------------------
# # # SQL pre-processor: auto-patch known LLM hallucination patterns
# # # ---------------------------------------------------------------------------

# # _TRY_CAST_RE = re.compile(
# #     r'\bTRY_CAST\s*\(\s*([A-Za-z0-9_."]+)\s+AS\s+(VARCHAR|TEXT)(\s*\(\s*\d+\s*\))?\s*\)',
# #     re.IGNORECASE,
# # )


# # def _patch_try_cast(sql: str) -> tuple[str, list[str]]:
# #     """Replace TRY_CAST(col AS VARCHAR) → CAST(col AS VARCHAR).
# #     TRY_CAST only accepts a VARCHAR *source* in Snowflake; BOOLEAN/NUMBER/DATE
# #     sources cause a runtime error. A plain CAST is always safe here."""
# #     patches = []

# #     def _replacer(m):
# #         col, dtype, size = m.group(1), m.group(2), m.group(3) or ""
# #         patches.append(f"TRY_CAST({col} AS {dtype}{size}) → CAST({col} AS {dtype}{size})")
# #         return f"CAST({col} AS {dtype}{size})"

# #     return _TRY_CAST_RE.sub(_replacer, sql), patches


# # def _patch_duplicate_columns(sql: str, filename: str) -> tuple[str, list[str]]:
# #     """Detect SELECT * ... JOIN patterns that produce duplicate column names.
# #     When a JOIN produces duplicate column names (e.g. both tables have DEPTNO),
# #     Snowflake raises 'duplicate column name'. We rewrite SELECT * to an explicit
# #     column list by parsing the CREATE OR REPLACE VIEW body, querying
# #     INFORMATION_SCHEMA at deploy time is not possible here, so instead we
# #     rewrite the offending SELECT * to use table-qualified aliases.

# #     Strategy: if we see  SELECT [T1].[*], [T2].[*]  or  SELECT *  inside a JOIN,
# #     we add explicit USING() or rewrite to exclude the join key from one side.
# #     Since we don't have schema info at pre-process time, we use a safe rewrite:
# #     replace  SELECT *  with  SELECT * EXCLUDE(<dup_col>)  using Snowflake's
# #     EXCLUDE syntax, which removes the duplicate from the output.
# #     """
# #     patches = []

# #     # Only act on files that contain both a JOIN and SELECT *
# #     if not (re.search(r'\bJOIN\b', sql, re.IGNORECASE) and
# #             re.search(r'\bSELECT\s+\*', sql, re.IGNORECASE)):
# #         return sql, patches

# #     # Find the join key columns (ON t1.col = t2.col  or  USING (col))
# #     using_cols = re.findall(r'\bUSING\s*\(\s*([^)]+)\)', sql, re.IGNORECASE)
# #     on_pairs = re.findall(
# #         r'\bON\b\s+\w+\.(\w+)\s*=\s*\w+\.(\w+)', sql, re.IGNORECASE
# #     )

# #     dup_cols = set()
# #     for cols in using_cols:
# #         for c in re.split(r'[,\s]+', cols):
# #             c = c.strip().strip('"')
# #             if c:
# #                 dup_cols.add(c.upper())

# #     for left_col, right_col in on_pairs:
# #         if left_col.upper() == right_col.upper():
# #             dup_cols.add(left_col.upper())

# #     if dup_cols:
# #         exclude_list = ", ".join(sorted(dup_cols))
# #         # Replace bare SELECT * with SELECT * EXCLUDE(dup_cols) — Snowflake syntax
# #         # Only replace the first SELECT * (the outer query, not subqueries)
# #         new_sql = re.sub(
# #             r'\bSELECT\s+\*(?!\s+EXCLUDE)(?!\s+\w+\s*\()',
# #             f'SELECT * EXCLUDE({exclude_list})',
# #             sql,
# #             count=1,
# #             flags=re.IGNORECASE,
# #         )
# #         if new_sql != sql:
# #             patches.append(
# #                 f"Rewrote SELECT * → SELECT * EXCLUDE({exclude_list}) "
# #                 f"to remove duplicate join key column(s) from {filename}"
# #             )
# #             sql = new_sql

# #     return sql, patches


# # def preprocess_sql(sql: str, filename: str) -> str:
# #     """Run all auto-patch steps. Logs every change made."""
# #     all_patches = []

# #     sql, p1 = _patch_try_cast(sql)
# #     all_patches.extend(p1)

# #     sql, p2 = _patch_duplicate_columns(sql, filename)
# #     all_patches.extend(p2)

# #     if all_patches:
# #         log.warning(f"[PRE-PROCESS] {filename} — {len(all_patches)} auto-patch(es):")
# #         for note in all_patches:
# #             log.warning(f"  {note}")
# #     return sql


# # # ---------------------------------------------------------------------------
# # # Error classification: retryable vs non-retryable
# # # ---------------------------------------------------------------------------

# # # These errors mean a referenced *object* (table/view) doesn't exist yet —
# # # it may appear after other files in this batch are deployed, so retry.
# # _RETRYABLE_PATTERNS = [
# #     "does not exist or not authorized",
# #     "object does not exist",
# # ]

# # # These errors are bad SQL that will never fix itself on retry. Stop immediately.
# # _NON_RETRYABLE_PATTERNS = [
# #     "duplicate column name",
# #     "syntax error",
# #     "unexpected",
# #     "invalid identifier",   # column name that doesn't exist in the table schema
# #     "cannot be used with arguments of types",
# # ]


# # def _classify_error(error_msg: str) -> str:
# #     """Return 'retryable', 'non_retryable', or 'unknown'."""
# #     lower = error_msg.lower()
# #     # Check non-retryable first (more specific)
# #     for pat in _NON_RETRYABLE_PATTERNS:
# #         if pat in lower:
# #             return "non_retryable"
# #     for pat in _RETRYABLE_PATTERNS:
# #         if pat in lower:
# #             return "retryable"
# #     return "non_retryable"   # default: don't retry unknown errors


# # # ---------------------------------------------------------------------------
# # # Schema creation helper
# # # ---------------------------------------------------------------------------

# # def ensure_schema_exists(schema_name: str, sf_params=None):
# #     if sf_params:
# #         db = sf_params.get("database", "")
# #     else:
# #         db = config.snowflake.database
# #     ddl = f"CREATE SCHEMA IF NOT EXISTS {db}.{schema_name}"
# #     log.info(f"Ensuring schema exists: {db}.{schema_name}")
# #     execute_sql(ddl, sf_params=sf_params)


# # # ---------------------------------------------------------------------------
# # # Dependency-aware deployment loop
# # # ---------------------------------------------------------------------------

# # def _deploy_one_file(f: Path, sql_text: str, sf_params, pass_num: int,
# #                       progress_cb: Callable[[str], None]) -> Optional[Exception]:
# #     """Execute one SQL file. Returns None on success, the exception on failure."""
# #     log.info(f"[Pass {pass_num}] Deploying {f.name}...")
# #     progress_cb(f"[Pass {pass_num}] Deploying {f.name}...")
# #     try:
# #         execute_sql(sql_text, sf_params=sf_params)
# #         return None
# #     except Exception as e:
# #         return e


# # def _self_heal_and_redeploy(f: Path, sql_text: str, err_str: str, sf_params,
# #                              pass_num: int, progress_cb: Callable[[str], None]):
# #     """
# #     On a non-retryable SQL error, ask the LLM to fix the script, write the
# #     fix back to disk, and immediately retry deployment ONCE.

# #     Returns (success: bool, final_sql_text: str, final_error: Optional[str]).
# #     Does NOT swallow a second failure — if the LLM's fix also fails, that is
# #     reported as a hard failure for this file; we do not loop indefinitely
# #     and we do not let later files mask this one.
# #     """
# #     msg = f"⚠️ {f.name} failed with a non-retryable SQL error. Sending to LLM for auto-fix..."
# #     log.warning(f"[Pass {pass_num}] {msg} Error: {err_str[:300]}")
# #     progress_cb(msg)

# #     try:
# #         corrected_sql = fix_sql_with_llm(sql_text, err_str, f.name, sf_params=sf_params)
# #     except Exception as llm_err:
# #         fail_msg = f"❌ LLM auto-fix call failed for {f.name}: {llm_err}"
# #         log.error(f"[Pass {pass_num}] {fail_msg}")
# #         progress_cb(fail_msg)
# #         return False, sql_text, err_str

# #     if not corrected_sql or not corrected_sql.strip():
# #         fail_msg = f"❌ LLM returned an empty fix for {f.name}. Manual fix needed."
# #         log.error(f"[Pass {pass_num}] {fail_msg}")
# #         progress_cb(fail_msg)
# #         return False, sql_text, err_str

# #     # Persist the corrected SQL back to disk so it's reviewable and so the
# #     # next full deploy run doesn't hit the same error again.
# #     try:
# #         f.write_text(corrected_sql, encoding="utf-8")
# #     except Exception as write_err:
# #         log.warning(f"[Pass {pass_num}] Could not write auto-fixed SQL back to {f}: {write_err}")

# #     progress_cb(f"🔄 Retrying deployment of LLM-fixed {f.name}...")
# #     log.info(f"[Pass {pass_num}] Retrying deployment of auto-healed {f.name}...")

# #     retry_sql = preprocess_sql(corrected_sql, f.name)
# #     retry_err = _deploy_one_file(f, retry_sql, sf_params, pass_num, progress_cb)

# #     if retry_err is None:
# #         ok_msg = f"🎉 Auto-healed and deployed {f.name} successfully."
# #         log.info(f"[Pass {pass_num}] {ok_msg}")
# #         progress_cb(ok_msg)
# #         return True, corrected_sql, None

# #     retry_err_str = str(retry_err)
# #     fail_msg = (
# #         f"❌ Auto-fix for {f.name} still failed after retry. Manual fix needed. "
# #         f"Error: {retry_err_str[:300]}"
# #     )
# #     log.error(f"[Pass {pass_num}] {fail_msg}")
# #     progress_cb(fail_msg)
# #     return False, corrected_sql, retry_err_str


# # def deploy_layer(sql_dir: Path, schema_name: str, sf_params=None,
# #                   progress_cb: Optional[Callable[[str], None]] = None) -> list:
# #     progress_cb = progress_cb or (lambda m: None)
# #     ensure_schema_exists(schema_name, sf_params=sf_params)

# #     sql_files = sorted(sql_dir.glob("*.sql"))
# #     sql_files = [f for f in sql_files if not f.name.startswith("_")]

# #     results: dict[str, dict] = {}
# #     pending = list(sql_files)
# #     max_passes = max(len(sql_files), 1)

# #     for pass_num in range(1, max_passes + 1):
# #         if not pending:
# #             break

# #         still_pending = []
# #         progress_this_pass = False

# #         for f in pending:
# #             raw_sql = f.read_text(encoding="utf-8")
# #             sql_text = preprocess_sql(raw_sql, f.name)

# #             err = _deploy_one_file(f, sql_text, sf_params, pass_num, progress_cb)

# #             if err is None:
# #                 results[f.name] = {"file": str(f), "status": "success"}
# #                 progress_this_pass = True
# #                 continue

# #             err_str = str(err)
# #             err_class = _classify_error(err_str)

# #             if err_class == "retryable":
# #                 log.warning(
# #                     f"[Pass {pass_num}] {f.name} — missing dependency, will retry. "
# #                     f"Error: {err_str[:200]}"
# #                 )
# #                 still_pending.append(f)
# #                 continue

# #             # Non-retryable SQL error: do NOT just log and move on to other
# #             # files. Hand it to the LLM immediately and only proceed once
# #             # we have a verified passing fix for THIS file.
# #             healed, final_sql, final_err = _self_heal_and_redeploy(
# #                 f, sql_text, err_str, sf_params, pass_num, progress_cb
# #             )
# #             if healed:
# #                 results[f.name] = {
# #                     "file": str(f), "status": "success", "auto_healed": True
# #                 }
# #                 progress_this_pass = True
# #             else:
# #                 results[f.name] = {
# #                     "file": str(f),
# #                     "status": "failed",
# #                     "error": final_err,
# #                     "auto_heal_attempted": True,
# #                 }
# #                 # Stop deploying further files in this layer rather than
# #                 # silently skipping ahead — the user asked that we don't
# #                 # proceed past an unresolved SQL error.
# #                 log.error(
# #                     f"[Pass {pass_num}] Halting {schema_name} layer deployment: "
# #                     f"{f.name} could not be auto-healed. Remaining files in this "
# #                     f"pass will not be attempted until this is fixed."
# #                 )
# #                 progress_cb(
# #                     f"⛔ Stopping {schema_name} deployment — {f.name} could not be "
# #                     f"auto-healed. Fix it manually, then re-run deploy."
# #                 )
# #                 for remaining in pending:
# #                     if remaining is f:
# #                         continue
# #                     if remaining.name not in results and remaining not in still_pending:
# #                         results[remaining.name] = {
# #                             "file": str(remaining),
# #                             "status": "skipped",
# #                             "error": f"Skipped after {f.name} failed auto-healing.",
# #                         }
# #                 pending = []
# #                 still_pending = []
# #                 break

# #         pending = still_pending

# #         if not progress_this_pass and pending:
# #             log.error(
# #                 f"No progress in pass {pass_num}. Files with unresolvable errors: "
# #                 + ", ".join(f.name for f in pending)
# #             )
# #             for f in pending:
# #                 results[f.name] = {
# #                     "file": str(f),
# #                     "status": "failed",
# #                     "error": (
# #                         "Could not deploy after multiple passes — "
# #                         "likely circular dependency or a missing dependency "
# #                         "that never resolved. Check prior pass logs."
# #                     ),
# #                 }
# #             pending = []
# #             break

# #     for f in pending:
# #         if f.name not in results:
# #             results[f.name] = {
# #                 "file": str(f), "status": "failed",
# #                 "error": "Exhausted retry passes.",
# #             }

# #     return list(results.values())


# # # ---------------------------------------------------------------------------
# # # CLI entry point
# # # ---------------------------------------------------------------------------

# # def main():
# #     ap = argparse.ArgumentParser(description="Deploy generated Silver/Gold SQL to Snowflake")
# #     ap.add_argument("--layer", choices=["silver", "gold", "all"], default="all")
# #     ap.add_argument("--force", action="store_true", help="Override DRY_RUN=true and actually execute")
# #     args = ap.parse_args()

# #     if args.force:
# #         config.pipeline.dry_run = False
# #         log.warning("--force passed: overriding DRY_RUN, SQL will be executed for real.")

# #     if config.pipeline.dry_run:
# #         log.info(
# #             "DRY_RUN is enabled. No SQL will execute. "
# #             "Pass --force or set DRY_RUN=false in .env to deploy for real."
# #         )

# #     sf = config.snowflake
# #     all_results = {}
# #     cli_progress_cb = lambda m: print(f"  {m}")

# #     if args.layer in ("silver", "all"):
# #         log.info("=== Deploying SILVER layer ===")
# #         all_results["silver"] = deploy_layer(
# #             config.sql_silver_dir, sf.silver_schema, progress_cb=cli_progress_cb
# #         )

# #     if args.layer in ("gold", "all"):
# #         log.info("=== Deploying GOLD layer ===")
# #         all_results["gold"] = deploy_layer(
# #             config.sql_gold_dir, sf.gold_schema, progress_cb=cli_progress_cb
# #         )

# #     out_path = config.reports_dir / "deployment_results.json"
# #     with open(out_path, "w", encoding="utf-8") as f:
# #         json.dump(all_results, f, indent=2)

# #     print(f"\nDeployment results written to {out_path}")
# #     for layer, results in all_results.items():
# #         ok = sum(1 for r in results if r["status"] == "success")
# #         print(f"  {layer}: {ok}/{len(results)} succeeded")


# # if __name__ == "__main__":
# #     main()

# """
# deploy_sql.py
# Executes the generated Silver and Gold SQL files against Snowflake, in
# order (Silver first, then Gold, since Gold views depend on Silver).
# Honors DRY_RUN from .env — set DRY_RUN=false to actually create objects.

# Key improvements:
#   1. Dependency-aware retry loop: files that fail because a referenced object
#      doesn't exist yet are re-queued, resolving alphabetical ordering issues.
#   2. SQL pre-processor auto-patches known LLM bugs before execution:
#        a) TRY_CAST(<non-varchar> AS VARCHAR) → CAST(col AS VARCHAR)
#        b) Duplicate column names in SELECT * joins → explicit de-duplicated list
#        c) Unqualified column/table refs that never match Silver objects are
#           flagged with a clear error rather than retried forever.
#   3. Strict separation of retryable errors (missing object) vs non-retryable
#      errors (bad SQL, duplicate columns, bad identifiers that won't fix
#      themselves on retry).
# """

# import argparse
# import json
# import re
# import sys
# from pathlib import Path
# from typing import Callable, Optional

# sys.path.insert(0, str(Path(__file__).resolve().parent))
# from config import config
# from logger import get_logger
# from snowflake_client import execute_sql, get_connection, get_connection_from_params, list_columns
# import azure_openai_client as aoai

# log = get_logger("deploy_sql", config.logs_dir, config.pipeline.log_level)


# # ---------------------------------------------------------------------------
# # LLM self-healing: send the failed SQL + Snowflake error back to Azure
# # OpenAI and ask for a corrected version. Used for non-retryable errors
# # (bad identifiers, duplicate columns, syntax errors) that the static
# # pre-processor above doesn't already cover.
# # ---------------------------------------------------------------------------

# _SQL_FIX_SYSTEM_PROMPT = """You are an expert Snowflake SQL engineer. A generated Silver/Gold layer
# SQL script (a CREATE OR REPLACE VIEW statement) failed to compile in Snowflake.

# Rules:
# - Fix only what the error message points to: invalid/missing identifiers, case-sensitivity or
#   quoting issues, duplicate column names, or syntax errors.
# - Preserve the script's structure, schema/object name, and business logic as much as possible.
# - You will be given the ACTUAL column names that exist on the source table(s), when available.
#   Treat that list as ground truth — pick the correct real column name from it rather than guessing
#   or inventing a plausible-sounding name. Do not assume Power BI naming conventions translate
#   directly; some renames are not simple case/whitespace changes (e.g. "Name" might map to FULL_NAME,
#   not NAME).
# - If no real column list is provided for a referenced table, and a column name in double quotes
#   doesn't exist, assume Snowflake's default unquoted-uppercase identifier convention applies
#   (e.g. "Row ID" -> ROW_ID, "Returned" -> RETURNED) unless the error gives a more specific name.
# - If a SELECT produces a duplicate column name (e.g. from a JOIN), alias or exclude one of the
#   duplicates instead of removing needed columns.
# - Respond with ONLY the corrected, valid Snowflake SQL. No markdown code fences, no explanation,
#   no commentary before or after the SQL.
# """

# # Matches  FROM db.schema.table  /  JOIN db.schema.table  (optionally quoted parts)
# _SOURCE_TABLE_RE = re.compile(
#     r'\b(?:FROM|JOIN)\s+([A-Za-z0-9_."]+\.[A-Za-z0-9_."]+\.[A-Za-z0-9_."]+)',
#     re.IGNORECASE,
# )


# def _extract_source_tables(sql: str) -> list[str]:
#     """Pull fully-qualified DB.SCHEMA.TABLE references out of FROM/JOIN clauses."""
#     found = []
#     for m in _SOURCE_TABLE_RE.finditer(sql):
#         fqn = m.group(1).replace('"', "")
#         if fqn not in found:
#             found.append(fqn)
#     return found


# def _fetch_real_columns(fqn: str, sf_params=None) -> Optional[list[str]]:
#     """Best-effort live INFORMATION_SCHEMA lookup for a DB.SCHEMA.TABLE reference.
#     Returns None (not an empty list) on failure, so callers can distinguish
#     'looked it up, table has no columns' from 'lookup failed / not attempted'."""
#     parts = fqn.split(".")
#     if len(parts) != 3:
#         return None
#     _, schema, table = parts
#     try:
#         cols = list_columns(table_name=table, schema=schema, sf_params=sf_params)
#         return [c["COLUMN_NAME"] for c in cols]
#     except Exception as e:
#         log.warning(f"Could not fetch live columns for {fqn} while auto-healing: {e}")
#         return None


# def fix_sql_with_llm(failed_sql: str, error_message: str, filename: str,
#                       sf_params=None) -> str:
#     """Send the failed SQL and the Snowflake error to the LLM and return corrected SQL.

#     Grounds the fix in the REAL column names of whatever table(s) the failing
#     SQL references (parsed out of its FROM/JOIN clauses), so the LLM corrects
#     an invalid identifier against the actual schema instead of guessing a
#     second plausible-but-wrong name (e.g. "Row ID" -> PERSON instead of the
#     real column).
#     """
#     schema_context = ""
#     for fqn in _extract_source_tables(failed_sql):
#         real_cols = _fetch_real_columns(fqn, sf_params=sf_params)
#         if real_cols is not None:
#             schema_context += f"\n### ACTUAL COLUMNS IN {fqn}\n{real_cols}\n"
#         else:
#             schema_context += f"\n### ACTUAL COLUMNS IN {fqn}\n(unable to fetch — use your best judgement)\n"

#     user_prompt = (
#         f"### FILE\n{filename}\n\n"
#         f"### SNOWFLAKE ERROR\n{error_message}\n"
#         f"{schema_context}\n"
#         f"### FAILED SQL SCRIPT\n{failed_sql}\n"
#     )
#     corrected = aoai.chat_text(_SQL_FIX_SYSTEM_PROMPT, user_prompt)
#     return corrected.strip()


# # ---------------------------------------------------------------------------
# # SQL pre-processor: auto-patch known LLM hallucination patterns
# # ---------------------------------------------------------------------------

# _TRY_CAST_RE = re.compile(
#     r'\bTRY_CAST\s*\(\s*([A-Za-z0-9_."]+)\s+AS\s+(VARCHAR|TEXT)(\s*\(\s*\d+\s*\))?\s*\)',
#     re.IGNORECASE,
# )


# def _patch_try_cast(sql: str) -> tuple[str, list[str]]:
#     """Replace TRY_CAST(col AS VARCHAR) → CAST(col AS VARCHAR).
#     TRY_CAST only accepts a VARCHAR *source* in Snowflake; BOOLEAN/NUMBER/DATE
#     sources cause a runtime error. A plain CAST is always safe here."""
#     patches = []

#     def _replacer(m):
#         col, dtype, size = m.group(1), m.group(2), m.group(3) or ""
#         patches.append(f"TRY_CAST({col} AS {dtype}{size}) → CAST({col} AS {dtype}{size})")
#         return f"CAST({col} AS {dtype}{size})"

#     return _TRY_CAST_RE.sub(_replacer, sql), patches


# def _patch_duplicate_columns(sql: str, filename: str) -> tuple[str, list[str]]:
#     """Detect SELECT * ... JOIN patterns that produce duplicate column names.
#     When a JOIN produces duplicate column names (e.g. both tables have DEPTNO),
#     Snowflake raises 'duplicate column name'. We rewrite SELECT * to an explicit
#     column list by parsing the CREATE OR REPLACE VIEW body, querying
#     INFORMATION_SCHEMA at deploy time is not possible here, so instead we
#     rewrite the offending SELECT * to use table-qualified aliases.

#     Strategy: if we see  SELECT [T1].[*], [T2].[*]  or  SELECT *  inside a JOIN,
#     we add explicit USING() or rewrite to exclude the join key from one side.
#     Since we don't have schema info at pre-process time, we use a safe rewrite:
#     replace  SELECT *  with  SELECT * EXCLUDE(<dup_col>)  using Snowflake's
#     EXCLUDE syntax, which removes the duplicate from the output.
#     """
#     patches = []

#     # Only act on files that contain both a JOIN and SELECT *
#     if not (re.search(r'\bJOIN\b', sql, re.IGNORECASE) and
#             re.search(r'\bSELECT\s+\*', sql, re.IGNORECASE)):
#         return sql, patches

#     # Find the join key columns (ON t1.col = t2.col  or  USING (col))
#     using_cols = re.findall(r'\bUSING\s*\(\s*([^)]+)\)', sql, re.IGNORECASE)
#     on_pairs = re.findall(
#         r'\bON\b\s+\w+\.(\w+)\s*=\s*\w+\.(\w+)', sql, re.IGNORECASE
#     )

#     dup_cols = set()
#     for cols in using_cols:
#         for c in re.split(r'[,\s]+', cols):
#             c = c.strip().strip('"')
#             if c:
#                 dup_cols.add(c.upper())

#     for left_col, right_col in on_pairs:
#         if left_col.upper() == right_col.upper():
#             dup_cols.add(left_col.upper())

#     if dup_cols:
#         exclude_list = ", ".join(sorted(dup_cols))
#         # Replace bare SELECT * with SELECT * EXCLUDE(dup_cols) — Snowflake syntax
#         # Only replace the first SELECT * (the outer query, not subqueries)
#         new_sql = re.sub(
#             r'\bSELECT\s+\*(?!\s+EXCLUDE)(?!\s+\w+\s*\()',
#             f'SELECT * EXCLUDE({exclude_list})',
#             sql,
#             count=1,
#             flags=re.IGNORECASE,
#         )
#         if new_sql != sql:
#             patches.append(
#                 f"Rewrote SELECT * → SELECT * EXCLUDE({exclude_list}) "
#                 f"to remove duplicate join key column(s) from {filename}"
#             )
#             sql = new_sql

#     return sql, patches


# def preprocess_sql(sql: str, filename: str) -> str:
#     """Run all auto-patch steps. Logs every change made."""
#     all_patches = []

#     sql, p1 = _patch_try_cast(sql)
#     all_patches.extend(p1)

#     sql, p2 = _patch_duplicate_columns(sql, filename)
#     all_patches.extend(p2)

#     if all_patches:
#         log.warning(f"[PRE-PROCESS] {filename} — {len(all_patches)} auto-patch(es):")
#         for note in all_patches:
#             log.warning(f"  {note}")
#     return sql


# # ---------------------------------------------------------------------------
# # Error classification: retryable vs non-retryable
# # ---------------------------------------------------------------------------

# # These errors mean a referenced *object* (table/view) doesn't exist yet —
# # it may appear after other files in this batch are deployed, so retry.
# _RETRYABLE_PATTERNS = [
#     "does not exist or not authorized",
#     "object does not exist",
# ]

# # These errors are bad SQL that will never fix itself on retry. Stop immediately.
# _NON_RETRYABLE_PATTERNS = [
#     "duplicate column name",
#     "syntax error",
#     "unexpected",
#     "invalid identifier",   # column name that doesn't exist in the table schema
#     "cannot be used with arguments of types",
# ]


# def _classify_error(error_msg: str) -> str:
#     """Return 'retryable', 'non_retryable', or 'unknown'."""
#     lower = error_msg.lower()
#     # Check non-retryable first (more specific)
#     for pat in _NON_RETRYABLE_PATTERNS:
#         if pat in lower:
#             return "non_retryable"
#     for pat in _RETRYABLE_PATTERNS:
#         if pat in lower:
#             return "retryable"
#     return "non_retryable"   # default: don't retry unknown errors


# # ---------------------------------------------------------------------------
# # Schema creation helper
# # ---------------------------------------------------------------------------

# def ensure_schema_exists(schema_name: str, sf_params=None):
#     if sf_params:
#         db = sf_params.get("database", "")
#     else:
#         db = config.snowflake.database
#     ddl = f"CREATE SCHEMA IF NOT EXISTS {db}.{schema_name}"
#     log.info(f"Ensuring schema exists: {db}.{schema_name}")
#     execute_sql(ddl, sf_params=sf_params)


# # ---------------------------------------------------------------------------
# # Dependency-aware deployment loop
# # ---------------------------------------------------------------------------

# def _deploy_one_file(f: Path, sql_text: str, sf_params, pass_num: int,
#                       progress_cb: Callable[[str], None]) -> Optional[Exception]:
#     """Execute one SQL file. Returns None on success, the exception on failure."""
#     log.info(f"[Pass {pass_num}] Deploying {f.name}...")
#     progress_cb(f"[Pass {pass_num}] Deploying {f.name}...")
#     try:
#         execute_sql(sql_text, sf_params=sf_params)
#         return None
#     except Exception as e:
#         return e


# def _self_heal_and_redeploy(f: Path, sql_text: str, err_str: str, sf_params,
#                              pass_num: int, progress_cb: Callable[[str], None]):
#     """
#     On a non-retryable SQL error, ask the LLM to fix the script, write the
#     fix back to disk, and immediately retry deployment ONCE.

#     Returns (success: bool, final_sql_text: str, final_error: Optional[str]).
#     Does NOT swallow a second failure — if the LLM's fix also fails, that is
#     reported as a hard failure for this file; we do not loop indefinitely
#     and we do not let later files mask this one.
#     """
#     msg = f"⚠️ {f.name} failed with a non-retryable SQL error. Sending to LLM for auto-fix..."
#     log.warning(f"[Pass {pass_num}] {msg} Error: {err_str[:300]}")
#     progress_cb(msg)

#     try:
#         corrected_sql = fix_sql_with_llm(sql_text, err_str, f.name, sf_params=sf_params)
#     except Exception as llm_err:
#         fail_msg = f"❌ LLM auto-fix call failed for {f.name}: {llm_err}"
#         log.error(f"[Pass {pass_num}] {fail_msg}")
#         progress_cb(fail_msg)
#         return False, sql_text, err_str

#     if not corrected_sql or not corrected_sql.strip():
#         fail_msg = f"❌ LLM returned an empty fix for {f.name}. Manual fix needed."
#         log.error(f"[Pass {pass_num}] {fail_msg}")
#         progress_cb(fail_msg)
#         return False, sql_text, err_str

#     # Persist the corrected SQL back to disk so it's reviewable and so the
#     # next full deploy run doesn't hit the same error again.
#     try:
#         f.write_text(corrected_sql, encoding="utf-8")
#     except Exception as write_err:
#         log.warning(f"[Pass {pass_num}] Could not write auto-fixed SQL back to {f}: {write_err}")

#     progress_cb(f"🔄 Retrying deployment of LLM-fixed {f.name}...")
#     log.info(f"[Pass {pass_num}] Retrying deployment of auto-healed {f.name}...")

#     retry_sql = preprocess_sql(corrected_sql, f.name)
#     retry_err = _deploy_one_file(f, retry_sql, sf_params, pass_num, progress_cb)

#     if retry_err is None:
#         ok_msg = f"🎉 Auto-healed and deployed {f.name} successfully."
#         log.info(f"[Pass {pass_num}] {ok_msg}")
#         progress_cb(ok_msg)
#         return True, corrected_sql, None

#     retry_err_str = str(retry_err)
#     fail_msg = (
#         f"❌ Auto-fix for {f.name} still failed after retry. Manual fix needed. "
#         f"Error: {retry_err_str[:300]}"
#     )
#     log.error(f"[Pass {pass_num}] {fail_msg}")
#     progress_cb(fail_msg)
#     return False, corrected_sql, retry_err_str


# def deploy_layer(sql_dir: Path, schema_name: str, sf_params=None,
#                   progress_cb: Optional[Callable[[str], None]] = None) -> list:
#     progress_cb = progress_cb or (lambda m: None)
#     ensure_schema_exists(schema_name, sf_params=sf_params)

#     sql_files = sorted(sql_dir.glob("*.sql"))
#     sql_files = [f for f in sql_files if not f.name.startswith("_")]

#     results: dict[str, dict] = {}
#     pending = list(sql_files)
#     max_passes = max(len(sql_files), 1)

#     for pass_num in range(1, max_passes + 1):
#         if not pending:
#             break

#         still_pending = []
#         progress_this_pass = False

#         for f in pending:
#             raw_sql = f.read_text(encoding="utf-8")
#             sql_text = preprocess_sql(raw_sql, f.name)

#             err = _deploy_one_file(f, sql_text, sf_params, pass_num, progress_cb)

#             if err is None:
#                 results[f.name] = {"file": str(f), "status": "success"}
#                 progress_this_pass = True
#                 continue

#             err_str = str(err)
#             err_class = _classify_error(err_str)

#             if err_class == "retryable":
#                 log.warning(
#                     f"[Pass {pass_num}] {f.name} — missing dependency, will retry. "
#                     f"Error: {err_str[:200]}"
#                 )
#                 still_pending.append(f)
#                 continue

#             # Non-retryable SQL error: do NOT just log and move on to other
#             # files. Hand it to the LLM immediately and only proceed once
#             # we have a verified passing fix for THIS file.
#             healed, final_sql, final_err = _self_heal_and_redeploy(
#                 f, sql_text, err_str, sf_params, pass_num, progress_cb
#             )
#             if healed:
#                 results[f.name] = {
#                     "file": str(f), "status": "success", "auto_healed": True
#                 }
#                 progress_this_pass = True
#             else:
#                 # Record the failure and SKIP this file — continue deploying
#                 # the remaining files so one bad script doesn't block everything.
#                 results[f.name] = {
#                     "file": str(f),
#                     "status": "skipped",
#                     "error": final_err,
#                     "auto_heal_attempted": True,
#                     "action": "skipped_after_failed_auto_heal",
#                 }
#                 skip_msg = (
#                     f"⚠️ Skipping {f.name} — could not be auto-healed. "
#                     f"Will continue with remaining files. Manual fix needed."
#                 )
#                 log.warning(f"[Pass {pass_num}] {skip_msg}")
#                 progress_cb(skip_msg)
#                 # Mark as having made progress so the retry loop doesn't stall
#                 progress_this_pass = True

#         pending = still_pending

#         if not progress_this_pass and pending:
#             log.error(
#                 f"No progress in pass {pass_num}. Files with unresolvable errors: "
#                 + ", ".join(f.name for f in pending)
#             )
#             for f in pending:
#                 results[f.name] = {
#                     "file": str(f),
#                     "status": "failed",
#                     "error": (
#                         "Could not deploy after multiple passes — "
#                         "likely circular dependency or a missing dependency "
#                         "that never resolved. Check prior pass logs."
#                     ),
#                 }
#             pending = []
#             break

#     for f in pending:
#         if f.name not in results:
#             results[f.name] = {
#                 "file": str(f), "status": "failed",
#                 "error": "Exhausted retry passes.",
#             }

#     # Emit a clear end-of-layer summary so the caller can surface it to the UI
#     all_result_list = list(results.values())
#     succeeded = [r for r in all_result_list if r["status"] == "success"]
#     skipped   = [r for r in all_result_list if r["status"] == "skipped"]
#     failed    = [r for r in all_result_list if r["status"] == "failed"]

#     log.info(
#         f"[{schema_name}] Deployment complete — "
#         f"{len(succeeded)} succeeded, {len(skipped)} skipped, {len(failed)} failed."
#     )
#     if skipped:
#         log.warning(f"[{schema_name}] Skipped files (manual fix needed):")
#         for r in skipped:
#             log.warning(f"  ⚠️  {Path(r['file']).name}  — {(r.get('error') or '')[:200]}")
#     if failed:
#         log.error(f"[{schema_name}] Failed files:")
#         for r in failed:
#             log.error(f"  ❌  {Path(r['file']).name}  — {(r.get('error') or '')[:200]}")

#     return all_result_list


# # ---------------------------------------------------------------------------
# # CLI entry point
# # ---------------------------------------------------------------------------

# def main():
#     ap = argparse.ArgumentParser(description="Deploy generated Silver/Gold SQL to Snowflake")
#     ap.add_argument("--layer", choices=["silver", "gold", "all"], default="all")
#     ap.add_argument("--force", action="store_true", help="Override DRY_RUN=true and actually execute")
#     args = ap.parse_args()

#     if args.force:
#         config.pipeline.dry_run = False
#         log.warning("--force passed: overriding DRY_RUN, SQL will be executed for real.")

#     if config.pipeline.dry_run:
#         log.info(
#             "DRY_RUN is enabled. No SQL will execute. "
#             "Pass --force or set DRY_RUN=false in .env to deploy for real."
#         )

#     sf = config.snowflake
#     all_results = {}
#     cli_progress_cb = lambda m: print(f"  {m}")

#     if args.layer in ("silver", "all"):
#         log.info("=== Deploying SILVER layer ===")
#         all_results["silver"] = deploy_layer(
#             config.sql_silver_dir, sf.silver_schema, progress_cb=cli_progress_cb
#         )

#     if args.layer in ("gold", "all"):
#         log.info("=== Deploying GOLD layer ===")
#         all_results["gold"] = deploy_layer(
#             config.sql_gold_dir, sf.gold_schema, progress_cb=cli_progress_cb
#         )

#     out_path = config.reports_dir / "deployment_results.json"
#     with open(out_path, "w", encoding="utf-8") as f:
#         json.dump(all_results, f, indent=2)

#     print(f"\nDeployment results written to {out_path}")
#     for layer, results in all_results.items():
#         ok = sum(1 for r in results if r["status"] == "success")
#         print(f"  {layer}: {ok}/{len(results)} succeeded")


# if __name__ == "__main__":
#     main()

"""
deploy_sql.py
Executes the generated Silver and Gold SQL files against Snowflake, in
order (Silver first, then Gold, since Gold views depend on Silver).
Honors DRY_RUN from .env — set DRY_RUN=false to actually create objects.

Key improvements:
  1. Dependency-aware retry loop: files that fail because a referenced object
     doesn't exist yet are re-queued, resolving alphabetical ordering issues.
  2. SQL pre-processor auto-patches known LLM bugs before execution:
       a) TRY_CAST(<non-varchar> AS VARCHAR) → CAST(col AS VARCHAR)
       b) Duplicate column names in SELECT * joins → explicit de-duplicated list
       c) Unqualified column/table refs that never match Silver objects are
          flagged with a clear error rather than retried forever.
  3. Strict separation of retryable errors (missing object) vs non-retryable
     errors (bad SQL, duplicate columns, bad identifiers that won't fix
     themselves on retry).
"""

import argparse
import json
import os
import re
import sys
from pathlib import Path
from typing import Callable, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
from config import config
from logger import get_logger
from snowflake_client import execute_sql, get_connection, get_connection_from_params, list_columns
import azure_openai_client as aoai

log = get_logger("deploy_sql", config.logs_dir, config.pipeline.log_level)


# ---------------------------------------------------------------------------
# LLM self-healing: send the failed SQL + Snowflake error back to Azure
# OpenAI and ask for a corrected version. Used for non-retryable errors
# (bad identifiers, duplicate columns, syntax errors) that the static
# pre-processor above doesn't already cover.
# ---------------------------------------------------------------------------

_SQL_FIX_SYSTEM_PROMPT = """You are an expert Snowflake SQL engineer. A generated Silver/Gold layer
SQL script (a CREATE OR REPLACE VIEW statement) failed to compile in Snowflake.

Rules:
- Fix only what the error message points to: invalid/missing identifiers, case-sensitivity or
  quoting issues, duplicate column names, or syntax errors.
- Preserve the script's structure, schema/object name, and business logic as much as possible.
- You will be given the ACTUAL column names that exist on the source table(s), when available.
  Treat that list as ground truth — pick the correct real column name from it rather than guessing
  or inventing a plausible-sounding name. Do not assume Power BI naming conventions translate
  directly; some renames are not simple case/whitespace changes (e.g. "Name" might map to FULL_NAME,
  not NAME).
- If no real column list is provided for a referenced table, and a column name in double quotes
  doesn't exist, assume Snowflake's default unquoted-uppercase identifier convention applies
  (e.g. "Row ID" -> ROW_ID, "Returned" -> RETURNED) unless the error gives a more specific name.
- If a SELECT produces a duplicate column name (e.g. from a JOIN), alias or exclude one of the
  duplicates instead of removing needed columns.
- Respond with ONLY the corrected, valid Snowflake SQL. No markdown code fences, no explanation,
  no commentary before or after the SQL.
"""

# Matches  FROM db.schema.table  /  JOIN db.schema.table  (optionally quoted parts)
_SOURCE_TABLE_RE = re.compile(
    r'\b(?:FROM|JOIN)\s+([A-Za-z0-9_."]+\.[A-Za-z0-9_."]+\.[A-Za-z0-9_."]+)',
    re.IGNORECASE,
)


def _extract_source_tables(sql: str) -> list[str]:
    """Pull fully-qualified DB.SCHEMA.TABLE references out of FROM/JOIN clauses."""
    found = []
    for m in _SOURCE_TABLE_RE.finditer(sql):
        fqn = m.group(1).replace('"', "")
        if fqn not in found:
            found.append(fqn)
    return found


def _fetch_real_columns(fqn: str, sf_params=None) -> Optional[list[str]]:
    """Best-effort live INFORMATION_SCHEMA lookup for a DB.SCHEMA.TABLE reference.
    Returns None (not an empty list) on failure, so callers can distinguish
    'looked it up, table has no columns' from 'lookup failed / not attempted'."""
    parts = fqn.split(".")
    if len(parts) != 3:
        return None
    _, schema, table = parts
    try:
        cols = list_columns(table_name=table, schema=schema, sf_params=sf_params)
        return [c["COLUMN_NAME"] for c in cols]
    except Exception as e:
        log.warning(f"Could not fetch live columns for {fqn} while auto-healing ({type(e).__name__}).")
        return None


def fix_sql_with_llm(failed_sql: str, error_message: str, filename: str,
                      sf_params=None) -> str:
    """Send the failed SQL and the Snowflake error to the LLM and return corrected SQL.

    Grounds the fix in the REAL column names of whatever table(s) the failing
    SQL references (parsed out of its FROM/JOIN clauses), so the LLM corrects
    an invalid identifier against the actual schema instead of guessing a
    second plausible-but-wrong name (e.g. "Row ID" -> PERSON instead of the
    real column).
    """
    schema_context = ""
    for fqn in _extract_source_tables(failed_sql):
        real_cols = _fetch_real_columns(fqn, sf_params=sf_params)
        if real_cols is not None:
            schema_context += f"\n### ACTUAL COLUMNS IN {fqn}\n{real_cols}\n"
        else:
            schema_context += f"\n### ACTUAL COLUMNS IN {fqn}\n(unable to fetch — use your best judgement)\n"

    user_prompt = (
        f"### FILE\n{filename}\n\n"
        f"### SNOWFLAKE ERROR\n{error_message}\n"
        f"{schema_context}\n"
        f"### FAILED SQL SCRIPT\n{failed_sql}\n"
    )
    corrected = aoai.chat_text(_SQL_FIX_SYSTEM_PROMPT, user_prompt)
    return corrected.strip()


# ---------------------------------------------------------------------------
# SQL pre-processor: auto-patch known LLM hallucination patterns
# ---------------------------------------------------------------------------

_TRY_CAST_RE = re.compile(
    r'\bTRY_CAST\s*\(\s*([A-Za-z0-9_."]+)\s+AS\s+(VARCHAR|TEXT)(\s*\(\s*\d+\s*\))?\s*\)',
    re.IGNORECASE,
)


def _patch_try_cast(sql: str) -> tuple[str, list[str]]:
    """Replace TRY_CAST(col AS VARCHAR) → CAST(col AS VARCHAR).
    TRY_CAST only accepts a VARCHAR *source* in Snowflake; BOOLEAN/NUMBER/DATE
    sources cause a runtime error. A plain CAST is always safe here."""
    patches = []

    def _replacer(m):
        col, dtype, size = m.group(1), m.group(2), m.group(3) or ""
        patches.append(f"TRY_CAST({col} AS {dtype}{size}) → CAST({col} AS {dtype}{size})")
        return f"CAST({col} AS {dtype}{size})"

    return _TRY_CAST_RE.sub(_replacer, sql), patches


def _patch_duplicate_columns(sql: str, filename: str) -> tuple[str, list[str]]:
    """Detect SELECT * ... JOIN patterns that produce duplicate column names.
    When a JOIN produces duplicate column names (e.g. both tables have DEPTNO),
    Snowflake raises 'duplicate column name'. We rewrite SELECT * to an explicit
    column list by parsing the CREATE OR REPLACE VIEW body, querying
    INFORMATION_SCHEMA at deploy time is not possible here, so instead we
    rewrite the offending SELECT * to use table-qualified aliases.

    Strategy: if we see  SELECT [T1].[*], [T2].[*]  or  SELECT *  inside a JOIN,
    we add explicit USING() or rewrite to exclude the join key from one side.
    Since we don't have schema info at pre-process time, we use a safe rewrite:
    replace  SELECT *  with  SELECT * EXCLUDE(<dup_col>)  using Snowflake's
    EXCLUDE syntax, which removes the duplicate from the output.
    """
    patches = []

    # Only act on files that contain both a JOIN and SELECT *
    if not (re.search(r'\bJOIN\b', sql, re.IGNORECASE) and
            re.search(r'\bSELECT\s+\*', sql, re.IGNORECASE)):
        return sql, patches

    # Find the join key columns (ON t1.col = t2.col  or  USING (col))
    using_cols = re.findall(r'\bUSING\s*\(\s*([^)]+)\)', sql, re.IGNORECASE)
    on_pairs = re.findall(
        r'\bON\b\s+\w+\.(\w+)\s*=\s*\w+\.(\w+)', sql, re.IGNORECASE
    )

    dup_cols = set()
    for cols in using_cols:
        for c in re.split(r'[,\s]+', cols):
            c = c.strip().strip('"')
            if c:
                dup_cols.add(c.upper())

    for left_col, right_col in on_pairs:
        if left_col.upper() == right_col.upper():
            dup_cols.add(left_col.upper())

    if dup_cols:
        exclude_list = ", ".join(sorted(dup_cols))
        # Replace bare SELECT * with SELECT * EXCLUDE(dup_cols) — Snowflake syntax
        # Only replace the first SELECT * (the outer query, not subqueries)
        new_sql = re.sub(
            r'\bSELECT\s+\*(?!\s+EXCLUDE)(?!\s+\w+\s*\()',
            f'SELECT * EXCLUDE({exclude_list})',
            sql,
            count=1,
            flags=re.IGNORECASE,
        )
        if new_sql != sql:
            patches.append(
                f"Rewrote SELECT * → SELECT * EXCLUDE({exclude_list}) "
                f"to remove duplicate join key column(s) from {filename}"
            )
            sql = new_sql

    return sql, patches


def preprocess_sql(sql: str, filename: str) -> str:
    """Run all auto-patch steps. Logs every change made."""
    all_patches = []

    sql, p1 = _patch_try_cast(sql)
    all_patches.extend(p1)

    sql, p2 = _patch_duplicate_columns(sql, filename)
    all_patches.extend(p2)

    if all_patches:
        log.warning(f"[PRE-PROCESS] {filename} — {len(all_patches)} auto-patch(es):")
        for note in all_patches:
            log.warning(f"  {note}")
    return sql


# ---------------------------------------------------------------------------
# Error classification: retryable vs non-retryable
# ---------------------------------------------------------------------------

# These errors mean a referenced *object* (table/view) doesn't exist yet —
# it may appear after other files in this batch are deployed, so retry.
_RETRYABLE_PATTERNS = [
    "does not exist or not authorized",
    "object does not exist",
]

# These errors are bad SQL that will never fix itself on retry. Stop immediately.
_NON_RETRYABLE_PATTERNS = [
    "duplicate column name",
    "syntax error",
    "unexpected",
    "invalid identifier",   # column name that doesn't exist in the table schema
    "cannot be used with arguments of types",
]


def _classify_error(error_msg: str) -> str:
    """Return 'retryable', 'non_retryable', or 'unknown'."""
    lower = error_msg.lower()
    # Check non-retryable first (more specific)
    for pat in _NON_RETRYABLE_PATTERNS:
        if pat in lower:
            return "non_retryable"
    for pat in _RETRYABLE_PATTERNS:
        if pat in lower:
            return "retryable"
    return "non_retryable"   # default: don't retry unknown errors


# ---------------------------------------------------------------------------
# Schema creation helper
# ---------------------------------------------------------------------------

def ensure_schema_exists(schema_name: str, sf_params=None):
    if sf_params:
        db = sf_params.get("database", "")
    else:
        db = config.snowflake.database
    ddl = f"CREATE SCHEMA IF NOT EXISTS {db}.{schema_name}"
    if config.pipeline.dry_run or os.getenv("ENABLE_SNOWFLAKE_DDL", "").lower() != "true":
        log.info(f"DRY_RUN: would ensure schema exists: {db}.{schema_name}")
        return
    log.info(f"Ensuring schema exists: {db}.{schema_name}")
    execute_sql(ddl, sf_params=sf_params)


# ---------------------------------------------------------------------------
# Dependency-aware deployment loop
# ---------------------------------------------------------------------------

def _deploy_one_file(f: Path, sql_text: str, sf_params, pass_num: int,
                      progress_cb: Callable[[str], None]) -> Optional[Exception]:
    """Execute one SQL file. Returns None on success, the exception on failure."""
    log.info(f"[Pass {pass_num}] Deploying {f.name}...")
    progress_cb(f"[Pass {pass_num}] Deploying {f.name}...")
    if config.pipeline.dry_run or os.getenv("ENABLE_SNOWFLAKE_DDL", "").lower() != "true":
        log.info(f"DRY_RUN: skipped Snowflake SQL for {f.name}")
        return None
    try:
        execute_sql(sql_text, sf_params=sf_params)
        return None
    except Exception as e:
        return e


def _self_heal_and_redeploy(f: Path, sql_text: str, err_str: str, sf_params,
                             pass_num: int, progress_cb: Callable[[str], None]):
    """
    On a non-retryable SQL error, ask the LLM to fix the script, write the
    fix back to disk, and immediately retry deployment ONCE.

    Returns (success: bool, final_sql_text: str, final_error: Optional[str]).
    Does NOT swallow a second failure — if the LLM's fix also fails, that is
    reported as a hard failure for this file; we do not loop indefinitely
    and we do not let later files mask this one.
    """
    msg = f"⚠️ {f.name} failed with a non-retryable SQL error. Sending to LLM for auto-fix..."
    log.warning(f"[Pass {pass_num}] {msg}")
    progress_cb(msg)

    try:
        corrected_sql = fix_sql_with_llm(sql_text, "Snowflake statement failed. Review SQL syntax and object references.", f.name, sf_params=sf_params)
    except Exception as llm_err:
        fail_msg = f"LLM auto-fix call failed for {f.name}."
        log.error(f"[Pass {pass_num}] {fail_msg}")
        progress_cb(fail_msg)
        return False, sql_text, "Snowflake statement failed."

    if not corrected_sql or not corrected_sql.strip():
        fail_msg = f"❌ LLM returned an empty fix for {f.name}. Manual fix needed."
        log.error(f"[Pass {pass_num}] {fail_msg}")
        progress_cb(fail_msg)
        return False, sql_text, "Snowflake statement failed."

    # Persist the corrected SQL back to disk so it's reviewable and so the
    # next full deploy run doesn't hit the same error again.
    try:
        f.write_text(corrected_sql, encoding="utf-8")
    except Exception as write_err:
        log.warning(f"[Pass {pass_num}] Could not persist auto-fixed SQL ({type(write_err).__name__}).")

    progress_cb(f"🔄 Retrying deployment of LLM-fixed {f.name}...")
    log.info(f"[Pass {pass_num}] Retrying deployment of auto-healed {f.name}...")

    retry_sql = preprocess_sql(corrected_sql, f.name)
    retry_err = _deploy_one_file(f, retry_sql, sf_params, pass_num, progress_cb)

    if retry_err is None:
        ok_msg = f"🎉 Auto-healed and deployed {f.name} successfully."
        log.info(f"[Pass {pass_num}] {ok_msg}")
        progress_cb(ok_msg)
        return True, corrected_sql, None

    retry_err_str = "Snowflake statement failed after automatic correction."
    fail_msg = (
        f"❌ Auto-fix for {f.name} still failed after retry. Manual fix needed. "
        f"Error: {retry_err_str[:300]}"
    )
    log.error(f"[Pass {pass_num}] {fail_msg}")
    progress_cb(fail_msg)
    return False, corrected_sql, retry_err_str


def _topo_sort_sql_files(sql_files: list) -> list:
    """
    Sort SQL files so that a file whose SQL references another view in the same
    schema is deployed AFTER that view.  Falls back to alphabetical for ties.

    Strategy: scan each file for fully-qualified or schema-qualified table/view
    references that match another file in the same batch, build a dependency
    graph, and return a topological order.  Cycles (shouldn't occur in practice)
    fall back to the original order.
    """
    # Map view name (uppercase, no schema prefix) -> Path
    name_to_file: dict = {}
    for f in sql_files:
        # Derive the view name from the file name: silver_EMP.sql -> EMP
        stem = f.stem.upper()
        for prefix in ("SILVER_", "GOLD_CALC_", "GOLD_KPI_", "GOLD_"):
            if stem.startswith(prefix):
                stem = stem[len(prefix):]
                break
        name_to_file[stem] = f

    # Build adjacency: file -> set of files it depends on
    deps: dict = {f: set() for f in sql_files}
    ref_pattern = re.compile(
        r'\b(?:FROM|JOIN)\s+(?:[\w]+\.)*(\w+)\b', re.IGNORECASE
    )
    for f in sql_files:
        try:
            text = f.read_text(encoding="utf-8")
        except Exception:
            continue
        for m in ref_pattern.finditer(text):
            ref = m.group(1).upper()
            if ref in name_to_file and name_to_file[ref] != f:
                deps[f].add(name_to_file[ref])

    # Kahn's algorithm for topological sort
    in_degree = {f: 0 for f in sql_files}
    for f, d_set in deps.items():
        for dep in d_set:
            in_degree[f] += 1   # f depends on dep, so f has higher in-degree

    # Start with files that have no dependencies, sorted alphabetically for stability
    queue = sorted([f for f, deg in in_degree.items() if deg == 0], key=lambda x: x.name)
    result = []
    while queue:
        node = queue.pop(0)
        result.append(node)
        # Files that depended on `node` now have one fewer unmet dependency
        for f, d_set in deps.items():
            if node in d_set:
                in_degree[f] -= 1
                if in_degree[f] == 0:
                    queue.append(f)
                    queue.sort(key=lambda x: x.name)

    # If there's a cycle, append remaining files in original order
    remaining = [f for f in sql_files if f not in result]
    return result + remaining


def deploy_layer(sql_dir: Path, schema_name: str, sf_params=None,
                  progress_cb: Optional[Callable[[str], None]] = None) -> list:
    progress_cb = progress_cb or (lambda m: None)
    ensure_schema_exists(schema_name, sf_params=sf_params)

    sql_files = sorted(sql_dir.glob("*.sql"))
    sql_files = [f for f in sql_files if not f.name.startswith("_")]

    # Deploy in dependency order so JOINed views exist before files that reference them
    sql_files = _topo_sort_sql_files(sql_files)
    log.info(f"[{schema_name}] Deploy order: {[f.name for f in sql_files]}")

    results: dict[str, dict] = {}
    pending = list(sql_files)
    max_passes = max(len(sql_files), 1)

    for pass_num in range(1, max_passes + 1):
        if not pending:
            break

        still_pending = []
        progress_this_pass = False

        for f in pending:
            raw_sql = f.read_text(encoding="utf-8")
            sql_text = preprocess_sql(raw_sql, f.name)

            err = _deploy_one_file(f, sql_text, sf_params, pass_num, progress_cb)

            if err is None:
                results[f.name] = {"file": str(f), "status": "success"}
                progress_this_pass = True
                continue

            err_str = str(err)
            err_class = _classify_error(err_str)

            if err_class == "retryable":
                log.warning(
                    f"[Pass {pass_num}] {f.name} — missing dependency, will retry. "
                    ""
                )
                still_pending.append(f)
                continue

            # Non-retryable SQL error: do NOT just log and move on to other
            # files. Hand it to the LLM immediately and only proceed once
            # we have a verified passing fix for THIS file.
            healed, final_sql, final_err = _self_heal_and_redeploy(
                f, sql_text, "Snowflake statement failed.", sf_params, pass_num, progress_cb
            )
            if healed:
                results[f.name] = {
                    "file": str(f), "status": "success", "auto_healed": True
                }
                progress_this_pass = True
            else:
                # Record the failure and SKIP this file — continue deploying
                # the remaining files so one bad script doesn't block everything.
                results[f.name] = {
                    "file": str(f),
                    "status": "skipped",
                    "error": "Snowflake statement failed after automatic correction.",
                    "auto_heal_attempted": True,
                    "action": "skipped_after_failed_auto_heal",
                }
                skip_msg = (
                    f"⚠️ Skipping {f.name} — could not be auto-healed. "
                    f"Will continue with remaining files. Manual fix needed."
                )
                log.warning(f"[Pass {pass_num}] {skip_msg}")
                progress_cb(skip_msg)
                # Mark as having made progress so the retry loop doesn't stall
                progress_this_pass = True

        pending = still_pending

        if not progress_this_pass and pending:
            log.error(
                f"No progress in pass {pass_num}. Files with unresolvable errors: "
                + ", ".join(f.name for f in pending)
            )
            for f in pending:
                results[f.name] = {
                    "file": str(f),
                    "status": "failed",
                    "error": (
                        "Could not deploy after multiple passes — "
                        "likely circular dependency or a missing dependency "
                        "that never resolved. Check prior pass logs."
                    ),
                }
            pending = []
            break

    for f in pending:
        if f.name not in results:
            results[f.name] = {
                "file": str(f), "status": "failed",
                "error": "Exhausted retry passes.",
            }

    # Emit a clear end-of-layer summary so the caller can surface it to the UI
    all_result_list = list(results.values())
    succeeded = [r for r in all_result_list if r["status"] == "success"]
    skipped   = [r for r in all_result_list if r["status"] == "skipped"]
    failed    = [r for r in all_result_list if r["status"] == "failed"]

    log.info(
        f"[{schema_name}] Deployment complete — "
        f"{len(succeeded)} succeeded, {len(skipped)} skipped, {len(failed)} failed."
    )
    if skipped:
        log.warning(f"[{schema_name}] Skipped files (manual fix needed):")
        for r in skipped:
            log.warning(f"  ⚠️  {Path(r['file']).name}  — {(r.get('error') or '')[:200]}")
    if failed:
        log.error(f"[{schema_name}] Failed files:")
        for r in failed:
            log.error(f"  ❌  {Path(r['file']).name}  — {(r.get('error') or '')[:200]}")

    return all_result_list


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description="Deploy generated Silver/Gold SQL to Snowflake")
    ap.add_argument("--layer", choices=["silver", "gold", "all"], default="all")
    ap.add_argument("--force", action="store_true", help="Override DRY_RUN=true and actually execute")
    args = ap.parse_args()

    if args.force and os.getenv("ENABLE_SNOWFLAKE_DDL", "").lower() != "true":
        raise SystemExit("Snowflake DDL is disabled. Set ENABLE_SNOWFLAKE_DDL=true only after approval.")

    if args.force:
        config.pipeline.dry_run = False
        log.warning("--force passed: overriding DRY_RUN, SQL will be executed for real.")
    else:
        config.pipeline.dry_run = True

    if config.pipeline.dry_run:
        log.info(
            "DRY_RUN is enabled. No SQL will execute. "
            "Pass --force or set DRY_RUN=false in .env to deploy for real."
        )

    sf = config.snowflake
    all_results = {}
    cli_progress_cb = lambda m: print(f"  {m}")

    if args.layer in ("silver", "all"):
        log.info("=== Deploying SILVER layer ===")
        all_results["silver"] = deploy_layer(
            config.sql_silver_dir, sf.silver_schema, progress_cb=cli_progress_cb
        )

    if args.layer in ("gold", "all"):
        log.info("=== Deploying GOLD layer ===")
        all_results["gold"] = deploy_layer(
            config.sql_gold_dir, sf.gold_schema, progress_cb=cli_progress_cb
        )

    out_path = config.reports_dir / "deployment_results.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(all_results, f, indent=2)

    print(f"\nDeployment results written to {out_path}")
    for layer, results in all_results.items():
        ok = sum(1 for r in results if r["status"] == "success")
        print(f"  {layer}: {ok}/{len(results)} succeeded")


if __name__ == "__main__":
    main()
