"""
app.extractor
==============
Core PBIX metadata extraction logic, refactored from the original
extract_pbix_metadata.py script into an importable module that returns
JSON-serializable Python objects instead of writing CSV/Excel files.

Engine: https://github.com/Hugoberry/pbixray (reads the VertiPaq DataModel
stream directly out of the pbix container -- no Power BI Desktop / AS engine
required).
"""

import math
import re
from datetime import date, datetime

import numpy as np
import pandas as pd

# ---------------------------------------------------------------------------
# Heuristic Power Query connector detection (same approach as the original
# script -- TMSCHEMA_DATASOURCES is frequently empty in modern pbix files
# because Power BI pushes connection details into the M code instead).
# ---------------------------------------------------------------------------

CONNECTOR_FUNCTIONS = [
    "Sql.Database", "Sql.Databases",
    "AnalysisServices.Database", "AnalysisServices.Databases",
    "Web.Contents", "Web.Page",
    "OData.Feed",
    "Odbc.DataSource", "Odbc.Query",
    "OleDb.DataSource", "OleDb.Query",
    "AdoDotNet.DataSource",
    "Excel.Workbook",
    "Csv.Document",
    "Json.Document",
    "Xml.Tables", "Xml.Document",
    "Folder.Files", "Folder.Contents",
    "SharePoint.Files", "SharePoint.Contents",
    "PostgreSQL.Database",
    "MySQL.Database",
    "Oracle.Database",
    "Teradata.Database",
    "Snowflake.Databases",
    "GoogleBigQuery.Database",
    "AmazonRedshift.Database",
    "SapHana.Database",
    "Exchange.Contents",
    "Salesforce.Reports", "Salesforce.Tables",
    "Dynamics365.Contents", "CommonDataService.Database",
    "PowerPlatform.Dataflows",
    "Hdfs.Contents",
    "AzureStorage.Blobs", "AzureStorage.DataLake", "AzureStorage.Tables",
    "AzureDataLakeStorage.Contents",
    "File.Contents",
    "SharePoint.Tables",
    "MicrosoftAccount.Contents",
]

_CONNECTOR_REGEX_CACHE = {}
_ARG_PATTERN = r'(?:"([^"]*)"|([A-Za-z_][\w.]*))'

_SYSTEM_TABLE_PATTERNS = [
    re.compile(r'^LocalDateTable_[0-9a-fA-F\-]+$'),
    re.compile(r'^DateTableTemplate_[0-9a-fA-F\-]+$'),
]

def _is_system_table(name: str) -> bool:
    if not isinstance(name, str):
        return False
    return any(p.match(name) for p in _SYSTEM_TABLE_PATTERNS)



def _connector_regex(fn_name):
    if fn_name not in _CONNECTOR_REGEX_CACHE:
        escaped = re.escape(fn_name)
        pattern = re.compile(
            rf'{escaped}\s*\(\s*{_ARG_PATTERN}\s*(?:,\s*{_ARG_PATTERN})?',
            re.MULTILINE,
        )
        _CONNECTOR_REGEX_CACHE[fn_name] = pattern
    return _CONNECTOR_REGEX_CACHE[fn_name]


def build_parameter_map(m_parameters_df):
    param_map = {}
    if m_parameters_df is None or m_parameters_df.empty:
        return param_map
    if not {"ParameterName", "Expression"}.issubset(m_parameters_df.columns):
        return param_map
    for _, row in m_parameters_df.iterrows():
        name = row.get("ParameterName")
        expr = row.get("Expression")
        if not isinstance(name, str) or not isinstance(expr, str):
            continue
        literal_match = re.search(r'"([^"]*)"', expr)
        if literal_match:
            param_map[name] = literal_match.group(1)
    return param_map


def _resolve_arg(str_val, ident_val, param_map):
    if str_val:
        return str_val, str_val, False
    if ident_val:
        resolved = param_map.get(ident_val)
        return ident_val, resolved if resolved is not None else ident_val, ident_val in param_map
    return "", "", False


