# Navidex

A self-hosted service for music in the style of Sonarr and Radarr. It shows what's missing from your library,
fetches it through plugins you choose, imports it through beets, and tells your media server about it. It sits
beside Navidrome rather than replacing it: Navidex finds, fetches and imports; Navidrome keeps streaming.

> **Status: Phase 2 (fetching).** Navidex scans your library, matches it against MusicBrainz, and shows each
> artist's albums as owned, partly owned, covered or missing. Ask for a track, an album or everything an artist is
> missing, and it fetches each one through your retrieval plugins in turn, checks it, files it through beets, and
> tells Navidrome to scan. The Activity page shows every attempt; Review holds what needs a person.

## Layout

| Path | What |
| --- | --- |
| `src/Navidex.Contracts` | Plugin contracts: index plugins, media-server connectors, and the retrieval HTTP wire types |
| `src/Navidex.Core` | The core service: public API at `/api/v1`, data model, library scan and matching, task queue |
| `src/Navidex.Plugins.MusicBrainz` | The built-in index plugin: MusicBrainz, at its one request a second |
| `src/Navidex.Plugins.Navidrome` | The built-in media-server connector: Navidrome, over the Subsonic API |
| `src/Navidex.Client` | Typed client and models for the public API |
| `src/Navidex.Web` | The web app (Blazor WebAssembly), served by the core and talking only to the public API |
| `tests/Navidex.Core.Tests` | Tests, including the Phase 1 gate against the test library |
| `beets-worker` | The import engine: files staged downloads into the library with beets ([docs](docs/import-worker.md)) |
| `plugins/retriever-ytdlp` | Retrieval plugin: YouTube Music through yt-dlp |
| `plugins/retriever-inbox` | Retrieval plugin: a folder you drop files into |
| `tests/fixtures/musicbrainz` | Recorded MusicBrainz responses the gate test replays |
| `docs/contracts` | The three plugin contracts, written down, including the retrieval OpenAPI spec |
| `tools/testlib` | Generates a test music library from real MusicBrainz data, with an answer key |
| `sandbox` | A throwaway stack (Navidrome, Navidex, the beets worker and both plugins) over a copy of the test library |
| `docker-compose.yml` | The stack. It joins an existing Navidrome setup. |

## Running

With Docker:

```bash
MUSIC_PATH=/path/to/your/music PUID=$(id -u) PGID=$(id -g) docker compose up --build
```

Then open `http://localhost:8787` and press **Scan library**. To fetch, add your media server and retrieval plugins
under **Settings → Plugins**: Navidrome, `http://retriever-ytdlp:8080` and `http://retriever-inbox:8080`. The beets
worker is the only container that writes the music folder, so run it as the user who owns it (`PUID` and `PGID`). The OpenAPI document is at `/openapi/v1.json`.

The first scan of a big library takes a while. Matching is quick, but filling in the catalogue (every album's
editions, to tell official albums from bootlegs and to see what's covered by tracks on other albums) costs about one
MusicBrainz request per album, at one request a second. Pages fill in as it goes, and later scans only refresh
what's stale (discographies weekly, editions monthly).

From source (.NET 10 SDK):

```bash
dotnet run --project src/Navidex.Core
```

The database lives in `Navidex:DataPath` (`data/` by default) and is migrated on startup. Override any path with
environment variables such as `Navidex__LibraryPath=/music`.

## Development

```bash
python3 tools/testlib/generate.py   # once: the gate test needs the test library, and is skipped without it
dotnet test
```

To try things out without a real library, see [docs/testing.md](docs/testing.md):

```bash
sandbox/sandbox.sh up
```

Schema changes need a migration:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/Navidex.Core -o Data/Migrations
```

## Plugins

Navidex is pluggable on three axes: where catalogue data comes from (index plugins), how music is fetched
(retrieval plugins), and where it ends up (media-server connectors). See [docs/contracts](docs/contracts/README.md).

The core ships no media sources. Retrieval plugins are separate services that speak an HTTP contract; the two in
`plugins/` live here for now and will move to their own repositories. Users are responsible for the plugins they run
and for complying with each source's terms.

## Licence

[MIT](LICENSE)
