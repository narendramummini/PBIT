"""
pbix_parser.py
Parses a PBIX metadata JSON export (tables, Power Query / M, DAX measures,
calculated columns, calculated tables, relationships) into normalized
Python objects used by the rest of the pipeline.

Supports TWO JSON formats (auto-detected):

  FORMAT A — Legacy / generic export (e.g. pbi-tools, Tabular Editor):
      {
        "tables": [ { "name": "...", "mQuery": "...", "columns": [...], ... } ],
        "measures": [ { "name": "...", "table": "...", "expression": "..." } ],
        "relationships": [ { "fromTable": "...", ... } ]
      }

  FORMAT B — New structured export (Outputresponse.json):
      {
        "summary": { "table_count": ..., ... },
        "model": {
          "tables": [
            {
              "table_name": "...",
              "columns": [ { "column_name": "...", "data_type": "..." } ],
              "power_query_m": "...",
              "measures": [ { "name": "...", "expression": "..." } ],
              "calculated_columns": [ { "column_name": "...", "expression": "..." } ],
              "is_calculated_table": false,
              "calculated_table_expression": "...",
              "data_sources": [ { "connector_function": "...", ... } ]
            }
          ],
          "relationships": [ { ... } ]
        }
      }

Run directly:
    python src/pbix_parser.py --input sample_data/pbix_metadata.json
"""

import argparse
import json
import sys
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Any, Dict, List, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
from config import config
from logger import get_logger

log = get_logger("pbix_parser", config.logs_dir, config.pipeline.log_level)


# ---------------------------------------------------------------------------
# Normalized data model
# ---------------------------------------------------------------------------

@dataclass
class ColumnDef:
    name: str
    data_type: str = "string"
    source_column: Optional[str] = None
    is_hidden: bool = False


@dataclass
class TableDef:
    name: str
    columns: List[ColumnDef] = field(default_factory=list)
    m_query: Optional[str] = None
    source_type: Optional[str] = None  # e.g. "Snowflake", "SQL", "Folder", etc.
    is_calculated_table: bool = False
    calculated_table_dax: Optional[str] = None


@dataclass
class MeasureDef:
    name: str
    table: str
    expression: str
    format_string: Optional[str] = None
    is_hidden: bool = False


@dataclass
class CalculatedColumnDef:
    name: str
    table: str
    expression: str
    data_type: str = "string"


@dataclass
class RelationshipDef:
    from_table: str
    from_column: str
    to_table: str
    to_column: str
    cardinality: Optional[str] = None  # e.g. "many-to-one"
    cross_filter_direction: Optional[str] = None
    is_active: bool = True


@dataclass
class PBIXModel:
    tables: List[TableDef] = field(default_factory=list)
    measures: List[MeasureDef] = field(default_factory=list)
    calculated_columns: List[CalculatedColumnDef] = field(default_factory=list)
    relationships: List[RelationshipDef] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "tables": [asdict(t) for t in self.tables],
            "measures": [asdict(m) for m in self.measures],
            "calculated_columns": [asdict(c) for c in self.calculated_columns],
            "relationships": [asdict(r) for r in self.relationships],
        }


# ---------------------------------------------------------------------------
# Parsing helpers — tolerant of varying export schemas
# ---------------------------------------------------------------------------

def _first_present(d: dict, *keys, default=None):
    for k in keys:
        if k in d and d[k] is not None:
            return d[k]
    return default


def _is_new_format(raw: dict) -> bool:
    """Detect Format B: top-level 'model' key with 'tables' list inside it."""
    return "model" in raw and isinstance(raw["model"], dict) and "tables" in raw["model"]


# ---------------------------------------------------------------------------
# Format A parsers (legacy / generic)
# ---------------------------------------------------------------------------

def _parse_columns_format_a(table_raw: dict) -> List[ColumnDef]:
    cols_raw = _first_present(table_raw, "columns", "Columns", default=[])
    cols = []
    for c in cols_raw:
        if isinstance(c, str):
            cols.append(ColumnDef(name=c))
            continue
        cols.append(ColumnDef(
            name=_first_present(c, "name", "Name", default="UNKNOWN_COLUMN"),
            data_type=_first_present(c, "dataType", "data_type", "DataType", default="string"),
            source_column=_first_present(c, "sourceColumn", "source_column", "SourceColumn"),
            is_hidden=bool(_first_present(c, "isHidden", "is_hidden", "IsHidden", default=False)),
        ))
    return cols


def _parse_tables_format_a(raw: dict) -> List[TableDef]:
    tables_raw = _first_present(raw, "tables", "Tables", default=[])
    tables = []
    for t in tables_raw:
        name = _first_present(t, "name", "Name", default="UNKNOWN_TABLE")
        m_query = _first_present(t, "mQuery", "m_query", "expression", "Expression", "partitionExpression")
        is_calc = bool(_first_present(t, "isCalculatedTable", "is_calculated_table", default=False))
        calc_dax = _first_present(t, "calculatedTableDax", "calculated_table_dax") if is_calc else None
        tables.append(TableDef(
            name=name,
            columns=_parse_columns_format_a(t),
            m_query=m_query,
            source_type=_first_present(t, "sourceType", "source_type", "SourceType"),
            is_calculated_table=is_calc,
            calculated_table_dax=calc_dax,
        ))
    return tables


