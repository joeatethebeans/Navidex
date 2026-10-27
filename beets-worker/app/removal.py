"""Deleting files from the library, for removing an artist, album or track. See docs/import-worker.md."""

from __future__ import annotations

import os

# What a folder may still hold and be deleted with its music: artwork, playlists, rip logs and notes.
LEFTOVERS = {".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".cue", ".log", ".m3u", ".m3u8", ".nfo", ".txt",
             ".sfv", ".accurip", ".md5"}


def delete(library: str, paths: list[str]) -> tuple[list[str], list[dict[str, str]]]:
    """Deletes each file, then any folder left with nothing but leftovers, up to the library. Returns the paths deleted
    and those that couldn't be, with why. A file that's already gone counts as deleted."""
    root = os.path.realpath(library)
    deleted: list[str] = []
    failed: list[dict[str, str]] = []
    folders: set[str] = set()
    for path in paths:
        full = os.path.realpath(os.path.join(root, path))
        if not full.startswith(root + os.sep):
            failed.append({"path": path, "error": "It isn't inside the library."})
            continue
        try:
            os.remove(full)
        except FileNotFoundError:
            pass
        except OSError as e:
            failed.append({"path": path, "error": e.strerror or str(e)})
            continue
        deleted.append(path)
        folders.add(os.path.dirname(full))
    for folder in sorted(folders, key=len, reverse=True):
        _remove_emptied(folder, root)
    return deleted, failed


def _remove_emptied(folder: str, root: str) -> None:
    while folder.startswith(root + os.sep) and os.path.isdir(folder):
        try:
            entries = list(os.scandir(folder))
            if any(e.is_dir() or os.path.splitext(e.name)[1].lower() not in LEFTOVERS for e in entries):
                return
            for entry in entries:
                os.remove(entry.path)
            os.rmdir(folder)
        except OSError:
            return
        folder = os.path.dirname(folder)
