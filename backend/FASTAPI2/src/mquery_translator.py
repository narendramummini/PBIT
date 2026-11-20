# # # """
# # # mquery_translator.py
# # # Translates Power Query (M) expressions into Snowflake SQL using a small
# # # rule engine for common, deterministic M steps (renaming, type changes,
# # # simple filters) plus an Azure OpenAI fallback for anything more complex
# # # (custom logic, merges, pivots, etc).

# # # Run:
# # #     python src/mquery_translator.py \
# # #         --model output/mapping/parsed_model.json \
# # #         --mapping output/mapping/table_mapping.json \
# # #         --output-dir output/sql/silver
# # # """

# # # import argparse
# # # import json
# # # import re
# # # import sys
# # # from pathlib import Path
# # # from typing import Optional

# # # sys.path.insert(0, str(Path(__file__).resolve().parent))
# # # from config import config
# # # from logger import get_logger
# # # import azure_openai_client as aoai

# # # log = get_logger("mquery_translator", config.logs_dir, config.pipeline.log_level)


# # # # ---------------------------------------------------------------------------
# # # # Rule engine: handles simple, common M patterns deterministically.
# # # # Anything not recognized falls through to the LLM.
# # # # ---------------------------------------------------------------------------

# # # RENAME_RE = re.compile(r'Table\.RenameColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# # # RENAME_PAIR_RE = re.compile(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}')

# # # SELECT_COLS_RE = re.compile(r'Table\.SelectColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# # # COL_NAME_RE = re.compile(r'"([^"]+)"')

# # # TYPE_MAP = {
# # #     "Int64.Type": "NUMBER(38,0)",
# # #     "Number.Type": "FLOAT",
# # #     "Text.Type": "VARCHAR",
# # #     "Date.Type": "DATE",
# # #     "DateTime.Type": "TIMESTAMP_NTZ",
# # #     "Logical.Type": "BOOLEAN",
# # #     "Currency.Type": "NUMBER(38,4)",
# # # }


# # # def _try_rule_based(m_query: str, source_table_fqn: str) -> Optional[str]:
# # #     """Attempt a deterministic translation for simple single-purpose M
# # #     queries (only rename or only column selection). Returns None if the
# # #     M query is too complex for the rule engine."""
# # #     if not m_query:
# # #         return None

# # #     # Count actual `let`-block step bindings (lines like "Name = Expr(...),")
# # #     # rather than every "=" in the query, which overcounts boilerplate source
# # #     # steps (Snowflake.Databases(...), {[Name="..."]}[Data], etc).
# # #     step_lines = re.findall(r'^\s*\w+\s*=\s*', m_query, flags=re.MULTILINE)
# # #     step_count = len(step_lines)
# # #     if step_count > 6:
# # #         return None  # too complex, let the LLM handle it

# # #     rename_match = RENAME_RE.search(m_query)
# # #     select_match = SELECT_COLS_RE.search(m_query)

# # #     if rename_match and not select_match and "Table.TransformColumnTypes" not in m_query \
# # #             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
# # #             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
# # #         pairs = RENAME_PAIR_RE.findall(rename_match.group(1))
# # #         if not pairs:
# # #             return None
# # #         select_list = ",\n    ".join(f'"{old}" AS "{new}"' for old, new in pairs)
# # #         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

# # #     if select_match and not rename_match and "Table.TransformColumnTypes" not in m_query \
# # #             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
# # #             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
# # #         cols = COL_NAME_RE.findall(select_match.group(1))
# # #         if not cols:
# # #             return None
# # #         select_list = ",\n    ".join(f'"{c}"' for c in cols)
# # #         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

# # #     return None


# # # LLM_SYSTEM_PROMPT = """You are a senior data engineer translating Microsoft Power Query (M language)
# # # ETL steps into equivalent Snowflake SQL (a SELECT statement to be used inside a CREATE OR REPLACE VIEW
# # # or CREATE TABLE AS SELECT). Follow these rules:

# # # - Output ONLY the SQL SELECT statement. No explanation, no markdown fences.
# # # - Use ANSI SQL compatible with Snowflake (e.g. IFF, QUALIFY, DATE_TRUNC are all fine).
# # # - Reference the given source table fully-qualified name as the FROM source.
# # # - Preserve renaming, type conversions, filtering, derived/calculated columns, and joins faithfully.
# # # - For Table.TransformColumnTypes, map Power Query types to Snowflake types reasonably
# # #   (Int64.Type -> NUMBER, Number.Type -> FLOAT, Text.Type -> VARCHAR, Date.Type -> DATE,
# # #   DateTime.Type -> TIMESTAMP_NTZ, Logical.Type -> BOOLEAN).
# # # - For Table.SelectRows filters, translate the logical condition into a WHERE clause as faithfully as possible.
# # # - If a step cannot be translated with certainty, add a SQL comment "-- TODO: manual review: <reason>" above
# # #   the affected clause, but still produce your best-effort SQL.
# # # - Always alias the final output columns clearly.

# # # CRITICAL — TYPE CASTING RULES (Snowflake-specific, violations cause deployment failures):
# # #   * NEVER use TRY_CAST() to cast a BOOLEAN column to VARCHAR or any other type.
# # #     TRY_CAST() only accepts VARCHAR as its first argument. Snowflake will raise
# # #     "Function TRY_CAST cannot be used with arguments of types BOOLEAN and VARCHAR".
# # #     Instead use: CAST(col AS VARCHAR)  or  col::VARCHAR
# # #   * NEVER use TRY_CAST() on NUMBER, DATE, TIMESTAMP, or BOOLEAN source columns.
# # #     Only use TRY_CAST() when the source column is VARCHAR/TEXT and the target is a
# # #     non-string type (e.g. TRY_CAST(varchar_col AS NUMBER)).
# # #   * For BOOLEAN → VARCHAR: use CAST(col AS VARCHAR) or IFF(col, 'true', 'false').
# # #   * For NUMBER → VARCHAR: use col::VARCHAR or CAST(col AS VARCHAR).
# # #   * For DATE/TIMESTAMP → VARCHAR: use TO_VARCHAR(col) or TO_VARCHAR(col, 'YYYY-MM-DD').

# # # CRITICAL — TABLE ALIAS RULES (violations cause "invalid identifier" errors):
# # #   * Every table alias you use in SELECT, WHERE, JOIN ON, or ORDER BY clauses MUST be
# # #     explicitly defined in the FROM or JOIN clause of the same query.
# # #   * Single-letter aliases (R, T, X, etc.) are a common source of errors. Only use
# # #     them if you explicitly write e.g. FROM mytable R or JOIN othertable AS R.
# # #   * Before finalising the SQL, mentally trace every <ALIAS>.<column> reference back
# # #     to its FROM/JOIN alias definition. If you cannot trace it, remove the alias prefix
# # #     or rewrite the query.
# # #   * NEVER invent or assume aliases that are not in your generated FROM/JOIN clauses.
# # # """


# # # def _llm_translate(m_query: str, table_name: str, source_table_fqn: str, columns: list) -> str:
# # #     user_prompt = f"""Power BI table name: {table_name}
# # # Snowflake source table (Bronze layer, fully qualified): {source_table_fqn}
# # # Known column names: {[c["name"] if isinstance(c, dict) else c for c in (columns or [])]}

# # # Power Query (M) expression to translate:
# # # ---
# # # {m_query}
# # # ---

# # # Produce the equivalent Snowflake SQL SELECT statement reading from {source_table_fqn}.
# # # """
# # #     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# # # # ---------------------------------------------------------------------------
# # # # Post-generation SQL linters
# # # # Called immediately after the LLM returns SQL to catch hallucination bugs
# # # # before writing any .sql file to disk.
# # # # ---------------------------------------------------------------------------

# # # _LINT_TRY_CAST_RE = re.compile(
# # #     r'\bTRY_CAST\s*\(\s*([A-Za-z0-9_."]+)\s+AS\s+(VARCHAR|TEXT)(\s*\(\s*\d+\s*\))?\s*\)',
# # #     re.IGNORECASE,
# # # )


# # # def _lint_fix_try_cast(sql: str) -> tuple[str, list[str]]:
# # #     """Replace TRY_CAST(col AS VARCHAR) → CAST(col AS VARCHAR).
# # #     TRY_CAST only accepts a VARCHAR *source*; using it on BOOLEAN/NUMBER/DATE
# # #     causes a Snowflake runtime error.  A plain CAST is always safe here."""
# # #     notes = []

# # #     def _replace(m):
# # #         col, dtype, size = m.group(1), m.group(2), m.group(3) or ""
# # #         notes.append(
# # #             f"TRY_CAST({col} AS {dtype}{size}) → CAST({col} AS {dtype}{size}) "
# # #             "(TRY_CAST cannot accept non-VARCHAR source columns in Snowflake)"
# # #         )
# # #         return f"CAST({col} AS {dtype}{size})"

# # #     patched = _LINT_TRY_CAST_RE.sub(_replace, sql)
# # #     return patched, notes


# # # _LINT_ALIAS_USE_RE = re.compile(r'\b([A-Z])\.\b([A-Za-z0-9_]+)\b')
# # # _LINT_ALIAS_DEF_RE = re.compile(
# # #     r'\b(?:FROM|JOIN)\s+\S+\s+(?:AS\s+)?([A-Z])\b', re.IGNORECASE
# # # )


# # # def _lint_check_aliases(sql: str) -> tuple[str, list[str]]:
# # #     """Detect single-letter aliases used but never defined in FROM/JOIN.
# # #     These cause Snowflake 'invalid identifier <ALIAS>.<col>' errors.
# # #     We cannot auto-fix (we don't know the intended table), so we prepend a
# # #     visible TODO comment so the issue is obvious in the generated .sql file."""
# # #     defined = set(_LINT_ALIAS_DEF_RE.findall(sql.upper()))
# # #     used = set(m.group(1).upper() for m in _LINT_ALIAS_USE_RE.finditer(sql))
# # #     undefined = used - defined
# # #     notes = []
# # #     if undefined:
# # #         comment = (
# # #             "-- !! LINT WARNING: Undefined single-letter alias(es) detected: "
# # #             + ", ".join(sorted(undefined))
# # #             + "\n"
# # #             "-- These will cause 'invalid identifier' errors in Snowflake.\n"
# # #             "-- Check the FROM/JOIN clauses and correct the alias references below.\n"
# # #         )
# # #         first_select = re.search(r'\bSELECT\b', sql, re.IGNORECASE)
# # #         if first_select:
# # #             sql = sql[: first_select.start()] + comment + sql[first_select.start():]
# # #         else:
# # #             sql = comment + sql
# # #         notes.append(
# # #             f"Undefined alias(es) {sorted(undefined)} referenced in generated SQL — "
# # #             "TODO comment prepended. Manual fix required."
# # #         )
# # #     return sql, notes


# # # def translate_table(
# # #     table: dict,
# # #     bronze_fqn: Optional[str],
# # #     column_mapping: Optional[dict] = None,
# # #     bronze_columns: Optional[list] = None,
# # # ) -> dict:
# # #     """Returns dict: {table_name, sql, method, warnings}

# # #     Args:
# # #         table:          Parsed PBIX table dict.
# # #         bronze_fqn:     Fully-qualified Bronze table name, or None if unmatched.
# # #         column_mapping: Optional {pbix_col: bronze_col} rename map (passed by
# # #                         newer pipeline_runner versions; used to enrich the LLM
# # #                         prompt so column aliases are grounded in real names).
# # #         bronze_columns: Optional list of actual Bronze column names (passed by
# # #                         newer pipeline_runner versions; used for alias validation).
# # #     """
# # #     name = table["name"]
# # #     m_query = table.get("m_query")
# # #     warnings = []
# # #     # Merge any externally supplied column info into the table dict so the
# # #     # LLM prompt receives accurate column names and the alias linter can
# # #     # cross-check against real Bronze columns.
# # #     if bronze_columns:
# # #         table = {**table, "_bronze_columns": bronze_columns}
# # #     if column_mapping:
# # #         table = {**table, "_column_mapping": column_mapping}

# # #     if table.get("is_calculated_table"):
# # #         return {
# # #             "table_name": name, "sql": None, "method": "skipped_calculated_table",
# # #             "warnings": ["Calculated table — handled in DAX->SQL translation (Gold layer), not Silver."],
# # #         }

# # #     if not bronze_fqn:
# # #         return {
# # #             "table_name": name, "sql": None, "method": "skipped_unmatched",
# # #             "warnings": [f"No matched Bronze table for '{name}'; cannot generate Silver SQL."],
# # #         }

# # #     if not m_query:
# # #         sql = f"SELECT *\nFROM {bronze_fqn}"
# # #         return {
# # #             "table_name": name, "sql": sql, "method": "passthrough",
# # #             "warnings": ["No M query found; generated pass-through SELECT *."],
# # #         }

# # #     rule_sql = _try_rule_based(m_query, bronze_fqn)
# # #     if rule_sql:
# # #         log.info(f"[RULE]   {name}: translated via rule engine")
# # #         return {"table_name": name, "sql": rule_sql, "method": "rule_engine", "warnings": []}

# # #     log.info(f"[LLM]    {name}: M query too complex for rule engine, calling Azure OpenAI")
# # #     try:
# # #         # Prefer actual Bronze column names (from newer pipeline_runner) over
# # #         # PBIX column names for grounding the LLM prompt in real identifiers.
# # #         cols = bronze_columns or table.get("columns", [])
# # #         sql = _llm_translate(m_query, name, bronze_fqn, cols)
# # #         if "-- TODO: manual review" in sql:
# # #             warnings.append("LLM flagged at least one clause for manual review — search SQL for 'TODO'.")

