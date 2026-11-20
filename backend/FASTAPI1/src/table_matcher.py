"""
table_matcher.py
Matches each PBIX table (from parsed_model.json) to a Snowflake Bronze table
(from INFORMATION_SCHEMA), using fuzzy name matching first and Azure OpenAI
to resolve ambiguous / low-confidence cases.

Run:
    python src/table_matcher.py \
        --model output/mapping/parsed_model.json \
        --output output/mapping/table_mapping.json
"""

import argparse
import difflib
import json
import sys
from dataclasses import dataclass, asdict, field
from pathlib import Path
from typing import Dict, List, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
from config import config
from logger import get_logger
from snowflake_client import list_tables, list_columns
import azure_openai_client as aoai

log = get_logger("table_matcher", config.logs_dir, config.pipeline.log_level)


@dataclass
class TableMatch:
    pbix_table: str
    bronze_table: Optional[str]
    bronze_schema: Optional[str]
    confidence: float
    method: str  # "exact", "fuzzy", "llm", "manual", "unmatched"
    reasoning: str = ""
    column_mapping: Dict[str, str] = field(default_factory=dict)


def _normalize(name: str) -> str:
    return name.strip().lower().replace(" ", "_").replace("-", "_")


def _fuzzy_score(a: str, b: str) -> float:
    return difflib.SequenceMatcher(None, _normalize(a), _normalize(b)).ratio()


def _best_fuzzy_candidates(pbix_table_name: str, bronze_tables: List[dict], top_n: int = 5):
    scored = [(_fuzzy_score(pbix_table_name, bt["TABLE_NAME"]), bt) for bt in bronze_tables]
    scored.sort(key=lambda x: x[0], reverse=True)
    return scored[:top_n]

def _build_column_mapping(pbix_columns: list, bronze_columns: list) -> dict:
    """Build a best-effort PBIX->Snowflake column name mapping using fuzzy matching.
    PowerBI column names like 'First Column' are matched to Snowflake names like 'first_column'.
    """
    mapping = {}
    sf_col_names = [c["COLUMN_NAME"] for c in bronze_columns]

    for pbix_col in pbix_columns:
        pbix_norm = _normalize(pbix_col)
        best_score = 0.0
        best_sf = None
        for sf_col in sf_col_names:
            score = _fuzzy_score(pbix_col, sf_col)
            if score > best_score:
                best_score = score
                best_sf = sf_col
        # Accept if score >= 0.6 and the normalized names are plausibly the same
        if best_sf and best_score >= 0.6:
            mapping[pbix_col] = best_sf
    return mapping




LLM_SYSTEM_PROMPT = """You are a data engineering assistant helping migrate a Power BI model to Snowflake.
You will be given a Power BI table name (with its column names) and a shortlist of candidate Snowflake
Bronze-layer tables (with their column names). Pick the single best matching Bronze table, or say none
match well.

Respond ONLY with a JSON object of this exact shape:
{
  "best_match": "<bronze table name or null>",
  "confidence": <float 0.0-1.0>,
  "reasoning": "<one or two sentence explanation>",
  "column_mapping": {"<pbix_column>": "<bronze_column>", ...}
}
Only include column_mapping entries you are reasonably confident about. If no candidate is a good match,
set best_match to null and confidence to 0.0.
"""


def _llm_resolve(pbix_table, candidates: List[dict], bronze_columns_by_table: Dict[str, List[dict]]) -> dict:
    candidate_block = []
    for _, bt in candidates:
        tname = bt["TABLE_NAME"]
        cols = [c["COLUMN_NAME"] for c in bronze_columns_by_table.get(tname, [])]
        candidate_block.append({"table": tname, "columns": cols})

    user_prompt = json.dumps({
        "pbix_table": {
            "name": pbix_table.name,
            "columns": [c.name for c in pbix_table.columns],
        },
        "candidates": candidate_block,
    }, indent=2)

    return aoai.chat_json(LLM_SYSTEM_PROMPT, user_prompt, deployment=config.azure.deployment_fast)


