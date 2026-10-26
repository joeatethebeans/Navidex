"""Finding music on YouTube Music with ytmusicapi, and fetching it with yt-dlp."""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

import yt_dlp
from ytmusicapi import YTMusic

from .contract import Candidate, Cancelled, Job, Kind, Target, probe
from .matching import similarity, track_score, version_penalty

SOURCE = "YouTube Music"
FORMATS = ["opus", "m4a", "mp3"]

# What each output format asks yt-dlp for. Opus and m4a are YouTube's own streams, remuxed without re-encoding;
# mp3 is the only one that transcodes.
_DOWNLOAD = {
    "opus": ("bestaudio[acodec=opus]/bestaudio", "opus"),
    "m4a": ("bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio", "m4a"),
    "mp3": ("bestaudio", "mp3"),
}
_BITRATES = {"opus": 160, "m4a": 128}


def _music() -> YTMusic:
    return YTMusic()


def _artists(item: dict[str, Any]) -> list[str]:
    return [a["name"] for a in item.get("artists") or [] if a.get("name")]


def _watch(video_id: str) -> str:
    return f"https://music.youtube.com/watch?v={video_id}"


def _bitrate(settings: dict[str, Any]) -> int:
    return int(settings.get("bitrateKbps") or 320) if settings.get("format") == "mp3" else _BITRATES.get(settings.get("format", "opus"), 160)


def search(target: Target, limit: int, settings: dict[str, Any], music: YTMusic | None = None) -> list[Candidate]:
    music = music or _music()
    found = search_album(target, limit, settings, music) if target.kind == Kind.album else search_track(target, limit, settings, music)
    minimum = float(settings.get("minScore", 0.5))
    return sorted((c for c in found if (c.score or 0) >= minimum), key=lambda c: c.score or 0, reverse=True)[:limit]


