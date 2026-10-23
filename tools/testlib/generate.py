#!/usr/bin/env python3
"""Generate the test music library and its answer key.

Builds silent audio files (FLAC, MP3 and M4A) with the exact durations MusicBrainz lists, tags them the way
each scenario album asks (full MusicBrainz tags as beets and Picard write them, ISRC only, basic tags only,
or none), and writes expected.json: what Navidex should match each file to and, per artist, which albums are
owned, which tracks they're missing, and which albums the missing view should show.

    python3 tools/testlib/generate.py [--out DIR]

Needs ffmpeg and mutagen. Works offline from catalogue.json; see fetch_catalogue.py to refresh it.
"""

import argparse
import json
import re
import shutil
import subprocess
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from pathlib import Path

from mutagen.flac import FLAC
from mutagen.id3 import ID3, TALB, TDOR, TDRC, TIT2, TPE1, TPE2, TPOS, TRCK, TSRC, TXXX, UFID
from mutagen.mp4 import MP4, MP4FreeForm

HERE = Path(__file__).resolve().parent
ENCODERS = {
    "flac": ["-c:a", "flac"],
    "mp3": ["-c:a", "libmp3lame", "-q:a", "9"],
    "m4a": ["-c:a", "aac", "-b:a", "16k"],
}

# Match methods, named as in Navidex.Core.Data.MatchMethod.
METHOD_FOR_TAGS = {"musicbrainz": "MusicBrainzTags", "isrc": "IsrcOrUpc", "basic": "Fuzzy"}


@dataclass
class File:
    path: str
    format: str
    duration: float
    tags: dict[str, str]
    expect: dict
    ids: dict[str, str] = field(default_factory=dict)


def safe(name: str) -> str:
    return re.sub(r'[\\/:*?"<>|]', "_", name).strip(". ")


def year(date: str | None) -> str:
    return (date or "")[:4]


def album_files(album: dict, edition: dict) -> list[File]:
    group = edition["releaseGroup"]
    omitted = {tuple(o) for o in album.get("omit", [])}
    overrides = {(o["disc"], o["position"]): o for o in album.get("overrides", [])}
    multi_disc = len(edition["media"]) > 1
    folder = album.get("folder") or f"{safe(edition['artistCredit'])}/{safe(edition['title'])} ({year(group['firstReleaseDate'])})"
    files = []

    for medium in edition["media"]:
        for track in medium["tracks"]:
            key = (medium["position"], track["position"])
            if key in omitted:
                continue
            override = overrides.get(key, {})
            if override and override["was"] != track["title"]:
                raise SystemExit(f"override for {edition['title']} {key} expects '{override['was']}', found '{track['title']}'")
            title = override.get("title", track["title"])
            artist = override.get("artist", track["artistCredit"])
            name = f"{track['position']:02d} {safe(title)}.{album['format']}"
            if album["tags"] == "basic" and album.get("folder"):
                name = f"{track['position']:02d}. {safe(title)}.{album['format']}"
            path = f"{folder}/Disc {medium['position']}/{name}" if multi_disc else f"{folder}/{name}"

            tags = {
                "title": title,
                "artist": artist,
                "album": edition["title"],
                "albumartist": edition["artistCredit"],
                "tracknumber": str(track["position"]),
                "tracktotal": str(len(medium["tracks"])),
                "discnumber": str(medium["position"]),
                "disctotal": str(len(edition["media"])),
                "date": edition["date"] or "",
            }
            ids = {}
            if album["tags"] in ("musicbrainz", "isrc") and track["recording"]["isrcs"]:
                ids["isrc"] = track["recording"]["isrcs"][0]
            if album["tags"] == "musicbrainz":
                ids |= {
                    "recording": track["recording"]["mbid"],
                    "releasetrack": track["trackMbid"],
                    "release": edition["mbid"],
                    "releasegroup": group["mbid"],
                    "artist": edition["artistMbid"],
                    "albumartist": edition["artistMbid"],
                    "barcode": edition["barcode"] or "",
                    "releasetype": (group["primaryType"] or "").lower(),
                    "releasestatus": (edition["status"] or "").lower(),
                    "releasecountry": edition["country"] or "",
                    "originaldate": group["firstReleaseDate"] or "",
                }

            files.append(File(
                path=path,
                format=album["format"],
                duration=track["lengthMs"] / 1000,
                tags=tags,
                ids=ids,
                expect={
                    "method": METHOD_FOR_TAGS[album["tags"]],
                    "recordingMbid": track["recording"]["mbid"],
                    "releaseMbid": edition["mbid"],
                    "releaseGroupMbid": group["mbid"],
                    "disc": medium["position"],
                    "position": track["position"],
                },
            ))
    return files


