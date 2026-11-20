"""
config.py
Centralized configuration loader for the PBIX -> Snowflake migration pipeline.
Reads from .env and exposes a single Config object used by every CLI script.
"""

import os
import sys
from contextvars import ContextVar
from dataclasses import dataclass, field
from pathlib import Path

from dotenv import load_dotenv

PROJECT_ROOT = Path(__file__).resolve().parent.parent
ENV_PATH = PROJECT_ROOT / ".env"
_workspace = ContextVar("b2_workspace", default=None)


def set_workspace(path):
    """Set the artifact root for the current request/job context."""
    return _workspace.set(Path(path).resolve())


def reset_workspace(token):
    _workspace.reset(token)

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


class Config:
    """Lazily-built top level config. Instantiate fields only when accessed
    so commands that don't need Snowflake/Azure (e.g. parse-only) don't fail
    on missing credentials."""

    def __init__(self):
        self._snowflake = None
        self._azure = None
        self.pipeline = PipelineConfig()
        (PROJECT_ROOT / "output").mkdir(parents=True, exist_ok=True)

    @property
    def output_dir(self):
        return _workspace.get() or (PROJECT_ROOT / "output")

    def _dir(self, *parts):
        path = self.output_dir.joinpath(*parts)
        path.mkdir(parents=True, exist_ok=True)
        return path

    @property
    def mapping_dir(self): return self._dir("mapping")
    @property
    def sql_silver_dir(self): return self._dir("sql", "silver")
    @property
    def sql_gold_dir(self): return self._dir("sql", "gold")
    @property
    def reports_dir(self): return self._dir("reports")
    @property
    def logs_dir(self): return self._dir("logs")

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


config = Config()
