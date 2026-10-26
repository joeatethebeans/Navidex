import json
import shutil
import subprocess
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from mutagen.flac import FLAC

from app import main
from app.inbox import Inbox

pytestmark = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg to make test files")


def flac(path: Path, seconds: float, **tags: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", f"anullsrc=r=8000:cl=mono", "-t", str(seconds),
                    "-y", str(path)], check=True)
    audio = FLAC(path)
    for name, value in tags.items():
        audio[name] = value
    audio.save()


@pytest.fixture
def client(monkeypatch, tmp_path):
    (tmp_path / "staging").mkdir()
    monkeypatch.setattr(main, "STAGING", str(tmp_path / "staging"))
    monkeypatch.setattr(main, "inbox", Inbox(str(tmp_path / "inbox")))
    return TestClient(main.app)


@pytest.fixture
def inbox(tmp_path) -> Path:
    root = tmp_path / "inbox"
    flac(root / "loose" / "glory.flac", 30, title="Glory Box", artist="Portishead", album="Dummy")
    flac(root / "Dummy" / "01.flac", 20, title="Mysterons", artist="Portishead", album="Dummy", tracknumber="1")
    flac(root / "Dummy" / "02.flac", 25, title="Sour Times", artist="Portishead", album="Dummy", tracknumber="2")
    flac(root / "other.flac", 30, title="Glory Box", artist="Someone Else", musicbrainz_trackid="rec-1")
    return root


def wait(client, job_id):
    for _ in range(200):
        status = client.get(f"/v1/fetch/{job_id}").json()
        if status["state"] not in ("queued", "running"):
            return status
        time.sleep(0.02)
    raise TimeoutError(status)


def test_a_track_is_found_by_its_tags_and_moved_to_staging(client, inbox, tmp_path):
    target = {"kind": "track", "title": "Glory Box", "artist": "Portishead", "album": "Dummy", "durationSeconds": 30}
    candidates = client.post("/v1/search", json={"settings": {}, "target": target}).json()["candidates"]
    assert json.loads(candidates[0]["candidateId"]) == {"f": "loose/glory.flac"}
    assert all(c["artist"] != "Someone Else" for c in candidates)

    body = {"settings": {}, "jobId": "j", "target": target, "candidate": candidates[0], "stagingDirectory": "j"}
    assert client.post("/v1/fetch", json=body).status_code == 202
    status = wait(client, "j")
    assert status["state"] == "completed"
    assert status["files"][0]["path"] == "track.flac"
    assert status["files"][0]["codec"] == "flac"
    assert abs(status["files"][0]["durationSeconds"] - 30) < 0.1
    assert (tmp_path / "staging" / "j" / "track.flac").exists()
    assert not (inbox / "loose").exists()  # moved, and the empty folder tidied away


def test_a_musicbrainz_tag_beats_everything_else(client, inbox):
    target = {"kind": "track", "title": "Something", "artist": "Nobody", "ids": [{"provider": "musicbrainz", "value": "rec-1"}]}
    candidates = client.post("/v1/search", json={"settings": {}, "target": target}).json()["candidates"]
    assert [json.loads(c["candidateId"])["f"] for c in candidates] == ["other.flac"]
    assert candidates[0]["score"] == 1.0


def test_an_album_folder_is_offered_when_it_has_every_track(client, inbox, tmp_path):
    tracks = [{"disc": 1, "position": 1, "title": "Mysterons", "durationSeconds": 20},
              {"disc": 1, "position": 2, "title": "Sour Times", "durationSeconds": 25}]
    target = {"kind": "album", "title": "Dummy", "artist": "Portishead", "tracks": tracks}
    candidates = client.post("/v1/search", json={"settings": {}, "target": target}).json()["candidates"]
    assert len(candidates) == 1
    assert json.loads(candidates[0]["candidateId"]) == {"d": "Dummy", "t": [[1, 1, "Dummy/01.flac"], [1, 2, "Dummy/02.flac"]]}

    body = {"settings": {"keepOriginals": True}, "jobId": "a", "target": target, "candidate": candidates[0], "stagingDirectory": "a"}
    client.post("/v1/fetch", json=body)
    status = wait(client, "a")
    assert [(f["path"], f["position"]) for f in status["files"]] == [("1-01.flac", 1), ("1-02.flac", 2)]
    assert (inbox / "Dummy" / "01.flac").exists()  # copied, not moved

    tracks.append({"disc": 1, "position": 3, "title": "Strangers"})
    assert client.post("/v1/search", json={"settings": {}, "target": target}).json()["candidates"] == []


def test_a_file_taken_since_the_search_fails_the_fetch(client, inbox):
    target = {"kind": "track", "title": "Glory Box", "artist": "Portishead"}
    candidate = client.post("/v1/search", json={"settings": {}, "target": target}).json()["candidates"][0]
    (inbox / "loose" / "glory.flac").unlink()
    client.post("/v1/fetch", json={"settings": {}, "jobId": "g", "target": target, "candidate": candidate, "stagingDirectory": "g"})
    status = wait(client, "g")
    assert status["state"] == "failed"
    assert "no longer in the inbox" in status["error"]


def test_health_reports_a_missing_inbox(client):
    assert client.post("/v1/health", json={"settings": {}}).json()["status"] == "unhealthy"