# # #         # --- Post-generation lint: fix known LLM hallucination patterns ---
# # #         sql, cast_patches = _lint_fix_try_cast(sql)
# # #         for note in cast_patches:
# # #             warnings.append(f"AUTO-FIXED: {note}")
# # #             log.warning(f"[LINT] {name}: {note}")

# # #         sql, alias_warnings = _lint_check_aliases(sql)
# # #         for note in alias_warnings:
# # #             warnings.append(f"ALIAS WARNING: {note}")
# # #             log.warning(f"[LINT] {name}: {note}")

# # #         return {"table_name": name, "sql": sql, "method": "llm", "warnings": warnings}
# # #     except Exception as e:
# # #         log.error(f"LLM translation failed for {name}: {e}")
# # #         fallback_sql = f"SELECT *\nFROM {bronze_fqn}\n-- TODO: manual review: LLM translation failed ({e})"
# # #         return {"table_name": name, "sql": fallback_sql, "method": "llm_failed_fallback",
# # #                 "warnings": [f"LLM translation failed: {e}. Generated pass-through SELECT * as fallback."]}


# # # def build_silver_ddl(table_name: str, select_sql: str, silver_schema: str,
# # #                      database: str = None) -> str:
# # #     """Build CREATE OR REPLACE VIEW DDL for a Silver layer object.
# # #     The optional `database` arg is accepted for compatibility with pipeline_runner
# # #     versions that pass it as a 4th positional argument; it is not used in the DDL
# # #     because silver_schema should already be fully qualified if needed.
# # #     """
# # #     safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table_name).upper()
# # #     return (
# # #         f"-- Silver layer object for PBIX table: {table_name}\n"
# # #         f"CREATE OR REPLACE VIEW {silver_schema}.{safe_name} AS\n"
# # #         f"{select_sql.strip()}\n;\n"
# # #     )


# # # def main():
# # #     ap = argparse.ArgumentParser(description="Translate Power Query (M) to Snowflake SQL and generate Silver layer DDL")
# # #     ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
# # #     ap.add_argument("--mapping", default=str(config.mapping_dir / "table_mapping.json"))
# # #     ap.add_argument("--output-dir", default=str(config.sql_silver_dir))
# # #     args = ap.parse_args()

# # #     with open(args.model, "r", encoding="utf-8") as f:
# # #         parsed_model = json.load(f)
# # #     with open(args.mapping, "r", encoding="utf-8") as f:
# # #         table_mapping = json.load(f)

# # #     mapping_by_pbix = {m["pbix_table"]: m for m in table_mapping}
# # #     sf = config.snowflake
# # #     out_dir = Path(args.output_dir)
# # #     out_dir.mkdir(parents=True, exist_ok=True)

# # #     results = []
# # #     for table in parsed_model["tables"]:
# # #         name = table["name"]
# # #         match = mapping_by_pbix.get(name)
# # #         bronze_fqn = None
# # #         if match and match.get("bronze_table"):
# # #             bronze_fqn = f'{sf.database}.{match["bronze_schema"]}.{match["bronze_table"]}'

# # #         result = translate_table(table, bronze_fqn)
# # #         results.append(result)

# # #         if result["sql"]:
# # #             ddl = build_silver_ddl(name, result["sql"], sf.silver_schema)
# # #             safe_name = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
# # #             out_file = out_dir / f"silver_{safe_name}.sql"
# # #             with open(out_file, "w", encoding="utf-8") as f:
# # #                 f.write(ddl)
# # #             result["output_file"] = str(out_file)
# # #             log.info(f"Wrote {out_file}")

# # #     summary_path = out_dir / "_translation_summary.json"
# # #     with open(summary_path, "w", encoding="utf-8") as f:
# # #         json.dump(results, f, indent=2)

# # #     print("\nM Query -> SQL Translation Summary:")
# # #     for r in results:
# # #         status = "OK" if r["sql"] else "SKIPPED"
# # #         print(f"  [{status:7s}] {r['table_name']:30s} method={r['method']}")
# # #         for w in r["warnings"]:
# # #             print(f"             ! {w}")


# # # if __name__ == "__main__":
# # #     main()

# # """
# # mquery_translator.py
# # Translates Power Query (M) expressions into Snowflake SQL using a small
# # rule engine for common, deterministic M steps (renaming, type changes,
# # simple filters) plus an Azure OpenAI fallback for anything more complex
# # (custom logic, merges, pivots, etc).

# # Run:
# #     python src/mquery_translator.py \
# #         --model output/mapping/parsed_model.json \
# #         --mapping output/mapping/table_mapping.json \
# #         --output-dir output/sql/silver
# # """

# # import argparse
# # import json
# # import re
# # import sys
# # from pathlib import Path
# # from typing import Optional

# # sys.path.insert(0, str(Path(__file__).resolve().parent))
# # from config import config
# # from logger import get_logger
# # import azure_openai_client as aoai

# # log = get_logger("mquery_translator", config.logs_dir, config.pipeline.log_level)


# # # ---------------------------------------------------------------------------
# # # Rule engine: handles simple, common M patterns deterministically.
# # # Anything not recognized falls through to the LLM.
# # # ---------------------------------------------------------------------------

# # RENAME_RE = re.compile(r'Table\.RenameColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# # RENAME_PAIR_RE = re.compile(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}')

# # SELECT_COLS_RE = re.compile(r'Table\.SelectColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# # COL_NAME_RE = re.compile(r'"([^"]+)"')

# # TYPE_MAP = {
# #     "Int64.Type": "NUMBER(38,0)",
# #     "Number.Type": "FLOAT",
# #     "Text.Type": "VARCHAR",
# #     "Date.Type": "DATE",
# #     "DateTime.Type": "TIMESTAMP_NTZ",
# #     "Logical.Type": "BOOLEAN",
# #     "Currency.Type": "NUMBER(38,4)",
# # }


# # def _try_rule_based(m_query: str, source_table_fqn: str) -> Optional[str]:
# #     """Attempt a deterministic translation for simple single-purpose M
# #     queries (only rename or only column selection). Returns None if the
# #     M query is too complex for the rule engine."""
# #     if not m_query:
# #         return None

# #     # Count actual `let`-block step bindings (lines like "Name = Expr(...),")
# #     # rather than every "=" in the query, which overcounts boilerplate source
# #     # steps (Snowflake.Databases(...), {[Name="..."]}[Data], etc).
# #     step_lines = re.findall(r'^\s*\w+\s*=\s*', m_query, flags=re.MULTILINE)
# #     step_count = len(step_lines)
# #     if step_count > 6:
# #         return None  # too complex, let the LLM handle it

# #     rename_match = RENAME_RE.search(m_query)
# #     select_match = SELECT_COLS_RE.search(m_query)

# #     if rename_match and not select_match and "Table.TransformColumnTypes" not in m_query \
# #             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
# #             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
# #         pairs = RENAME_PAIR_RE.findall(rename_match.group(1))
# #         if not pairs:
# #             return None
# #         select_list = ",\n    ".join(f'"{old}" AS "{new}"' for old, new in pairs)
# #         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

# #     if select_match and not rename_match and "Table.TransformColumnTypes" not in m_query \
# #             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
# #             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
# #         cols = COL_NAME_RE.findall(select_match.group(1))
# #         if not cols:
# #             return None
# #         select_list = ",\n    ".join(f'"{c}"' for c in cols)
# #         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

# #     return None


# # LLM_SYSTEM_PROMPT = """You are a senior data engineer translating Microsoft Power Query (M language)
# # ETL steps into equivalent Snowflake SQL (a SELECT statement to be used inside a CREATE OR REPLACE VIEW
# # or CREATE TABLE AS SELECT). Follow these rules:

# # - Output ONLY the SQL SELECT statement. No explanation, no markdown fences.
# # - Use ANSI SQL compatible with Snowflake (e.g. IFF, QUALIFY, DATE_TRUNC are all fine).
# # - Reference the given source table fully-qualified name as the FROM source.
# # - Preserve renaming, type conversions, filtering, derived/calculated columns, and joins faithfully.
# # - For Table.TransformColumnTypes, map Power Query types to Snowflake types reasonably
# #   (Int64.Type -> NUMBER, Number.Type -> FLOAT, Text.Type -> VARCHAR, Date.Type -> DATE,
# #   DateTime.Type -> TIMESTAMP_NTZ, Logical.Type -> BOOLEAN).
# # - For Table.SelectRows filters, translate the logical condition into a WHERE clause as faithfully as possible.
# # - If a step cannot be translated with certainty, add a SQL comment "-- TODO: manual review: <reason>" above
# #   the affected clause, but still produce your best-effort SQL.
# # - Always alias the final output columns clearly.

# # CRITICAL — TYPE CASTING RULES (Snowflake-specific, violations cause deployment failures):
# #   * NEVER use TRY_CAST() to cast a BOOLEAN column to VARCHAR or any other type.
# #     TRY_CAST() only accepts VARCHAR as its first argument. Snowflake will raise
# #     "Function TRY_CAST cannot be used with arguments of types BOOLEAN and VARCHAR".
# #     Instead use: CAST(col AS VARCHAR)  or  col::VARCHAR
# #   * NEVER use TRY_CAST() on NUMBER, DATE, TIMESTAMP, or BOOLEAN source columns.
# #     Only use TRY_CAST() when the source column is VARCHAR/TEXT and the target is a
# #     non-string type (e.g. TRY_CAST(varchar_col AS NUMBER)).
# #   * For BOOLEAN → VARCHAR: use CAST(col AS VARCHAR) or IFF(col, 'true', 'false').
# #   * For NUMBER → VARCHAR: use col::VARCHAR or CAST(col AS VARCHAR).
# #   * For DATE/TIMESTAMP → VARCHAR: use TO_VARCHAR(col) or TO_VARCHAR(col, 'YYYY-MM-DD').

# # CRITICAL — TABLE ALIAS RULES (violations cause "invalid identifier" errors):
# #   * Every table alias you use in SELECT, WHERE, JOIN ON, or ORDER BY clauses MUST be
# #     explicitly defined in the FROM or JOIN clause of the same query.
# #   * Single-letter aliases (R, T, X, etc.) are a common source of errors. Only use
# #     them if you explicitly write e.g. FROM mytable R or JOIN othertable AS R.
# #   * Before finalising the SQL, mentally trace every <ALIAS>.<column> reference back
# #     to its FROM/JOIN alias definition. If you cannot trace it, remove the alias prefix
# #     or rewrite the query.
# #   * NEVER invent or assume aliases that are not in your generated FROM/JOIN clauses.
# # """


# # def _llm_translate(m_query: str, table_name: str, source_table_fqn: str, columns: list,
# #                    all_bronze_columns: dict = None) -> str:
# #     """
# #     all_bronze_columns: optional dict of {bronze_fqn: [col_name, ...]} for every
# #     table that might appear in a JOIN inside this M query.  Passing this prevents
# #     the LLM from hallucinating column names on joined tables.
# #     """
# #     col_list = [c["name"] if isinstance(c, dict) else c for c in (columns or [])]
# #     primary_cols_line = f"Columns available in {source_table_fqn}: {col_list}"

# #     joined_cols_block = ""
# #     if all_bronze_columns:
# #         lines = []
# #         for fqn, cols in all_bronze_columns.items():
# #             if fqn != source_table_fqn:
# #                 lines.append(f"  {fqn}: {cols}")
# #         if lines:
# #             joined_cols_block = (
# #                 "\nColumns available in other Bronze tables that may appear in JOINs "
# #                 "(use ONLY these column names — do not invent others):\n"
# #                 + "\n".join(lines)
# #             )

# #     user_prompt = f"""Power BI table name: {table_name}
# # Snowflake source table (Bronze layer, fully qualified): {source_table_fqn}
# # {primary_cols_line}{joined_cols_block}

# # Power Query (M) expression to translate:
# # ---
# # {m_query}
# # ---

# # Produce the equivalent Snowflake SQL SELECT statement reading from {source_table_fqn}.
# # CRITICAL: Use ONLY the column names listed above. Never invent column names not in those lists.
# # """
# #     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# # # ---------------------------------------------------------------------------
# # # Post-generation SQL linters
# # # Called immediately after the LLM returns SQL to catch hallucination bugs
# # # before writing any .sql file to disk.
# # # ---------------------------------------------------------------------------

# # _LINT_TRY_CAST_RE = re.compile(
# #     r'\bTRY_CAST\s*\(\s*([A-Za-z0-9_."]+)\s+AS\s+(VARCHAR|TEXT)(\s*\(\s*\d+\s*\))?\s*\)',
# #     re.IGNORECASE,
# # )


# # def _lint_fix_try_cast(sql: str) -> tuple[str, list[str]]:
# #     """Replace TRY_CAST(col AS VARCHAR) → CAST(col AS VARCHAR).
# #     TRY_CAST only accepts a VARCHAR *source*; using it on BOOLEAN/NUMBER/DATE
# #     causes a Snowflake runtime error.  A plain CAST is always safe here."""
# #     notes = []

# #     def _replace(m):
# #         col, dtype, size = m.group(1), m.group(2), m.group(3) or ""
# #         notes.append(
# #             f"TRY_CAST({col} AS {dtype}{size}) → CAST({col} AS {dtype}{size}) "
# #             "(TRY_CAST cannot accept non-VARCHAR source columns in Snowflake)"
# #         )
# #         return f"CAST({col} AS {dtype}{size})"

# #     patched = _LINT_TRY_CAST_RE.sub(_replace, sql)
# #     return patched, notes