def search_track(target: Target, limit: int, settings: dict[str, Any], music: YTMusic) -> list[Candidate]:
    results: dict[str, Candidate] = {}

    def offer(item: dict[str, Any], bonus: float = 0, factor: float = 1) -> None:
        video_id = item.get("videoId")
        if not video_id:
            return
        score = track_score(target.title, target.artist, target.duration_seconds, item.get("title", ""), _artists(item),
                            item.get("duration_seconds"))
        score = round(min(1.0, score * factor + bonus), 3)
        if video_id in results and (results[video_id].score or 0) >= score:
            return
        results[video_id] = Candidate(
            candidate_id=json.dumps({"v": video_id}, separators=(",", ":")), kind=Kind.track, title=item.get("title", ""),
            artist=", ".join(_artists(item)) or None, album=(item.get("album") or {}).get("name"),
            duration_seconds=item.get("duration_seconds"), source=SOURCE, url=_watch(video_id),
            format=settings.get("format", "opus"), bitrate_kbps=_bitrate(settings), score=score)

    # An ISRC search on YouTube Music usually finds the exact recording.
    if isrc := target.id("isrc"):
        for item in music.search(isrc, filter="songs", limit=5):
            if similarity(target.title, item.get("title")) >= 0.6:
                offer(item, bonus=0.1)
    query = f"{target.artist} {target.title}"
    for item in music.search(query, filter="songs", limit=limit):
        offer(item)
    if settings.get("includeVideos", True):
        # Music videos often have intros and outros, so they rank below songs of the same name.
        for item in music.search(query, filter="videos", limit=max(3, limit // 2)):
            offer(item, factor=0.9)
    return list(results.values())


def search_album(target: Target, limit: int, settings: dict[str, Any], music: YTMusic) -> list[Candidate]:
    candidates = []
    wanted = target.tracks or []
    for item in music.search(f"{target.artist} {target.title}", filter="albums", limit=5)[:4]:
        title_match = similarity(target.title, item.get("title"))
        if title_match < 0.6 or not item.get("browseId"):
            continue
        album = music.get_album(item["browseId"])
        mapping = map_tracks(wanted, album.get("tracks") or [])
        # Only whole matches are offered: the core fetches track by track when no plugin has the whole album.
        if mapping is None:
            continue
        artist_match = max([similarity(target.artist, a) for a in _artists(album) or _artists(item)] + [0.0])
        durations = [t.get("duration_seconds") or 0 for t in album.get("tracks") or [] if t.get("videoId") in {m[2] for m in mapping}]
        total = sum(durations) or None
        length = 1 - min(1.0, abs((target.duration_seconds or total or 0) - (total or 0)) / max(60.0, len(wanted) * 10))
        score = round((0.35 * title_match + 0.25 * artist_match + 0.4 * length) * version_penalty(target.title, album.get("title", "")), 3)
        candidates.append(Candidate(
            candidate_id=json.dumps({"b": item["browseId"], "t": mapping}, separators=(",", ":")), kind=Kind.album,
            title=album.get("title", item.get("title", "")), artist=", ".join(_artists(album) or _artists(item)) or None,
            album=album.get("title"), duration_seconds=total, source=SOURCE,
            url=f"https://music.youtube.com/browse/{item['browseId']}", format=settings.get("format", "opus"),
            bitrate_kbps=_bitrate(settings), score=score))
    return candidates


def map_tracks(wanted: list, offered: list[dict[str, Any]]) -> list[list[Any]] | None:
    """Pairs each wanted track with a video on the album, as [disc, position, videoId]; None unless all of them pair."""
    available = [t for t in offered if t.get("videoId")]
    used: set[str] = set()
    mapping = []
    for index, track in enumerate(wanted):
        def fit(item: dict[str, Any]) -> float:
            same_place = 0.1 if index < len(offered) and offered[index] is item else 0
            return similarity(track.title, item.get("title")) + same_place

        best = max((t for t in available if t["videoId"] not in used), key=fit, default=None)
        if best is None or similarity(track.title, best.get("title")) < 0.6:
            return None
        if track.duration_seconds and best.get("duration_seconds") and abs(track.duration_seconds - best["duration_seconds"]) > 20:
            return None
        used.add(best["videoId"])
        mapping.append([track.disc, track.position, best["videoId"]])
    return mapping or None


def fetch(job: Job, candidate_id: str, directory: Path, settings: dict[str, Any]) -> list[dict[str, Any]]:
    data = json.loads(candidate_id)
    if "v" in data:
        _download(job, data["v"], directory, "track", settings, lambda p: job.__setattr__("progress", p))
        return [probe(_only(directory, "track"), directory)]
    files = []
    tracks = data["t"]
    for index, (disc, position, video_id) in enumerate(tracks):
        stem = f"{disc}-{position:02d}"
        _download(job, video_id, directory, stem, settings,
                  lambda p, i=index: job.__setattr__("progress", (i + p) / len(tracks)))
        files.append(probe(_only(directory, stem), directory, disc, position))
    return files


def _only(directory: Path, stem: str) -> Path:
    matches = [p for p in directory.iterdir() if p.stem == stem and p.suffix not in (".part", ".ytdl")]
    if not matches:
        raise RuntimeError(f"yt-dlp finished without writing {stem}")
    return matches[0]


def _download(job: Job, video_id: str, directory: Path, stem: str, settings: dict[str, Any], progress) -> None:
    fmt = settings.get("format", "opus")
    selector, codec = _DOWNLOAD[fmt]

    def hook(status: dict[str, Any]) -> None:
        if job.cancelled.is_set():
            raise yt_dlp.utils.DownloadCancelled()
        total = status.get("total_bytes") or status.get("total_bytes_estimate")
        if status.get("status") == "downloading" and total:
            progress(min(0.99, status.get("downloaded_bytes", 0) / total))

    options: dict[str, Any] = {
        "format": selector,
        "outtmpl": str(directory / f"{stem}.%(ext)s"),
        "noplaylist": True,
        "quiet": True,
        "no_warnings": True,
        "noprogress": True,
        "overwrites": True,
        "retries": 3,
        "progress_hooks": [hook],
        "postprocessors": [{"key": "FFmpegExtractAudio", "preferredcodec": codec,
                            **({"preferredquality": str(_bitrate(settings))} if fmt == "mp3" else {})}],
    }
    if settings.get("cookiesFile"):
        options["cookiefile"] = settings["cookiesFile"]
    try:
        with yt_dlp.YoutubeDL(options) as ydl:
            ydl.download([_watch(video_id)])
    except yt_dlp.utils.DownloadCancelled:
        raise Cancelled()
    except yt_dlp.utils.DownloadError as e:
        raise RuntimeError(re.sub(r"\x1b\[[0-9;]*m", "", str(e)).removeprefix("ERROR: ")) from None
