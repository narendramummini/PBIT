# # """
# # dax_translator.py
# # Translates DAX measures, calculated columns, and calculated tables into
# # Snowflake SQL (Gold-layer KPI views / aggregations), using Azure OpenAI.
# # DAX's evaluation-context semantics don't map 1:1 to SQL, so this is
# # LLM-driven by design rather than rule-based, with explicit caveats surfaced
# # in the generated SQL comments for anything involving complex filter context
# # (CALCULATE, ALL, FILTER, time intelligence, etc).

# # Run:
# #     python src/dax_translator.py \
# #         --model output/mapping/parsed_model.json \
# #         --output-dir output/sql/gold
# # """

# # import argparse
# # import json
# # import re
# # import sys
# # from pathlib import Path

# # sys.path.insert(0, str(Path(__file__).resolve().parent))
# # from config import config
# # from logger import get_logger
# # import azure_openai_client as aoai

# # log = get_logger("dax_translator", config.logs_dir, config.pipeline.log_level)


# # LLM_SYSTEM_PROMPT = """You are a senior analytics engineer translating Power BI DAX (measures, calculated
# # columns, and calculated tables) into equivalent Snowflake SQL for a Gold-layer KPI/semantic view.

# # Rules:
# # - Output ONLY SQL. No explanation outside SQL comments, no markdown fences.
# # - Assume the Silver-layer tables are already available, fully qualified as <DATABASE>.<SILVER_SCHEMA>.<TABLE_NAME>
# #   (table names will be given to you).
# # - Translate the DAX expression's *business logic* faithfully into a SQL SELECT (for measures: typically
# #   an aggregate expression suitable for a GROUP BY view; for calculated columns: a derived column expression;
# #   for calculated tables: a full SELECT producing that table's rows).
# # - DAX functions to be aware of: CALCULATE (filter context override), FILTER, ALL/ALLEXCEPT (remove filters),
# #   SUMX/AVERAGEX/etc (row-context iterators), RELATED (cross-table lookup via relationship), time intelligence
# #   (TOTALYTD, SAMEPERIODLASTYEAR, DATEADD, etc).
# # - Map iterators (SUMX, AVERAGEX, COUNTX) to SQL aggregates over the appropriate join/subquery.
# # - Map CALCULATE+FILTER combinations to WHERE/QUALIFY clauses or window functions as appropriate.
# # - Map time intelligence functions to SQL window functions or date-range filters using DATE_TRUNC,
# #   DATEADD, and QUALIFY ROW_NUMBER()/LAG/LEAD where appropriate.
# # - If a DAX construct has no clean SQL equivalent or the translation is uncertain, include a SQL comment
# #   "-- TODO: manual review: <reason>" directly above your best-effort attempt. Never silently drop logic.
# # - Always alias output columns meaningfully (snake_case or matching the original measure name, your choice,
# #   but be consistent).

# # CRITICAL — TYPE CASTING RULES (Snowflake-specific):
# #   * NEVER use TRY_CAST() on BOOLEAN, NUMBER, DATE, or TIMESTAMP source columns.
# #     TRY_CAST() only accepts a VARCHAR first argument. Use CAST(col AS type) or col::type instead.
# #   * For BOOLEAN → VARCHAR: use CAST(col AS VARCHAR) or IFF(col, 'true', 'false').
# #   * For NUMBER → VARCHAR: use col::VARCHAR or CAST(col AS VARCHAR).
# #   * For DATE/TIMESTAMP → VARCHAR: use TO_VARCHAR(col) or TO_VARCHAR(col, 'YYYY-MM-DD').

# # CRITICAL — TABLE ALIAS RULES:
# #   * Every alias used in SELECT/WHERE/ON must be explicitly defined in your FROM or JOIN clauses.
# #   * Before finalising, trace every <ALIAS>.<column> reference back to its FROM/JOIN definition.
# #   * NEVER invent aliases that are not explicitly defined in your generated SQL.
# # """


# # def _build_silver_table_map(table_names: list, database: str, silver_schema: str) -> dict:
# #     """Build a mapping of PBIX table name -> fully-qualified Silver object name.

# #     Called by pipeline_runner.run_translate_gold to give the LLM prompts a
# #     concrete lookup of every available Silver table so generated SQL can
# #     reference correct fully-qualified names rather than hallucinating them.

# #     Returns:
# #         {
# #             "Dept":   "MIGRATION_DD.SILVER.DEPT",
# #             "Orders": "MIGRATION_DD.SILVER.ORDERS",
# #             ...
# #         }
# #     """
# #     result = {}
# #     for name in table_names:
# #         safe = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
# #         fqn = f"{database}.{silver_schema}.{safe}" if database else f"{silver_schema}.{safe}"
# #         result[name] = fqn
# #     return result


# # def _llm_translate_measure(measure: dict, related_tables: list, silver_schema: str, database: str,
# #                             silver_table_map: dict = None) -> str:
# #     # Build fully-qualified table references so the LLM cannot hallucinate table names
# #     if silver_table_map:
# #         fq_tables = {name: fqn for name, fqn in silver_table_map.items()}
# #     else:
# #         fq_tables = {
# #             t: f"{database}.{silver_schema}.{re.sub(r'[^A-Za-z0-9_]', '_', t).upper()}"
# #             for t in related_tables
# #         }
# #     table_ref_block = "\n".join(f"  {name}  →  {fqn}" for name, fqn in fq_tables.items())

# #     user_prompt = f"""Measure name: {measure['name']}
# # Home table: {measure['table']}
# # DAX expression:
# # ---
# # {measure['expression']}
# # ---