# # _LINT_ALIAS_USE_RE = re.compile(r'\b([A-Z])\.\b([A-Za-z0-9_]+)\b')
# # _LINT_ALIAS_DEF_RE = re.compile(
# #     r'\b(?:FROM|JOIN)\s+\S+\s+(?:AS\s+)?([A-Z])\b', re.IGNORECASE
# # )


# # def _lint_check_aliases(sql: str) -> tuple[str, list[str]]:
# #     """Detect single-letter aliases used but never defined in FROM/JOIN.
# #     These cause Snowflake 'invalid identifier <ALIAS>.<col>' errors.
# #     We cannot auto-fix (we don't know the intended table), so we prepend a
# #     visible TODO comment so the issue is obvious in the generated .sql file."""
# #     defined = set(_LINT_ALIAS_DEF_RE.findall(sql.upper()))
# #     used = set(m.group(1).upper() for m in _LINT_ALIAS_USE_RE.finditer(sql))
# #     undefined = used - defined
# #     notes = []
# #     if undefined:
# #         comment = (
# #             "-- !! LINT WARNING: Undefined single-letter alias(es) detected: "
# #             + ", ".join(sorted(undefined))
# #             + "\n"
# #             "-- These will cause 'invalid identifier' errors in Snowflake.\n"
# #             "-- Check the FROM/JOIN clauses and correct the alias references below.\n"
# #         )
# #         first_select = re.search(r'\bSELECT\b', sql, re.IGNORECASE)
# #         if first_select:
# #             sql = sql[: first_select.start()] + comment + sql[first_select.start():]
# #         else:
# #             sql = comment + sql
# #         notes.append(
# #             f"Undefined alias(es) {sorted(undefined)} referenced in generated SQL — "
# #             "TODO comment prepended. Manual fix required."
# #         )
# #     return sql, notes


# # def translate_table(
# #     table: dict,
# #     bronze_fqn: Optional[str],
# #     column_mapping: Optional[dict] = None,
# #     bronze_columns: Optional[list] = None,
# #     all_bronze_columns: Optional[dict] = None,
# # ) -> dict:
# #     """Returns dict: {table_name, sql, method, warnings}

# #     Args:
# #         table:          Parsed PBIX table dict.
# #         bronze_fqn:     Fully-qualified Bronze table name, or None if unmatched.
# #         column_mapping: Optional {pbix_col: bronze_col} rename map (passed by
# #                         newer pipeline_runner versions; used to enrich the LLM
# #                         prompt so column aliases are grounded in real names).
# #         bronze_columns: Optional list of actual Bronze column names (passed by
# #                         newer pipeline_runner versions; used for alias validation).
# #     """
# #     name = table["name"]
# #     m_query = table.get("m_query")
# #     warnings = []
# #     # Merge any externally supplied column info into the table dict so the
# #     # LLM prompt receives accurate column names and the alias linter can
# #     # cross-check against real Bronze columns.
# #     if bronze_columns:
# #         table = {**table, "_bronze_columns": bronze_columns}
# #     if column_mapping:
# #         table = {**table, "_column_mapping": column_mapping}
# #     if all_bronze_columns:
# #         table = {**table, "_all_bronze_columns": all_bronze_columns}

# #     if table.get("is_calculated_table"):
# #         return {
# #             "table_name": name, "sql": None, "method": "skipped_calculated_table",
# #             "warnings": ["Calculated table — handled in DAX->SQL translation (Gold layer), not Silver."],
# #         }

# #     if not bronze_fqn:
# #         return {
# #             "table_name": name, "sql": None, "method": "skipped_unmatched",
# #             "warnings": [f"No matched Bronze table for '{name}'; cannot generate Silver SQL."],
# #         }

# #     if not m_query:
# #         sql = f"SELECT *\nFROM {bronze_fqn}"
# #         return {
# #             "table_name": name, "sql": sql, "method": "passthrough",
# #             "warnings": ["No M query found; generated pass-through SELECT *."],
# #         }

# #     rule_sql = _try_rule_based(m_query, bronze_fqn)
# #     if rule_sql:
# #         log.info(f"[RULE]   {name}: translated via rule engine")
# #         return {"table_name": name, "sql": rule_sql, "method": "rule_engine", "warnings": []}

# #     log.info(f"[LLM]    {name}: M query too complex for rule engine, calling Azure OpenAI")
# #     try:
# #         cols = bronze_columns or table.get("columns", [])
# #         # Pass all known Bronze column lists so the LLM can't hallucinate
# #         # column names on tables it JOINs to.
# #         all_bronze_columns = table.get("_all_bronze_columns") or None
# #         sql = _llm_translate(m_query, name, bronze_fqn, cols,
# #                              all_bronze_columns=all_bronze_columns)
# #         if "-- TODO: manual review" in sql:
# #             warnings.append("LLM flagged at least one clause for manual review — search SQL for 'TODO'.")

# #         # --- Post-generation lint: fix known LLM hallucination patterns ---
# #         sql, cast_patches = _lint_fix_try_cast(sql)
# #         for note in cast_patches:
# #             warnings.append(f"AUTO-FIXED: {note}")
# #             log.warning(f"[LINT] {name}: {note}")

# #         sql, alias_warnings = _lint_check_aliases(sql)
# #         for note in alias_warnings:
# #             warnings.append(f"ALIAS WARNING: {note}")
# #             log.warning(f"[LINT] {name}: {note}")

# #         return {"table_name": name, "sql": sql, "method": "llm", "warnings": warnings}
# #     except Exception as e:
# #         log.error(f"LLM translation failed for {name}: {e}")
# #         fallback_sql = f"SELECT *\nFROM {bronze_fqn}\n-- TODO: manual review: LLM translation failed ({e})"
# #         return {"table_name": name, "sql": fallback_sql, "method": "llm_failed_fallback",
# #                 "warnings": [f"LLM translation failed: {e}. Generated pass-through SELECT * as fallback."]}


# # def build_silver_ddl(table_name: str, select_sql: str, silver_schema: str,
# #                      database: str = None) -> str:
# #     """Build CREATE OR REPLACE VIEW DDL for a Silver layer object.
# #     The optional `database` arg is accepted for compatibility with pipeline_runner
# #     versions that pass it as a 4th positional argument; it is not used in the DDL
# #     because silver_schema should already be fully qualified if needed.
# #     """
# #     safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table_name).upper()
# #     return (
# #         f"-- Silver layer object for PBIX table: {table_name}\n"
# #         f"CREATE OR REPLACE VIEW {silver_schema}.{safe_name} AS\n"
# #         f"{select_sql.strip()}\n;\n"
# #     )


# # def main():
# #     ap = argparse.ArgumentParser(description="Translate Power Query (M) to Snowflake SQL and generate Silver layer DDL")
# #     ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
# #     ap.add_argument("--mapping", default=str(config.mapping_dir / "table_mapping.json"))
# #     ap.add_argument("--output-dir", default=str(config.sql_silver_dir))
# #     args = ap.parse_args()

# #     with open(args.model, "r", encoding="utf-8") as f:
# #         parsed_model = json.load(f)
# #     with open(args.mapping, "r", encoding="utf-8") as f:
# #         table_mapping = json.load(f)

# #     mapping_by_pbix = {m["pbix_table"]: m for m in table_mapping}
# #     sf = config.snowflake
# #     out_dir = Path(args.output_dir)
# #     out_dir.mkdir(parents=True, exist_ok=True)

# #     results = []
# #     for table in parsed_model["tables"]:
# #         name = table["name"]
# #         match = mapping_by_pbix.get(name)
# #         bronze_fqn = None
# #         if match and match.get("bronze_table"):
# #             bronze_fqn = f'{sf.database}.{match["bronze_schema"]}.{match["bronze_table"]}'

# #         result = translate_table(table, bronze_fqn)
# #         results.append(result)

# #         if result["sql"]:
# #             ddl = build_silver_ddl(name, result["sql"], sf.silver_schema)
# #             safe_name = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
# #             out_file = out_dir / f"silver_{safe_name}.sql"
# #             with open(out_file, "w", encoding="utf-8") as f:
# #                 f.write(ddl)
# #             result["output_file"] = str(out_file)
# #             log.info(f"Wrote {out_file}")

# #     summary_path = out_dir / "_translation_summary.json"
# #     with open(summary_path, "w", encoding="utf-8") as f:
# #         json.dump(results, f, indent=2)

# #     print("\nM Query -> SQL Translation Summary:")
# #     for r in results:
# #         status = "OK" if r["sql"] else "SKIPPED"
# #         print(f"  [{status:7s}] {r['table_name']:30s} method={r['method']}")
# #         for w in r["warnings"]:
# #             print(f"             ! {w}")


# # if __name__ == "__main__":
# #     main()

# """
# mquery_translator.py
# Translates Power Query (M) expressions into Snowflake SQL using a small
# rule engine for common, deterministic M steps (renaming, type changes,
# simple filters) plus an Azure OpenAI fallback for anything more complex
# (custom logic, merges, pivots, etc).

# Run:
#     python src/mquery_translator.py \
#         --model output/mapping/parsed_model.json \
#         --mapping output/mapping/table_mapping.json \
#         --output-dir output/sql/silver
# """

# import argparse
# import json
# import re
# import sys
# from pathlib import Path
# from typing import Optional

# sys.path.insert(0, str(Path(__file__).resolve().parent))
# from config import config
# from logger import get_logger
# import azure_openai_client as aoai

# log = get_logger("mquery_translator", config.logs_dir, config.pipeline.log_level)


# # ---------------------------------------------------------------------------
# # Rule engine: handles simple, common M patterns deterministically.
# # Anything not recognized falls through to the LLM.
# # ---------------------------------------------------------------------------

# RENAME_RE = re.compile(r'Table\.RenameColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# RENAME_PAIR_RE = re.compile(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}')

# SELECT_COLS_RE = re.compile(r'Table\.SelectColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# COL_NAME_RE = re.compile(r'"([^"]+)"')

# TYPE_MAP = {
#     "Int64.Type": "NUMBER(38,0)",
#     "Number.Type": "FLOAT",
#     "Text.Type": "VARCHAR",
#     "Date.Type": "DATE",
#     "DateTime.Type": "TIMESTAMP_NTZ",
#     "Logical.Type": "BOOLEAN",
#     "Currency.Type": "NUMBER(38,4)",
# }


# def _try_rule_based(m_query: str, source_table_fqn: str) -> Optional[str]:
#     """Attempt a deterministic translation for simple single-purpose M
#     queries (only rename or only column selection). Returns None if the
#     M query is too complex for the rule engine."""
#     if not m_query:
#         return None

#     # Count actual `let`-block step bindings (lines like "Name = Expr(...),")
#     # rather than every "=" in the query, which overcounts boilerplate source
#     # steps (Snowflake.Databases(...), {[Name="..."]}[Data], etc).
#     step_lines = re.findall(r'^\s*\w+\s*=\s*', m_query, flags=re.MULTILINE)
#     step_count = len(step_lines)
#     if step_count > 6:
#         return None  # too complex, let the LLM handle it

#     rename_match = RENAME_RE.search(m_query)
#     select_match = SELECT_COLS_RE.search(m_query)

#     if rename_match and not select_match and "Table.TransformColumnTypes" not in m_query \
#             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
#             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
#         pairs = RENAME_PAIR_RE.findall(rename_match.group(1))
#         if not pairs:
#             return None
#         select_list = ",\n    ".join(f'"{old}" AS "{new}"' for old, new in pairs)
#         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

#     if select_match and not rename_match and "Table.TransformColumnTypes" not in m_query \
#             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
#             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
#         cols = COL_NAME_RE.findall(select_match.group(1))
#         if not cols:
#             return None
#         select_list = ",\n    ".join(f'"{c}"' for c in cols)
#         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

#     return None


# LLM_SYSTEM_PROMPT = """You are a senior data engineer translating Microsoft Power Query (M language)
# ETL steps into equivalent Snowflake SQL (a SELECT statement to be used inside a CREATE OR REPLACE VIEW
# or CREATE TABLE AS SELECT). Follow these rules:

# - Output ONLY the SQL SELECT statement. No explanation, no markdown fences.
# - Use ANSI SQL compatible with Snowflake (e.g. IFF, QUALIFY, DATE_TRUNC are all fine).
# - Reference the given source table fully-qualified name as the FROM source.
# - Preserve renaming, type conversions, filtering, derived/calculated columns, and joins faithfully.
# - For Table.TransformColumnTypes, map Power Query types to Snowflake types reasonably
#   (Int64.Type -> NUMBER, Number.Type -> FLOAT, Text.Type -> VARCHAR, Date.Type -> DATE,
#   DateTime.Type -> TIMESTAMP_NTZ, Logical.Type -> BOOLEAN).
# - For Table.SelectRows filters, translate the logical condition into a WHERE clause as faithfully as possible.
# - If a step cannot be translated with certainty, add a SQL comment "-- TODO: manual review: <reason>" above
#   the affected clause, but still produce your best-effort SQL.
# - Always alias the final output columns clearly.

# CRITICAL — TYPE CASTING RULES (Snowflake-specific, violations cause deployment failures):
#   * NEVER use TRY_CAST() to cast a BOOLEAN column to VARCHAR or any other type.
#     TRY_CAST() only accepts VARCHAR as its first argument. Snowflake will raise
#     "Function TRY_CAST cannot be used with arguments of types BOOLEAN and VARCHAR".
#     Instead use: CAST(col AS VARCHAR)  or  col::VARCHAR
#   * NEVER use TRY_CAST() on NUMBER, DATE, TIMESTAMP, or BOOLEAN source columns.
#     Only use TRY_CAST() when the source column is VARCHAR/TEXT and the target is a
#     non-string type (e.g. TRY_CAST(varchar_col AS NUMBER)).
#   * For BOOLEAN → VARCHAR: use CAST(col AS VARCHAR) or IFF(col, 'true', 'false').
#   * For NUMBER → VARCHAR: use col::VARCHAR or CAST(col AS VARCHAR).
#   * For DATE/TIMESTAMP → VARCHAR: use TO_VARCHAR(col) or TO_VARCHAR(col, 'YYYY-MM-DD').