def stray_file(stray: dict, editions: dict[str, dict]) -> File:
    duration = stray.get("durationSeconds")
    if "like" in stray:
        like = stray["like"]
        track = editions[like["release"]]["media"][like["disc"] - 1]["tracks"][like["position"] - 1]
        if track["title"] != stray["tags"]["title"]:
            raise SystemExit(f"stray {stray['path']} is modelled on '{track['title']}', not '{stray['tags']['title']}'")
        duration = track["lengthMs"] / 1000
    duration += stray.get("durationOffset", 0)
    return File(
        path=stray["path"],
        format=Path(stray["path"]).suffix[1:],
        duration=duration,
        tags=stray["tags"],
        expect={"method": "Unmatched", "review": True, "why": stray["why"]},
    )


def encode(file: File, root: Path) -> None:
    target = root / file.path
    target.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono",
         "-t", f"{file.duration:.3f}", *ENCODERS[file.format], "-map_metadata", "-1", str(target)],
        check=True,
    )
    write_tags(target, file)


def write_tags(target: Path, file: File) -> None:
    t, ids = file.tags, file.ids
    if not t and not ids:
        return

    if file.format == "flac":
        audio = FLAC(target)
        vorbis = {
            "TITLE": t.get("title"), "ARTIST": t.get("artist"), "ALBUM": t.get("album"),
            "ALBUMARTIST": t.get("albumartist"), "TRACKNUMBER": t.get("tracknumber"), "TRACKTOTAL": t.get("tracktotal"),
            "DISCNUMBER": t.get("discnumber"), "DISCTOTAL": t.get("disctotal"), "DATE": t.get("date"),
            "ISRC": ids.get("isrc"), "BARCODE": ids.get("barcode"), "ORIGINALDATE": ids.get("originaldate"),
            "MUSICBRAINZ_TRACKID": ids.get("recording"), "MUSICBRAINZ_RELEASETRACKID": ids.get("releasetrack"),
            "MUSICBRAINZ_ALBUMID": ids.get("release"), "MUSICBRAINZ_RELEASEGROUPID": ids.get("releasegroup"),
            "MUSICBRAINZ_ARTISTID": ids.get("artist"), "MUSICBRAINZ_ALBUMARTISTID": ids.get("albumartist"),
            "RELEASETYPE": ids.get("releasetype"), "RELEASESTATUS": ids.get("releasestatus"),
            "RELEASECOUNTRY": ids.get("releasecountry"),
        }
        for key, value in vorbis.items():
            if value:
                audio[key] = value
        audio.save()

    elif file.format == "mp3":
        tags = ID3()
        simple = {TIT2: t.get("title"), TPE1: t.get("artist"), TALB: t.get("album"), TPE2: t.get("albumartist"),
                  TDRC: t.get("date"), TSRC: ids.get("isrc"), TDOR: ids.get("originaldate")}
        for frame, value in simple.items():
            if value:
                tags.add(frame(encoding=3, text=value))
        if t.get("tracknumber"):
            tags.add(TRCK(encoding=3, text=f"{t['tracknumber']}/{t['tracktotal']}"))
        if t.get("discnumber"):
            tags.add(TPOS(encoding=3, text=f"{t['discnumber']}/{t['disctotal']}"))
        if ids.get("recording"):
            tags.add(UFID(owner="http://musicbrainz.org", data=ids["recording"].encode()))
        txxx = {
            "MusicBrainz Release Track Id": "releasetrack", "MusicBrainz Album Id": "release",
            "MusicBrainz Release Group Id": "releasegroup", "MusicBrainz Artist Id": "artist",
            "MusicBrainz Album Artist Id": "albumartist", "BARCODE": "barcode", "MusicBrainz Album Type": "releasetype",
            "MusicBrainz Album Status": "releasestatus", "MusicBrainz Album Release Country": "releasecountry",
        }
        for desc, key in txxx.items():
            if ids.get(key):
                tags.add(TXXX(encoding=3, desc=desc, text=ids[key]))
        tags.save(target, v2_version=4)

    elif file.format == "m4a":
        audio = MP4(target)
        simple = {"\xa9nam": t.get("title"), "\xa9ART": t.get("artist"), "\xa9alb": t.get("album"),
                  "aART": t.get("albumartist"), "\xa9day": t.get("date")}
        for key, value in simple.items():
            if value:
                audio[key] = [value]
        if t.get("tracknumber"):
            audio["trkn"] = [(int(t["tracknumber"]), int(t["tracktotal"]))]
        if t.get("discnumber"):
            audio["disk"] = [(int(t["discnumber"]), int(t["disctotal"]))]
        freeform = {
            "MusicBrainz Track Id": "recording", "MusicBrainz Release Track Id": "releasetrack",
            "MusicBrainz Album Id": "release", "MusicBrainz Release Group Id": "releasegroup",
            "MusicBrainz Artist Id": "artist", "MusicBrainz Album Artist Id": "albumartist", "ISRC": "isrc",
            "BARCODE": "barcode", "MusicBrainz Album Type": "releasetype", "MusicBrainz Album Status": "releasestatus",
            "MusicBrainz Album Release Country": "releasecountry",
        }
        for name, key in freeform.items():
            if ids.get(key):
                audio[f"----:com.apple.iTunes:{name}"] = [MP4FreeForm(ids[key].encode())]
        audio.save()


