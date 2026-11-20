"""
api/powerbi/workspaces.py
Lists the logged-in user's Power BI workspaces, with their reports and
datasets nested inline. Uses the SSO access_token from the session —
no service principal / admin credentials needed.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "src"))

import requests
import jwt
from fastapi import APIRouter, Request, HTTPException, Body
from config import config

router = APIRouter( tags=["powerbi-workspaces"])

def _user(request: Request):
    auth = request.headers.get("Authorization", "")
    if not auth.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Migration bearer token required")
    try:
        claims = jwt.decode(auth[7:], config.powerbi.migration_signing_key, algorithms=["HS256"],
            issuer=config.powerbi.migration_authority, audience=config.powerbi.migration_audience)
    except Exception:
        raise HTTPException(status_code=401, detail="Invalid or expired migration token")
    subject = claims.get("sub")
    from powerbi.auth import _sessions
    session = _sessions.get(subject)
    if not session or session["expires_at"] <= __import__("time").time():
        raise HTTPException(status_code=401, detail="Microsoft session expired; sign in again")
    return subject, session


@router.get("/workspaces")
def get_workspaces(request: Request):
    subject, session = _user(request)
    access_token = session["access_token"]

    headers = {"Authorization": f"Bearer {access_token}"}

    ws_resp = requests.get(f"{config.powerbi.api_base}/groups", headers=headers, timeout=30)
    if ws_resp.status_code != 200:
        raise HTTPException(status_code=ws_resp.status_code, detail=ws_resp.text)

    workspaces = ws_resp.json().get("value", [])

    for ws in workspaces:
        workspace_id = ws["id"]

        r_resp = requests.get(
            f"{config.powerbi.api_base}/groups/{workspace_id}/reports", headers=headers, timeout=15
        )
        ws["reports"] = r_resp.json().get("value", []) if r_resp.status_code == 200 else []

        d_resp = requests.get(
            f"{config.powerbi.api_base}/groups/{workspace_id}/datasets", headers=headers, timeout=15
        )
        ws["datasets"] = d_resp.json().get("value", []) if d_resp.status_code == 200 else []

    return {"count": len(workspaces), "workspaces": workspaces}

@router.post("/workspaces/select")
def select_workspace(request: Request, payload: dict = Body(...)):
    _, session = _user(request)
    workspace_id = payload.get("workspace_id")
    if not workspace_id:
        raise HTTPException(status_code=400, detail="workspace_id is required")
    headers = {"Authorization": f"Bearer {session['access_token']}"}
    resp = requests.get(f"{config.powerbi.api_base}/groups/{workspace_id}", headers=headers, timeout=20)
    if resp.status_code != 200:
        raise HTTPException(status_code=resp.status_code, detail="Workspace is not accessible to this user")
    session["workspace_id"] = workspace_id
    return {"workspace_id": workspace_id, "workspace": resp.json()}
@router.post("/workspaces/add-sp")
def add_service_principal_to_workspace(request: Request, payload: dict = Body(...)):
    _, session = _user(request)
    if not config.powerbi.sp_object_id:
        raise HTTPException(status_code=500, detail="SP_OBJECT_ID not configured in .env yet.")

    access_token = session["access_token"]

    workspace_id = payload.get("workspace_id")
    if not workspace_id:
        raise HTTPException(status_code=400, detail="workspace_id is required")

    headers = {"Authorization": f"Bearer {access_token}", "Content-Type": "application/json"}
    add_payload = {
        "identifier": config.powerbi.sp_object_id,
        "principalType": "App",
        "groupUserAccessRight": "Admin",
    }

    resp = requests.post(
        f"{config.powerbi.api_base}/groups/{workspace_id}/users",
        headers=headers, json=add_payload, timeout=30,
    )

    if resp.status_code in (200, 201, 204):
        return {
            "status": "success",
            "message": "Service principal added as Admin to workspace",
            "workspace_id": workspace_id,
        }

    try:
        error_detail = resp.json()
    except Exception:
        error_detail = resp.text

    # Already a member — treat as success, not failure.
    error_code = (
        error_detail.get("error", {}).get("code")
        if isinstance(error_detail, dict) else None
    )
    if resp.status_code == 400 and error_code == "AddingAlreadyExistsGroupUserNotSupportedError":
        return {
            "status": "success",
            "message": "Service principal already has access to this workspace",
            "workspace_id": workspace_id,
        }

    if resp.status_code == 403:
        raise HTTPException(
            status_code=403,
            detail={
                "error": "Forbidden",
                "reason": (
                    "Ensure you are a Workspace Admin, and SP_OBJECT_ID is the Enterprise "
                    "Application's Object ID, not the App Registration's Client ID."
                ),
                "ms_response": error_detail,
            },
        )
    raise HTTPException(status_code=resp.status_code, detail=error_detail)