# CRITICAL — TABLE ALIAS RULES (violations cause "invalid identifier" errors):
#   * Every table alias you use in SELECT, WHERE, JOIN ON, or ORDER BY clauses MUST be
#     explicitly defined in the FROM or JOIN clause of the same query.
#   * Single-letter aliases (R, T, X, etc.) are a common source of errors. Only use
#     them if you explicitly write e.g. FROM mytable R or JOIN othertable AS R.
#   * Before finalising the SQL, mentally trace every <ALIAS>.<column> reference back
#     to its FROM/JOIN alias definition. If you cannot trace it, remove the alias prefix
#     or rewrite the query.
#   * NEVER invent or assume aliases that are not in your generated FROM/JOIN clauses.

# CRITICAL — JOIN RULES (violations cause "object does not exist" or "duplicate column" errors):
#   * NEVER reference a joined table by a bare unqualified name (e.g. JOIN Emp).
#     ALL tables in FROM and JOIN clauses MUST use the fully-qualified name provided
#     in the "all_bronze_columns" or "silver_table_map" lists in the prompt.
#     If the joined table is a Silver view, use its Silver FQN (e.g. MIGRATION_DD.SILVER.EMP).
#     If it is a Bronze table, use its Bronze FQN (e.g. MIGRATION_DD.BRONZE.EMP).
#   * NEVER use SELECT alias.* or SELECT * when two joined tables share a column name
#     (e.g. both have DEPTNO). Always list every column explicitly with an alias to
#     avoid "duplicate column name" errors. Omit the duplicate column from the secondary
#     table or rename it (e.g. E.DEPTNO AS EMP_DEPTNO).
# """


# def _llm_translate(m_query: str, table_name: str, source_table_fqn: str, columns: list,
#                    all_bronze_columns: dict = None, all_silver_fqns: dict = None) -> str:
#     """
#     all_bronze_columns: {bronze_fqn: [col_name, ...]} for the primary and any joined tables.
#     all_silver_fqns:    {pbix_table_name: silver_fqn} — the Silver FQNs the LLM must use
#                         when it needs to JOIN another already-translated Silver view.
#     """
#     col_list = [c["name"] if isinstance(c, dict) else c for c in (columns or [])]
#     primary_cols_line = f"Columns available in {source_table_fqn}: {col_list}"

#     joined_block = ""
#     if all_silver_fqns or all_bronze_columns:
#         lines = []
#         # Silver FQNs take priority — these are what a JOIN should reference
#         if all_silver_fqns:
#             for pbix_name, sfqn in all_silver_fqns.items():
#                 bronze_fqn = next(
#                     (k for k in (all_bronze_columns or {}) if k.upper().endswith(f".{pbix_name.upper()}")),
#                     None
#                 )
#                 cols = (all_bronze_columns or {}).get(bronze_fqn, []) if bronze_fqn else []
#                 col_str = f", columns: {cols}" if cols else ""
#                 lines.append(f"  {pbix_name}  →  Silver FQN: {sfqn}{col_str}")
#         if lines:
#             joined_block = (
#                 "\nOther Silver views available for JOINs — ALWAYS use these exact Silver FQNs"
#                 " in JOIN clauses, NEVER bare table names like 'Emp' or 'Returns':\n"
#                 + "\n".join(lines)
#             )

#     user_prompt = f"""Power BI table name: {table_name}
# Snowflake source table (Bronze layer, fully qualified): {source_table_fqn}
# {primary_cols_line}{joined_block}

# Power Query (M) expression to translate:
# ---
# {m_query}
# ---

# Produce the equivalent Snowflake SQL SELECT statement reading from {source_table_fqn}.
# CRITICAL:
# - Use ONLY the column names listed above. Never invent column names not in those lists.
# - For any JOIN, use the Silver FQN shown above (e.g. MIGRATION_DD.SILVER.EMP), never a bare name.
# - NEVER use SELECT alias.* when joined tables share column names — list each column explicitly
#   and rename duplicates (e.g. E.DEPTNO AS EMP_DEPTNO) to avoid duplicate column name errors.
# """
#     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# # ---------------------------------------------------------------------------
# # Post-generation SQL linters
# # Called immediately after the LLM returns SQL to catch hallucination bugs
# # before writing any .sql file to disk.
# # ---------------------------------------------------------------------------

# _LINT_TRY_CAST_RE = re.compile(
#     r'\bTRY_CAST\s*\(\s*([A-Za-z0-9_."]+)\s+AS\s+(VARCHAR|TEXT)(\s*\(\s*\d+\s*\))?\s*\)',
#     re.IGNORECASE,
# )


# def _lint_fix_try_cast(sql: str) -> tuple[str, list[str]]:
#     """Replace TRY_CAST(col AS VARCHAR) → CAST(col AS VARCHAR).
#     TRY_CAST only accepts a VARCHAR *source*; using it on BOOLEAN/NUMBER/DATE
#     causes a Snowflake runtime error.  A plain CAST is always safe here."""
#     notes = []

#     def _replace(m):
#         col, dtype, size = m.group(1), m.group(2), m.group(3) or ""
#         notes.append(
#             f"TRY_CAST({col} AS {dtype}{size}) → CAST({col} AS {dtype}{size}) "
#             "(TRY_CAST cannot accept non-VARCHAR source columns in Snowflake)"
#         )
#         return f"CAST({col} AS {dtype}{size})"

#     patched = _LINT_TRY_CAST_RE.sub(_replace, sql)
#     return patched, notes


# _LINT_ALIAS_USE_RE = re.compile(r'\b([A-Z])\.\b([A-Za-z0-9_]+)\b')
# _LINT_ALIAS_DEF_RE = re.compile(
#     r'\b(?:FROM|JOIN)\s+\S+\s+(?:AS\s+)?([A-Z])\b', re.IGNORECASE
# )


# def _lint_check_aliases(sql: str) -> tuple[str, list[str]]:
#     """Detect single-letter aliases used but never defined in FROM/JOIN.
#     These cause Snowflake 'invalid identifier <ALIAS>.<col>' errors.
#     We cannot auto-fix (we don't know the intended table), so we prepend a
#     visible TODO comment so the issue is obvious in the generated .sql file."""
#     defined = set(_LINT_ALIAS_DEF_RE.findall(sql.upper()))
#     used = set(m.group(1).upper() for m in _LINT_ALIAS_USE_RE.finditer(sql))
#     undefined = used - defined
#     notes = []
#     if undefined:
#         comment = (
#             "-- !! LINT WARNING: Undefined single-letter alias(es) detected: "
#             + ", ".join(sorted(undefined))
#             + "\n"
#             "-- These will cause 'invalid identifier' errors in Snowflake.\n"
#             "-- Check the FROM/JOIN clauses and correct the alias references below.\n"
#         )
#         first_select = re.search(r'\bSELECT\b', sql, re.IGNORECASE)
#         if first_select:
#             sql = sql[: first_select.start()] + comment + sql[first_select.start():]
#         else:
#             sql = comment + sql
#         notes.append(
#             f"Undefined alias(es) {sorted(undefined)} referenced in generated SQL — "
#             "TODO comment prepended. Manual fix required."
#         )
#     return sql, notes


# def translate_table(
#     table: dict,
#     bronze_fqn: Optional[str],
#     column_mapping: Optional[dict] = None,
#     bronze_columns: Optional[list] = None,
#     all_bronze_columns: Optional[dict] = None,
#     all_silver_fqns: Optional[dict] = None,
# ) -> dict:
#     """Returns dict: {table_name, sql, method, warnings}

#     Args:
#         table:          Parsed PBIX table dict.
#         bronze_fqn:     Fully-qualified Bronze table name, or None if unmatched.
#         column_mapping: Optional {pbix_col: bronze_col} rename map (passed by
#                         newer pipeline_runner versions; used to enrich the LLM
#                         prompt so column aliases are grounded in real names).
#         bronze_columns: Optional list of actual Bronze column names (passed by
#                         newer pipeline_runner versions; used for alias validation).
#     """
#     name = table["name"]
#     m_query = table.get("m_query")
#     warnings = []
#     # Merge any externally supplied column info into the table dict so the
#     # LLM prompt receives accurate column names and the alias linter can
#     # cross-check against real Bronze columns.
#     if bronze_columns:
#         table = {**table, "_bronze_columns": bronze_columns}
#     if column_mapping:
#         table = {**table, "_column_mapping": column_mapping}
#     if all_bronze_columns:
#         table = {**table, "_all_bronze_columns": all_bronze_columns}
#     if all_silver_fqns:
#         table = {**table, "_all_silver_fqns": all_silver_fqns}

#     if table.get("is_calculated_table"):
#         return {
#             "table_name": name, "sql": None, "method": "skipped_calculated_table",
#             "warnings": ["Calculated table — handled in DAX->SQL translation (Gold layer), not Silver."],
#         }

#     if not bronze_fqn:
#         return {
#             "table_name": name, "sql": None, "method": "skipped_unmatched",
#             "warnings": [f"No matched Bronze table for '{name}'; cannot generate Silver SQL."],
#         }

#     if not m_query:
#         sql = f"SELECT *\nFROM {bronze_fqn}"
#         return {
#             "table_name": name, "sql": sql, "method": "passthrough",
#             "warnings": ["No M query found; generated pass-through SELECT *."],
#         }

#     rule_sql = _try_rule_based(m_query, bronze_fqn)
#     if rule_sql:
#         log.info(f"[RULE]   {name}: translated via rule engine")
#         return {"table_name": name, "sql": rule_sql, "method": "rule_engine", "warnings": []}

#     log.info(f"[LLM]    {name}: M query too complex for rule engine, calling Azure OpenAI")
#     try:
#         cols = bronze_columns or table.get("columns", [])
#         # Pass all known Bronze column lists so the LLM can't hallucinate
#         # column names on tables it JOINs to.
#         all_bronze_columns = table.get("_all_bronze_columns") or None
#         all_silver_fqns = table.get("_all_silver_fqns") or None
#         sql = _llm_translate(m_query, name, bronze_fqn, cols,
#                              all_bronze_columns=all_bronze_columns,
#                              all_silver_fqns=all_silver_fqns)
#         if "-- TODO: manual review" in sql:
#             warnings.append("LLM flagged at least one clause for manual review — search SQL for 'TODO'.")

#         # --- Post-generation lint: fix known LLM hallucination patterns ---
#         sql, cast_patches = _lint_fix_try_cast(sql)
#         for note in cast_patches:
#             warnings.append(f"AUTO-FIXED: {note}")
#             log.warning(f"[LINT] {name}: {note}")

#         sql, alias_warnings = _lint_check_aliases(sql)
#         for note in alias_warnings:
#             warnings.append(f"ALIAS WARNING: {note}")
#             log.warning(f"[LINT] {name}: {note}")

#         return {"table_name": name, "sql": sql, "method": "llm", "warnings": warnings}
#     except Exception as e:
#         log.error(f"LLM translation failed for {name}: {e}")
#         fallback_sql = f"SELECT *\nFROM {bronze_fqn}\n-- TODO: manual review: LLM translation failed ({e})"
#         return {"table_name": name, "sql": fallback_sql, "method": "llm_failed_fallback",
#                 "warnings": [f"LLM translation failed: {e}. Generated pass-through SELECT * as fallback."]}


# def build_silver_ddl(table_name: str, select_sql: str, silver_schema: str,
#                      database: str = None) -> str:
#     """Build CREATE OR REPLACE VIEW DDL for a Silver layer object.
#     The optional `database` arg is accepted for compatibility with pipeline_runner
#     versions that pass it as a 4th positional argument; it is not used in the DDL
#     because silver_schema should already be fully qualified if needed.
#     """
#     safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table_name).upper()
#     return (
#         f"-- Silver layer object for PBIX table: {table_name}\n"
#         f"CREATE OR REPLACE VIEW {silver_schema}.{safe_name} AS\n"
#         f"{select_sql.strip()}\n;\n"
#     )


# def main():
#     ap = argparse.ArgumentParser(description="Translate Power Query (M) to Snowflake SQL and generate Silver layer DDL")
#     ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
#     ap.add_argument("--mapping", default=str(config.mapping_dir / "table_mapping.json"))
#     ap.add_argument("--output-dir", default=str(config.sql_silver_dir))
#     args = ap.parse_args()

#     with open(args.model, "r", encoding="utf-8") as f:
#         parsed_model = json.load(f)
#     with open(args.mapping, "r", encoding="utf-8") as f:
#         table_mapping = json.load(f)

#     mapping_by_pbix = {m["pbix_table"]: m for m in table_mapping}
#     sf = config.snowflake
#     out_dir = Path(args.output_dir)
#     out_dir.mkdir(parents=True, exist_ok=True)

#     results = []
#     for table in parsed_model["tables"]:
#         name = table["name"]
#         match = mapping_by_pbix.get(name)
#         bronze_fqn = None
#         if match and match.get("bronze_table"):
#             bronze_fqn = f'{sf.database}.{match["bronze_schema"]}.{match["bronze_table"]}'

#         result = translate_table(table, bronze_fqn)
#         results.append(result)