# # EXACT fully-qualified Silver table names you MUST use (do not invent other names):
# # {table_ref_block}

# # IMPORTANT: The table that holds sales/order data is listed above. Use ONLY the names
# # shown — do not use 'SALES', 'FACT_SALES', or any other name not in the list above.

# # Produce a Snowflake SQL SELECT that computes this measure's logic as a KPI view.
# # Include a sensible GROUP BY if the measure implies aggregation — otherwise a scalar SELECT.
# # """
# #     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# # def _llm_translate_calc_column(col: dict, silver_schema: str, database: str,
# #                                 silver_table_map: dict = None) -> str:
# #     safe = re.sub(r'[^A-Za-z0-9_]', '_', col['table']).upper()
# #     if silver_table_map and col['table'] in silver_table_map:
# #         target_fqn = silver_table_map[col['table']]
# #     else:
# #         target_fqn = f"{database}.{silver_schema}.{safe}" if database else f"{silver_schema}.{safe}"

# #     user_prompt = f"""Calculated column name: {col['name']}
# # Home table: {col['table']}
# # DAX expression:
# # ---
# # {col['expression']}
# # ---

# # Target Silver table (fully qualified, use EXACTLY this name): {target_fqn}

# # Produce a Snowflake SQL SELECT that adds this calculated column as a derived expression:
# # SELECT *, <expression> AS "{col['name']}" FROM {target_fqn}
# # Use only columns that logically exist in a table named {col['table']}.
# # """
# #     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# # def _llm_translate_calc_table(table: dict, all_table_names: list, silver_schema: str, database: str,
# #                                silver_table_map: dict = None) -> str:
# #     if silver_table_map:
# #         fq_tables = silver_table_map
# #     else:
# #         fq_tables = {
# #             t: f"{database}.{silver_schema}.{re.sub(r'[^A-Za-z0-9_]', '_', t).upper()}"
# #             for t in all_table_names
# #         }
# #     table_ref_block = "\n".join(f"  {name}  →  {fqn}" for name, fqn in fq_tables.items())

# #     user_prompt = f"""Calculated table name: {table['name']}
# # DAX table expression:
# # ---
# # {table.get('calculated_table_dax', '')}
# # ---

# # EXACT fully-qualified Silver table names you MUST use (do not invent other names):
# # {table_ref_block}

# # Produce a Snowflake SQL SELECT that reproduces the rows this calculated table would contain.
# # """
# #     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# # def build_gold_ddl(object_name: str, kind: str, select_sql: str, gold_schema: str,
# #                    database: str = None) -> str:
# #     """Optional `database` arg accepted for pipeline_runner compatibility; not used in DDL."""
# #     safe_name = re.sub(r"[^A-Za-z0-9_]", "_", object_name).upper()
# #     prefix = {"measure": "KPI", "calculated_column": "CALC", "calculated_table": "TBL"}.get(kind, "OBJ")
# #     return (
# #         f"-- Gold layer object for PBIX {kind}: {object_name}\n"
# #         f"CREATE OR REPLACE VIEW {gold_schema}.{prefix}_{safe_name} AS\n"
# #         f"{select_sql.strip()}\n;\n"
# #     )


# # def main():
# #     ap = argparse.ArgumentParser(description="Translate DAX measures/calculated columns/tables to Snowflake Gold-layer SQL")
# #     ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
# #     ap.add_argument("--output-dir", default=str(config.sql_gold_dir))
# #     args = ap.parse_args()

# #     with open(args.model, "r", encoding="utf-8") as f:
# #         parsed_model = json.load(f)

# #     sf = config.snowflake
# #     out_dir = Path(args.output_dir)
# #     out_dir.mkdir(parents=True, exist_ok=True)

# #     all_table_names = [t["name"] for t in parsed_model["tables"]]
# #     results = []

# #     # --- Measures -> KPI views ---
# #     for measure in parsed_model.get("measures", []):
# #         log.info(f"[LLM] Translating measure: {measure['name']}")
# #         try:
# #             sql = _llm_translate_measure(measure, all_table_names, sf.silver_schema, sf.database)
# #             method = "llm"
# #         except Exception as e:
# #             log.error(f"Measure translation failed for {measure['name']}: {e}")
# #             sql = f"-- TODO: manual review: LLM translation failed ({e})\nSELECT NULL AS \"{measure['name']}\""
# #             method = "llm_failed_fallback"

# #         ddl = build_gold_ddl(measure["name"], "measure", sql, sf.gold_schema)
# #         safe_name = re.sub(r"[^A-Za-z0-9_]", "_", measure["name"]).upper()
# #         out_file = out_dir / f"gold_kpi_{safe_name}.sql"
# #         with open(out_file, "w", encoding="utf-8") as f:
# #             f.write(ddl)
# #         results.append({"name": measure["name"], "kind": "measure", "method": method, "output_file": str(out_file)})
# #         log.info(f"Wrote {out_file}")

# #     # --- Calculated columns -> derived column views ---
# #     for col in parsed_model.get("calculated_columns", []):
# #         log.info(f"[LLM] Translating calculated column: {col['table']}.{col['name']}")
# #         try:
# #             sql = _llm_translate_calc_column(col, sf.silver_schema, sf.database)
# #             method = "llm"
# #         except Exception as e:
# #             log.error(f"Calculated column translation failed for {col['name']}: {e}")
# #             sql = f"-- TODO: manual review: LLM translation failed ({e})\nSELECT * FROM {sf.database}.{sf.silver_schema}.{col['table']}"
# #             method = "llm_failed_fallback"