def match_tables(parsed_model: dict, bronze_schema: Optional[str] = None,
                  sf_params: Optional[dict] = None) -> List[TableMatch]:
    if sf_params:
        default_schema = sf_params.get("schema_") or sf_params.get("schema", "BRONZE")
    else:
        default_schema = config.snowflake.schema
    bronze_schema = bronze_schema or default_schema

    log.info("Discovering Bronze tables in Snowflake...")
    bronze_tables = list_tables(schema=bronze_schema, sf_params=sf_params)
    if not bronze_tables:
        log.warning(f"No tables found in schema {bronze_schema}.")

    log.info(f"Fetching Bronze column metadata for {len(bronze_tables)} table(s)...")
    bronze_columns_by_table: Dict[str, List[dict]] = {}
    for bt in bronze_tables:
        tname = bt["TABLE_NAME"]
        try:
            cols = list_columns(table_name=tname, schema=bronze_schema, sf_params=sf_params)
            bronze_columns_by_table[tname] = cols
        except Exception as e:
            log.warning(f"Could not fetch columns for {bronze_schema}.{tname} ({type(e).__name__}).")
            bronze_columns_by_table[tname] = []

    matches: List[TableMatch] = []
    threshold = config.pipeline.match_confidence_threshold

    for t in parsed_model["tables"]:
        pbix_name = t["name"]
        if t.get("is_calculated_table"):
            # Calculated tables have no Bronze source — they're derived via DAX later
            matches.append(TableMatch(
                pbix_table=pbix_name, bronze_table=None, bronze_schema=None,
                confidence=1.0, method="calculated_table",
                reasoning="Calculated table in PBIX; will be derived via DAX->SQL translation, not matched to Bronze.",
            ))
            continue

        # 1. exact name match
        exact = next((bt for bt in bronze_tables if _normalize(bt["TABLE_NAME"]) == _normalize(pbix_name)), None)
        if exact:
            pbix_cols = [c["name"] for c in t.get("columns", [])]
            bronze_cols = bronze_columns_by_table.get(exact["TABLE_NAME"], [])
            col_map = _build_column_mapping(pbix_cols, bronze_cols)
            matches.append(TableMatch(
                pbix_table=pbix_name, bronze_table=exact["TABLE_NAME"], bronze_schema=bronze_schema,
                confidence=1.0, method="exact",
                reasoning="Exact name match (case/whitespace-insensitive).",
                column_mapping=col_map,
            ))
            log.info(f"[EXACT]  {pbix_name} -> {exact['TABLE_NAME']} ({len(col_map)} col mappings)")
            continue

        # 2. fuzzy candidates
        candidates = _best_fuzzy_candidates(pbix_name, bronze_tables)
        top_score, top_table = candidates[0] if candidates else (0.0, None)

        if top_score >= 0.92 and top_table:
            pbix_cols = [c["name"] for c in t.get("columns", [])]
            bronze_cols = bronze_columns_by_table.get(top_table["TABLE_NAME"], [])
            col_map = _build_column_mapping(pbix_cols, bronze_cols)
            matches.append(TableMatch(
                pbix_table=pbix_name, bronze_table=top_table["TABLE_NAME"], bronze_schema=bronze_schema,
                confidence=round(top_score, 3), method="fuzzy",
                reasoning=f"High-confidence fuzzy name match (score={top_score:.2f}).",
                column_mapping=col_map,
            ))
            log.info(f"[FUZZY]  {pbix_name} -> {top_table['TABLE_NAME']} (score={top_score:.2f}, {len(col_map)} col mappings)")
            continue

        # 3. ambiguous -> ask Azure OpenAI
        if candidates and top_table:
            log.info(f"[LLM]    Resolving ambiguous match for '{pbix_name}' among {[c[1]['TABLE_NAME'] for c in candidates]}")
            # Build a lightweight pbix table object for the prompt
            class _T:
                pass
            pt = _T()
            pt.name = pbix_name
            pt.columns = [_T() for _ in t.get("columns", [])]
            for col_obj, col in zip(pt.columns, t.get("columns", [])):
                col_obj.name = col["name"]

            try:
                result = _llm_resolve(pt, candidates, bronze_columns_by_table)
                best = result.get("best_match")
                conf = float(result.get("confidence", 0.0))
                reasoning = result.get("reasoning", "")
                col_map = result.get("column_mapping", {})
            except Exception as e:
                log.error(f"LLM resolution failed for {pbix_name} ({type(e).__name__}).")
                best, conf, reasoning, col_map = None, 0.0, "LLM call failed.", {}

            if best and conf >= threshold:
                # Backfill any columns the LLM didn't map with fuzzy fallback
                pbix_cols = [c["name"] for c in t.get("columns", [])]
                bronze_cols = bronze_columns_by_table.get(best, [])
                fuzzy_map = _build_column_mapping(pbix_cols, bronze_cols)
                for pbix_c, sf_c in fuzzy_map.items():
                    col_map.setdefault(pbix_c, sf_c)
                matches.append(TableMatch(
                    pbix_table=pbix_name, bronze_table=best, bronze_schema=bronze_schema,
                    confidence=round(conf, 3), method="llm", reasoning=reasoning, column_mapping=col_map,
                ))
                log.info(f"[LLM]    {pbix_name} -> {best} (confidence={conf:.2f})")
            else:
                matches.append(TableMatch(
                    pbix_table=pbix_name, bronze_table=best, bronze_schema=bronze_schema if best else None,
                    confidence=round(conf, 3), method="llm_low_confidence", reasoning=reasoning, column_mapping=col_map,
                ))
                log.warning(f"[REVIEW] '{pbix_name}' needs manual review (best guess={best}, confidence={conf:.2f})")
        else:
            matches.append(TableMatch(
                pbix_table=pbix_name, bronze_table=None, bronze_schema=None,
                confidence=0.0, method="unmatched",
                reasoning="No Bronze table candidates found at all.",
            ))
            log.warning(f"[UNMATCHED] No candidates found for '{pbix_name}'")

    return matches