#         if result["sql"]:
#             ddl = build_silver_ddl(name, result["sql"], sf.silver_schema)
#             safe_name = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
#             out_file = out_dir / f"silver_{safe_name}.sql"
#             with open(out_file, "w", encoding="utf-8") as f:
#                 f.write(ddl)
#             result["output_file"] = str(out_file)
#             log.info(f"Wrote {out_file}")

#     summary_path = out_dir / "_translation_summary.json"
#     with open(summary_path, "w", encoding="utf-8") as f:
#         json.dump(results, f, indent=2)

#     print("\nM Query -> SQL Translation Summary:")
#     for r in results:
#         status = "OK" if r["sql"] else "SKIPPED"
#         print(f"  [{status:7s}] {r['table_name']:30s} method={r['method']}")
#         for w in r["warnings"]:
#             print(f"             ! {w}")


# if __name__ == "__main__":
#     main()

# # """
# # mquery_translator.py
# # Translates Power Query (M) expressions into Snowflake SQL using a small
# # rule engine for common, deterministic M steps (renaming, type changes,
# # simple filters) plus an Azure OpenAI fallback for anything more complex
# # (custom logic, merges, pivots, etc).

# # Run:
# #     python src/mquery_translator.py \
# #         --model output/mapping/parsed_model.json \
# #         --mapping output/mapping/table_mapping.json \
# #         --output-dir output/sql/silver
# # """

# # import argparse
# # import json
# # import re
# # import sys
# # from pathlib import Path
# # from typing import Optional

# # sys.path.insert(0, str(Path(__file__).resolve().parent))
# # from config import config
# # from logger import get_logger
# # import azure_openai_client as aoai

# # log = get_logger("mquery_translator", config.logs_dir, config.pipeline.log_level)


# # # ---------------------------------------------------------------------------
# # # Rule engine: handles simple, common M patterns deterministically.
# # # Anything not recognized falls through to the LLM.
# # # ---------------------------------------------------------------------------

# # RENAME_RE = re.compile(r'Table\.RenameColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# # RENAME_PAIR_RE = re.compile(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}')

# # SELECT_COLS_RE = re.compile(r'Table\.SelectColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# # COL_NAME_RE = re.compile(r'"([^"]+)"')

# # TYPE_MAP = {
# #     "Int64.Type": "NUMBER(38,0)",
# #     "Number.Type": "FLOAT",
# #     "Text.Type": "VARCHAR",
# #     "Date.Type": "DATE",
# #     "DateTime.Type": "TIMESTAMP_NTZ",
# #     "Logical.Type": "BOOLEAN",
# #     "Currency.Type": "NUMBER(38,4)",
# # }


# # def _try_rule_based(m_query: str, source_table_fqn: str) -> Optional[str]:
# #     """Attempt a deterministic translation for simple single-purpose M
# #     queries (only rename or only column selection). Returns None if the
# #     M query is too complex for the rule engine."""
# #     if not m_query:
# #         return None

# #     # Count actual `let`-block step bindings (lines like "Name = Expr(...),")
# #     # rather than every "=" in the query, which overcounts boilerplate source
# #     # steps (Snowflake.Databases(...), {[Name="..."]}[Data], etc).
# #     step_lines = re.findall(r'^\s*\w+\s*=\s*', m_query, flags=re.MULTILINE)
# #     step_count = len(step_lines)
# #     if step_count > 6:
# #         return None  # too complex, let the LLM handle it

# #     rename_match = RENAME_RE.search(m_query)
# #     select_match = SELECT_COLS_RE.search(m_query)

# #     if rename_match and not select_match and "Table.TransformColumnTypes" not in m_query \
# #             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
# #             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
# #         pairs = RENAME_PAIR_RE.findall(rename_match.group(1))
# #         if not pairs:
# #             return None
# #         select_list = ",\n    ".join(f'"{old}" AS "{new}"' for old, new in pairs)
# #         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

# #     if select_match and not rename_match and "Table.TransformColumnTypes" not in m_query \
# #             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
# #             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
# #         cols = COL_NAME_RE.findall(select_match.group(1))
# #         if not cols:
# #             return None
# #         select_list = ",\n    ".join(f'"{c}"' for c in cols)
# #         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

# #     return None


# # LLM_SYSTEM_PROMPT = """You are a senior data engineer translating Microsoft Power Query (M language)
# # ETL steps into equivalent Snowflake SQL (a SELECT statement to be used inside a CREATE OR REPLACE VIEW
# # or CREATE TABLE AS SELECT). Follow these rules:

# # - Output ONLY the SQL SELECT statement. No explanation, no markdown fences.
# # - Use ANSI SQL compatible with Snowflake (e.g. IFF, QUALIFY, DATE_TRUNC are all fine).
# # - Reference the given source table fully-qualified name as the FROM source.
# # - Preserve renaming, type conversions, filtering, derived/calculated columns, and joins faithfully.
# # - For Table.TransformColumnTypes, map Power Query types to Snowflake types reasonably
# #   (Int64.Type -> NUMBER, Number.Type -> FLOAT, Text.Type -> VARCHAR, Date.Type -> DATE,
# #   DateTime.Type -> TIMESTAMP_NTZ, Logical.Type -> BOOLEAN).
# # - For Table.SelectRows filters, translate the logical condition into a WHERE clause as faithfully as possible.
# # - If a step cannot be translated with certainty, add a SQL comment "-- TODO: manual review: <reason>" above
# #   the affected clause, but still produce your best-effort SQL.
# # - Always alias the final output columns clearly.

# # CRITICAL — TYPE CASTING RULES (Snowflake-specific, violations cause deployment failures):
# #   * NEVER use TRY_CAST() to cast a BOOLEAN column to VARCHAR or any other type.
# #     TRY_CAST() only accepts VARCHAR as its first argument. Snowflake will raise
# #     "Function TRY_CAST cannot be used with arguments of types BOOLEAN and VARCHAR".
# #     Instead use: CAST(col AS VARCHAR)  or  col::VARCHAR
# #   * NEVER use TRY_CAST() on NUMBER, DATE, TIMESTAMP, or BOOLEAN source columns.
# #     Only use TRY_CAST() when the source column is VARCHAR/TEXT and the target is a
# #     non-string type (e.g. TRY_CAST(varchar_col AS NUMBER)).
# #   * For BOOLEAN → VARCHAR: use CAST(col AS VARCHAR) or IFF(col, 'true', 'false').
# #   * For NUMBER → VARCHAR: use col::VARCHAR or CAST(col AS VARCHAR).
# #   * For DATE/TIMESTAMP → VARCHAR: use TO_VARCHAR(col) or TO_VARCHAR(col, 'YYYY-MM-DD').

# # CRITICAL — TABLE ALIAS RULES (violations cause "invalid identifier" errors):
# #   * Every table alias you use in SELECT, WHERE, JOIN ON, or ORDER BY clauses MUST be
# #     explicitly defined in the FROM or JOIN clause of the same query.
# #   * Single-letter aliases (R, T, X, etc.) are a common source of errors. Only use
# #     them if you explicitly write e.g. FROM mytable R or JOIN othertable AS R.
# #   * Before finalising the SQL, mentally trace every <ALIAS>.<column> reference back
# #     to its FROM/JOIN alias definition. If you cannot trace it, remove the alias prefix
# #     or rewrite the query.
# #   * NEVER invent or assume aliases that are not in your generated FROM/JOIN clauses.
# # """


# # def _llm_translate(m_query: str, table_name: str, source_table_fqn: str, columns: list) -> str:
# #     user_prompt = f"""Power BI table name: {table_name}
# # Snowflake source table (Bronze layer, fully qualified): {source_table_fqn}
# # Known column names: {[c["name"] if isinstance(c, dict) else c for c in (columns or [])]}

# # Power Query (M) expression to translate:
# # ---
# # {m_query}
# # ---

# # Produce the equivalent Snowflake SQL SELECT statement reading from {source_table_fqn}.
# # """
# #     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# # # ---------------------------------------------------------------------------
# # # Post-generation SQL linters
# # # Called immediately after the LLM returns SQL to catch hallucination bugs
# # # before writing any .sql file to disk.
# # # ---------------------------------------------------------------------------

# # _LINT_TRY_CAST_RE = re.compile(
# #     r'\bTRY_CAST\s*\(\s*([A-Za-z0-9_."]+)\s+AS\s+(VARCHAR|TEXT)(\s*\(\s*\d+\s*\))?\s*\)',
# #     re.IGNORECASE,
# # )


# # def _lint_fix_try_cast(sql: str) -> tuple[str, list[str]]:
# #     """Replace TRY_CAST(col AS VARCHAR) → CAST(col AS VARCHAR).
# #     TRY_CAST only accepts a VARCHAR *source*; using it on BOOLEAN/NUMBER/DATE
# #     causes a Snowflake runtime error.  A plain CAST is always safe here."""
# #     notes = []

# #     def _replace(m):
# #         col, dtype, size = m.group(1), m.group(2), m.group(3) or ""
# #         notes.append(
# #             f"TRY_CAST({col} AS {dtype}{size}) → CAST({col} AS {dtype}{size}) "
# #             "(TRY_CAST cannot accept non-VARCHAR source columns in Snowflake)"
# #         )
# #         return f"CAST({col} AS {dtype}{size})"

# #     patched = _LINT_TRY_CAST_RE.sub(_replace, sql)
# #     return patched, notes


# # _LINT_ALIAS_USE_RE = re.compile(r'\b([A-Z])\.\b([A-Za-z0-9_]+)\b')
# # _LINT_ALIAS_DEF_RE = re.compile(
# #     r'\b(?:FROM|JOIN)\s+\S+\s+(?:AS\s+)?([A-Z])\b', re.IGNORECASE
# # )


# # def _lint_check_aliases(sql: str) -> tuple[str, list[str]]:
# #     """Detect single-letter aliases used but never defined in FROM/JOIN.
# #     These cause Snowflake 'invalid identifier <ALIAS>.<col>' errors.
# #     We cannot auto-fix (we don't know the intended table), so we prepend a
# #     visible TODO comment so the issue is obvious in the generated .sql file."""
# #     defined = set(_LINT_ALIAS_DEF_RE.findall(sql.upper()))
# #     used = set(m.group(1).upper() for m in _LINT_ALIAS_USE_RE.finditer(sql))
# #     undefined = used - defined
# #     notes = []
# #     if undefined:
# #         comment = (
# #             "-- !! LINT WARNING: Undefined single-letter alias(es) detected: "
# #             + ", ".join(sorted(undefined))
# #             + "\n"
# #             "-- These will cause 'invalid identifier' errors in Snowflake.\n"
# #             "-- Check the FROM/JOIN clauses and correct the alias references below.\n"
# #         )
# #         first_select = re.search(r'\bSELECT\b', sql, re.IGNORECASE)
# #         if first_select:
# #             sql = sql[: first_select.start()] + comment + sql[first_select.start():]
# #         else:
# #             sql = comment + sql
# #         notes.append(
# #             f"Undefined alias(es) {sorted(undefined)} referenced in generated SQL — "
# #             "TODO comment prepended. Manual fix required."
# #         )
# #     return sql, notes


# # def translate_table(
# #     table: dict,
# #     bronze_fqn: Optional[str],
# #     column_mapping: Optional[dict] = None,
# #     bronze_columns: Optional[list] = None,
# # ) -> dict:
# #     """Returns dict: {table_name, sql, method, warnings}

# #     Args:
# #         table:          Parsed PBIX table dict.
# #         bronze_fqn:     Fully-qualified Bronze table name, or None if unmatched.
# #         column_mapping: Optional {pbix_col: bronze_col} rename map (passed by
# #                         newer pipeline_runner versions; used to enrich the LLM
# #                         prompt so column aliases are grounded in real names).
# #         bronze_columns: Optional list of actual Bronze column names (passed by
# #                         newer pipeline_runner versions; used for alias validation).
# #     """
# #     name = table["name"]
# #     m_query = table.get("m_query")
# #     warnings = []
# #     # Merge any externally supplied column info into the table dict so the
# #     # LLM prompt receives accurate column names and the alias linter can
# #     # cross-check against real Bronze columns.
# #     if bronze_columns:
# #         table = {**table, "_bronze_columns": bronze_columns}
# #     if column_mapping:
# #         table = {**table, "_column_mapping": column_mapping}

# #     if table.get("is_calculated_table"):
# #         return {
# #             "table_name": name, "sql": None, "method": "skipped_calculated_table",
# #             "warnings": ["Calculated table — handled in DAX->SQL translation (Gold layer), not Silver."],
# #         }

# #     if not bronze_fqn:
# #         return {
# #             "table_name": name, "sql": None, "method": "skipped_unmatched",
# #             "warnings": [f"No matched Bronze table for '{name}'; cannot generate Silver SQL."],
# #         }

# #     if not m_query:
# #         sql = f"SELECT *\nFROM {bronze_fqn}"
# #         return {
# #             "table_name": name, "sql": sql, "method": "passthrough",
# #             "warnings": ["No M query found; generated pass-through SELECT *."],
# #         }

# #     rule_sql = _try_rule_based(m_query, bronze_fqn)
# #     if rule_sql:
# #         log.info(f"[RULE]   {name}: translated via rule engine")
# #         return {"table_name": name, "sql": rule_sql, "method": "rule_engine", "warnings": []}

# #     log.info(f"[LLM]    {name}: M query too complex for rule engine, calling Azure OpenAI")
# #     try:
# #         # Prefer actual Bronze column names (from newer pipeline_runner) over
# #         # PBIX column names for grounding the LLM prompt in real identifiers.
# #         cols = bronze_columns or table.get("columns", [])
# #         sql = _llm_translate(m_query, name, bronze_fqn, cols)
# #         if "-- TODO: manual review" in sql:
# #             warnings.append("LLM flagged at least one clause for manual review — search SQL for 'TODO'.")