# #         ddl = build_gold_ddl(f"{col['table']}_{col['name']}", "calculated_column", sql, sf.gold_schema)
# #         safe_name = re.sub(r"[^A-Za-z0-9_]", "_", f"{col['table']}_{col['name']}").upper()
# #         out_file = out_dir / f"gold_calc_{safe_name}.sql"
# #         with open(out_file, "w", encoding="utf-8") as f:
# #             f.write(ddl)
# #         results.append({"name": f"{col['table']}.{col['name']}", "kind": "calculated_column", "method": method, "output_file": str(out_file)})
# #         log.info(f"Wrote {out_file}")

# #     # --- Calculated tables -> Gold tables ---
# #     for table in parsed_model["tables"]:
# #         if not table.get("is_calculated_table"):
# #             continue
# #         log.info(f"[LLM] Translating calculated table: {table['name']}")
# #         try:
# #             sql = _llm_translate_calc_table(table, all_table_names, sf.silver_schema, sf.database)
# #             method = "llm"
# #         except Exception as e:
# #             log.error(f"Calculated table translation failed for {table['name']}: {e}")
# #             sql = f"-- TODO: manual review: LLM translation failed ({e})\nSELECT 1 AS placeholder"
# #             method = "llm_failed_fallback"

# #         ddl = build_gold_ddl(table["name"], "calculated_table", sql, sf.gold_schema)
# #         safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table["name"]).upper()
# #         out_file = out_dir / f"gold_table_{safe_name}.sql"
# #         with open(out_file, "w", encoding="utf-8") as f:
# #             f.write(ddl)
# #         results.append({"name": table["name"], "kind": "calculated_table", "method": method, "output_file": str(out_file)})
# #         log.info(f"Wrote {out_file}")

# #     summary_path = out_dir / "_translation_summary.json"
# #     with open(summary_path, "w", encoding="utf-8") as f:
# #         json.dump(results, f, indent=2)

# #     print("\nDAX -> SQL Translation Summary:")
# #     for r in results:
# #         print(f"  [{r['kind']:18s}] {r['name']:35s} method={r['method']}")


# # if __name__ == "__main__":
# #     main()

# """
# dax_translator.py
# Translates DAX measures, calculated columns, and calculated tables into
# Snowflake SQL (Gold-layer KPI views / aggregations), using Azure OpenAI.
# DAX's evaluation-context semantics don't map 1:1 to SQL, so this is
# LLM-driven by design rather than rule-based, with explicit caveats surfaced
# in the generated SQL comments for anything involving complex filter context
# (CALCULATE, ALL, FILTER, time intelligence, etc).

# Run:
#     python src/dax_translator.py \
#         --model output/mapping/parsed_model.json \
#         --output-dir output/sql/gold
# """

# import argparse
# import json
# import re
# import sys
# from pathlib import Path

# sys.path.insert(0, str(Path(__file__).resolve().parent))
# from config import config
# from logger import get_logger
# import azure_openai_client as aoai

# log = get_logger("dax_translator", config.logs_dir, config.pipeline.log_level)


# LLM_SYSTEM_PROMPT = """You are a senior analytics engineer translating Power BI DAX (measures, calculated
# columns, and calculated tables) into equivalent Snowflake SQL for a Gold-layer KPI/semantic view.

# Rules:
# - Output ONLY SQL. No explanation outside SQL comments, no markdown fences.
# - Assume the Silver-layer tables are already available, fully qualified as <DATABASE>.<SILVER_SCHEMA>.<TABLE_NAME>
#   (table names will be given to you).
# - Translate the DAX expression's *business logic* faithfully into a SQL SELECT (for measures: typically
#   an aggregate expression suitable for a GROUP BY view; for calculated columns: a derived column expression;
#   for calculated tables: a full SELECT producing that table's rows).
# - DAX functions to be aware of: CALCULATE (filter context override), FILTER, ALL/ALLEXCEPT (remove filters),
#   SUMX/AVERAGEX/etc (row-context iterators), RELATED (cross-table lookup via relationship), time intelligence
#   (TOTALYTD, SAMEPERIODLASTYEAR, DATEADD, etc).
# - Map iterators (SUMX, AVERAGEX, COUNTX) to SQL aggregates over the appropriate join/subquery.
# - Map CALCULATE+FILTER combinations to WHERE/QUALIFY clauses or window functions as appropriate.
# - Map time intelligence functions to SQL window functions or date-range filters using DATE_TRUNC,
#   DATEADD, and QUALIFY ROW_NUMBER()/LAG/LEAD where appropriate.
# - If a DAX construct has no clean SQL equivalent or the translation is uncertain, include a SQL comment
#   "-- TODO: manual review: <reason>" directly above your best-effort attempt. Never silently drop logic.
# - Always alias output columns meaningfully (snake_case or matching the original measure name, your choice,
#   but be consistent).

# CRITICAL — TYPE CASTING RULES (Snowflake-specific):
#   * NEVER use TRY_CAST() on BOOLEAN, NUMBER, DATE, or TIMESTAMP source columns.
#     TRY_CAST() only accepts a VARCHAR first argument. Use CAST(col AS type) or col::type instead.
#   * For BOOLEAN → VARCHAR: use CAST(col AS VARCHAR) or IFF(col, 'true', 'false').
#   * For NUMBER → VARCHAR: use col::VARCHAR or CAST(col AS VARCHAR).
#   * For DATE/TIMESTAMP → VARCHAR: use TO_VARCHAR(col) or TO_VARCHAR(col, 'YYYY-MM-DD').