def shown_by_default(group: dict, policy: dict) -> bool:
    if group["primaryType"] not in policy["shownPrimaryTypes"]:
        return False
    if policy["hideAnySecondaryType"] and group["secondaryTypes"]:
        return False
    return not policy["requireOfficialRelease"] or "Official" in group["releaseStatuses"]


def answer_key(scenario: dict, catalogue: dict, editions: dict[str, dict], files: list[File]) -> dict:
    policy = scenario["policy"]
    owned_tracks = {(f.expect["releaseMbid"], f.expect["disc"], f.expect["position"]) for f in files if "releaseMbid" in f.expect}
    artists = []
    for artist in catalogue["artists"]:
        owned = []
        for album in scenario["albums"]:
            edition = editions[album["release"]]
            if edition["artistMbid"] != artist["mbid"]:
                continue
            missing = [
                {"disc": m["position"], "position": t["position"], "title": t["title"], "recordingMbid": t["recording"]["mbid"]}
                for m in edition["media"] for t in m["tracks"]
                if (edition["mbid"], m["position"], t["position"]) not in owned_tracks
            ]
            group = edition["releaseGroup"]
            owned.append({
                "releaseGroupMbid": group["mbid"],
                "title": group["title"],
                "primaryType": group["primaryType"],
                "secondaryTypes": group["secondaryTypes"],
                "editionMbid": edition["mbid"],
                "complete": not missing,
                "missingTracks": missing,
            })
        owned_groups = {o["releaseGroupMbid"] for o in owned}
        not_owned = [g for g in artist["releaseGroups"] if g["mbid"] not in owned_groups]
        artists.append({
            "mbid": artist["mbid"],
            "name": artist["name"],
            "owned": owned,
            "missing": [
                {"releaseGroupMbid": g["mbid"], "title": g["title"], "primaryType": g["primaryType"], "firstReleaseDate": g["firstReleaseDate"]}
                for g in not_owned if shown_by_default(g, policy)
            ],
            "missingHiddenByDefault": sum(1 for g in not_owned if not shown_by_default(g, policy)),
            "releaseGroupCount": len(artist["releaseGroups"]),
        })

    return {
        "about": "Answer key for the generated test library. Paths are relative to the music folder.",
        "catalogueFetchedAt": catalogue["fetchedAt"],
        "policy": policy,
        "artists": artists,
        "files": [{"path": f.path, "format": f.format, "durationSeconds": round(f.duration, 3), **f.expect} for f in files],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", type=Path, default=HERE / "out", help="output folder (default: tools/testlib/out)")
    args = parser.parse_args()
    if not shutil.which("ffmpeg"):
        raise SystemExit("ffmpeg is required")

    scenario = json.loads((HERE / "scenario.json").read_text())
    catalogue = json.loads((HERE / "catalogue.json").read_text())
    editions = {e["mbid"]: e for e in catalogue["editions"]}

    files = [f for album in scenario["albums"] for f in album_files(album, editions[album["release"]])]
    files += [stray_file(s, editions) for s in scenario["strays"]]

    music = args.out / "music"
    if music.exists():
        shutil.rmtree(music)
    with ThreadPoolExecutor() as pool:
        list(pool.map(lambda f: encode(f, music), files))

    key = answer_key(scenario, catalogue, editions, files)
    (args.out / "expected.json").write_text(json.dumps(key, indent=1, ensure_ascii=False) + "\n")

    print(f"{len(files)} files in {music}")
    for artist in key["artists"]:
        partial = sum(1 for o in artist["owned"] if not o["complete"])
        print(f"  {artist['name']}: {len(artist['owned'])} albums owned ({partial} partial), "
              f"{len(artist['missing'])} missing shown, {artist['missingHiddenByDefault']} hidden")
    print(f"answer key: {args.out / 'expected.json'}")


if __name__ == "__main__":
    main()
