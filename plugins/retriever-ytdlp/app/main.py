"""The yt-dlp retrieval plugin's HTTP API (Melodex retrieval contract v1.0)."""

from __future__ import annotations

import shutil
from typing import Any

import yt_dlp.version
from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.responses import JSONResponse

from . import youtube
from .contract import (CONTRACT_VERSION, FetchRequest, HealthRequest, Jobs, SearchRequest, dump, env, error,
                       staging_directory)

VERSION = "0.1.0"
STAGING = env("STAGING_ROOT", "/staging")

SETTINGS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "format": {
            "type": "string", "enum": youtube.FORMATS, "default": "opus", "title": "Format",
            "description": "Opus and m4a are YouTube's own audio, kept as it is. MP3 is re-encoded and loses a little quality.",
        },
        "bitrateKbps": {
            "type": "integer", "minimum": 96, "maximum": 320, "default": 320, "title": "MP3 bitrate",
            "description": "Only used for MP3.",
        },
        "includeVideos": {
            "type": "boolean", "default": True, "title": "Include music videos",
            "description": "Also offer uploads that aren't listed as songs, ranked below them.",
        },
        "minScore": {
            "type": "number", "minimum": 0, "maximum": 1, "default": 0.5, "title": "Lowest match score",
            "description": "Results that match the request less well than this aren't offered.",
        },
        "cookiesFile": {
            "type": "string", "title": "Cookies file",
            "description": "Path inside the container to a Netscape cookies file, for age-restricted songs.",
        },
    },
}

MANIFEST: dict[str, Any] = {
    "id": "melodex.retrieval.ytdlp",
    "name": "YouTube (yt-dlp)",
    "version": VERSION,
    "type": "retrieval",
    "contractVersion": CONTRACT_VERSION,
    "description": "Finds songs on YouTube Music and downloads their audio with yt-dlp.",
    "homepage": "https://github.com/yt-dlp/yt-dlp",
    "capabilities": ["fetch-track", "fetch-album", "cancel"],
    "settingsSchema": SETTINGS_SCHEMA,
    "access": {
        "network": ["music.youtube.com", "www.youtube.com", "*.googlevideo.com"],
        "files": ["staging (write)"],
    },
    "maxRequestsPerSecond": 2,
    "retrieval": {"formats": youtube.FORMATS, "minBitrateKbps": 96, "maxBitrateKbps": 320, "lossless": False},
}

app = FastAPI(title="Melodex yt-dlp retriever", version=VERSION)
jobs = Jobs(int(env("PARALLEL_FETCHES", "2")))


@app.exception_handler(HTTPException)
async def contract_error(_: Request, e: HTTPException) -> JSONResponse:
    body = e.detail if isinstance(e.detail, dict) else {"error": str(e.detail)}
    return JSONResponse(body, status_code=e.status_code)


def check_settings(settings: dict[str, Any]) -> None:
    if settings.get("format", "opus") not in youtube.FORMATS:
        raise error(400, f"format must be one of {', '.join(youtube.FORMATS)}.", "invalid-settings")


@app.get("/v1/manifest")
def manifest() -> dict[str, Any]:
    return MANIFEST


@app.post("/v1/health")
def health(body: HealthRequest) -> dict[str, Any]:
    try:
        check_settings(body.settings)
    except HTTPException as e:
        return {"status": "unhealthy", "message": e.detail["error"]}
    if not shutil.which("ffmpeg"):
        return {"status": "unhealthy", "message": "ffmpeg isn't installed in the container."}
    if not shutil.which("deno"):
        return {"status": "degraded", "message": "deno isn't installed, so yt-dlp can't solve YouTube's JavaScript challenges and some downloads will fail."}
    try:
        youtube._music().search("test", filter="songs", limit=1)
    except Exception as e:
        return {"status": "unhealthy", "message": f"Couldn't search YouTube Music: {e}"}
    return {"status": "healthy", "message": f"yt-dlp {yt_dlp.version.__version__}"}


@app.post("/v1/search")
def search(body: SearchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    try:
        candidates = youtube.search(body.target, max(1, min(body.limit, 25)), body.settings)
    except Exception as e:
        raise error(502, f"YouTube Music search failed: {e}", "source-unavailable")
    return {"candidates": dump(candidates)}


@app.post("/v1/fetch", status_code=202)
def fetch(body: FetchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    directory = staging_directory(STAGING, body.staging_directory)
    job = jobs.start(body.job_id, lambda j: youtube.fetch(j, body.candidate.candidate_id, directory, body.settings))
    return job.status()


@app.get("/v1/fetch/{job_id}")
def status(job_id: str) -> dict[str, Any]:
    if (job := jobs.get(job_id)) is None:
        raise error(404, "No such job; the plugin may have restarted.", "unknown-job")
    return job.status()


@app.delete("/v1/fetch/{job_id}")
def cancel(job_id: str) -> dict[str, Any]:
    if (job := jobs.get(job_id)) is None:
        raise error(404, "No such job; the plugin may have restarted.", "unknown-job")
    job.cancelled.set()
    if job.state == "queued":
        job.state = "cancelled"
    return job.status()