# CRITICAL — TABLE ALIAS RULES:
#   * Every alias used in SELECT/WHERE/ON must be explicitly defined in your FROM or JOIN clauses.
#   * Before finalising, trace every <ALIAS>.<column> reference back to its FROM/JOIN definition.
#   * NEVER invent aliases that are not explicitly defined in your generated SQL.
# """


# def _build_silver_table_map(table_names: list, database: str, silver_schema: str) -> dict:
#     """Build a mapping of PBIX table name -> fully-qualified Silver object name.

#     Called by pipeline_runner.run_translate_gold to give the LLM prompts a
#     concrete lookup of every available Silver table so generated SQL can
#     reference correct fully-qualified names rather than hallucinating them.

#     Returns:
#         {
#             "Dept":   "MIGRATION_DD.SILVER.DEPT",
#             "Orders": "MIGRATION_DD.SILVER.ORDERS",
#             ...
#         }
#     """
#     result = {}
#     for name in table_names:
#         safe = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
#         fqn = f"{database}.{silver_schema}.{safe}" if database else f"{silver_schema}.{safe}"
#         result[name] = fqn
#     return result


# def _llm_translate_measure(measure: dict, related_tables: list, silver_schema: str, database: str,
#                             silver_table_map: dict = None) -> str:
#     # Build fully-qualified table references so the LLM cannot hallucinate table names
#     if silver_table_map:
#         fq_tables = {name: fqn for name, fqn in silver_table_map.items()}
#     else:
#         fq_tables = {
#             t: f"{database}.{silver_schema}.{re.sub(r'[^A-Za-z0-9_]', '_', t).upper()}"
#             for t in related_tables
#         }
#     table_ref_block = "\n".join(f"  {name}  →  {fqn}" for name, fqn in fq_tables.items())

#     user_prompt = f"""Measure name: {measure['name']}
# Home table: {measure['table']}
# DAX expression:
# ---
# {measure['expression']}
# ---

# EXACT fully-qualified Silver table names you MUST use (do not invent other names):
# {table_ref_block}

# IMPORTANT: The table that holds sales/order data is listed above. Use ONLY the names
# shown — do not use 'SALES', 'FACT_SALES', or any other name not in the list above.

# Produce a Snowflake SQL SELECT that computes this measure's logic as a KPI view.
# Include a sensible GROUP BY if the measure implies aggregation — otherwise a scalar SELECT.
# """
#     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# def _llm_translate_calc_column(col: dict, silver_schema: str, database: str,
#                                 silver_table_map: dict = None) -> str:
#     """Translate a SINGLE calculated column — used internally by the grouped translator."""
#     safe = re.sub(r'[^A-Za-z0-9_]', '_', col['table']).upper()
#     if silver_table_map and col['table'] in silver_table_map:
#         target_fqn = silver_table_map[col['table']]
#     else:
#         target_fqn = f"{database}.{silver_schema}.{safe}" if database else f"{silver_schema}.{safe}"

#     user_prompt = f"""Calculated column name: {col['name']}
# Home table: {col['table']}
# DAX expression:
# ---
# {col['expression']}
# ---

# Target Silver table (fully qualified, use EXACTLY this name): {target_fqn}

# Produce ONLY the derived column expression (not a full SELECT).
# Format: <snowflake_expression> AS "{col['name']}"
# Example: EXTRACT(MONTH FROM "Order Date") AS "Order_Month"
# No SELECT, no FROM, no semicolons — just the column expression.
# """
#     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# def _llm_translate_calc_columns_grouped(table_name: str, cols: list, silver_schema: str,
#                                          database: str, silver_table_map: dict = None) -> str:
#     """
#     Translate ALL calculated columns for one table in a single LLM call,
#     returning a full SELECT that includes every derived expression.
#     This avoids generating N redundant copies of the same base table — one
#     view per table, not one view per column.
#     """
#     safe = re.sub(r'[^A-Za-z0-9_]', '_', table_name).upper()
#     if silver_table_map and table_name in silver_table_map:
#         target_fqn = silver_table_map[table_name]
#     else:
#         target_fqn = f"{database}.{silver_schema}.{safe}" if database else f"{silver_schema}.{safe}"

#     col_block = "\n".join(
#         f"  - {c['name']}: {c['expression']}"
#         for c in cols
#     )

#     user_prompt = f"""Table: {table_name}
# Target Silver table (fully qualified, use EXACTLY this name): {target_fqn}

# Calculated columns to add (name: DAX expression):
# {col_block}

# Produce a single Snowflake SQL SELECT that reads all rows from {target_fqn}
# and appends every calculated column as a derived expression:

# SELECT
#   *,
#   <expr1> AS "<name1>",
#   <expr2> AS "<name2>",
#   ...
# FROM {target_fqn}

# Rules:
# - Output ONLY the SELECT statement. No CREATE VIEW, no semicolons, no markdown.
# - Translate each DAX expression faithfully to Snowflake SQL.
# - Use only columns that logically exist in a table named {table_name}.
# - If a DAX expression is uncertain, add a comment -- TODO: manual review: <reason>
#   directly above the expression but still include a best-effort translation.
# """
#     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# def _llm_translate_calc_table(table: dict, all_table_names: list, silver_schema: str, database: str,
#                                silver_table_map: dict = None) -> str:
#     if silver_table_map:
#         fq_tables = silver_table_map
#     else:
#         fq_tables = {
#             t: f"{database}.{silver_schema}.{re.sub(r'[^A-Za-z0-9_]', '_', t).upper()}"
#             for t in all_table_names
#         }
#     table_ref_block = "\n".join(f"  {name}  →  {fqn}" for name, fqn in fq_tables.items())