# #         # --- Post-generation lint: fix known LLM hallucination patterns ---
# #         sql, cast_patches = _lint_fix_try_cast(sql)
# #         for note in cast_patches:
# #             warnings.append(f"AUTO-FIXED: {note}")
# #             log.warning(f"[LINT] {name}: {note}")

# #         sql, alias_warnings = _lint_check_aliases(sql)
# #         for note in alias_warnings:
# #             warnings.append(f"ALIAS WARNING: {note}")
# #             log.warning(f"[LINT] {name}: {note}")

# #         return {"table_name": name, "sql": sql, "method": "llm", "warnings": warnings}
# #     except Exception as e:
# #         log.error(f"LLM translation failed for {name}: {e}")
# #         fallback_sql = f"SELECT *\nFROM {bronze_fqn}\n-- TODO: manual review: LLM translation failed ({e})"
# #         return {"table_name": name, "sql": fallback_sql, "method": "llm_failed_fallback",
# #                 "warnings": [f"LLM translation failed: {e}. Generated pass-through SELECT * as fallback."]}


# # def build_silver_ddl(table_name: str, select_sql: str, silver_schema: str,
# #                      database: str = None) -> str:
# #     """Build CREATE OR REPLACE VIEW DDL for a Silver layer object.
# #     The optional `database` arg is accepted for compatibility with pipeline_runner
# #     versions that pass it as a 4th positional argument; it is not used in the DDL
# #     because silver_schema should already be fully qualified if needed.
# #     """
# #     safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table_name).upper()
# #     return (
# #         f"-- Silver layer object for PBIX table: {table_name}\n"
# #         f"CREATE OR REPLACE VIEW {silver_schema}.{safe_name} AS\n"
# #         f"{select_sql.strip()}\n;\n"
# #     )


# # def main():
# #     ap = argparse.ArgumentParser(description="Translate Power Query (M) to Snowflake SQL and generate Silver layer DDL")
# #     ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
# #     ap.add_argument("--mapping", default=str(config.mapping_dir / "table_mapping.json"))
# #     ap.add_argument("--output-dir", default=str(config.sql_silver_dir))
# #     args = ap.parse_args()

# #     with open(args.model, "r", encoding="utf-8") as f:
# #         parsed_model = json.load(f)
# #     with open(args.mapping, "r", encoding="utf-8") as f:
# #         table_mapping = json.load(f)

# #     mapping_by_pbix = {m["pbix_table"]: m for m in table_mapping}
# #     sf = config.snowflake
# #     out_dir = Path(args.output_dir)
# #     out_dir.mkdir(parents=True, exist_ok=True)

# #     results = []
# #     for table in parsed_model["tables"]:
# #         name = table["name"]
# #         match = mapping_by_pbix.get(name)
# #         bronze_fqn = None
# #         if match and match.get("bronze_table"):
# #             bronze_fqn = f'{sf.database}.{match["bronze_schema"]}.{match["bronze_table"]}'

# #         result = translate_table(table, bronze_fqn)
# #         results.append(result)

# #         if result["sql"]:
# #             ddl = build_silver_ddl(name, result["sql"], sf.silver_schema)
# #             safe_name = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
# #             out_file = out_dir / f"silver_{safe_name}.sql"
# #             with open(out_file, "w", encoding="utf-8") as f:
# #                 f.write(ddl)
# #             result["output_file"] = str(out_file)
# #             log.info(f"Wrote {out_file}")

# #     summary_path = out_dir / "_translation_summary.json"
# #     with open(summary_path, "w", encoding="utf-8") as f:
# #         json.dump(results, f, indent=2)

# #     print("\nM Query -> SQL Translation Summary:")
# #     for r in results:
# #         status = "OK" if r["sql"] else "SKIPPED"
# #         print(f"  [{status:7s}] {r['table_name']:30s} method={r['method']}")
# #         for w in r["warnings"]:
# #             print(f"             ! {w}")


# # if __name__ == "__main__":
# #     main()

# """
# mquery_translator.py
# Translates Power Query (M) expressions into Snowflake SQL using a small
# rule engine for common, deterministic M steps (renaming, type changes,
# simple filters) plus an Azure OpenAI fallback for anything more complex
# (custom logic, merges, pivots, etc).

# Run:
#     python src/mquery_translator.py \
#         --model output/mapping/parsed_model.json \
#         --mapping output/mapping/table_mapping.json \
#         --output-dir output/sql/silver
# """

# import argparse
# import json
# import re
# import sys
# from pathlib import Path
# from typing import Optional

# sys.path.insert(0, str(Path(__file__).resolve().parent))
# from config import config
# from logger import get_logger
# import azure_openai_client as aoai

# log = get_logger("mquery_translator", config.logs_dir, config.pipeline.log_level)


# # ---------------------------------------------------------------------------
# # Rule engine: handles simple, common M patterns deterministically.
# # Anything not recognized falls through to the LLM.
# # ---------------------------------------------------------------------------

# RENAME_RE = re.compile(r'Table\.RenameColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# RENAME_PAIR_RE = re.compile(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}')

# SELECT_COLS_RE = re.compile(r'Table\.SelectColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
# COL_NAME_RE = re.compile(r'"([^"]+)"')

# TYPE_MAP = {
#     "Int64.Type": "NUMBER(38,0)",
#     "Number.Type": "FLOAT",
#     "Text.Type": "VARCHAR",
#     "Date.Type": "DATE",
#     "DateTime.Type": "TIMESTAMP_NTZ",
#     "Logical.Type": "BOOLEAN",
#     "Currency.Type": "NUMBER(38,4)",
# }


# def _try_rule_based(m_query: str, source_table_fqn: str) -> Optional[str]:
#     """Attempt a deterministic translation for simple single-purpose M
#     queries (only rename or only column selection). Returns None if the
#     M query is too complex for the rule engine."""
#     if not m_query:
#         return None

#     # Count actual `let`-block step bindings (lines like "Name = Expr(...),")
#     # rather than every "=" in the query, which overcounts boilerplate source
#     # steps (Snowflake.Databases(...), {[Name="..."]}[Data], etc).
#     step_lines = re.findall(r'^\s*\w+\s*=\s*', m_query, flags=re.MULTILINE)
#     step_count = len(step_lines)
#     if step_count > 6:
#         return None  # too complex, let the LLM handle it

#     rename_match = RENAME_RE.search(m_query)
#     select_match = SELECT_COLS_RE.search(m_query)

#     if rename_match and not select_match and "Table.TransformColumnTypes" not in m_query \
#             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
#             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
#         pairs = RENAME_PAIR_RE.findall(rename_match.group(1))
#         if not pairs:
#             return None
#         select_list = ",\n    ".join(f'"{old}" AS "{new}"' for old, new in pairs)
#         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

#     if select_match and not rename_match and "Table.TransformColumnTypes" not in m_query \
#             and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
#             and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
#         cols = COL_NAME_RE.findall(select_match.group(1))
#         if not cols:
#             return None
#         select_list = ",\n    ".join(f'"{c}"' for c in cols)
#         return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

#     return None


# LLM_SYSTEM_PROMPT = """You are a senior data engineer translating Microsoft Power Query (M language)
# ETL steps into equivalent Snowflake SQL (a SELECT statement to be used inside a CREATE OR REPLACE VIEW
# or CREATE TABLE AS SELECT). Follow these rules:

# - Output ONLY the SQL SELECT statement. No explanation, no markdown fences.
# - Use ANSI SQL compatible with Snowflake (e.g. IFF, QUALIFY, DATE_TRUNC are all fine).
# - Reference the given source table fully-qualified name as the FROM source.
# - Preserve renaming, type conversions, filtering, derived/calculated columns, and joins faithfully.
# - For Table.TransformColumnTypes, map Power Query types to Snowflake types reasonably
#   (Int64.Type -> NUMBER, Number.Type -> FLOAT, Text.Type -> VARCHAR, Date.Type -> DATE,
#   DateTime.Type -> TIMESTAMP_NTZ, Logical.Type -> BOOLEAN).
# - For Table.SelectRows filters, translate the logical condition into a WHERE clause as faithfully as possible.
# - If a step cannot be translated with certainty, add a SQL comment "-- TODO: manual review: <reason>" above
#   the affected clause, but still produce your best-effort SQL.
# - Always alias the final output columns clearly.

# CRITICAL — TYPE CASTING RULES (Snowflake-specific, violations cause deployment failures):
#   * NEVER use TRY_CAST() to cast a BOOLEAN column to VARCHAR or any other type.
#     TRY_CAST() only accepts VARCHAR as its first argument. Snowflake will raise
#     "Function TRY_CAST cannot be used with arguments of types BOOLEAN and VARCHAR".
#     Instead use: CAST(col AS VARCHAR)  or  col::VARCHAR
#   * NEVER use TRY_CAST() on NUMBER, DATE, TIMESTAMP, or BOOLEAN source columns.
#     Only use TRY_CAST() when the source column is VARCHAR/TEXT and the target is a
#     non-string type (e.g. TRY_CAST(varchar_col AS NUMBER)).
#   * For BOOLEAN → VARCHAR: use CAST(col AS VARCHAR) or IFF(col, 'true', 'false').
#   * For NUMBER → VARCHAR: use col::VARCHAR or CAST(col AS VARCHAR).
#   * For DATE/TIMESTAMP → VARCHAR: use TO_VARCHAR(col) or TO_VARCHAR(col, 'YYYY-MM-DD').

# CRITICAL — TABLE ALIAS RULES (violations cause "invalid identifier" errors):
#   * Every table alias you use in SELECT, WHERE, JOIN ON, or ORDER BY clauses MUST be
#     explicitly defined in the FROM or JOIN clause of the same query.
#   * Single-letter aliases (R, T, X, etc.) are a common source of errors. Only use
#     them if you explicitly write e.g. FROM mytable R or JOIN othertable AS R.
#   * Before finalising the SQL, mentally trace every <ALIAS>.<column> reference back
#     to its FROM/JOIN alias definition. If you cannot trace it, remove the alias prefix
#     or rewrite the query.
#   * NEVER invent or assume aliases that are not in your generated FROM/JOIN clauses.
# """


# def _llm_translate(m_query: str, table_name: str, source_table_fqn: str, columns: list,
#                    all_bronze_columns: dict = None) -> str:
#     """
#     all_bronze_columns: optional dict of {bronze_fqn: [col_name, ...]} for every
#     table that might appear in a JOIN inside this M query.  Passing this prevents
#     the LLM from hallucinating column names on joined tables.
#     """
#     col_list = [c["name"] if isinstance(c, dict) else c for c in (columns or [])]
#     primary_cols_line = f"Columns available in {source_table_fqn}: {col_list}"

#     joined_cols_block = ""
#     if all_bronze_columns:
#         lines = []
#         for fqn, cols in all_bronze_columns.items():
#             if fqn != source_table_fqn:
#                 lines.append(f"  {fqn}: {cols}")
#         if lines:
#             joined_cols_block = (
#                 "\nColumns available in other Bronze tables that may appear in JOINs "
#                 "(use ONLY these column names — do not invent others):\n"
#                 + "\n".join(lines)
#             )

#     user_prompt = f"""Power BI table name: {table_name}
# Snowflake source table (Bronze layer, fully qualified): {source_table_fqn}
# {primary_cols_line}{joined_cols_block}

# Power Query (M) expression to translate:
# ---
# {m_query}
# ---

# Produce the equivalent Snowflake SQL SELECT statement reading from {source_table_fqn}.
# CRITICAL: Use ONLY the column names listed above. Never invent column names not in those lists.
# """
#     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# # ---------------------------------------------------------------------------
# # Post-generation SQL linters
# # Called immediately after the LLM returns SQL to catch hallucination bugs
# # before writing any .sql file to disk.
# # ---------------------------------------------------------------------------

# _LINT_TRY_CAST_RE = re.compile(
#     r'\bTRY_CAST\s*\(\s*([A-Za-z0-9_."]+)\s+AS\s+(VARCHAR|TEXT)(\s*\(\s*\d+\s*\))?\s*\)',
#     re.IGNORECASE,
# )


# def _lint_fix_try_cast(sql: str) -> tuple[str, list[str]]:
#     """Replace TRY_CAST(col AS VARCHAR) → CAST(col AS VARCHAR).
#     TRY_CAST only accepts a VARCHAR *source*; using it on BOOLEAN/NUMBER/DATE
#     causes a Snowflake runtime error.  A plain CAST is always safe here."""
#     notes = []

#     def _replace(m):
#         col, dtype, size = m.group(1), m.group(2), m.group(3) or ""
#         notes.append(
#             f"TRY_CAST({col} AS {dtype}{size}) → CAST({col} AS {dtype}{size}) "
#             "(TRY_CAST cannot accept non-VARCHAR source columns in Snowflake)"
#         )
#         return f"CAST({col} AS {dtype}{size})"

#     patched = _LINT_TRY_CAST_RE.sub(_replace, sql)
#     return patched, notes


# _LINT_ALIAS_USE_RE = re.compile(r'\b([A-Z])\.\b([A-Za-z0-9_]+)\b')
# _LINT_ALIAS_DEF_RE = re.compile(
#     r'\b(?:FROM|JOIN)\s+\S+\s+(?:AS\s+)?([A-Z])\b', re.IGNORECASE
# )