def _parse_measures_format_a(raw: dict) -> List[MeasureDef]:
    measures_raw = _first_present(raw, "measures", "Measures", default=[])
    # Some exports nest measures under each table instead of top-level
    if not measures_raw:
        measures_raw = []
        for t in _first_present(raw, "tables", "Tables", default=[]):
            tname = _first_present(t, "name", "Name", default="UNKNOWN_TABLE")
            for m in _first_present(t, "measures", "Measures", default=[]):
                m = dict(m)
                m["table"] = tname
                measures_raw.append(m)

    measures = []
    for m in measures_raw:
        measures.append(MeasureDef(
            name=_first_present(m, "name", "Name", default="UNKNOWN_MEASURE"),
            table=_first_present(m, "table", "Table", "tableName", default="UNKNOWN_TABLE"),
            expression=_first_present(m, "expression", "Expression", "dax", "DAX", default=""),
            format_string=_first_present(m, "formatString", "format_string", "FormatString"),
            is_hidden=bool(_first_present(m, "isHidden", "is_hidden", default=False)),
        ))
    return measures


def _parse_calculated_columns_format_a(raw: dict) -> List[CalculatedColumnDef]:
    out = []
    for t in _first_present(raw, "tables", "Tables", default=[]):
        tname = _first_present(t, "name", "Name", default="UNKNOWN_TABLE")
        for c in _first_present(t, "columns", "Columns", default=[]):
            if not isinstance(c, dict):
                continue
            expr = _first_present(c, "expression", "Expression", "dax")
            is_calc = bool(_first_present(c, "isCalculated", "is_calculated", default=bool(expr)))
            if is_calc and expr:
                out.append(CalculatedColumnDef(
                    name=_first_present(c, "name", "Name", default="UNKNOWN_COLUMN"),
                    table=tname,
                    expression=expr,
                    data_type=_first_present(c, "dataType", "data_type", default="string"),
                ))
    return out


def _parse_relationships_format_a(raw: dict) -> List[RelationshipDef]:
    rels_raw = _first_present(raw, "relationships", "Relationships", default=[])
    rels = []
    for r in rels_raw:
        rels.append(RelationshipDef(
            from_table=_first_present(r, "fromTable", "from_table", "FromTable", default=""),
            from_column=_first_present(r, "fromColumn", "from_column", "FromColumn", default=""),
            to_table=_first_present(r, "toTable", "to_table", "ToTable", default=""),
            to_column=_first_present(r, "toColumn", "to_column", "ToColumn", default=""),
            cardinality=_first_present(r, "cardinality", "Cardinality"),
            cross_filter_direction=_first_present(r, "crossFilterDirection", "cross_filter_direction"),
            is_active=bool(_first_present(r, "isActive", "is_active", default=True)),
        ))
    return rels


# ---------------------------------------------------------------------------
# Format B parsers (new Outputresponse.json structured export)
# ---------------------------------------------------------------------------

def _parse_columns_format_b(table_raw: dict) -> List[ColumnDef]:
    """
    Format B columns look like:
      { "column_name": "ISSUE_KEY", "data_type": "string" }
    """
    cols_raw = table_raw.get("columns", [])
    cols = []
    for c in cols_raw:
        if isinstance(c, str):
            cols.append(ColumnDef(name=c))
            continue
        # In Format B the calculated_columns list has its own "expression" field;
        # regular columns under "columns" do NOT — so never treat them as calc-cols here.
        cols.append(ColumnDef(
            name=_first_present(c, "column_name", "name", "Name", default="UNKNOWN_COLUMN"),
            data_type=_first_present(c, "data_type", "dataType", "DataType", default="string"),
            source_column=_first_present(c, "sourceColumn", "source_column"),
            is_hidden=bool(_first_present(c, "isHidden", "is_hidden", default=False)),
        ))
    return cols


def _source_type_from_data_sources(table_raw: dict) -> Optional[str]:
    """
    Format B carries data source info in a 'data_sources' list.
    Extract the connector function name as a human-readable source type.
    e.g. "SharePoint.Files" → "SharePoint", "OData.Feed" → "OData"
    """
    data_sources = table_raw.get("data_sources", [])
    if not data_sources:
        return None
    connector = data_sources[0].get("connector_function", "")
    if connector:
        # Return the prefix before the first dot for readability, or the full name
        return connector.split(".")[0] if "." in connector else connector
    return None


def _parse_tables_format_b(model: dict) -> List[TableDef]:
    """
    Format B tables are under raw["model"]["tables"] and look like:
      {
        "table_name": "...",
        "columns": [...],
        "power_query_m": "...",
        "measures": [...],
        "calculated_columns": [...],
        "is_calculated_table": false,
        "calculated_table_expression": "...",
        "data_sources": [...]
      }
    """
    tables_raw = model.get("tables", [])
    tables = []
    for t in tables_raw:
        is_calc = bool(t.get("is_calculated_table", False))
        calc_dax = t.get("calculated_table_expression") if is_calc else None
        tables.append(TableDef(
            name=t.get("table_name", "UNKNOWN_TABLE"),
            columns=_parse_columns_format_b(t),
            m_query=t.get("power_query_m"),
            source_type=_source_type_from_data_sources(t),
            is_calculated_table=is_calc,
            calculated_table_dax=calc_dax,
        ))
    return tables


