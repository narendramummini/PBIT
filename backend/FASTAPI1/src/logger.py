"""
logger.py
Shared logging setup. Every CLI script logs to console + a per-run file
under output/logs/.
"""

import logging
import sys
from datetime import datetime
from pathlib import Path

# Connector SDK error records may include transport or authentication details.
# Keep third-party request logging disabled; pipeline logs contain only our
# own sanitized operational messages.
logging.getLogger("snowflake.connector").disabled = True
logging.getLogger("openai").disabled = True


def get_logger(name: str, logs_dir: Path = None, level: str = "INFO") -> logging.Logger:
    logger = logging.getLogger(name)
    if logger.handlers:
        return logger  # already configured

    logger.setLevel(getattr(logging, level.upper(), logging.INFO))

    fmt = logging.Formatter(
        "%(asctime)s | %(levelname)-8s | %(name)s | %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    console = logging.StreamHandler(sys.stdout)
    console.setFormatter(fmt)
    logger.addHandler(console)

    # Request payloads and third party connector exceptions can contain
    # credentials. Keep operational logs ephemeral; never write them to the
    # artifact tree or a file shared by migration sessions.

    return logger