# def _lint_check_aliases(sql: str) -> tuple[str, list[str]]:
#     """Detect single-letter aliases used but never defined in FROM/JOIN.
#     These cause Snowflake 'invalid identifier <ALIAS>.<col>' errors.
#     We cannot auto-fix (we don't know the intended table), so we prepend a
#     visible TODO comment so the issue is obvious in the generated .sql file."""
#     defined = set(_LINT_ALIAS_DEF_RE.findall(sql.upper()))
#     used = set(m.group(1).upper() for m in _LINT_ALIAS_USE_RE.finditer(sql))
#     undefined = used - defined
#     notes = []
#     if undefined:
#         comment = (
#             "-- !! LINT WARNING: Undefined single-letter alias(es) detected: "
#             + ", ".join(sorted(undefined))
#             + "\n"
#             "-- These will cause 'invalid identifier' errors in Snowflake.\n"
#             "-- Check the FROM/JOIN clauses and correct the alias references below.\n"
#         )
#         first_select = re.search(r'\bSELECT\b', sql, re.IGNORECASE)
#         if first_select:
#             sql = sql[: first_select.start()] + comment + sql[first_select.start():]
#         else:
#             sql = comment + sql
#         notes.append(
#             f"Undefined alias(es) {sorted(undefined)} referenced in generated SQL — "
#             "TODO comment prepended. Manual fix required."
#         )
#     return sql, notes


# def translate_table(
#     table: dict,
#     bronze_fqn: Optional[str],
#     column_mapping: Optional[dict] = None,
#     bronze_columns: Optional[list] = None,
#     all_bronze_columns: Optional[dict] = None,
# ) -> dict:
#     """Returns dict: {table_name, sql, method, warnings}

#     Args:
#         table:          Parsed PBIX table dict.
#         bronze_fqn:     Fully-qualified Bronze table name, or None if unmatched.
#         column_mapping: Optional {pbix_col: bronze_col} rename map (passed by
#                         newer pipeline_runner versions; used to enrich the LLM
#                         prompt so column aliases are grounded in real names).
#         bronze_columns: Optional list of actual Bronze column names (passed by
#                         newer pipeline_runner versions; used for alias validation).
#     """
#     name = table["name"]
#     m_query = table.get("m_query")
#     warnings = []
#     # Merge any externally supplied column info into the table dict so the
#     # LLM prompt receives accurate column names and the alias linter can
#     # cross-check against real Bronze columns.
#     if bronze_columns:
#         table = {**table, "_bronze_columns": bronze_columns}
#     if column_mapping:
#         table = {**table, "_column_mapping": column_mapping}
#     if all_bronze_columns:
#         table = {**table, "_all_bronze_columns": all_bronze_columns}

#     if table.get("is_calculated_table"):
#         return {
#             "table_name": name, "sql": None, "method": "skipped_calculated_table",
#             "warnings": ["Calculated table — handled in DAX->SQL translation (Gold layer), not Silver."],
#         }

#     if not bronze_fqn:
#         return {
#             "table_name": name, "sql": None, "method": "skipped_unmatched",
#             "warnings": [f"No matched Bronze table for '{name}'; cannot generate Silver SQL."],
#         }

#     if not m_query:
#         sql = f"SELECT *\nFROM {bronze_fqn}"
#         return {
#             "table_name": name, "sql": sql, "method": "passthrough",
#             "warnings": ["No M query found; generated pass-through SELECT *."],
#         }

#     rule_sql = _try_rule_based(m_query, bronze_fqn)
#     if rule_sql:
#         log.info(f"[RULE]   {name}: translated via rule engine")
#         return {"table_name": name, "sql": rule_sql, "method": "rule_engine", "warnings": []}

#     log.info(f"[LLM]    {name}: M query too complex for rule engine, calling Azure OpenAI")
#     try:
#         cols = bronze_columns or table.get("columns", [])
#         # Pass all known Bronze column lists so the LLM can't hallucinate
#         # column names on tables it JOINs to.
#         all_bronze_columns = table.get("_all_bronze_columns") or None
#         sql = _llm_translate(m_query, name, bronze_fqn, cols,
#                              all_bronze_columns=all_bronze_columns)
#         if "-- TODO: manual review" in sql:
#             warnings.append("LLM flagged at least one clause for manual review — search SQL for 'TODO'.")

#         # --- Post-generation lint: fix known LLM hallucination patterns ---
#         sql, cast_patches = _lint_fix_try_cast(sql)
#         for note in cast_patches:
#             warnings.append(f"AUTO-FIXED: {note}")
#             log.warning(f"[LINT] {name}: {note}")

#         sql, alias_warnings = _lint_check_aliases(sql)
#         for note in alias_warnings:
#             warnings.append(f"ALIAS WARNING: {note}")
#             log.warning(f"[LINT] {name}: {note}")

#         return {"table_name": name, "sql": sql, "method": "llm", "warnings": warnings}
#     except Exception as e:
#         log.error(f"LLM translation failed for {name}: {e}")
#         fallback_sql = f"SELECT *\nFROM {bronze_fqn}\n-- TODO: manual review: LLM translation failed ({e})"
#         return {"table_name": name, "sql": fallback_sql, "method": "llm_failed_fallback",
#                 "warnings": [f"LLM translation failed: {e}. Generated pass-through SELECT * as fallback."]}


# def build_silver_ddl(table_name: str, select_sql: str, silver_schema: str,
#                      database: str = None) -> str:
#     """Build CREATE OR REPLACE VIEW DDL for a Silver layer object.
#     The optional `database` arg is accepted for compatibility with pipeline_runner
#     versions that pass it as a 4th positional argument; it is not used in the DDL
#     because silver_schema should already be fully qualified if needed.
#     """
#     safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table_name).upper()
#     return (
#         f"-- Silver layer object for PBIX table: {table_name}\n"
#         f"CREATE OR REPLACE VIEW {silver_schema}.{safe_name} AS\n"
#         f"{select_sql.strip()}\n;\n"
#     )


# def main():
#     ap = argparse.ArgumentParser(description="Translate Power Query (M) to Snowflake SQL and generate Silver layer DDL")
#     ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
#     ap.add_argument("--mapping", default=str(config.mapping_dir / "table_mapping.json"))
#     ap.add_argument("--output-dir", default=str(config.sql_silver_dir))
#     args = ap.parse_args()

#     with open(args.model, "r", encoding="utf-8") as f:
#         parsed_model = json.load(f)
#     with open(args.mapping, "r", encoding="utf-8") as f:
#         table_mapping = json.load(f)

#     mapping_by_pbix = {m["pbix_table"]: m for m in table_mapping}
#     sf = config.snowflake
#     out_dir = Path(args.output_dir)
#     out_dir.mkdir(parents=True, exist_ok=True)

#     results = []
#     for table in parsed_model["tables"]:
#         name = table["name"]
#         match = mapping_by_pbix.get(name)
#         bronze_fqn = None
#         if match and match.get("bronze_table"):
#             bronze_fqn = f'{sf.database}.{match["bronze_schema"]}.{match["bronze_table"]}'

#         result = translate_table(table, bronze_fqn)
#         results.append(result)

#         if result["sql"]:
#             ddl = build_silver_ddl(name, result["sql"], sf.silver_schema)
#             safe_name = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
#             out_file = out_dir / f"silver_{safe_name}.sql"
#             with open(out_file, "w", encoding="utf-8") as f:
#                 f.write(ddl)
#             result["output_file"] = str(out_file)
#             log.info(f"Wrote {out_file}")

#     summary_path = out_dir / "_translation_summary.json"
#     with open(summary_path, "w", encoding="utf-8") as f:
#         json.dump(results, f, indent=2)

#     print("\nM Query -> SQL Translation Summary:")
#     for r in results:
#         status = "OK" if r["sql"] else "SKIPPED"
#         print(f"  [{status:7s}] {r['table_name']:30s} method={r['method']}")
#         for w in r["warnings"]:
#             print(f"             ! {w}")


# if __name__ == "__main__":
#     main()

"""
mquery_translator.py
Translates Power Query (M) expressions into Snowflake SQL using a small
rule engine for common, deterministic M steps (renaming, type changes,
simple filters) plus an Azure OpenAI fallback for anything more complex
(custom logic, merges, pivots, etc).

Run:
    python src/mquery_translator.py \
        --model output/mapping/parsed_model.json \
        --mapping output/mapping/table_mapping.json \
        --output-dir output/sql/silver
"""

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
from config import config
from logger import get_logger
import azure_openai_client as aoai

log = get_logger("mquery_translator", config.logs_dir, config.pipeline.log_level)


# ---------------------------------------------------------------------------
# Rule engine: handles simple, common M patterns deterministically.
# Anything not recognized falls through to the LLM.
# ---------------------------------------------------------------------------

