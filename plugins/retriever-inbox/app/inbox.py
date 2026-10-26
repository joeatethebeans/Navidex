"""Matching requests against audio files dropped into the inbox folder, and moving them to staging."""

from __future__ import annotations

import json
import os
import shutil
import threading
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import mutagen

from .contract import Candidate, Job, Kind, Target, TargetTrack, probe
from .matching import similarity, track_score

SOURCE = "Inbox"
AUDIO = {".flac", ".mp3", ".m4a", ".mp4", ".opus", ".ogg", ".oga", ".wav", ".aac", ".wma", ".aiff", ".aif", ".ape", ".wv"}


@dataclass(frozen=True)
class File:
    path: str  # relative to the inbox, with forward slashes
    title: str
    artist: str
    album: str
    duration: float | None
    disc: int | None
    position: int | None
    recording_id: str | None
    release_id: str | None
    isrc: str | None
    format: str
    bitrate: int | None


def _first(tags: Any, *names: str) -> str:
    for name in names:
        try:
            value = tags.get(name) if tags is not None else None
        except (KeyError, ValueError):
            value = None
        if value:
            return str(value[0] if isinstance(value, list) else value).strip()
    return ""


def _number(text: str) -> int | None:
    head = text.split("/")[0].strip()
    return int(head) if head.isdigit() else None


class Inbox:
    """The inbox folder's audio files with their tags, re-read only when a file changes."""

    def __init__(self, root: str):
        self.root = Path(root)
        self._cache: dict[str, tuple[float, int, File | None]] = {}
        self._lock = threading.Lock()

    def files(self) -> list[File]:
        found: list[File] = []
        seen: set[str] = set()
        with self._lock:
            for path in sorted(self.root.rglob("*")):
                if path.suffix.lower() not in AUDIO or not path.is_file():
                    continue
                relative = path.relative_to(self.root).as_posix()
                stat = path.stat()
                seen.add(relative)
                cached = self._cache.get(relative)
                if cached is None or cached[:2] != (stat.st_mtime, stat.st_size):
                    cached = self._cache[relative] = (stat.st_mtime, stat.st_size, self._read(path, relative))
                if cached[2] is not None:
                    found.append(cached[2])
            for gone in set(self._cache) - seen:
                del self._cache[gone]
        return found

    @staticmethod
    def _read(path: Path, relative: str) -> File | None:
        try:
            audio = mutagen.File(path, easy=True)
        except Exception:
            return None  # unreadable or half-copied; it's tried again once it changes
        if audio is None:
            return None
        tags = audio.tags
        title = _first(tags, "title") or path.stem
        info = audio.info
        return File(
            path=relative, title=title, artist=_first(tags, "artist", "albumartist"),
            album=_first(tags, "album") or path.parent.name, duration=getattr(info, "length", None) or None,
            disc=_number(_first(tags, "discnumber")) or 1, position=_number(_first(tags, "tracknumber")),
            recording_id=_first(tags, "musicbrainz_trackid") or None, release_id=_first(tags, "musicbrainz_albumid") or None,
            isrc=_first(tags, "isrc").replace("-", "").upper() or None, format=path.suffix.lower().lstrip("."),
            bitrate=round(info.bitrate / 1000) if getattr(info, "bitrate", 0) else None)

    def search(self, target: Target, limit: int, settings: dict[str, Any]) -> list[Candidate]:
        files = self.files()
        found = self._albums(target, files) if target.kind == Kind.album else self._tracks(target, files)
        minimum = float(settings.get("minScore", 0.6))
        return sorted((c for c in found if (c.score or 0) >= minimum), key=lambda c: c.score or 0, reverse=True)[:limit]

    def _tracks(self, target: Target, files: list[File]) -> list[Candidate]:
        candidates = []
        for file in files:
            score = file_score(target.title, target.artist, target.duration_seconds, target.ids, file)
            if target.album and similarity(target.album, file.album) >= 0.8:
                score = min(1.0, score + 0.03)
            candidates.append(_candidate({"f": file.path}, Kind.track, file.title, file.artist, file.album, file.duration,
                                         file.path, file.format, file.bitrate, score))
        return candidates

    def _albums(self, target: Target, files: list[File]) -> list[Candidate]:
        folders: dict[str, list[File]] = {}
        for file in files:
            folders.setdefault(file.path.rpartition("/")[0], []).append(file)
        candidates = []
        for folder, group in folders.items():
            album = Counter(f.album for f in group).most_common(1)[0][0]
            artist = Counter(f.artist for f in group).most_common(1)[0][0]
            release_match = target.id("musicbrainz") is not None and any(f.release_id == target.id("musicbrainz") for f in group)
            if not release_match and similarity(target.title, album) < 0.6:
                continue
            mapping = map_tracks(target.tracks or [], group, target.artist)
            if mapping is None:
                continue
            chosen = [f for f in group if f.path in {m[2] for m in mapping}]
            total = sum(f.duration or 0 for f in chosen) or None
            score = 1.0 if release_match else round(0.5 * similarity(target.title, album) + 0.2 * similarity(target.artist, artist) + 0.3, 3)
            candidates.append(_candidate({"d": folder, "t": mapping}, Kind.album, album, artist, album, total,
                                         folder or "(inbox)", Counter(f.format for f in chosen).most_common(1)[0][0],
                                         None, score))
        return candidates

    def fetch(self, job: Job, candidate_id: str, directory: Path, settings: dict[str, Any]) -> list[dict[str, Any]]:
        data = json.loads(candidate_id)
        keep = bool(settings.get("keepOriginals", False))
        moves = [(data["f"], "track", None, None)] if "f" in data else [(p, f"{d}-{n:02d}", d, n) for d, n, p in data["t"]]
        sources = []
        for relative, _, _, _ in moves:
            source = (self.root / relative).resolve()
            if self.root.resolve() not in source.parents:
                raise ValueError("The candidate points outside the inbox.")
            if not source.is_file():
                raise FileNotFoundError(f"{relative} is no longer in the inbox.")
            sources.append(source)
        files = []
        for index, ((_, stem, disc, position), source) in enumerate(zip(moves, sources)):
            job.check()
            target = directory / f"{stem}{source.suffix.lower()}"
            if keep:
                shutil.copy2(source, target)
            else:
                shutil.move(source, target)
                self._prune(source.parent)
            files.append(probe(target, directory, disc, position))
            job.progress = (index + 1) / len(moves)
        return files

    def _prune(self, folder: Path) -> None:
        """Removes folders the move left empty, up to the inbox itself."""
        root = self.root.resolve()
        while folder != root and root in folder.parents:
            try:
                os.rmdir(folder)
            except OSError:
                return
            folder = folder.parent


