"""
azure_openai_client.py
Thin wrapper around the Azure OpenAI SDK used by table_matcher.py,
mquery_translator.py, and dax_translator.py.

Centralizes: client construction, JSON-mode prompting with robust parsing,
and retry-on-failure behavior.
"""

import json
import re
import sys
import time
from pathlib import Path
from typing import Optional

from openai import AzureOpenAI, APIError, APIConnectionError, RateLimitError

sys.path.insert(0, str(Path(__file__).resolve().parent))
from config import config
from logger import get_logger

log = get_logger("azure_openai_client", config.logs_dir, config.pipeline.log_level)

_client: Optional[AzureOpenAI] = None


def get_client() -> AzureOpenAI:
    global _client
    if _client is None:
        az = config.azure
        _client = AzureOpenAI(
            azure_endpoint=az.endpoint,
            api_key=az.api_key,
            api_version=az.api_version,
        )
    return _client


def _strip_code_fences(text: str) -> str:
    text = text.strip()
    text = re.sub(r"^```(json)?", "", text).strip()
    text = re.sub(r"```$", "", text).strip()
    return text


def chat_json(system_prompt: str, user_prompt: str, deployment: Optional[str] = None,
              max_retries: int = 2, temperature: float = 0.0) -> dict:
    """Call Azure OpenAI expecting a JSON object back. Retries on transient
    errors and on malformed JSON (re-prompting once)."""
    az = config.azure
    deployment = deployment or az.deployment
    client = get_client()

    last_err = None
    for attempt in range(1, max_retries + 1):
        try:
            resp = client.chat.completions.create(
                model=deployment,
                temperature=temperature,
                messages=[
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt},
                ],
                response_format={"type": "json_object"},
            )
            raw = resp.choices[0].message.content
            cleaned = _strip_code_fences(raw)
            return json.loads(cleaned)
        except (json.JSONDecodeError,) as e:
            last_err = e
            log.warning(f"Attempt {attempt}: model did not return valid JSON ({e}). Retrying...")
        except (RateLimitError,) as e:
            last_err = e
            wait = 2 ** attempt
            log.warning(f"Attempt {attempt}: rate limited. Waiting {wait}s...")
            time.sleep(wait)
        except (APIConnectionError, APIError) as e:
            last_err = e
            log.warning(f"Attempt {attempt}: API error ({e}). Retrying...")
            time.sleep(1.5 * attempt)

    log.error(f"Azure OpenAI call failed after {max_retries} attempts: {last_err}")
    raise RuntimeError(f"Azure OpenAI call failed: {last_err}")


def chat_text(system_prompt: str, user_prompt: str, deployment: Optional[str] = None,
              max_retries: int = 2, temperature: float = 0.0) -> str:
    """Call Azure OpenAI expecting free-text back (used for SQL generation
    where we want raw SQL, not JSON)."""
    az = config.azure
    deployment = deployment or az.deployment
    client = get_client()

    last_err = None
    for attempt in range(1, max_retries + 1):
        try:
            resp = client.chat.completions.create(
                model=deployment,
                temperature=temperature,
                messages=[
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt},
                ],
            )
            text = resp.choices[0].message.content.strip()
            text = re.sub(r"^```(sql|SQL)?\s*", "", text)
            text = re.sub(r"```\s*$", "", text)
            return text.strip()
        except (RateLimitError,) as e:
            last_err = e
            wait = 2 ** attempt
            log.warning(f"Attempt {attempt}: rate limited. Waiting {wait}s...")
            time.sleep(wait)
        except (APIConnectionError, APIError) as e:
            last_err = e
            log.warning(f"Attempt {attempt}: API error ({e}). Retrying...")
            time.sleep(1.5 * attempt)

    log.error(f"Azure OpenAI call failed after {max_retries} attempts: {last_err}")
    raise RuntimeError(f"Azure OpenAI call failed: {last_err}")


if __name__ == "__main__":
    # Quick smoke test
    result = chat_json(
        "You respond only in JSON.",
        "Return {\"status\": \"ok\", \"message\": \"Azure OpenAI connection works\"}",
    )
    print(result)