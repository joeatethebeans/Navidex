"""The inbox retrieval plugin's HTTP API (Melodex retrieval contract v1.0)."""

from __future__ import annotations

from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.responses import JSONResponse

from .contract import (CONTRACT_VERSION, FetchRequest, HealthRequest, Jobs, SearchRequest, dump, env, error,
                       staging_directory)
from .inbox import AUDIO, Inbox

VERSION = "0.1.0"
STAGING = env("STAGING_ROOT", "/staging")

SETTINGS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "keepOriginals": {
            "type": "boolean", "default": False, "title": "Keep originals",
            "description": "Copy files out of the inbox instead of moving them.",
        },
        "minScore": {
            "type": "number", "minimum": 0, "maximum": 1, "default": 0.6, "title": "Lowest match score",
            "description": "Files that match the request less well than this aren't offered.",
        },
    },
}

MANIFEST: dict[str, Any] = {
    "id": "melodex.retrieval.inbox",
    "name": "Inbox folder",
    "version": VERSION,
    "type": "retrieval",
    "contractVersion": CONTRACT_VERSION,
    "description": "Offers audio files you drop into a folder, matched to requests by their tags.",
    "capabilities": ["fetch-track", "fetch-album"],
    "settingsSchema": SETTINGS_SCHEMA,
    "access": {"network": [], "files": ["inbox (read, and write when moving files out)", "staging (write)"]},
    "retrieval": {"formats": sorted(e.lstrip(".") for e in AUDIO), "lossless": True},
}

app = FastAPI(title="Melodex inbox retriever", version=VERSION)
jobs = Jobs(int(env("PARALLEL_FETCHES", "2")))
inbox = Inbox(env("INBOX_ROOT", "/inbox"))


@app.exception_handler(HTTPException)
async def contract_error(_: Request, e: HTTPException) -> JSONResponse:
    body = e.detail if isinstance(e.detail, dict) else {"error": str(e.detail)}
    return JSONResponse(body, status_code=e.status_code)


@app.get("/v1/manifest")
def manifest() -> dict[str, Any]:
    return MANIFEST


@app.post("/v1/health")
def health(_: HealthRequest) -> dict[str, Any]:
    if not inbox.root.is_dir():
        return {"status": "unhealthy", "message": f"The inbox folder {inbox.root} isn't mounted."}
    count = len(inbox.files())
    return {"status": "healthy", "message": f"{count} audio file{'' if count == 1 else 's'} in the inbox"}


@app.post("/v1/search")
def search(body: SearchRequest) -> dict[str, Any]:
    if not inbox.root.is_dir():
        raise error(503, f"The inbox folder {inbox.root} isn't mounted.", "source-unavailable")
    return {"candidates": dump(inbox.search(body.target, max(1, min(body.limit, 25)), body.settings))}


@app.post("/v1/fetch", status_code=202)
def fetch(body: FetchRequest) -> dict[str, Any]:
    directory = staging_directory(STAGING, body.staging_directory)
    job = jobs.start(body.job_id, lambda j: inbox.fetch(j, body.candidate.candidate_id, directory, body.settings))
    return job.status()


@app.get("/v1/fetch/{job_id}")
def status(job_id: str) -> dict[str, Any]:
    if (job := jobs.get(job_id)) is None:
        raise error(404, "No such job; the plugin may have restarted.", "unknown-job")
    return job.status()