def _parse_measures_format_b(model: dict) -> List[MeasureDef]:
    """
    Format B nests measures directly under each table:
      { "name": "...", "expression": "...", "display_folder": null, "description": null }
    """
    measures = []
    for t in model.get("tables", []):
        tname = t.get("table_name", "UNKNOWN_TABLE")
        for m in t.get("measures", []):
            expr = m.get("expression") or ""
            # Skip placeholder/null measures
            if expr is None:
                expr = ""
            measures.append(MeasureDef(
                name=_first_present(m, "name", "Name", default="UNKNOWN_MEASURE"),
                table=tname,
                expression=expr,
                format_string=_first_present(m, "formatString", "format_string", "display_folder"),
                is_hidden=bool(_first_present(m, "isHidden", "is_hidden", default=False)),
            ))
    return measures


def _parse_calculated_columns_format_b(model: dict) -> List[CalculatedColumnDef]:
    """
    Format B nests calculated columns under each table as 'calculated_columns':
      { "column_name": "...", "expression": "..." }
    """
    out = []
    for t in model.get("tables", []):
        tname = t.get("table_name", "UNKNOWN_TABLE")
        for c in t.get("calculated_columns", []):
            expr = _first_present(c, "expression", "Expression", "dax")
            if not expr:
                continue
            out.append(CalculatedColumnDef(
                name=_first_present(c, "column_name", "name", "Name", default="UNKNOWN_COLUMN"),
                table=tname,
                expression=expr,
                data_type=_first_present(c, "data_type", "dataType", default="string"),
            ))
    return out


def _parse_relationships_format_b(model: dict) -> List[RelationshipDef]:
    """
    Format B relationships are under raw["model"]["relationships"].
    Key names may vary; fall back gracefully.
    """
    rels_raw = model.get("relationships", [])
    rels = []
    for r in rels_raw:
        rels.append(RelationshipDef(
            from_table=_first_present(r, "from_table", "fromTable", "FromTable", default=""),
            from_column=_first_present(r, "from_column", "fromColumn", "FromColumn", default=""),
            to_table=_first_present(r, "to_table", "toTable", "ToTable", default=""),
            to_column=_first_present(r, "to_column", "toColumn", "ToColumn", default=""),
            cardinality=_first_present(r, "cardinality", "Cardinality"),
            cross_filter_direction=_first_present(r, "crossFilterDirection", "cross_filter_direction"),
            is_active=bool(_first_present(r, "isActive", "is_active", default=True)),
        ))
    return rels


# ---------------------------------------------------------------------------
# Unified entry point
# ---------------------------------------------------------------------------

def parse_pbix_metadata(path: str) -> PBIXModel:
    p = Path(path)
    if not p.exists():
        log.error(f"PBIX metadata file not found: {p}")
        sys.exit(1)

    log.info(f"Reading PBIX metadata from {p}")
    with open(p, "r", encoding="utf-8") as f:
        raw = json.load(f)

    if _is_new_format(raw):
        log.info("Detected Format B (structured model export with 'model' key)")
        model_section = raw["model"]
        model = PBIXModel(
            tables=_parse_tables_format_b(model_section),
            measures=_parse_measures_format_b(model_section),
            calculated_columns=_parse_calculated_columns_format_b(model_section),
            relationships=_parse_relationships_format_b(model_section),
        )
    else:
        log.info("Detected Format A (legacy / generic export)")
        model = PBIXModel(
            tables=_parse_tables_format_a(raw),
            measures=_parse_measures_format_a(raw),
            calculated_columns=_parse_calculated_columns_format_a(raw),
            relationships=_parse_relationships_format_a(raw),
        )

    log.info(
        f"Parsed {len(model.tables)} tables, {len(model.measures)} measures, "
        f"{len(model.calculated_columns)} calculated columns, "
        f"{len(model.relationships)} relationships"
    )
    return model


def main():
    ap = argparse.ArgumentParser(description="Parse PBIX metadata JSON into normalized model")
    ap.add_argument("--input", default=config.pipeline.pbix_metadata_path, help="Path to PBIX metadata JSON")
    ap.add_argument("--output", default=str(config.mapping_dir / "parsed_model.json"), help="Where to write normalized model JSON")
    args = ap.parse_args()

    model = parse_pbix_metadata(args.input)

    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(model.to_dict(), f, indent=2)

    log.info(f"Normalized model written to {out_path}")

    print("\nTables found:")
    for t in model.tables:
        kind = "CALCULATED" if t.is_calculated_table else "SOURCED"
        print(f"  - {t.name} [{kind}] ({len(t.columns)} columns)")


if __name__ == "__main__":
    main()
