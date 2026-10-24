# Navidex

A self-hosted service for music in the style of Sonarr and Radarr. It shows what's missing from your library,
fetches it through plugins you choose, imports it through beets, and tells your media server about it. It sits
beside Navidrome rather than replacing it: Navidex finds, fetches and imports; Navidrome keeps streaming.

> **Status: Phase 1 (the missing view).** Navidex scans your library, matches it against MusicBrainz, and shows
> each artist's albums as owned, partly owned, covered or missing, in a web app served by the core. Nothing is
> fetched yet; that's Phase 2.

## Layout

| Path | What |
| --- | --- |
| `src/Navidex.Contracts` | Plugin contracts: index plugins, media-server connectors, and the retrieval HTTP wire types |
| `src/Navidex.Core` | The core service: public API at `/api/v1`, data model, library scan and matching, task queue |
| `src/Navidex.Plugins.MusicBrainz` | The built-in index plugin: MusicBrainz, at its one request a second |
| `src/Navidex.Client` | Typed client and models for the public API |
| `src/Navidex.Web` | The web app (Blazor WebAssembly), served by the core and talking only to the public API |
| `tests/Navidex.Core.Tests` | Tests, including the Phase 1 gate against the test library |
| `tests/fixtures/musicbrainz` | Recorded MusicBrainz responses the gate test replays |
| `docs/contracts` | The three plugin contracts, written down, including the retrieval OpenAPI spec |
| `tools/testlib` | Generates a test music library from real MusicBrainz data, with an answer key |
| `sandbox` | A throwaway Navidrome + Navidex stack over the test library |
| `docker-compose.yml` | The stack. It joins an existing Navidrome setup. |

## Running

With Docker:

```bash
MUSIC_PATH=/path/to/your/music docker compose up --build
```

Then open `http://localhost:8787` and press **Scan library**. The OpenAPI document is at `/openapi/v1.json`.

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

The core ships no media sources. Retrieval plugins live in their own repositories, and users are responsible for
the plugins they run and for complying with each source's terms.

## Licence

[MIT](LICENSE)