#     user_prompt = f"""Calculated table name: {table['name']}
# DAX table expression:
# ---
# {table.get('calculated_table_dax', '')}
# ---

# EXACT fully-qualified Silver table names you MUST use (do not invent other names):
# {table_ref_block}

# Produce a Snowflake SQL SELECT that reproduces the rows this calculated table would contain.
# """
#     return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


# def build_gold_ddl(object_name: str, kind: str, select_sql: str, gold_schema: str,
#                    database: str = None) -> str:
#     """Optional `database` arg accepted for pipeline_runner compatibility; not used in DDL."""
#     safe_name = re.sub(r"[^A-Za-z0-9_]", "_", object_name).upper()
#     prefix = {"measure": "KPI", "calculated_column": "CALC", "calculated_table": "TBL"}.get(kind, "OBJ")
#     return (
#         f"-- Gold layer object for PBIX {kind}: {object_name}\n"
#         f"CREATE OR REPLACE VIEW {gold_schema}.{prefix}_{safe_name} AS\n"
#         f"{select_sql.strip()}\n;\n"
#     )


# def main():
#     ap = argparse.ArgumentParser(description="Translate DAX measures/calculated columns/tables to Snowflake Gold-layer SQL")
#     ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
#     ap.add_argument("--output-dir", default=str(config.sql_gold_dir))
#     args = ap.parse_args()

#     with open(args.model, "r", encoding="utf-8") as f:
#         parsed_model = json.load(f)

#     sf = config.snowflake
#     out_dir = Path(args.output_dir)
#     out_dir.mkdir(parents=True, exist_ok=True)

#     all_table_names = [t["name"] for t in parsed_model["tables"]]
#     results = []

#     # --- Measures -> KPI views ---
#     for measure in parsed_model.get("measures", []):
#         log.info(f"[LLM] Translating measure: {measure['name']}")
#         try:
#             sql = _llm_translate_measure(measure, all_table_names, sf.silver_schema, sf.database)
#             method = "llm"
#         except Exception as e:
#             log.error(f"Measure translation failed for {measure['name']}: {e}")
#             sql = f"-- TODO: manual review: LLM translation failed ({e})\nSELECT NULL AS \"{measure['name']}\""
#             method = "llm_failed_fallback"

#         ddl = build_gold_ddl(measure["name"], "measure", sql, sf.gold_schema)
#         safe_name = re.sub(r"[^A-Za-z0-9_]", "_", measure["name"]).upper()
#         out_file = out_dir / f"gold_kpi_{safe_name}.sql"
#         with open(out_file, "w", encoding="utf-8") as f:
#             f.write(ddl)
#         results.append({"name": measure["name"], "kind": "measure", "method": method, "output_file": str(out_file)})
#         log.info(f"Wrote {out_file}")

#     # --- Calculated columns -> ONE view per table (all columns grouped) ---
#     from collections import defaultdict
#     calc_cols_by_table: dict = defaultdict(list)
#     for col in parsed_model.get("calculated_columns", []):
#         calc_cols_by_table[col["table"]].append(col)

#     for table_name, cols in calc_cols_by_table.items():
#         col_names = ", ".join(c["name"] for c in cols)
#         log.info(f"[LLM] Translating {len(cols)} calculated column(s) for table '{table_name}': {col_names}")
#         try:
#             sql = _llm_translate_calc_columns_grouped(table_name, cols, sf.silver_schema, sf.database)
#             method = "llm"
#         except Exception as e:
#             log.error(f"Calculated column group translation failed for table {table_name}: {e}")
#             safe_fb = re.sub(r'[^A-Za-z0-9_]', '_', table_name).upper()
#             sql = (
#                 f"-- TODO: manual review: LLM translation failed ({e})\n"
#                 f"SELECT * FROM {sf.database}.{sf.silver_schema}.{safe_fb}"
#             )
#             method = "llm_failed_fallback"

#         safe_table = re.sub(r"[^A-Za-z0-9_]", "_", table_name).upper()
#         view_name = f"{table_name}_CALC_COLUMNS"
#         ddl = build_gold_ddl(view_name, "calculated_column", sql, sf.gold_schema)
#         out_file = out_dir / f"gold_calc_{safe_table}.sql"
#         with open(out_file, "w", encoding="utf-8") as f:
#             f.write(ddl)
#         results.append({
#             "name": f"{table_name} ({len(cols)} calc columns: {col_names})",
#             "kind": "calculated_column",
#             "method": method,
#             "output_file": str(out_file),
#             "columns": [c["name"] for c in cols],
#         })
#         log.info(f"Wrote {out_file}")

#     # --- Calculated tables -> Gold tables ---
#     for table in parsed_model["tables"]:
#         if not table.get("is_calculated_table"):
#             continue
#         log.info(f"[LLM] Translating calculated table: {table['name']}")
#         try:
#             sql = _llm_translate_calc_table(table, all_table_names, sf.silver_schema, sf.database)
#             method = "llm"
#         except Exception as e:
#             log.error(f"Calculated table translation failed for {table['name']}: {e}")
#             sql = f"-- TODO: manual review: LLM translation failed ({e})\nSELECT 1 AS placeholder"
#             method = "llm_failed_fallback"

