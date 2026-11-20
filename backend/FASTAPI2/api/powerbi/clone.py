"""
api/powerbi/clone.py
- clone_report: uses Power BI's native Clone API. Works reliably, but the
  clone shares the SAME dataset as the original (Power BI limitation).
- upload_report: uploads a .pbix file from the user's device, creating a
  brand new report AND a brand new dataset. This is the reliable path to
  an independent dataset — works because a real uploaded .pbix always has
  an actual workbook behind it.
"""

import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "src"))

import requests
from fastapi import APIRouter, Request, HTTPException, Body, UploadFile, File, Form
from config import config

router = APIRouter(tags=["powerbi-clone"])

IMPORT_POLL_INTERVAL_SECONDS = 2
IMPORT_MAX_WAIT_SECONDS = 180


# ---------------------------------------------------------------------------
# Shared helpers (used by upload_report)
# ---------------------------------------------------------------------------

def _start_import(headers: dict, workspace_id: str, pbix_bytes: bytes, dataset_name: str) -> str:
    files = {"file": (f"{dataset_name}.pbix", pbix_bytes, "application/octet-stream")}
    resp = requests.post(
        f"{config.powerbi.api_base}/groups/{workspace_id}/imports",
        headers=headers,
        params={"datasetDisplayName": dataset_name, "nameConflict": "CreateOrOverwrite"},
        files=files,
        timeout=120,
    )
    if resp.status_code not in (200, 202):
        raise HTTPException(status_code=resp.status_code, detail=f"Import start failed: {resp.text}")
    return resp.json()["id"]


def _wait_for_import(headers: dict, workspace_id: str, import_id: str) -> dict:
    elapsed = 0
    status_url = f"{config.powerbi.api_base}/groups/{workspace_id}/imports/{import_id}"
    while elapsed < IMPORT_MAX_WAIT_SECONDS:
        resp = requests.get(status_url, headers=headers, timeout=15)
        if resp.status_code != 200:
            raise HTTPException(status_code=resp.status_code, detail=f"Import status check failed: {resp.text}")
        data = resp.json()
        state = data.get("importState")
        if state == "Succeeded":
            return data
        if state == "Failed":
            raise HTTPException(status_code=500, detail=f"Import failed: {data}")
        time.sleep(IMPORT_POLL_INTERVAL_SECONDS)
        elapsed += IMPORT_POLL_INTERVAL_SECONDS
    raise HTTPException(status_code=504, detail="Import timed out")


# ---------------------------------------------------------------------------
# Clone an existing workspace report (native API — shares original dataset)
# ---------------------------------------------------------------------------

@router.post("/workspaces/{workspace_id}/reports/{report_id}/clone")
def clone_report(request: Request, workspace_id: str, report_id: str, payload: dict = Body(default={})):
    access_token = request.session.get("access_token")
    if not access_token:
        raise HTTPException(status_code=401, detail="Not logged in")

    headers = {"Authorization": f"Bearer {access_token}", "Content-Type": "application/json"}
    clone_body = {}
    if payload.get("name"):
        clone_body["name"] = payload["name"]
    if payload.get("target_workspace_id"):
        clone_body["targetWorkspaceId"] = payload["target_workspace_id"]

    clone_url = f"{config.powerbi.api_base}/groups/{workspace_id}/reports/{report_id}/Clone"
    resp = requests.post(clone_url, headers=headers, json=clone_body, timeout=30)

    if resp.status_code not in (200, 201):
        raise HTTPException(status_code=resp.status_code, detail=resp.text)

    cloned = resp.json()
    return {
        "message": "Report cloned successfully",
        "report_id": cloned.get("id"),
        "dataset_id": cloned.get("datasetId"),
        "report_name": cloned.get("name"),
        "workspace_id": payload.get("target_workspace_id", workspace_id),
    }


# ---------------------------------------------------------------------------
# Upload a .pbix file from the user's device (new report + new dataset)
# ---------------------------------------------------------------------------

@router.post("/workspaces/{workspace_id}/reports/upload")
async def upload_report(
    request: Request,
    workspace_id: str,
    file: UploadFile = File(...),
    name: str = Form(default=None),
):
    if not file.filename.lower().endswith(".pbix"):
        raise HTTPException(status_code=400, detail="Expected a .pbix file")

    access_token = request.session.get("access_token")
    if not access_token:
        raise HTTPException(status_code=401, detail="Not logged in")

    pbix_bytes = await file.read()
    display_name = name or Path(file.filename).stem

    headers = {"Authorization": f"Bearer {access_token}"}
    import_id = _start_import(headers, workspace_id, pbix_bytes, display_name)
    result = _wait_for_import(headers, workspace_id, import_id)

    new_reports = result.get("reports", [])
    new_datasets = result.get("datasets", [])

    return {
        "message": "Report uploaded successfully",
        "report_id": new_reports[0]["id"] if new_reports else None,
        "report_name": new_reports[0]["name"] if new_reports else display_name,
        "dataset_id": new_datasets[0]["id"] if new_datasets else None,
        "workspace_id": workspace_id,
    }