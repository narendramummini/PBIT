"""
schemas.py
Pydantic request/response models for the FastAPI routes.
Snowflake credentials are passed directly in API requests — no .env needed.
"""

from typing import Dict, List, Optional

from pydantic import BaseModel, Field


class JobResponse(BaseModel):
    job_id: str
    stage: str
    status: str
    progress_message: str
    created_at: str
    started_at: Optional[str] = None
    finished_at: Optional[str] = None
    result: Optional[dict] = None
    error: Optional[str] = None


class SnowflakeConnection(BaseModel):
    """Snowflake connection details — pass in every request that touches Snowflake."""
    account: str = Field(..., description="Snowflake account identifier, e.g. xy12345.us-east-1")
    user: str = Field(..., description="Snowflake username")
    warehouse: str = Field(..., description="Snowflake virtual warehouse name")
    database: str = Field(..., description="Snowflake database name")
    role: str = Field("SYSADMIN", description="Snowflake role")
    schema_: str = Field("BRONZE", alias="schema", description="Bronze schema name")
    silver_schema: str = Field("SILVER", description="Silver schema name")
    gold_schema: str = Field("GOLD", description="Gold schema name")

    # Auth — supply exactly one of the three below
    pat_token: Optional[str] = Field(None, description="Programmatic Access Token (recommended)")
    password: Optional[str] = Field(None, description="Password (legacy)")
    private_key_path: Optional[str] = Field(None, description="Path to RSA private key (.p8) on the server")
    private_key_passphrase: Optional[str] = Field(None, description="Passphrase for the private key (if any)")

    model_config = {"populate_by_name": True}


class ParseRequest(BaseModel):
    input_path: Optional[str] = Field(
        None, description="Server-side path to PBIX metadata JSON. "
                           "Omit if uploading a file via /parse/upload instead."
    )


class MatchTablesRequest(BaseModel):
    snowflake: SnowflakeConnection
    bronze_schema: Optional[str] = Field(None, description="Override the Bronze schema (defaults to snowflake.schema)")
    manual_overrides: Optional[Dict[str, str]] = Field(
        None, description="Optional {pbix_table_name: bronze_table_name} map to force-resolve specific tables."
    )


class BronzeTablesRequest(BaseModel):
    snowflake: SnowflakeConnection
    schema_name: Optional[str] = Field(None, alias="schema")

    model_config = {"populate_by_name": True}


class ManualMatchRequest(BaseModel):
    snowflake: SnowflakeConnection
    overrides: Dict[str, str] = Field(..., description="{pbix_table_name: bronze_table_name}")


class DeployRequest(BaseModel):
    snowflake: SnowflakeConnection
    layer: str = Field("all", pattern="^(silver|gold|all)$")
    force: bool = Field(False, description="If true, actually executes SQL against Snowflake (not dry-run).")


class TranslateSilverRequest(BaseModel):
    pass


class TranslateGoldRequest(BaseModel):
    pass


class ValidateRequest(BaseModel):
    snowflake: SnowflakeConnection


class ReportRequest(BaseModel):
    pass


class RunAllRequest(BaseModel):
    snowflake: Optional[SnowflakeConnection] = Field(
        None, description="Snowflake connection — required if deploy=true or run_validation=true."
    )
    input_path: Optional[str] = None
    bronze_schema: Optional[str] = None
    deploy: bool = False
    force: bool = False
    run_validation: bool = Field(False, description="Run the validation stage after deploy")