def file_score(title: str, artist: str, duration: float | None, ids: list | None, file: File) -> float:
    wanted = {(i.provider, i.value.replace("-", "").upper() if i.provider == "isrc" else i.value) for i in ids or []}
    # Tagged files, from Picard or a store download, say exactly what they are.
    if file.recording_id and ("musicbrainz", file.recording_id) in wanted:
        return 1.0
    if file.isrc and ("isrc", file.isrc) in wanted:
        return 0.98
    return track_score(title, artist, duration, file.title, [file.artist] if file.artist else [], file.duration)


def map_tracks(wanted: list[TargetTrack], group: list[File], artist: str) -> list[list[Any]] | None:
    """Pairs each wanted track with a file in the folder, as [disc, position, path]; None unless all of them pair."""
    used: set[str] = set()
    mapping = []
    for track in wanted:
        def fit(file: File) -> float:
            same_place = 0.2 if (file.disc, file.position) == (track.disc, track.position) else 0
            return file_score(track.title, artist, track.duration_seconds, track.ids, file) + same_place

        best = max((f for f in group if f.path not in used), key=fit, default=None)
        if best is None or fit(best) < 0.7:
            return None
        used.add(best.path)
        mapping.append([track.disc, track.position, best.path])
    return mapping or None


def _candidate(data: dict[str, Any], kind: Kind, title: str, artist: str, album: str, duration: float | None, where: str,
               fmt: str, bitrate: int | None, score: float) -> Candidate:
    return Candidate(candidate_id=json.dumps(data, separators=(",", ":")), kind=kind, title=title, artist=artist or None,
                     album=album or None, duration_seconds=round(duration, 3) if duration else None,
                     source=f"{SOURCE}: {where}", format=fmt, bitrate_kbps=bitrate, score=round(score, 3))

