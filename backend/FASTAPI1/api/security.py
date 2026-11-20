"""Backend-issued migration tickets and request scoped artifact isolation."""
import base64
import hashlib
import hmac
import json
import os
import re
import time
from contextvars import ContextVar
from pathlib import Path

from fastapi import HTTPException, Request

_claims: ContextVar[dict | None] = ContextVar("migration_claims", default=None)
_migration_id = re.compile(r"^[0-9a-fA-F-]{36}$")


def current_claims() -> dict:
    claims = _claims.get()
    if not claims:
        raise RuntimeError("No authenticated migration context")
    return claims


def workspace_root() -> Path:
    claims = current_claims()
    migration_id = claims["migration_id"]
    if not _migration_id.fullmatch(migration_id):
        raise RuntimeError("Invalid migration identifier")
    root = Path(os.getenv("MIGRATION_DATA_DIR", Path(__file__).resolve().parent.parent / "data"))
    path = (root / migration_id).resolve()
    if root.resolve() not in path.parents:
        raise RuntimeError("Invalid migration workspace")
    path.mkdir(parents=True, exist_ok=True)
    return path


def _decode_ticket(token: str) -> dict:
    secret = os.getenv("MIGRATION_TICKET_SECRET", "")
    if len(secret) < 32:
        raise HTTPException(503, "Migration authentication is not configured")
    try:
        encoded, signature = token.split(".", 1)
        expected = hmac.new(secret.encode(), encoded.encode(), hashlib.sha256).digest()
        supplied = base64.urlsafe_b64decode(signature + "=" * (-len(signature) % 4))
        if not hmac.compare_digest(expected, supplied):
            raise ValueError("signature")
        claims = json.loads(base64.urlsafe_b64decode(encoded + "=" * (-len(encoded) % 4)))
        if (not isinstance(claims.get("owner_id"), str) or not claims["owner_id"]
                or not isinstance(claims.get("migration_id"), str)
                or not _migration_id.fullmatch(claims["migration_id"])
                or int(claims.get("exp", 0)) < int(time.time())):
            raise ValueError("claims")
        return claims
    except Exception:
        raise HTTPException(401, "Invalid migration authorization") from None


def issue_ticket(owner_id: str, migration_id: str, secret: str, ttl_seconds: int = 300) -> str:
    """Test/contract helper; the production issuer is the authenticated .NET backend."""
    claims = {"owner_id": owner_id, "migration_id": migration_id,
              "exp": int(time.time()) + ttl_seconds}
    encoded = base64.urlsafe_b64encode(json.dumps(claims, separators=(",", ":")).encode()).decode().rstrip("=")
    signature = hmac.new(secret.encode(), encoded.encode(), hashlib.sha256).digest()
    return encoded + "." + base64.urlsafe_b64encode(signature).decode().rstrip("=")


async def authenticate_migration(request: Request, call_next):
    if request.url.path == "/health":
        return await call_next(request)
    authorization = request.headers.get("authorization", "")
    if not authorization.startswith("Bearer "):
        return __import__("starlette.responses", fromlist=["JSONResponse"]).JSONResponse(
            {"detail": "Migration authorization required"}, status_code=401)
    try:
        claims = _decode_ticket(authorization[7:])
    except HTTPException as exc:
        return __import__("starlette.responses", fromlist=["JSONResponse"]).JSONResponse(
            {"detail": exc.detail}, status_code=exc.status_code)
    token = _claims.set(claims)
    from config import set_workspace, reset_workspace
    workspace_token = set_workspace(workspace_root())
    try:
        return await call_next(request)
    finally:
        reset_workspace(workspace_token)
        _claims.reset(token)
