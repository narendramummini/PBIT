"""
jobs.py
Lightweight in-memory async job manager. Each pipeline stage (parse, match,
translate, deploy, validate, report) runs as a background job so HTTP
requests return immediately with a job_id, and the client polls
GET /jobs/{job_id} for status/progress/result.

This is intentionally simple (in-process dict, no external queue/broker) —
fine for a single-instance API serving one migration project at a time.
If you need multi-worker/multi-instance support later, swap this for
Celery/RQ + Redis without changing the route handlers much.
"""

import asyncio
import sys
import traceback
import uuid
import os
import shutil
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from pathlib import Path
from typing import Any, Callable, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))
from logger import get_logger
from config import config
from security import current_claims

log = get_logger("api.jobs", config.logs_dir, config.pipeline.log_level)


def _utc_timestamp():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


class JobStatus(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    SUCCEEDED = "succeeded"
    FAILED = "failed"


@dataclass
class Job:
    id: str
    stage: str
    migration_id: str
    owner_id: str
    status: JobStatus = JobStatus.PENDING
    progress_message: str = ""
    created_at: str = field(default_factory=_utc_timestamp)
    started_at: Optional[str] = None
    finished_at: Optional[str] = None
    result: Optional[Any] = None
    error: Optional[str] = None
    traceback: Optional[str] = None

    def to_dict(self) -> dict:
        return {
            "job_id": self.id,
            "stage": self.stage,
            "migration_id": self.migration_id,
            "status": self.status.value,
            "progress_message": self.progress_message,
            "created_at": self.created_at,
            "started_at": self.started_at,
            "finished_at": self.finished_at,
            "result": self.result,
            "error": self.error,
        }


class JobManager:
    def __init__(self):
        self._jobs: Dict[str, Job] = {}
        self._lock = asyncio.Lock()
        self._migration_locks: Dict[str, threading.Lock] = {}
        self._migration_locks_guard = threading.Lock()

    async def create(self, stage: str) -> Job:
        claims = current_claims()
        job = Job(id=str(uuid.uuid4()), stage=stage,
                  migration_id=claims["migration_id"], owner_id=claims["owner_id"])
        async with self._lock:
            self._jobs[job.id] = job
        return job

    def get(self, job_id: str, migration_id: str, owner_id: str) -> Optional[Job]:
        job = self._jobs.get(job_id)
        if not job or job.migration_id != migration_id or job.owner_id != owner_id:
            return None
        return job

    def list(self, migration_id: str, owner_id: str, stage: Optional[str] = None) -> list:
        jobs = [j for j in self._jobs.values()
                if j.migration_id == migration_id and j.owner_id == owner_id]
        if stage:
            jobs = [j for j in jobs if j.stage == stage]
        jobs.sort(key=lambda j: j.created_at, reverse=True)
        return jobs

    async def update_progress(self, job_id: str, message: str):
        job = self._jobs.get(job_id)
        if job:
            job.progress_message = message
            log.info(f"[job {job_id[:8]}] {job.stage}: {message}")

    async def run(self, job: Job, fn: Callable, *args, **kwargs):
        """Execute fn(*args, progress_cb=..., **kwargs) in a worker thread
        (since the underlying pipeline code is sync: Snowflake connector,
        Azure OpenAI SDK, file I/O) and update job state as it goes."""
        job.status = JobStatus.RUNNING
        job.started_at = _utc_timestamp()

        def progress_cb(msg: str):
            job.progress_message = msg
            log.info(f"[job {job.id[:8]}] {job.stage}: {msg}")

        upload_dir = config.output_dir / "uploads" / job.id
        cancelled = threading.Event()
        with self._migration_locks_guard:
            migration_lock = self._migration_locks.setdefault(job.migration_id, threading.Lock())

        def execute_and_cleanup():
            with migration_lock:
                try:
                    if cancelled.is_set():
                        return None
                    return fn(*args, progress_cb=progress_cb, **kwargs)
                finally:
                    if upload_dir.exists():
                        shutil.rmtree(upload_dir, ignore_errors=True)

        try:
            timeout = max(1, int(os.getenv("MIGRATION_JOB_TIMEOUT_SECONDS", "900")))
            result = await asyncio.wait_for(
                asyncio.to_thread(execute_and_cleanup), timeout=timeout
            )
            job.result = result
            job.status = JobStatus.SUCCEEDED
            job.progress_message = "Completed."
        except asyncio.TimeoutError:
            cancelled.set()
            job.status = JobStatus.FAILED
            job.error = "Migration stage timed out."
            job.progress_message = "Timed out."
            log.error(f"[job {job.id[:8]}] {job.stage} timed out")
        except Exception as e:
            job.status = JobStatus.FAILED
            job.error = "Migration stage failed. Check the stage status and retry if appropriate."
            job.traceback = traceback.format_exc()
            job.progress_message = "Failed. Check the stage status and retry if appropriate."
            log.error(f"[job {job.id[:8]}] {job.stage} failed ({type(e).__name__})")
        finally:
            job.finished_at = _utc_timestamp()


job_manager = JobManager()