RENAME_RE = re.compile(r'Table\.RenameColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
RENAME_PAIR_RE = re.compile(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}')

SELECT_COLS_RE = re.compile(r'Table\.SelectColumns\(\s*\w+\s*,\s*\{(.*?)\}\s*\)', re.DOTALL)
COL_NAME_RE = re.compile(r'"([^"]+)"')

TYPE_MAP = {
    "Int64.Type": "NUMBER(38,0)",
    "Number.Type": "FLOAT",
    "Text.Type": "VARCHAR",
    "Date.Type": "DATE",
    "DateTime.Type": "TIMESTAMP_NTZ",
    "Logical.Type": "BOOLEAN",
    "Currency.Type": "NUMBER(38,4)",
}


def _try_rule_based(m_query: str, source_table_fqn: str) -> Optional[str]:
    """Attempt a deterministic translation for simple single-purpose M
    queries (only rename or only column selection). Returns None if the
    M query is too complex for the rule engine."""
    if not m_query:
        return None

    # Count actual `let`-block step bindings (lines like "Name = Expr(...),")
    # rather than every "=" in the query, which overcounts boilerplate source
    # steps (Snowflake.Databases(...), {[Name="..."]}[Data], etc).
    step_lines = re.findall(r'^\s*\w+\s*=\s*', m_query, flags=re.MULTILINE)
    step_count = len(step_lines)
    if step_count > 6:
        return None  # too complex, let the LLM handle it

    rename_match = RENAME_RE.search(m_query)
    select_match = SELECT_COLS_RE.search(m_query)

    if rename_match and not select_match and "Table.TransformColumnTypes" not in m_query \
            and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
            and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
        pairs = RENAME_PAIR_RE.findall(rename_match.group(1))
        if not pairs:
            return None
        select_list = ",\n    ".join(f'"{old}" AS "{new}"' for old, new in pairs)
        return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

    if select_match and not rename_match and "Table.TransformColumnTypes" not in m_query \
            and "Table.SelectRows" not in m_query and "Table.Group" not in m_query \
            and "Table.Join" not in m_query and "Table.NestedJoin" not in m_query:
        cols = COL_NAME_RE.findall(select_match.group(1))
        if not cols:
            return None
        select_list = ",\n    ".join(f'"{c}"' for c in cols)
        return f"SELECT\n    {select_list}\nFROM {source_table_fqn}"

    return None


LLM_SYSTEM_PROMPT = """You are a senior data engineer translating Microsoft Power Query (M language)
ETL steps into equivalent Snowflake SQL (a SELECT statement to be used inside a CREATE OR REPLACE VIEW
or CREATE TABLE AS SELECT). Follow these rules:

- Output ONLY the SQL SELECT statement. No explanation, no markdown fences.
- Use ANSI SQL compatible with Snowflake (e.g. IFF, QUALIFY, DATE_TRUNC are all fine).
- Reference the given source table fully-qualified name as the FROM source.
- Preserve renaming, type conversions, filtering, derived/calculated columns, and joins faithfully.
- For Table.TransformColumnTypes, map Power Query types to Snowflake types reasonably
  (Int64.Type -> NUMBER, Number.Type -> FLOAT, Text.Type -> VARCHAR, Date.Type -> DATE,
  DateTime.Type -> TIMESTAMP_NTZ, Logical.Type -> BOOLEAN).
- For Table.SelectRows filters, translate the logical condition into a WHERE clause as faithfully as possible.
- If a step cannot be translated with certainty, add a SQL comment "-- TODO: manual review: <reason>" above
  the affected clause, but still produce your best-effort SQL.
- Always alias the final output columns clearly.

CRITICAL — TYPE CASTING RULES (Snowflake-specific, violations cause deployment failures):
  * NEVER use TRY_CAST() to cast a BOOLEAN column to VARCHAR or any other type.
    TRY_CAST() only accepts VARCHAR as its first argument. Snowflake will raise
    "Function TRY_CAST cannot be used with arguments of types BOOLEAN and VARCHAR".
    Instead use: CAST(col AS VARCHAR)  or  col::VARCHAR
  * NEVER use TRY_CAST() on NUMBER, DATE, TIMESTAMP, or BOOLEAN source columns.
    Only use TRY_CAST() when the source column is VARCHAR/TEXT and the target is a
    non-string type (e.g. TRY_CAST(varchar_col AS NUMBER)).
  * For BOOLEAN → VARCHAR: use CAST(col AS VARCHAR) or IFF(col, 'true', 'false').
  * For NUMBER → VARCHAR: use col::VARCHAR or CAST(col AS VARCHAR).
  * For DATE/TIMESTAMP → VARCHAR: use TO_VARCHAR(col) or TO_VARCHAR(col, 'YYYY-MM-DD').

CRITICAL — TABLE ALIAS RULES (violations cause "invalid identifier" errors):
  * Every table alias you use in SELECT, WHERE, JOIN ON, or ORDER BY clauses MUST be
    explicitly defined in the FROM or JOIN clause of the same query.
  * Single-letter aliases (R, T, X, etc.) are a common source of errors. Only use
    them if you explicitly write e.g. FROM mytable R or JOIN othertable AS R.
  * Before finalising the SQL, mentally trace every <ALIAS>.<column> reference back
    to its FROM/JOIN alias definition. If you cannot trace it, remove the alias prefix
    or rewrite the query.
  * NEVER invent or assume aliases that are not in your generated FROM/JOIN clauses.

CRITICAL — JOIN RULES (violations cause "object does not exist" or "duplicate column" errors):
  * NEVER reference a joined table by a bare unqualified name (e.g. JOIN Emp).
    ALL tables in FROM and JOIN clauses MUST use the fully-qualified name provided
    in the "all_bronze_columns" or "silver_table_map" lists in the prompt.
    If the joined table is a Silver view, use its Silver FQN (e.g. MIGRATION_DD.SILVER.EMP).
    If it is a Bronze table, use its Bronze FQN (e.g. MIGRATION_DD.BRONZE.EMP).
  * NEVER use SELECT alias.* or SELECT * when two joined tables share a column name
    (e.g. both have DEPTNO). Always list every column explicitly with an alias to
    avoid "duplicate column name" errors. Omit the duplicate column from the secondary
    table or rename it (e.g. E.DEPTNO AS EMP_DEPTNO).
"""


def _llm_translate(m_query: str, table_name: str, source_table_fqn: str, columns: list,
                   all_bronze_columns: dict = None, all_silver_fqns: dict = None,
                   calc_columns: list = None) -> str:
    """
    all_bronze_columns: {bronze_fqn: [col_name, ...]} for the primary and any joined tables.
    all_silver_fqns:    {pbix_table_name: silver_fqn} — the Silver FQNs the LLM must use
                        when it needs to JOIN another already-translated Silver view.
    calc_columns:       [{name, expression}, ...] DAX calculated columns to bake
                        directly into the Silver SELECT as derived expressions.
    """
    col_list = [c["name"] if isinstance(c, dict) else c for c in (columns or [])]
    primary_cols_line = f"Columns available in {source_table_fqn}: {col_list}"

    joined_block = ""
    if all_silver_fqns or all_bronze_columns:
        lines = []
        # Silver FQNs take priority — these are what a JOIN should reference
        if all_silver_fqns:
            for pbix_name, sfqn in all_silver_fqns.items():
                bronze_fqn = next(
                    (k for k in (all_bronze_columns or {}) if k.upper().endswith(f".{pbix_name.upper()}")),
                    None
                )
                cols = (all_bronze_columns or {}).get(bronze_fqn, []) if bronze_fqn else []
                col_str = f", columns: {cols}" if cols else ""
                lines.append(f"  {pbix_name}  →  Silver FQN: {sfqn}{col_str}")
        if lines:
            joined_block = (
                "\nOther Silver views available for JOINs — ALWAYS use these exact Silver FQNs"
                " in JOIN clauses, NEVER bare table names like 'Emp' or 'Returns':\n"
                + "\n".join(lines)
            )

    calc_block = ""
    if calc_columns:
        col_lines = "\n".join(f"  - {c['name']}: {c['expression']}" for c in calc_columns)
        calc_block = f"""
Calculated columns to ADD to this table (translate each DAX expression to a Snowflake
derived column and include them at the END of the SELECT list — after all base columns):
{col_lines}
These are row-level expressions. Include them directly in the SELECT, NOT as a subquery.
"""

    user_prompt = f"""Power BI table name: {table_name}
Snowflake source table (Bronze layer, fully qualified): {source_table_fqn}
{primary_cols_line}{joined_block}
{calc_block}
Power Query (M) expression to translate:
---
{m_query}
---

Produce the equivalent Snowflake SQL SELECT statement reading from {source_table_fqn}.
CRITICAL:
- Use ONLY the column names listed above. Never invent column names not in those lists.
- For any JOIN, use the Silver FQN shown above (e.g. MIGRATION_DD.SILVER.EMP), never a bare name.
- NEVER use SELECT alias.* when joined tables share column names — list each column explicitly
  and rename duplicates (e.g. E.DEPTNO AS EMP_DEPTNO) to avoid duplicate column name errors.
- If calculated columns were provided above, include their derived expressions at the end of the SELECT list.
"""
    return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# ---------------------------------------------------------------------------
# Post-generation SQL linters
# Called immediately after the LLM returns SQL to catch hallucination bugs
# before writing any .sql file to disk.
# ---------------------------------------------------------------------------

_LINT_TRY_CAST_RE = re.compile(
    r'\bTRY_CAST\s*\(\s*([A-Za-z0-9_."]+)\s+AS\s+(VARCHAR|TEXT)(\s*\(\s*\d+\s*\))?\s*\)',
    re.IGNORECASE,
)


def _lint_fix_try_cast(sql: str) -> tuple[str, list[str]]:
    """Replace TRY_CAST(col AS VARCHAR) → CAST(col AS VARCHAR).
    TRY_CAST only accepts a VARCHAR *source*; using it on BOOLEAN/NUMBER/DATE
    causes a Snowflake runtime error.  A plain CAST is always safe here."""
    notes = []

    def _replace(m):
        col, dtype, size = m.group(1), m.group(2), m.group(3) or ""
        notes.append(
            f"TRY_CAST({col} AS {dtype}{size}) → CAST({col} AS {dtype}{size}) "
            "(TRY_CAST cannot accept non-VARCHAR source columns in Snowflake)"
        )
        return f"CAST({col} AS {dtype}{size})"

    patched = _LINT_TRY_CAST_RE.sub(_replace, sql)
    return patched, notes


_LINT_ALIAS_USE_RE = re.compile(r'\b([A-Z])\.\b([A-Za-z0-9_]+)\b')
_LINT_ALIAS_DEF_RE = re.compile(
    r'\b(?:FROM|JOIN)\s+\S+\s+(?:AS\s+)?([A-Z])\b', re.IGNORECASE
)


def _lint_check_aliases(sql: str) -> tuple[str, list[str]]:
    """Detect single-letter aliases used but never defined in FROM/JOIN.
    These cause Snowflake 'invalid identifier <ALIAS>.<col>' errors.
    We cannot auto-fix (we don't know the intended table), so we prepend a
    visible TODO comment so the issue is obvious in the generated .sql file."""
    defined = set(_LINT_ALIAS_DEF_RE.findall(sql.upper()))
    used = set(m.group(1).upper() for m in _LINT_ALIAS_USE_RE.finditer(sql))
    undefined = used - defined
    notes = []
    if undefined:
        comment = (
            "-- !! LINT WARNING: Undefined single-letter alias(es) detected: "
            + ", ".join(sorted(undefined))
            + "\n"
            "-- These will cause 'invalid identifier' errors in Snowflake.\n"
            "-- Check the FROM/JOIN clauses and correct the alias references below.\n"
        )
        first_select = re.search(r'\bSELECT\b', sql, re.IGNORECASE)
        if first_select:
            sql = sql[: first_select.start()] + comment + sql[first_select.start():]
        else:
            sql = comment + sql
        notes.append(
            f"Undefined alias(es) {sorted(undefined)} referenced in generated SQL — "
            "TODO comment prepended. Manual fix required."
        )
    return sql, notes


def translate_table(
    table: dict,
    bronze_fqn: Optional[str],
    column_mapping: Optional[dict] = None,
    bronze_columns: Optional[list] = None,
    all_bronze_columns: Optional[dict] = None,
    all_silver_fqns: Optional[dict] = None,
    calc_columns: Optional[list] = None,
) -> dict:
    """Returns dict: {table_name, sql, method, warnings}

    Args:
        table:          Parsed PBIX table dict.
        bronze_fqn:     Fully-qualified Bronze table name, or None if unmatched.
        column_mapping: Optional {pbix_col: bronze_col} rename map (passed by
                        newer pipeline_runner versions; used to enrich the LLM
                        prompt so column aliases are grounded in real names).
        bronze_columns: Optional list of actual Bronze column names (passed by
                        newer pipeline_runner versions; used for alias validation).
        calc_columns:   List of {name, expression} dicts for calculated columns
                        belonging to this table — baked directly into the Silver
                        SELECT so they never appear as separate Gold views.
    """
    name = table["name"]
    m_query = table.get("m_query")
    warnings = []
    # Merge any externally supplied column info into the table dict so the
    # LLM prompt receives accurate column names and the alias linter can
    # cross-check against real Bronze columns.
    if bronze_columns:
        table = {**table, "_bronze_columns": bronze_columns}
    if column_mapping:
        table = {**table, "_column_mapping": column_mapping}
    if all_bronze_columns:
        table = {**table, "_all_bronze_columns": all_bronze_columns}
    if all_silver_fqns:
        table = {**table, "_all_silver_fqns": all_silver_fqns}
    if calc_columns:
        table = {**table, "_calc_columns": calc_columns}

    if table.get("is_calculated_table"):
        return {
            "table_name": name, "sql": None, "method": "skipped_calculated_table",
            "warnings": ["Calculated table — handled in DAX->SQL translation (Gold layer), not Silver."],
        }

    if not bronze_fqn:
        return {
            "table_name": name, "sql": None, "method": "skipped_unmatched",
            "warnings": [f"No matched Bronze table for '{name}'; cannot generate Silver SQL."],
        }

    if not m_query:
        sql = f"SELECT *\nFROM {bronze_fqn}"
        return {
            "table_name": name, "sql": sql, "method": "passthrough",
            "warnings": ["No M query found; generated pass-through SELECT *."],
        }

    rule_sql = _try_rule_based(m_query, bronze_fqn)
    if rule_sql:
        log.info(f"[RULE]   {name}: translated via rule engine")
        return {"table_name": name, "sql": rule_sql, "method": "rule_engine", "warnings": []}

    log.info(f"[LLM]    {name}: M query too complex for rule engine, calling Azure OpenAI")
    try:
        cols = bronze_columns or table.get("columns", [])
        # Pass all known Bronze column lists so the LLM can't hallucinate
        # column names on tables it JOINs to.
        all_bronze_columns = table.get("_all_bronze_columns") or None
        all_silver_fqns = table.get("_all_silver_fqns") or None
        calc_cols = table.get("_calc_columns") or None
        sql = _llm_translate(m_query, name, bronze_fqn, cols,
                             all_bronze_columns=all_bronze_columns,
                             all_silver_fqns=all_silver_fqns,
                             calc_columns=calc_cols)
        if "-- TODO: manual review" in sql:
            warnings.append("LLM flagged at least one clause for manual review — search SQL for 'TODO'.")

        # --- Post-generation lint: fix known LLM hallucination patterns ---
        sql, cast_patches = _lint_fix_try_cast(sql)
        for note in cast_patches:
            warnings.append(f"AUTO-FIXED: {note}")
            log.warning(f"[LINT] {name}: {note}")

        sql, alias_warnings = _lint_check_aliases(sql)
        for note in alias_warnings:
            warnings.append(f"ALIAS WARNING: {note}")
            log.warning(f"[LINT] {name}: {note}")

        return {"table_name": name, "sql": sql, "method": "llm", "warnings": warnings}
    except Exception as e:
        log.error(f"LLM translation failed for {name}: {e}")
        fallback_sql = f"SELECT *\nFROM {bronze_fqn}\n-- TODO: manual review: LLM translation failed ({e})"
        return {"table_name": name, "sql": fallback_sql, "method": "llm_failed_fallback",
                "warnings": [f"LLM translation failed: {e}. Generated pass-through SELECT * as fallback."]}


def build_silver_ddl(table_name: str, select_sql: str, silver_schema: str,
                     database: str = None) -> str:
    """Build CREATE OR REPLACE VIEW DDL for a Silver layer object.
    The optional `database` arg is accepted for compatibility with pipeline_runner
    versions that pass it as a 4th positional argument; it is not used in the DDL
    because silver_schema should already be fully qualified if needed.
    """
    safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table_name).upper()
    return (
        f"-- Silver layer object for PBIX table: {table_name}\n"
        f"CREATE OR REPLACE VIEW {silver_schema}.{safe_name} AS\n"
        f"{select_sql.strip()}\n;\n"
    )


def main():
    ap = argparse.ArgumentParser(description="Translate Power Query (M) to Snowflake SQL and generate Silver layer DDL")
    ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
    ap.add_argument("--mapping", default=str(config.mapping_dir / "table_mapping.json"))
    ap.add_argument("--output-dir", default=str(config.sql_silver_dir))
    args = ap.parse_args()

    with open(args.model, "r", encoding="utf-8") as f:
        parsed_model = json.load(f)
    with open(args.mapping, "r", encoding="utf-8") as f:
        table_mapping = json.load(f)

    mapping_by_pbix = {m["pbix_table"]: m for m in table_mapping}
    sf = config.snowflake
    out_dir = Path(args.output_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    results = []
    for table in parsed_model["tables"]:
        name = table["name"]
        match = mapping_by_pbix.get(name)
        bronze_fqn = None
        if match and match.get("bronze_table"):
            bronze_fqn = f'{sf.database}.{match["bronze_schema"]}.{match["bronze_table"]}'

        result = translate_table(table, bronze_fqn)
        results.append(result)

        if result["sql"]:
            ddl = build_silver_ddl(name, result["sql"], sf.silver_schema)
            safe_name = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
            out_file = out_dir / f"silver_{safe_name}.sql"
            with open(out_file, "w", encoding="utf-8") as f:
                f.write(ddl)
            result["output_file"] = str(out_file)
            log.info(f"Wrote {out_file}")

    summary_path = out_dir / "_translation_summary.json"
    with open(summary_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)

    print("\nM Query -> SQL Translation Summary:")
    for r in results:
        status = "OK" if r["sql"] else "SKIPPED"
        print(f"  [{status:7s}] {r['table_name']:30s} method={r['method']}")
        for w in r["warnings"]:
            print(f"             ! {w}")


if __name__ == "__main__":
    main()