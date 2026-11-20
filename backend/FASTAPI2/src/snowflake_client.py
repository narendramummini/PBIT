"""
snowflake_client.py
Thin wrapper around snowflake-connector-python: connection handling +
INFORMATION_SCHEMA discovery used by table_matcher.py and the SQL
generators / executors.

Connections can be opened in two ways:
  1. get_connection()              — reads from config/.env (original CLI path)
  2. get_connection_from_params()  — accepts a dict of credentials directly
                                     (used by the API so no .env is needed)
"""

import sys
from pathlib import Path
from typing import Dict, List, Optional

import snowflake.connector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from config import config
from logger import get_logger

log = get_logger("snowflake_client", config.logs_dir, config.pipeline.log_level)


def _build_connect_kwargs(account, user, role, warehouse, database, schema,
                          pat_token=None, password=None,
                          private_key_path=None, private_key_passphrase=None):
    """Build the kwargs dict for snowflake.connector.connect from explicit params."""
    kwargs = dict(
        account=account,
        user=user,
        role=role,
        warehouse=warehouse,
        database=database,
        schema=schema,
    )
    if pat_token:
        kwargs["password"] = pat_token
    elif private_key_path:
        from cryptography.hazmat.backends import default_backend
        from cryptography.hazmat.primitives import serialization
        with open(private_key_path, "rb") as key_file:
            p_key = serialization.load_pem_private_key(
                key_file.read(),
                password=private_key_passphrase.encode() if private_key_passphrase else None,
                backend=default_backend(),
            )
        pkb = p_key.private_bytes(
            encoding=serialization.Encoding.DER,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
        kwargs["private_key"] = pkb
    elif password:
        kwargs["password"] = password
    else:
        raise ValueError(
            "No Snowflake auth supplied. Provide pat_token, password, or private_key_path."
        )
    return kwargs


def get_connection_from_params(params: dict, schema: Optional[str] = None):
    """
    Open a Snowflake connection from an explicit params dict.
    Expected keys (mirrors SnowflakeConnection schema):
        account, user, warehouse, database, role,
        schema_ (or schema), silver_schema, gold_schema,
        pat_token | password | private_key_path [+ private_key_passphrase]
    """
    target_schema = schema or params.get("schema_") or params.get("schema", "BRONZE")
    log.info(
        f"Connecting to Snowflake account={params['account']} "
        f"db={params['database']} schema={target_schema} warehouse={params['warehouse']}"
    )
    kwargs = _build_connect_kwargs(
        account=params["account"],
        user=params["user"],
        role=params.get("role", "SYSADMIN"),
        warehouse=params["warehouse"],
        database=params["database"],
        schema=target_schema,
        pat_token=params.get("pat_token"),
        password=params.get("password"),
        private_key_path=params.get("private_key_path"),
        private_key_passphrase=params.get("private_key_passphrase"),
    )
    return snowflake.connector.connect(**kwargs)


def get_connection(schema: Optional[str] = None):
    """
    Open a Snowflake connection using credentials from .env / config.
    (Original CLI path — kept for backward compatibility.)
    """
    sf = config.snowflake
    target_schema = schema or sf.schema
    log.info(
        f"Connecting to Snowflake account={sf.account} "
        f"db={sf.database} schema={target_schema} warehouse={sf.warehouse}"
    )
    kwargs = _build_connect_kwargs(
        account=sf.account,
        user=sf.user,
        role=sf.role,
        warehouse=sf.warehouse,
        database=sf.database,
        schema=target_schema,
        pat_token=sf.pat_token,
        password=sf.password,
        private_key_path=sf.private_key_path,
        private_key_passphrase=sf.private_key_passphrase,
    )
    return snowflake.connector.connect(**kwargs)


# ---------------------------------------------------------------------------
# INFORMATION_SCHEMA helpers — accept an optional explicit params dict so
# callers can choose between env-based and API-based connections.
# ---------------------------------------------------------------------------

def _conn(sf_params: Optional[dict] = None, schema: Optional[str] = None):
    """Return a connection: from explicit params if provided, else from env."""
    if sf_params:
        return get_connection_from_params(sf_params, schema=schema)
    return get_connection(schema=schema)


def list_tables(schema: Optional[str] = None, sf_params: Optional[dict] = None) -> List[Dict]:
    """List tables + row counts from INFORMATION_SCHEMA.TABLES."""
    sf_cfg = config.snowflake if not sf_params else None
    target_schema = schema or (sf_params.get("schema_") or sf_params.get("schema", "BRONZE") if sf_params else sf_cfg.schema)
    conn = _conn(sf_params, target_schema)
    try:
        cur = conn.cursor(snowflake.connector.DictCursor)
        cur.execute(
            """
            SELECT TABLE_NAME, TABLE_SCHEMA, TABLE_CATALOG, ROW_COUNT, BYTES, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = %s
            ORDER BY TABLE_NAME
            """,
            (target_schema,),
        )
        return cur.fetchall()
    finally:
        conn.close()


def list_columns(table_name: str, schema: Optional[str] = None,
                 sf_params: Optional[dict] = None) -> List[Dict]:
    """List columns for a specific table from INFORMATION_SCHEMA.COLUMNS."""
    sf_cfg = config.snowflake if not sf_params else None
    target_schema = schema or (sf_params.get("schema_") or sf_params.get("schema", "BRONZE") if sf_params else sf_cfg.schema)
    conn = _conn(sf_params, target_schema)
    try:
        cur = conn.cursor(snowflake.connector.DictCursor)
        cur.execute(
            """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = %s AND TABLE_NAME = %s
            ORDER BY ORDINAL_POSITION
            """,
            (target_schema, table_name),
        )
        return cur.fetchall()
    finally:
        conn.close()


def execute_sql(sql: str, schema: Optional[str] = None,
                sf_params: Optional[dict] = None) -> List[Dict]:
    """Execute arbitrary SQL and return rows as dicts."""
    conn = _conn(sf_params, schema)
    try:
        cur = conn.cursor(snowflake.connector.DictCursor)
        cur.execute(sql)
        return cur.fetchall()
    finally:
        conn.close()
