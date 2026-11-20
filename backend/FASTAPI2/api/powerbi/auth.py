import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "src"))

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import RedirectResponse
import msal
import secrets
import time
import jwt
import hashlib
import logging
import re
import os
import socket
from urllib.parse import urlsplit
import requests
from fastapi import Body
from pydantic import BaseModel
from config import config

router = APIRouter( tags=["powerbi-auth"])
log = logging.getLogger("auth.powerbi")

_sessions: dict[str, dict] = {}
_codes: dict[str, tuple[str, float]] = {}
_refresh_tokens: dict[str, tuple[str, float]] = {}

def _issue_migration_token(user: dict) -> tuple[str, int]:
    now = int(time.time())
    try:
        lifetime = int(os.environ.get("MIGRATION_ACCESS_TOKEN_SECONDS", "1800"))
    except ValueError:
        lifetime = 1800
    lifetime = min(max(lifetime, 60), 1800)
    token = jwt.encode({"sub": user["subject"], "name": user["name"], "email": user["email"],
        "iss": config.powerbi.migration_authority, "aud": config.powerbi.migration_audience,
        "iat": now, "nbf": now, "exp": now + lifetime}, _signing_key(), algorithm="HS256")
    return token, lifetime

def _new_refresh_token(subject: str, expires_at: float) -> str:
    token = secrets.token_urlsafe(48)
    _refresh_tokens[hashlib.sha256(token.encode("utf-8")).hexdigest()] = (subject, expires_at)
    return token

def _msal_app():
    p = config.powerbi
    try:
        # Requests (used by MSAL) honors proxy variables automatically. A
        # stale localhost proxy should not make Microsoft sign-in fail when
        # there is no proxy listener; keep configured working proxies intact.
        session = requests.Session()
        proxy_values = [
            os.environ.get(name) for name in
            ("HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy")
            if os.environ.get(name)
        ]
        for proxy in proxy_values:
            parsed_proxy = urlsplit(proxy if "://" in proxy else f"http://{proxy}")
            if parsed_proxy.hostname not in ("localhost", "127.0.0.1", "::1"):
                continue
            try:
                with socket.create_connection((parsed_proxy.hostname, parsed_proxy.port or 80), timeout=0.25):
                    pass
            except OSError:
                session.trust_env = False
                session.verify = os.environ.get("REQUESTS_CA_BUNDLE") or os.environ.get("CURL_CA_BUNDLE") or True
                log.warning("Ignoring an unavailable loopback proxy for Microsoft authentication")
                break
        return msal.ConfidentialClientApplication(
            p.client_id,
            authority=f"https://login.microsoftonline.com/{p.tenant_id}",
            client_credential=p.client_secret,
            http_client=session,
        )
    except Exception as exc:
        # Never log MSAL's exception text: it can contain request details.
        log.error("Microsoft OAuth client initialization failed (%s)", type(exc).__name__)
        raise


def _login_config_problem():
    p = config.powerbi
    missing = [name for name, value in (
        ("TENANT_ID", p.tenant_id), ("CLIENT_ID", p.client_id),
        ("CLIENT_SECRET", p.client_secret), ("REDIRECT_URI", p.redirect_uri),
        ("SESSION_SECRET_KEY", p.session_secret_key),
    ) if not value or not value.strip()]
    if missing:
        return "Missing required OAuth configuration: " + ", ".join(missing)
    uuid_pattern = r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
    if not re.fullmatch(uuid_pattern, p.tenant_id):
        return "TENANT_ID must be a Microsoft Entra tenant GUID"
    if not re.fullmatch(uuid_pattern, p.client_id):
        return "CLIENT_ID must be an application (client) ID GUID"
    parsed = urlsplit(p.redirect_uri)
    if parsed.scheme not in ("http", "https") or not parsed.netloc or parsed.query or parsed.fragment:
        return "REDIRECT_URI must be an absolute HTTP(S) callback URL without query or fragment"
    if parsed.scheme != "https" and parsed.hostname not in ("localhost", "127.0.0.1", "::1"):
        return "REDIRECT_URI must use HTTPS outside localhost"
    if len(p.session_secret_key) < 32:
        return "SESSION_SECRET_KEY must contain at least 32 characters"
    return None

def _signing_key():
    key = config.powerbi.migration_signing_key
    if len(key) < 32:
        raise HTTPException(status_code=503, detail="Migration auth signing key is not configured")
    return key


@router.get("/login")
def login(request: Request):
    problem = _login_config_problem()
    if problem:
        log.error("Microsoft OAuth configuration invalid: %s", problem)
        raise HTTPException(status_code=503, detail=problem)
    state = secrets.token_urlsafe(32)
    request.session.clear()
    request.session["oauth_state"] = state
    try:
        auth_url = _msal_app().get_authorization_request_url(
            scopes=config.powerbi.scope,
            redirect_uri=config.powerbi.redirect_uri,
            state=state,
        )
    except Exception as exc:
        log.error("Microsoft authorization URL creation failed (%s)", type(exc).__name__)
        raise HTTPException(
            status_code=503,
            detail="Microsoft sign-in is temporarily unavailable; check auth-service outbound access to login.microsoftonline.com",
        ) from exc
    return RedirectResponse(auth_url, status_code=302)