#         ddl = build_gold_ddl(table["name"], "calculated_table", sql, sf.gold_schema)
#         safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table["name"]).upper()
#         out_file = out_dir / f"gold_table_{safe_name}.sql"
#         with open(out_file, "w", encoding="utf-8") as f:
#             f.write(ddl)
#         results.append({"name": table["name"], "kind": "calculated_table", "method": method, "output_file": str(out_file)})
#         log.info(f"Wrote {out_file}")

#     summary_path = out_dir / "_translation_summary.json"
#     with open(summary_path, "w", encoding="utf-8") as f:
#         json.dump(results, f, indent=2)

#     print("\nDAX -> SQL Translation Summary:")
#     for r in results:
#         print(f"  [{r['kind']:18s}] {r['name']:35s} method={r['method']}")


# if __name__ == "__main__":
#     main()

"""
dax_translator.py
Translates DAX measures, calculated columns, and calculated tables into
Snowflake SQL (Gold-layer KPI views / aggregations), using Azure OpenAI.
DAX's evaluation-context semantics don't map 1:1 to SQL, so this is
LLM-driven by design rather than rule-based, with explicit caveats surfaced
in the generated SQL comments for anything involving complex filter context
(CALCULATE, ALL, FILTER, time intelligence, etc).

Run:
    python src/dax_translator.py \
        --model output/mapping/parsed_model.json \
        --output-dir output/sql/gold
"""

import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from config import config
from logger import get_logger
import azure_openai_client as aoai

log = get_logger("dax_translator", config.logs_dir, config.pipeline.log_level)


LLM_SYSTEM_PROMPT = """You are a senior analytics engineer translating Power BI DAX (measures, calculated
columns, and calculated tables) into equivalent Snowflake SQL for a Gold-layer KPI/semantic view.

Rules:
- Output ONLY SQL. No explanation outside SQL comments, no markdown fences.
- Assume the Silver-layer tables are already available, fully qualified as <DATABASE>.<SILVER_SCHEMA>.<TABLE_NAME>
  (table names will be given to you).
- Translate the DAX expression's *business logic* faithfully into a SQL SELECT (for measures: typically
  an aggregate expression suitable for a GROUP BY view; for calculated columns: a derived column expression;
  for calculated tables: a full SELECT producing that table's rows).
- DAX functions to be aware of: CALCULATE (filter context override), FILTER, ALL/ALLEXCEPT (remove filters),
  SUMX/AVERAGEX/etc (row-context iterators), RELATED (cross-table lookup via relationship), time intelligence
  (TOTALYTD, SAMEPERIODLASTYEAR, DATEADD, etc).
- Map iterators (SUMX, AVERAGEX, COUNTX) to SQL aggregates over the appropriate join/subquery.
- Map CALCULATE+FILTER combinations to WHERE/QUALIFY clauses or window functions as appropriate.
- Map time intelligence functions to SQL window functions or date-range filters using DATE_TRUNC,
  DATEADD, and QUALIFY ROW_NUMBER()/LAG/LEAD where appropriate.
- If a DAX construct has no clean SQL equivalent or the translation is uncertain, include a SQL comment
  "-- TODO: manual review: <reason>" directly above your best-effort attempt. Never silently drop logic.
- Always alias output columns meaningfully (snake_case or matching the original measure name, your choice,
  but be consistent).

CRITICAL — TYPE CASTING RULES (Snowflake-specific):
  * NEVER use TRY_CAST() on BOOLEAN, NUMBER, DATE, or TIMESTAMP source columns.
    TRY_CAST() only accepts a VARCHAR first argument. Use CAST(col AS type) or col::type instead.
  * For BOOLEAN → VARCHAR: use CAST(col AS VARCHAR) or IFF(col, 'true', 'false').
  * For NUMBER → VARCHAR: use col::VARCHAR or CAST(col AS VARCHAR).
  * For DATE/TIMESTAMP → VARCHAR: use TO_VARCHAR(col) or TO_VARCHAR(col, 'YYYY-MM-DD').

CRITICAL — TABLE ALIAS RULES:
  * Every alias used in SELECT/WHERE/ON must be explicitly defined in your FROM or JOIN clauses.
  * Before finalising, trace every <ALIAS>.<column> reference back to its FROM/JOIN definition.
  * NEVER invent aliases that are not explicitly defined in your generated SQL.
"""


def _build_silver_table_map(table_names: list, database: str, silver_schema: str) -> dict:
    """Build a mapping of PBIX table name -> fully-qualified Silver object name.

    Called by pipeline_runner.run_translate_gold to give the LLM prompts a
    concrete lookup of every available Silver table so generated SQL can
    reference correct fully-qualified names rather than hallucinating them.

    Returns:
        {
            "Dept":   "MIGRATION_DD.SILVER.DEPT",
            "Orders": "MIGRATION_DD.SILVER.ORDERS",
            ...
        }
    """
    result = {}
    for name in table_names:
        safe = re.sub(r"[^A-Za-z0-9_]", "_", name).upper()
        fqn = f"{database}.{silver_schema}.{safe}" if database else f"{silver_schema}.{safe}"
        result[name] = fqn
    return result