def detect_connectors_in_m_code(table_name, m_expression, param_map=None):
    found = []
    param_map = param_map or {}
    if not isinstance(m_expression, str) or not m_expression.strip():
        return found
    for fn_name in CONNECTOR_FUNCTIONS:
        if fn_name not in m_expression:
            continue
        for match in _connector_regex(fn_name).finditer(m_expression):
            str1, ident1, str2, ident2 = match.groups()
            raw1, resolved1, is_param1 = _resolve_arg(str1, ident1, param_map)
            raw2, resolved2, is_param2 = _resolve_arg(str2, ident2, param_map)
            found.append(
                {
                    "table_name": table_name,
                    "connector_function": fn_name,
                    "primary_argument": raw1,
                    "primary_argument_resolved": resolved1,
                    "secondary_argument": raw2,
                    "secondary_argument_resolved": resolved2,
                    "resolved_via_m_parameter": is_param1 or is_param2,
                    "detection_method": "parsed_from_m_code_heuristic",
                }
            )
    return found


# ---------------------------------------------------------------------------
# Safe attribute access helpers
# ---------------------------------------------------------------------------

def safe_get(model, attr_name):
    try:
        value = getattr(model, attr_name)
        return pd.DataFrame() if value is None else value
    except Exception:
        return pd.DataFrame()


def df_or_empty(df):
    if df is None:
        return pd.DataFrame()
    if isinstance(df, pd.DataFrame):
        return df
    try:
        return pd.DataFrame(df)
    except Exception:
        return pd.DataFrame()


# ---------------------------------------------------------------------------
# JSON-safe conversion (handles NaN/NaT/Timestamp/numpy types, which the
# standard json module chokes on)
# ---------------------------------------------------------------------------

def _json_safe_scalar(value):
    if value is None:
        return None
    if isinstance(value, (pd.Timestamp, datetime, date)):
        try:
            if pd.isna(value):
                return None
        except (TypeError, ValueError):
            pass
        return value.isoformat()
    if isinstance(value, float):
        if math.isnan(value) or math.isinf(value):
            return None
        return value
    if isinstance(value, (np.floating,)):
        v = float(value)
        return None if (math.isnan(v) or math.isinf(v)) else v
    if isinstance(value, (np.integer,)):
        return int(value)
    if isinstance(value, (np.bool_,)):
        return bool(value)
    try:
        if pd.isna(value):
            return None
    except (TypeError, ValueError):
        pass
    return value


def df_to_records(df):
    """Convert a DataFrame into a list of JSON-safe dicts."""
    df = df_or_empty(df)
    if df.empty:
        return []
    records = df.to_dict(orient="records")
    return [{k: _json_safe_scalar(v) for k, v in row.items()} for row in records]


# ---------------------------------------------------------------------------
# Core extraction
# ---------------------------------------------------------------------------

def extract_all(pbix_path, on_disk=False):
    """Open a .pbix file and pull every supported metadata category out of it.

    Returns a dict of pandas DataFrames (raw form) plus model_size_bytes.
    Use `build_json_payload()` to turn this into the final JSON structure.
    """
    from pbixray import PBIXRay

    kwargs = {"on_disk": True} if on_disk else {}

    with PBIXRay(pbix_path, **kwargs) as model:
        results = {}

        tables = list(safe_get(model, "tables"))
        results["tables"] = pd.DataFrame({"TableName": tables})

        results["schema"] = df_or_empty(safe_get(model, "schema"))
        results["relationships"] = df_or_empty(safe_get(model, "relationships"))

        power_query = df_or_empty(safe_get(model, "power_query"))
        results["power_query"] = power_query

        m_parameters = df_or_empty(safe_get(model, "m_parameters"))
        results["m_parameters"] = m_parameters

        results["dax_measures"] = df_or_empty(safe_get(model, "dax_measures"))
        results["dax_tables"] = df_or_empty(safe_get(model, "dax_tables"))
        results["calculated_columns"] = df_or_empty(safe_get(model, "dax_columns"))
        results["rls"] = df_or_empty(safe_get(model, "rls"))
        results["metadata"] = df_or_empty(safe_get(model, "metadata"))

        tmschema_sources = df_or_empty(safe_get(model, "tmschema_datasources"))

        param_map = build_parameter_map(m_parameters)
        detected_rows = []
        if not power_query.empty and {"TableName", "Expression"}.issubset(power_query.columns):
            for _, row in power_query.iterrows():
                detected_rows.extend(
                    detect_connectors_in_m_code(row["TableName"], row["Expression"], param_map)
                )

        results["data_sources_tmschema"] = tmschema_sources
        results["data_sources_detected"] = pd.DataFrame(detected_rows)

        try:
            results["model_size_bytes"] = int(model.size)
        except Exception:
            results["model_size_bytes"] = None

        return results


