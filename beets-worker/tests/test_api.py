import os
import shutil
import subprocess
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app import main
from app.importer import Importer

# Dummy by Portishead, and Glory Box on it.
RELEASE = {"musicBrainzId": "76df3287-6cda-33eb-8e9a-044b5e15ffdd", "title": "Dummy", "artist": "Portishead", "trackCount": 11}
GLORY_BOX = {"path": "glory.flac", "recordingId": "145f5c43-0ac2-4886-8b09-63d0e92ded5d", "title": "Glory Box",
             "artist": "Portishead", "disc": 1, "position": 11}


@pytest.fixture
def roots(monkeypatch, tmp_path):
    staging, library = tmp_path / "staging", tmp_path / "music"
    staging.mkdir()
    library.mkdir()
    monkeypatch.setattr(main, "STAGING", str(staging))
    monkeypatch.setattr(main, "LIBRARY", str(library))
    monkeypatch.setattr(main, "importer", Importer(str(staging), str(library)))
    return staging, library


@pytest.fixture
def client(roots):
    return TestClient(main.app)


def test_health_checks_both_volumes(client, roots, monkeypatch):
    assert client.get("/v1/health").json()["status"] == "healthy"
    monkeypatch.setattr(main, "LIBRARY", "/nowhere")
    assert "isn't mounted" in client.get("/v1/health").json()["message"]


def test_paths_outside_the_volumes_are_refused(client):
    body = {"stagingDirectory": "../x", "release": RELEASE, "files": [GLORY_BOX], "pathTemplate": "$album/$title"}
    response = client.post("/v1/import", json=body)
    assert response.status_code == 422
    assert "inside the staging volume" in response.json()["error"]

    body.update(stagingDirectory="job", destinationDirectory="../../etc")
    assert "inside the library" in client.post("/v1/import", json=body).json()["error"]


def test_a_missing_staged_file_fails_the_import(client, roots):
    (roots[0] / "job").mkdir()
    body = {"stagingDirectory": "job", "release": RELEASE, "files": [GLORY_BOX], "pathTemplate": "$album/$title"}
    assert "isn't in the staging directory" in client.post("/v1/import", json=body).json()["error"]


@pytest.mark.skipif(not os.environ.get("NETWORK_TESTS") or shutil.which("ffmpeg") is None,
                    reason="asks MusicBrainz; set NETWORK_TESTS=1 (needs ffmpeg)")
def test_a_file_is_tagged_and_filed_against_the_pinned_release(client, roots):
    staging, library = roots
    (staging / "job").mkdir()
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", "306",
                    str(staging / "job" / "glory.flac")], check=True)
    body = {"stagingDirectory": "job", "release": RELEASE, "files": [GLORY_BOX],
            "pathTemplate": "$albumartist/$album%aunique{}/$track $title"}
    response = client.post("/v1/import", json=body)
    assert response.status_code == 200, response.json()
    imported = response.json()["files"][0]
    assert imported["path"] == "Portishead/Dummy/11 Glory Box.flac"
    assert imported["tags"]["mb_trackid"] == GLORY_BOX["recordingId"]
    assert imported["tags"]["mb_albumid"] == RELEASE["musicBrainzId"]
    assert (library / imported["path"]).exists()
    assert not (staging / "job" / "glory.flac").exists()
