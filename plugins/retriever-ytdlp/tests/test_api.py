import json
import time

import pytest
from fastapi.testclient import TestClient

from app import main, youtube
from app.matching import track_score


class FakeMusic:
    """Stands in for ytmusicapi, with canned search results."""

    def __init__(self, songs=(), videos=(), albums=(), album_pages=None):
        self.songs, self.videos, self.albums, self.album_pages = list(songs), list(videos), list(albums), album_pages or {}

    def search(self, query, filter, limit):
        return {"songs": self.songs, "videos": self.videos, "albums": self.albums}[filter][:limit]

    def get_album(self, browse_id):
        return self.album_pages[browse_id]


def song(video_id, title, artist, seconds, kind="song"):
    return {"videoId": video_id, "title": title, "artists": [{"name": artist}], "duration_seconds": seconds, "resultType": kind}


TRACK = {"kind": "track", "title": "Glory Box", "artist": "Portishead", "album": "Dummy", "durationSeconds": 306}


@pytest.fixture
def client(monkeypatch, tmp_path):
    monkeypatch.setattr(main, "STAGING", str(tmp_path))
    return TestClient(main.app)


def test_manifest_declares_the_contract(client):
    manifest = client.get("/v1/manifest").json()
    assert manifest["type"] == "retrieval"
    assert manifest["contractVersion"] == "1.0"
    assert manifest["settingsSchema"]["properties"]["format"]["default"] == "opus"


def test_search_ranks_the_song_above_covers_live_takes_and_videos(client, monkeypatch):
    fake = FakeMusic(
        songs=[song("cover", "Glory Box", "Soul Groove", 303), song("real", "Glory Box", "Portishead", 309),
               song("live", "Glory Box (Live)", "Portishead", 330)],
        videos=[song("video", "Portishead - Glory Box (Official Video)", "Portishead", 312, "video")])
    monkeypatch.setattr(youtube, "_music", lambda: fake)
    candidates = client.post("/v1/search", json={"settings": {}, "target": TRACK, "limit": 10}).json()["candidates"]
    order = [json.loads(c["candidateId"])["v"] for c in candidates]
    assert order[:2] == ["real", "video"]
    assert "cover" not in order  # below the default lowest score
    assert candidates[0]["format"] == "opus"


def test_an_album_is_offered_only_when_every_track_is_there(client, monkeypatch):
    tracks = [{"disc": 1, "position": 1, "title": "Mysterons", "durationSeconds": 302},
              {"disc": 1, "position": 2, "title": "Sour Times", "durationSeconds": 254}]
    whole = {"title": "Dummy", "artists": [{"name": "Portishead"}],
             "tracks": [song("a", "Mysterons", "Portishead", 305), song("b", "Sour Times", "Portishead", 253)]}
    part = {"title": "Dummy", "artists": [{"name": "Portishead"}], "tracks": [song("a", "Mysterons", "Portishead", 305)]}
    fake = FakeMusic(albums=[{"browseId": "W", "title": "Dummy", "artists": [{"name": "Portishead"}]},
                             {"browseId": "P", "title": "Dummy", "artists": [{"name": "Portishead"}]}],
                     album_pages={"W": whole, "P": part})
    monkeypatch.setattr(youtube, "_music", lambda: fake)
    target = {"kind": "album", "title": "Dummy", "artist": "Portishead", "durationSeconds": 556, "tracks": tracks}
    candidates = client.post("/v1/search", json={"settings": {}, "target": target}).json()["candidates"]
    assert len(candidates) == 1
    assert json.loads(candidates[0]["candidateId"]) == {"b": "W", "t": [[1, 1, "a"], [1, 2, "b"]]}


def test_fetch_runs_in_the_background_and_reports_files(client, monkeypatch):
    def download(job, video_id, directory, stem, settings, progress):
        (directory / f"{stem}.opus").write_bytes(b"not really audio")

    monkeypatch.setattr(youtube, "_download", download)
    candidate = {"candidateId": '{"b":"W","t":[[1,1,"a"],[1,2,"b"]]}', "kind": "album", "title": "Dummy", "source": "x"}
    body = {"settings": {}, "jobId": "job-1", "target": {"kind": "album", "title": "Dummy", "artist": "Portishead"},
            "candidate": candidate, "stagingDirectory": "job-1"}
    assert client.post("/v1/fetch", json=body).status_code == 202
    for _ in range(100):
        status = client.get("/v1/fetch/job-1").json()
        if status["state"] != "running" and status["state"] != "queued":
            break
        time.sleep(0.02)
    assert status["state"] == "completed"
    assert [(f["path"], f["disc"], f["position"]) for f in status["files"]] == [("1-01.opus", 1, 1), ("1-02.opus", 1, 2)]


def test_staging_paths_cannot_escape_the_volume(client):
    body = {"settings": {}, "jobId": "x", "target": TRACK, "stagingDirectory": "../etc",
            "candidate": {"candidateId": '{"v":"a"}', "kind": "track", "title": "t", "source": "s"}}
    response = client.post("/v1/fetch", json=body)
    assert response.status_code == 400
    assert response.json()["code"] == "invalid-staging-directory"


def test_bad_settings_and_unknown_jobs_use_contract_errors(client):
    response = client.post("/v1/search", json={"settings": {"format": "flac"}, "target": TRACK})
    assert response.status_code == 400 and response.json()["code"] == "invalid-settings"
    assert client.get("/v1/fetch/nope").json()["code"] == "unknown-job"


def test_a_channel_upload_with_the_artist_in_the_title_still_matches():
    assert track_score("Glory Box", "Portishead", 306, "Portishead - Glory Box", ["Some Channel"], 307) > 0.8