def _llm_translate_measure(measure: dict, related_tables: list, silver_schema: str, database: str,
                            silver_table_map: dict = None, silver_columns_map: dict = None) -> str:
    """
    silver_columns_map: optional dict of {table_name: [col_name, ...]} with exact
    Silver column aliases so the LLM doesn't guess unquoted names or use table prefixes.
    """
    if silver_table_map:
        fq_tables = {name: fqn for name, fqn in silver_table_map.items()}
    else:
        fq_tables = {
            t: f"{database}.{silver_schema}.{re.sub(r'[^A-Za-z0-9_]', '_', t).upper()}"
            for t in related_tables
        }
    table_ref_block = "\n".join(f"  {name}  →  {fqn}" for name, fqn in fq_tables.items())

    silver_col_block = ""
    if silver_columns_map:
        lines = []
        for tname, cols in silver_columns_map.items():
            fqn = fq_tables.get(tname, tname)
            lines.append(f"  {fqn}: {cols}")
        if lines:
            silver_col_block = (
                "\nExact column names per Silver table (use ONLY these — do not guess or uppercase them):\n"
                + "\n".join(lines)
            )

    user_prompt = f"""Measure name: {measure['name']}
Home table: {measure['table']}
DAX expression:
---
{measure['expression']}
---

EXACT fully-qualified Silver table names you MUST use (do not invent other names):
{table_ref_block}
{silver_col_block}

CRITICAL RULES — violations cause Snowflake deployment failures:
- Use ONLY the table FQNs listed above.
- Reference columns using EXACTLY the names listed above (including quotes and case).
  Example: use "Sales" not SALES, use "Sub-Category" not SUB_CATEGORY.
- NEVER prefix columns with a table alias inside a view (e.g. ORDERS."Sales" is wrong
  unless you explicitly defined alias ORDERS in a FROM/JOIN clause).
- If you use an alias, it MUST be defined in your FROM or JOIN clause.

Produce a Snowflake SQL SELECT that computes this measure's logic as a KPI view.
Include a sensible GROUP BY if the measure implies aggregation — otherwise a scalar SELECT.
"""
    return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


def _llm_translate_calc_column(col: dict, silver_schema: str, database: str,
                                silver_table_map: dict = None) -> str:
    """Translate a SINGLE calculated column — used internally by the grouped translator."""
    safe = re.sub(r'[^A-Za-z0-9_]', '_', col['table']).upper()
    if silver_table_map and col['table'] in silver_table_map:
        target_fqn = silver_table_map[col['table']]
    else:
        target_fqn = f"{database}.{silver_schema}.{safe}" if database else f"{silver_schema}.{safe}"

    user_prompt = f"""Calculated column name: {col['name']}
Home table: {col['table']}
DAX expression:
---
{col['expression']}
---

Target Silver table (fully qualified, use EXACTLY this name): {target_fqn}

Produce ONLY the derived column expression (not a full SELECT).
Format: <snowflake_expression> AS "{col['name']}"
Example: EXTRACT(MONTH FROM "Order Date") AS "Order_Month"
No SELECT, no FROM, no semicolons — just the column expression.
"""
    return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


def _llm_translate_calc_columns_grouped(table_name: str, cols: list, silver_schema: str,
                                         database: str, silver_table_map: dict = None,
                                         silver_columns: list = None) -> str:
    """
    silver_columns: list of exact column name strings as they appear in the Silver
    view (e.g. ['"City"', '"Sub-Category"', 'EMPNO', ...]).  Passing this prevents
    the LLM from guessing unquoted uppercase names or using table-prefix syntax.
    """
    safe = re.sub(r'[^A-Za-z0-9_]', '_', table_name).upper()
    if silver_table_map and table_name in silver_table_map:
        target_fqn = silver_table_map[table_name]
    else:
        target_fqn = f"{database}.{silver_schema}.{safe}" if database else f"{silver_schema}.{safe}"

    col_block = "\n".join(
        f"  - {c['name']}: {c['expression']}"
        for c in cols
    )

    silver_col_note = ""
    if silver_columns:
        silver_col_note = (
            f"\nExact column names in {target_fqn} (use ONLY these — do not guess or uppercase them):\n"
            f"  {silver_columns}\n"
        )

    user_prompt = f"""Table: {table_name}
Target Silver table (fully qualified, use EXACTLY this name): {target_fqn}
{silver_col_note}
Calculated columns to add (name: DAX expression):
{col_block}

Produce a single Snowflake SQL SELECT that reads all rows from {target_fqn}
and appends every calculated column as a derived expression:

SELECT
  *,
  <expr1> AS "<name1>",
  <expr2> AS "<name2>",
  ...
FROM {target_fqn}

CRITICAL RULES — violations cause Snowflake deployment failures:
- Output ONLY the SELECT statement. No CREATE VIEW, no semicolons, no markdown.
- Translate each DAX expression faithfully to Snowflake SQL.
- Reference columns using EXACTLY the names listed above (including quotes and case).
  Example: use "City" not CITY, use "Sub-Category" not SUB_CATEGORY.
- NEVER prefix columns with a table name or alias (e.g. ORDERS."City" is wrong;
  just "City" is correct — there is no alias in a simple SELECT * FROM view).
- If a DAX expression is uncertain, add a comment -- TODO: manual review: <reason>
  directly above the expression but still include a best-effort translation.
"""
    return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


def _llm_translate_calc_table(table: dict, all_table_names: list, silver_schema: str, database: str,
                               silver_table_map: dict = None) -> str:
    if silver_table_map:
        fq_tables = silver_table_map
    else:
        fq_tables = {
            t: f"{database}.{silver_schema}.{re.sub(r'[^A-Za-z0-9_]', '_', t).upper()}"
            for t in all_table_names
        }
    table_ref_block = "\n".join(f"  {name}  →  {fqn}" for name, fqn in fq_tables.items())

    user_prompt = f"""Calculated table name: {table['name']}
DAX table expression:
---
{table.get('calculated_table_dax', '')}
---

EXACT fully-qualified Silver table names you MUST use (do not invent other names):
{table_ref_block}

Produce a Snowflake SQL SELECT that reproduces the rows this calculated table would contain.
"""
    return aoai.chat_text(LLM_SYSTEM_PROMPT, user_prompt)