@router.get("/auth/callback")
def auth_callback(request: Request, code: str | None = None, state: str | None = None, error: str | None = None):
    expected = request.session.pop("oauth_state", None)
    if not expected or not state or not secrets.compare_digest(expected, state):
        raise HTTPException(status_code=400, detail="Invalid or expired OAuth state")
    frontend = config.powerbi.frontend_url.rstrip("/")
    if error:
        return RedirectResponse(f"{frontend}/powerbi-auth-success?error=signin_cancelled", status_code=303)
    if not code:
        raise HTTPException(status_code=400, detail="Missing OAuth authorization code")
    try:
        token = _msal_app().acquire_token_by_authorization_code(
            code=code,
            scopes=config.powerbi.scope,
            redirect_uri=config.powerbi.redirect_uri,
        )
    except Exception as exc:
        log.error("Microsoft authorization-code exchange failed (%s)", type(exc).__name__)
        return RedirectResponse(f"{frontend}/powerbi-auth-success?error=exchange_unavailable", status_code=303)

    if "access_token" not in token:
        log.warning("Microsoft authorization-code exchange returned no access token")
        return RedirectResponse(f"{frontend}/powerbi-auth-success?error=exchange_failed", status_code=303)

    access_token = token["access_token"]
    user_claims = token.get("id_token_claims", {})
    subject = f"{user_claims.get('tid', '')}:{user_claims.get('oid', '')}"
    if subject == ":":
        log.warning("Microsoft token response did not include required identity claims")
        return RedirectResponse(f"{frontend}/powerbi-auth-success?error=identity_incomplete", status_code=303)
    _sessions[subject] = {"access_token": access_token, "expires_at": time.time() + int(token.get("expires_in", 3600)),
                          "workspace_id": None, "name": user_claims.get("name", "User"),
                          "email": user_claims.get("preferred_username", "")}
    handoff = secrets.token_urlsafe(32)
    _codes[handoff] = (subject, time.time() + 90)
    return RedirectResponse(f"{frontend}/powerbi-auth-success?code={handoff}", status_code=303)

class ExchangeRequest(BaseModel):
    code: str

@router.post("/auth/exchange")
def exchange(payload: ExchangeRequest):
    entry = _codes.pop(payload.code, None)
    if not entry or entry[1] < time.time():
        raise HTTPException(status_code=401, detail="Invalid or expired sign-in code")
    subject = entry[0]
    user = _sessions.get(subject)
    if not user or user["expires_at"] <= time.time():
        raise HTTPException(status_code=401, detail="Microsoft session expired")
    token_user = {**user, "subject": subject}
    migration_token, expires_in = _issue_migration_token(token_user)
    refresh_token = _new_refresh_token(subject, user["expires_at"])
    return {"api_access_token": migration_token, "refresh_token": refresh_token, "expires_in": expires_in,
            # Client ID and tenant ID are public identifiers needed to
            # initialize the browser MSAL client for delegated Fabric tokens.
            # Never return the confidential client secret here.
            "client_id": config.powerbi.client_id, "tenant_id": config.powerbi.tenant_id,
            "user": {"name": user["name"], "email": user["email"]}}

class RefreshRequest(BaseModel):
    refresh_token: str

@router.post("/auth/refresh")
def refresh(payload: RefreshRequest):
    token_hash = hashlib.sha256(payload.refresh_token.encode("utf-8")).hexdigest()
    entry = _refresh_tokens.pop(token_hash, None)
    if not entry or entry[1] <= time.time():
        raise HTTPException(status_code=401, detail="Refresh session expired; sign in again")
    subject, expires_at = entry
    user = _sessions.get(subject)
    if not user or user["expires_at"] <= time.time() or expires_at != user["expires_at"]:
        raise HTTPException(status_code=401, detail="Microsoft session expired; sign in again")
    token_user = {**user, "subject": subject}
    access_token, expires_in = _issue_migration_token(token_user)
    return {"api_access_token": access_token, "refresh_token": _new_refresh_token(subject, expires_at), "expires_in": expires_in}

@router.post("/internal/token-exchange")
def internal_token_exchange(request: Request, payload: dict = Body(...)):
    if request.headers.get("X-Internal-Auth") != config.powerbi.internal_service_key or not config.powerbi.internal_service_key:
        raise HTTPException(status_code=401, detail="Unauthorized")
    user = _sessions.get(payload.get("sub", ""))
    if not user or user["expires_at"] <= time.time():
        raise HTTPException(status_code=401, detail="Microsoft session expired")
    return {"access_token": user["access_token"]}
