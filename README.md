# Navidex

A self-hosted service for music in the style of Sonarr and Radarr. It shows what's missing from your library,
fetches it through plugins you choose, imports it through beets, and tells your media server about it. It sits
beside Navidrome rather than replacing it: Navidex finds, fetches and imports; Navidrome keeps streaming.

> **Status: Phase 0 (foundations).** The solution skeleton, core data model and all three plugin contracts are in
> place. Nothing is fetched yet.

## Layout

| Path | What |
| --- | --- |
| `src/Navidex.Contracts` | Plugin contracts: index plugins, media-server connectors, and the retrieval HTTP wire types |
| `src/Navidex.Core` | The core service: public API at `/api/v1`, data model, and later the job queue and workers |
| `tests/Navidex.Core.Tests` | Tests for the contracts, data model and API |
| `docs/contracts` | The three plugin contracts, written down, including the retrieval OpenAPI spec |
| `docker-compose.yml` | The stack. It joins an existing Navidrome setup. |

## Running

With Docker:

```bash
MUSIC_PATH=/path/to/your/music docker compose up --build
```

Then open `http://localhost:8787/api/v1/health`. The OpenAPI document is at `/openapi/v1.json`.

From source (.NET 10 SDK):

```bash
dotnet run --project src/Navidex.Core
```

The database lives in `Navidex:DataPath` (`data/` by default) and is migrated on startup. Override any path with
environment variables such as `Navidex__LibraryPath=/music`.

## Development

```bash
dotnet test
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