def interactive_review(matches: List[TableMatch]) -> List[TableMatch]:
    """If INTERACTIVE_REVIEW=true, prompt the user on the console to confirm
    or override any low-confidence / unmatched results."""
    needs_review = [m for m in matches if m.method in ("llm_low_confidence", "unmatched")]
    if not needs_review:
        return matches

    print(f"\n{len(needs_review)} table(s) need manual review (confidence below threshold):\n")
    for m in needs_review:
        print(f"  PBIX table: {m.pbix_table}")
        print(f"  Best guess: {m.bronze_table or '(none)'}  (confidence={m.confidence})")
        print(f"  Reasoning:  {m.reasoning}")
        answer = input("  Enter correct Bronze table name (or press Enter to accept guess / 'skip' to leave unmatched): ").strip()
        if answer.lower() == "skip" or answer == "" and not m.bronze_table:
            m.bronze_table = None
            m.method = "manual_skip"
        elif answer:
            m.bronze_table = answer
            m.method = "manual"
            m.confidence = 1.0
        else:
            m.method = "manual_accepted"
        print()
    return matches


def main():
    ap = argparse.ArgumentParser(description="Match PBIX tables to Snowflake Bronze tables")
    ap.add_argument("--model", default=str(config.mapping_dir / "parsed_model.json"))
    ap.add_argument("--output", default=str(config.mapping_dir / "table_mapping.json"))
    ap.add_argument("--bronze-schema", default=None)
    ap.add_argument("--no-interactive", action="store_true", help="Skip manual review prompts even if INTERACTIVE_REVIEW=true")
    args = ap.parse_args()

    with open(args.model, "r", encoding="utf-8") as f:
        parsed_model = json.load(f)

    matches = match_tables(parsed_model, bronze_schema=args.bronze_schema)

    if config.pipeline.interactive_review and not args.no_interactive:
        matches = interactive_review(matches)

    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump([asdict(m) for m in matches], f, indent=2)

    log.info(f"Table mapping written to {out_path}")

    matched = sum(1 for m in matches if m.bronze_table)
    print(f"\nSummary: {matched}/{len(matches)} PBIX tables matched to a Bronze table or calculated source.")
    for m in matches:
        flag = "OK" if m.bronze_table or m.method == "calculated_table" else "!!"
        print(f"  [{flag}] {m.pbix_table:30s} -> {m.bronze_table or '(unmatched)':30s} "
              f"[{m.method}, conf={m.confidence}]")


if __name__ == "__main__":
    main()