def build_gold_ddl(object_name: str, kind: str, select_sql: str, gold_schema: str,
                   database: str = None) -> str:
    """Optional `database` arg accepted for pipeline_runner compatibility; not used in DDL."""
    safe_name = re.sub(r"[^A-Za-z0-9_]", "_", object_name).upper()
    prefix = {"measure": "KPI", "calculated_column": "CALC", "calculated_table": "TBL"}.get(kind, "OBJ")
    return (
        f"-- Gold layer object for PBIX {kind}: {object_name}\n"
        f"CREATE OR REPLACE VIEW {gold_schema}.{prefix}_{safe_name} AS\n"
        f"{select_sql.strip()}\n;\n"
    )


def main():
    ap = argparse.ArgumentParser(description="Translate DAX measures/calculated columns/tables to Snowflake Gold-layer SQL")
    ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
    ap.add_argument("--output-dir", default=str(config.sql_gold_dir))
    args = ap.parse_args()

    with open(args.model, "r", encoding="utf-8") as f:
        parsed_model = json.load(f)

    sf = config.snowflake
    out_dir = Path(args.output_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    all_table_names = [t["name"] for t in parsed_model["tables"]]
    results = []

    # --- Measures -> KPI views ---
    for measure in parsed_model.get("measures", []):
        log.info(f"[LLM] Translating measure: {measure['name']}")
        try:
            sql = _llm_translate_measure(measure, all_table_names, sf.silver_schema, sf.database)
            method = "llm"
        except Exception as e:
            log.error(f"Measure translation failed for {measure['name']}: {e}")
            sql = f"-- TODO: manual review: LLM translation failed ({e})\nSELECT NULL AS \"{measure['name']}\""
            method = "llm_failed_fallback"

        ddl = build_gold_ddl(measure["name"], "measure", sql, sf.gold_schema)
        safe_name = re.sub(r"[^A-Za-z0-9_]", "_", measure["name"]).upper()
        out_file = out_dir / f"gold_kpi_{safe_name}.sql"
        with open(out_file, "w", encoding="utf-8") as f:
            f.write(ddl)
        results.append({"name": measure["name"], "kind": "measure", "method": method, "output_file": str(out_file)})
        log.info(f"Wrote {out_file}")

    # --- Calculated columns -> ONE view per table (all columns grouped) ---
    from collections import defaultdict
    calc_cols_by_table: dict = defaultdict(list)
    for col in parsed_model.get("calculated_columns", []):
        calc_cols_by_table[col["table"]].append(col)

    for table_name, cols in calc_cols_by_table.items():
        col_names = ", ".join(c["name"] for c in cols)
        log.info(f"[LLM] Translating {len(cols)} calculated column(s) for table '{table_name}': {col_names}")
        try:
            sql = _llm_translate_calc_columns_grouped(table_name, cols, sf.silver_schema, sf.database)
            method = "llm"
        except Exception as e:
            log.error(f"Calculated column group translation failed for table {table_name}: {e}")
            safe_fb = re.sub(r'[^A-Za-z0-9_]', '_', table_name).upper()
            sql = (
                f"-- TODO: manual review: LLM translation failed ({e})\n"
                f"SELECT * FROM {sf.database}.{sf.silver_schema}.{safe_fb}"
            )
            method = "llm_failed_fallback"

        safe_table = re.sub(r"[^A-Za-z0-9_]", "_", table_name).upper()
        view_name = f"{table_name}_CALC_COLUMNS"
        ddl = build_gold_ddl(view_name, "calculated_column", sql, sf.gold_schema)
        out_file = out_dir / f"gold_calc_{safe_table}.sql"
        with open(out_file, "w", encoding="utf-8") as f:
            f.write(ddl)
        results.append({
            "name": f"{table_name} ({len(cols)} calc columns: {col_names})",
            "kind": "calculated_column",
            "method": method,
            "output_file": str(out_file),
            "columns": [c["name"] for c in cols],
        })
        log.info(f"Wrote {out_file}")

    # --- Calculated tables -> Gold tables ---
    for table in parsed_model["tables"]:
        if not table.get("is_calculated_table"):
            continue
        log.info(f"[LLM] Translating calculated table: {table['name']}")
        try:
            sql = _llm_translate_calc_table(table, all_table_names, sf.silver_schema, sf.database)
            method = "llm"
        except Exception as e:
            log.error(f"Calculated table translation failed for {table['name']}: {e}")
            sql = f"-- TODO: manual review: LLM translation failed ({e})\nSELECT 1 AS placeholder"
            method = "llm_failed_fallback"

        ddl = build_gold_ddl(table["name"], "calculated_table", sql, sf.gold_schema)
        safe_name = re.sub(r"[^A-Za-z0-9_]", "_", table["name"]).upper()
        out_file = out_dir / f"gold_table_{safe_name}.sql"
        with open(out_file, "w", encoding="utf-8") as f:
            f.write(ddl)
        results.append({"name": table["name"], "kind": "calculated_table", "method": method, "output_file": str(out_file)})
        log.info(f"Wrote {out_file}")

    summary_path = out_dir / "_translation_summary.json"
    with open(summary_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)

    print("\nDAX -> SQL Translation Summary:")
    for r in results:
        print(f"  [{r['kind']:18s}] {r['name']:35s} method={r['method']}")


if __name__ == "__main__":
    main()