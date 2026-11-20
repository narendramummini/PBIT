"""
config.py
Centralized configuration loader for the PBIX -> Snowflake migration pipeline.
Reads from .env and exposes a single Config object used by every CLI script.
"""

import os
import sys
from dataclasses import dataclass, field
from pathlib import Path

from dotenv import load_dotenv

PROJECT_ROOT = Path(__file__).resolve().parent.parent
ENV_PATH = PROJECT_ROOT / ".env"

if not ENV_PATH.exists():
    print(f"[WARN] No .env file found at {ENV_PATH}.")
    print(f"       Copy .env.example to .env and fill in your credentials.")
load_dotenv(dotenv_path=ENV_PATH)


def _get(key: str, default=None, required=False):
    val = os.getenv(key, default)
    if required and not val:
        print(f"[ERROR] Missing required environment variable: {key}")
        print(f"        Set it in {ENV_PATH}")
        sys.exit(1)
    return val


def _get_bool(key: str, default=False):
    val = os.getenv(key)
    if val is None:
        return default
    return val.strip().lower() in ("1", "true", "yes", "y")


def _get_float(key: str, default=0.0):
    val = os.getenv(key)
    if val is None:
        return default
    try:
        return float(val)
    except ValueError:
        return default


@dataclass
class SnowflakeConfig:
    account: str = field(default_factory=lambda: _get("SNOWFLAKE_ACCOUNT", ""))
    user: str = field(default_factory=lambda: _get("SNOWFLAKE_USER", ""))
    password: str = field(default_factory=lambda: _get("SNOWFLAKE_PASSWORD"))
    pat_token: str = field(default_factory=lambda: _get("SNOWFLAKE_PAT_TOKEN"))
    private_key_path: str = field(default_factory=lambda: _get("SNOWFLAKE_PRIVATE_KEY_PATH"))
    private_key_passphrase: str = field(default_factory=lambda: _get("SNOWFLAKE_PRIVATE_KEY_PASSPHRASE"))
    role: str = field(default_factory=lambda: _get("SNOWFLAKE_ROLE", "SYSADMIN"))
    warehouse: str = field(default_factory=lambda: _get("SNOWFLAKE_WAREHOUSE", ""))
    database: str = field(default_factory=lambda: _get("SNOWFLAKE_DATABASE", ""))
    schema: str = field(default_factory=lambda: _get("SNOWFLAKE_SCHEMA", "BRONZE"))
    silver_schema: str = field(default_factory=lambda: _get("SNOWFLAKE_SILVER_SCHEMA", "SILVER"))
    gold_schema: str = field(default_factory=lambda: _get("SNOWFLAKE_GOLD_SCHEMA", "GOLD"))


@dataclass
class AzureOpenAIConfig:
    endpoint: str = field(default_factory=lambda: _get("AZURE_OPENAI_ENDPOINT", required=True))
    api_key: str = field(default_factory=lambda: _get("AZURE_OPENAI_API_KEY", required=True))
    api_version: str = field(default_factory=lambda: _get("AZURE_OPENAI_API_VERSION", "2024-08-01-preview"))
    deployment: str = field(default_factory=lambda: _get("AZURE_OPENAI_DEPLOYMENT", "gpt-4o"))
    deployment_fast: str = field(default_factory=lambda: _get("AZURE_OPENAI_DEPLOYMENT_FAST", "gpt-4o-mini"))


@dataclass
class PipelineConfig:
    match_confidence_threshold: float = field(default_factory=lambda: _get_float("MATCH_CONFIDENCE_THRESHOLD", 0.75))
    interactive_review: bool = field(default_factory=lambda: _get_bool("INTERACTIVE_REVIEW", True))
    dry_run: bool = field(default_factory=lambda: _get_bool("DRY_RUN", True))
    log_level: str = field(default_factory=lambda: _get("LOG_LEVEL", "INFO"))

@dataclass
class PowerBIConfig:
    client_id: str = field(default_factory=lambda: _get("CLIENT_ID", ""))
    client_secret: str = field(default_factory=lambda: _get("CLIENT_SECRET", ""))
    tenant_id: str = field(default_factory=lambda: _get("TENANT_ID", ""))
    redirect_uri: str = field(default_factory=lambda: _get("REDIRECT_URI", ""))
    session_secret_key: str = field(default_factory=lambda: _get("SESSION_SECRET_KEY", ""))
    sp_object_id: str = field(default_factory=lambda: _get("SP_OBJECT_ID", ""))   # <-- new
    migration_authority: str = field(default_factory=lambda: _get("MIGRATION_AUTHORITY", "http://localhost:8000"))
    migration_audience: str = field(default_factory=lambda: _get("MIGRATION_AUDIENCE", "pbix-migration-api"))
    migration_signing_key: str = field(default_factory=lambda: _get("MIGRATION_SIGNING_KEY", ""))
    internal_service_key: str = field(default_factory=lambda: _get("AUTH_INTERNAL_SERVICE_KEY", ""))
    frontend_url: str = field(default_factory=lambda: _get("FRONTEND_URL", "http://localhost:8080"))
    scope: list = field(default_factory=lambda: ["https://analysis.windows.net/powerbi/api/.default"])
    api_base: str = "https://api.powerbi.com/v1.0/myorg"


class Config:
    """Lazily-built top level config. Instantiate fields only when accessed
    so commands that don't need Snowflake/Azure (e.g. parse-only) don't fail
    on missing credentials."""

    def __init__(self):
        self._snowflake = None
        self._azure = None
        self._powerbi = None 
        self.pipeline = PipelineConfig()
        self.output_dir = PROJECT_ROOT / "output"
        self.mapping_dir = self.output_dir / "mapping"
        self.sql_silver_dir = self.output_dir / "sql" / "silver"
        self.sql_gold_dir = self.output_dir / "sql" / "gold"
        self.reports_dir = self.output_dir / "reports"
        self.logs_dir = self.output_dir / "logs"
        for d in (self.mapping_dir, self.sql_silver_dir, self.sql_gold_dir,
                   self.reports_dir, self.logs_dir):
            d.mkdir(parents=True, exist_ok=True)

    @property
    def snowflake(self) -> SnowflakeConfig:
        if self._snowflake is None:
            self._snowflake = SnowflakeConfig()
        return self._snowflake

    @property
    def azure(self) -> AzureOpenAIConfig:
        if self._azure is None:
            self._azure = AzureOpenAIConfig()
        return self._azure

    @property
    def powerbi(self) -> PowerBIConfig:
        if self._powerbi is None:
            self._powerbi = PowerBIConfig()
        return self._powerbi

config = Config()