# ---------------------------------------------------------------------------
# JSON payload builder
# ---------------------------------------------------------------------------
def build_json_payload(results, source_filename, include_system_tables: bool = False):
    flat = {
        "tables": df_to_records(results.get("tables")),
        "schema": df_to_records(results.get("schema")),
        "relationships": df_to_records(results.get("relationships")),
        "power_query": df_to_records(results.get("power_query")),
        "m_parameters": df_to_records(results.get("m_parameters")),
        "dax_measures": df_to_records(results.get("dax_measures")),
        "calculated_tables": df_to_records(results.get("dax_tables")),
        "calculated_columns": df_to_records(results.get("calculated_columns")),
        "row_level_security": df_to_records(results.get("rls")),
        "metadata": df_to_records(results.get("metadata")),
        "data_sources_tmschema": df_to_records(results.get("data_sources_tmschema")),
        "data_sources_detected": df_to_records(results.get("data_sources_detected")),
    }

    if not include_system_tables:
        removed = {r["TableName"] for r in flat["tables"] if _is_system_table(r.get("TableName"))}
        if removed:
            flat["tables"] = [r for r in flat["tables"] if r["TableName"] not in removed]
            flat["schema"] = [r for r in flat["schema"] if r.get("TableName") not in removed]
            flat["power_query"] = [r for r in flat["power_query"] if r.get("TableName") not in removed]
            flat["dax_measures"] = [r for r in flat["dax_measures"] if r.get("TableName") not in removed]
            flat["calculated_tables"] = [
                r for r in flat["calculated_tables"]
                if (r.get("TableName") or r.get("Name")) not in removed
            ]
            flat["calculated_columns"] = [r for r in flat["calculated_columns"] if r.get("TableName") not in removed]
            flat["data_sources_detected"] = [
                r for r in flat["data_sources_detected"] if r.get("table_name") not in removed
            ]

    # ---- build the denormalized per-table "model" view ----
    table_names = [row["TableName"] for row in flat["tables"]]
    tables_by_name = {name: {
        "table_name": name,
        "columns": [],
        "power_query_m": None,
        "measures": [],
        "calculated_columns": [],
        "is_calculated_table": False,
        "calculated_table_expression": None,
        "data_sources": [],
    } for name in table_names}

    for row in flat["schema"]:
        t = row.get("TableName")
        if t in tables_by_name:
            tables_by_name[t]["columns"].append({
                "column_name": row.get("ColumnName"),
                "data_type": row.get("PandasDataType"),
            })

    for row in flat["power_query"]:
        t = row.get("TableName")
        if t in tables_by_name:
            tables_by_name[t]["power_query_m"] = row.get("Expression")

    for row in flat["dax_measures"]:
        t = row.get("TableName")
        if t in tables_by_name:
            tables_by_name[t]["measures"].append({
                "name": row.get("Name"),
                "expression": row.get("Expression"),
                "display_folder": row.get("DisplayFolder"),
                "description": row.get("Description"),
            })

    for row in flat["calculated_columns"]:
        t = row.get("TableName")
        if t in tables_by_name:
            tables_by_name[t]["calculated_columns"].append({
                "column_name": row.get("ColumnName") or row.get("Name"),
                "expression": row.get("Expression"),
            })

    for row in flat["calculated_tables"]:
        t = row.get("TableName") or row.get("Name")
        if t in tables_by_name:
            tables_by_name[t]["is_calculated_table"] = True
            tables_by_name[t]["calculated_table_expression"] = row.get("Expression")

    for row in flat["data_sources_detected"]:
        t = row.get("table_name")
        if t in tables_by_name:
            tables_by_name[t]["data_sources"].append(row)

    model_tables = [tables_by_name[name] for name in table_names]

    summary = {
        "source_filename": source_filename,
        "model_size_bytes": results.get("model_size_bytes"),
        "table_count": len(table_names),
        "tables_with_power_query": len(flat["power_query"]),
        "dax_measure_count": len(flat["dax_measures"]),
        "calculated_table_count": len(flat["calculated_tables"]),
        "calculated_column_count": len(flat["calculated_columns"]),
        "relationship_count": len(flat["relationships"]),
        "data_sources_detected_count": len(flat["data_sources_detected"]),
        "data_sources_tmschema_count": len(flat["data_sources_tmschema"]),
        "row_level_security_role_count": len(flat["row_level_security"]),
    }

    return {
        "summary": summary,
        "model": {"tables": model_tables},
        "flat": flat,
    }
