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
from dataclasses import dataclass, field
from datetime import datetime
from enum import Enum
from pathlib import Path
from typing import Any, Callable, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))
from logger import get_logger
from config import config

log = get_logger("api.jobs", config.logs_dir, config.pipeline.log_level)


class JobStatus(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    SUCCEEDED = "succeeded"
    FAILED = "failed"


@dataclass
class Job:
    id: str
    stage: str
    status: JobStatus = JobStatus.PENDING
    progress_message: str = ""
    created_at: str = field(default_factory=lambda: datetime.utcnow().isoformat() + "Z")
    started_at: Optional[str] = None
    finished_at: Optional[str] = None
    result: Optional[Any] = None
    error: Optional[str] = None
    traceback: Optional[str] = None

    def to_dict(self) -> dict:
        return {
            "job_id": self.id,
            "stage": self.stage,
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

    async def create(self, stage: str) -> Job:
        job = Job(id=str(uuid.uuid4()), stage=stage)
        async with self._lock:
            self._jobs[job.id] = job
        return job

    def get(self, job_id: str) -> Optional[Job]:
        return self._jobs.get(job_id)

    def list(self, stage: Optional[str] = None) -> list:
        jobs = list(self._jobs.values())
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
        job.started_at = datetime.utcnow().isoformat() + "Z"

        def progress_cb(msg: str):
            job.progress_message = msg
            log.info(f"[job {job.id[:8]}] {job.stage}: {msg}")

        try:
            result = await asyncio.to_thread(fn, *args, progress_cb=progress_cb, **kwargs)
            job.result = result
            job.status = JobStatus.SUCCEEDED
            job.progress_message = "Completed."
        except Exception as e:
            job.status = JobStatus.FAILED
            job.error = str(e)
            job.traceback = traceback.format_exc()
            job.progress_message = f"Failed: {e}"
            log.error(f"[job {job.id[:8]}] {job.stage} failed: {e}\n{job.traceback}")
        finally:
            job.finished_at = datetime.utcnow().isoformat() + "Z"


job_manager = JobManager()
