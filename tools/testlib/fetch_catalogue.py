#!/usr/bin/env python3
"""Snapshot the MusicBrainz data the test library is built from.

Reads scenario.json and writes catalogue.json: every release group of each artist (with the statuses of
its releases, so bootleg-only groups can be told apart), plus full track listings for the editions the
scenario owns. The snapshot is committed, so generating the library needs no network and is repeatable.

Run it again only to refresh the data; it keeps to MusicBrainz's one request a second.
"""

import json
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
API = "https://musicbrainz.org/ws/2"
USER_AGENT = "Navidex-testlib/0.1 (test fixture generator for the Navidex project)"

_last_request = 0.0


def get(path: str, **params) -> dict:
    global _last_request
    wait = 1.1 - (time.monotonic() - _last_request)
    if wait > 0:
        time.sleep(wait)
    url = f"{API}/{path}?{urllib.parse.urlencode({**params, 'fmt': 'json'})}"
    for attempt in range(5):
        _last_request = time.monotonic()
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": USER_AGENT})) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code != 503 or attempt == 4:
                raise
            time.sleep(2 ** attempt)
    raise AssertionError("unreachable")


def browse(entity: str, key: str, **params) -> list[dict]:
    items, offset = [], 0
    while True:
        page = get(entity, limit=100, offset=offset, **params)
        items += page[key]
        offset += len(page[key])
        print(f"  {entity}: {offset}/{page[f'{entity}-count']}", file=sys.stderr)
        if not page[key] or offset >= page[f"{entity}-count"]:
            return items


def artist_credit(credits: list[dict]) -> str:
    return "".join(c["name"] + c.get("joinphrase", "") for c in credits)


def snapshot_artist(mbid: str) -> dict:
    artist = get(f"artist/{mbid}")
    print(f"{artist['name']}", file=sys.stderr)
    groups = browse("release-group", "release-groups", artist=mbid)
    releases = browse("release", "releases", artist=mbid, inc="release-groups")

    statuses: dict[str, set[str]] = {}
    for release in releases:
        statuses.setdefault(release["release-group"]["id"], set()).add(release.get("status") or "None")

    return {
        "mbid": mbid,
        "name": artist["name"],
        "sortName": artist["sort-name"],
        "releaseGroups": sorted(
            (
                {
                    "mbid": g["id"],
                    "title": g["title"],
                    "primaryType": g.get("primary-type"),
                    "secondaryTypes": g.get("secondary-types", []),
                    "firstReleaseDate": g.get("first-release-date") or None,
                    "releaseStatuses": sorted(statuses.get(g["id"], [])),
                }
                for g in groups
            ),
            key=lambda g: (g["firstReleaseDate"] or "9999", g["title"]),
        ),
    }


def snapshot_edition(mbid: str) -> dict:
    release = get(f"release/{mbid}", inc="recordings isrcs artist-credits release-groups")
    print(f"  edition: {release['title']} ({release.get('disambiguation') or release.get('country')})", file=sys.stderr)
    group = release["release-group"]
    return {
        "mbid": release["id"],
        "title": release["title"],
        "date": release.get("date") or None,
        "country": release.get("country"),
        "status": release.get("status"),
        "disambiguation": release.get("disambiguation") or None,
        "barcode": release.get("barcode") or None,
        "artistCredit": artist_credit(release["artist-credit"]),
        "artistMbid": release["artist-credit"][0]["artist"]["id"],
        "releaseGroup": {
            "mbid": group["id"],
            "title": group["title"],
            "primaryType": group.get("primary-type"),
            "secondaryTypes": group.get("secondary-types", []),
            "firstReleaseDate": group.get("first-release-date") or None,
        },
        "media": [
            {
                "position": medium["position"],
                "format": medium.get("format"),
                "tracks": [
                    {
                        "position": track["position"],
                        "number": track["number"],
                        "title": track["title"],
                        "lengthMs": track.get("length") or track["recording"].get("length"),
                        "trackMbid": track["id"],
                        "artistCredit": artist_credit(track.get("artist-credit") or track["recording"]["artist-credit"]),
                        "recording": {
                            "mbid": track["recording"]["id"],
                            "title": track["recording"]["title"],
                            "lengthMs": track["recording"].get("length"),
                            "isrcs": track["recording"].get("isrcs", []),
                        },
                    }
                    for track in medium["tracks"]
                ],
            }
            for medium in release["media"]
        ],
    }


def main() -> None:
    scenario = json.loads((HERE / "scenario.json").read_text())
    editions = {a["release"] for a in scenario["albums"]} | {s["like"]["release"] for s in scenario["strays"] if "like" in s}
    catalogue = {
        "source": "musicbrainz.org",
        "fetchedAt": time.strftime("%Y-%m-%d"),
        "artists": [snapshot_artist(a["mbid"]) for a in scenario["artists"]],
        "editions": [snapshot_edition(mbid) for mbid in sorted(editions)],
    }
    (HERE / "catalogue.json").write_text(json.dumps(catalogue, indent=1, ensure_ascii=False) + "\n")
    print("wrote catalogue.json", file=sys.stderr)


if __name__ == "__main__":
    main()
