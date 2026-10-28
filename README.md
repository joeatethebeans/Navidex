# Navidex

A self-hosted service for music in the style of Sonarr and Radarr. It shows what's missing from your library,
fetches it through plugins you choose, imports it through beets, and tells your media server about it. It sits
beside Navidrome rather than replacing it: Navidex finds, fetches and imports; Navidrome keeps streaming.

> **Status: Phase 3 (listen now).** Navidex scans your library, matches it against MusicBrainz, and shows each
> artist's albums as owned, partly owned, covered or missing. Ask for a track, an album or everything an artist is
> missing, and it fetches each one through your retrieval plugins in turn, checks it, files it through beets, and
> tells Navidrome to scan. The Activity page shows every attempt; Review holds what needs a person. Or press
> **Listen** to hear something before deciding: it's fetched to a separate store, plays in the web app's player
> beside the music you have, and is deleted an hour after you last play it unless you add it to the library.

## Layout

| Path | What |
| --- | --- |
| `src/Navidex.Contracts` | Plugin contracts: index, artwork and media-server plugins, and the retrieval HTTP wire types |
| `src/Navidex.Core` | The core service: public API at `/api/v1`, data model, library scan and matching, task queue |
| `src/Navidex.Plugins.MusicBrainz` | Built-in catalogue plugins: MusicBrainz releases, at its one request a second, and Cover Art Archive covers |
| `src/Navidex.Plugins.Navidrome` | The built-in media-server connector for Navidrome, over the Subsonic API, and its cover art from your own files |
| `src/Navidex.Plugins.Wikimedia` | Built-in artwork plugin: artist photos from Wikimedia Commons, by MusicBrainz ID through Wikidata |
| `src/Navidex.Plugins.Deezer` | Built-in artwork plugin: artist pictures from Deezer's public catalogue, by name |
| `src/Navidex.Plugins.FanartTv` | Built-in artwork plugin: artist pictures from fanart.tv, with your own API key |
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

Then open `http://localhost:8787`, make the admin account, and press **Scan library**. To fetch, add your media server and retrieval plugins
under **Settings → Plugins**: Navidrome, YouTube (yt-dlp) and the inbox folder are offered under **Add**. The beets
worker is the only container that writes the music folder, so run it as the user who owns it (`PUID` and `PGID`).

Everyone signs in, at home too; tick **Remember me** to stay signed in on that browser for 30 days. The admin, made
at setup, may do everything. Add other people under **Settings → Users**, giving each only what they need: Browse and
Listen for someone who just plays music, Request to let them fetch it, and so on through Review, Format, Remove, Scan,
Settings, API keys and Manage users. Nobody can give a permission they don't have, and only the admin can change the
admin's account. If the admin's password is lost, start Navidex once with `Navidex__ResetLogin=true` and set a new
one; other users and keys are kept.

The first scan of a big library takes a while. Matching is quick, but filling in the catalogue (every album's
editions, to tell official albums from bootlegs and to see what's covered by tracks on other albums) costs about one
MusicBrainz request per album, at one request a second. Pages fill in as it goes, and later scans only refresh
what's stale (discographies weekly, editions monthly).

From source (.NET 10 SDK):

```bash
dotnet run --project src/Navidex.Core
```

### For other apps

Apps call the API at `/api/v1` with a key from **Settings → API**, sent in the `X-Api-Key` header. Each key belongs to
the user who made it, has only the permissions it needs, and can never do more than its owner may. Navidex tells them what happens through
`GET /api/v1/events` and through signed webhooks set under **Settings → Webhooks**. The
[client guide](docs/client-guide.md) explains it all; the interactive reference is at `/api/docs`, and the OpenAPI
document at `/openapi/v1.json`.

### Listen now and events

Tracks fetched to listen to live in `Navidex:ListenNowPath` (the `listen-now` volume), never in the music folder.
Each one is kept for the time set under **Settings → Fetching** after it was last played (an hour by default), then
deleted; **Add to library** files the same file through beets without fetching it again. The API for other apps:

| Call | What |
| --- | --- |
| `POST /api/v1/listen` `{kind, id, editionId?}` | Fetch a track, or an album's missing tracks in order. Returns one item per track. |
| `GET /api/v1/listen?all=true` | Every listen-now track still to play, fetch or add; with `all`, also those discarded, expired or added |
| `GET /api/v1/listen/{id}` | Its state (`requested`, `fetching`, `playable`, `importing`, `inLibrary`, `failed`, `expired`), and a signed `streamUrl` with its `mediaType` while playable |
| `POST /api/v1/listen/{id}/add`, `DELETE /api/v1/listen/{id}` | Add it to the library, or discard it now |
| `GET /api/v1/tracks/{id}/play` | A signed stream URL for a track you have |
| `GET /api/v1/events` | Server-sent events naming what changed (`requests`, `listen`, `library`, `tasks`), so apps reload instead of polling, and what happened (`request.completed`, `listen.ready`, ...) |

Stream URLs need no credentials, support range requests for seeking, and stop working after 12 hours.

### Removing music

The Library page can remove an artist, album or track (`POST /api/v1/library/remove`, with
`GET /api/v1/library/removal` to see what it covers first). Either the beets worker deletes the files, with any
folder left holding only artwork, and the media server is told; or the files stay and are hidden, so Navidex stops
counting them until they're put back on the Review page.

### Files that don't match

Review lists files that couldn't be matched confidently. Besides approving a suggestion or searching, each can be
set aside (`PUT /api/v1/library/files/{id}/set-aside`, or `POST /api/v1/library/review/set-aside` for many):

- **Exclude**: never matched; the library shows it as its own tags say. Excluded files without a title or artist
  are listed under *Incomplete metadata*.
- **Defer**: off the review list. Every scan tries it again and matches it only when it beats the match threshold.
- **Hide**: out of the library altogether.

Excluded files with a title and artist are your own music: the library lists them as their tags say, under the artist
the library already has by that name or one of their own (`GET /api/v1/library/local/artists/{id}` and
`/local/albums/{id}`), with the picture embedded in them or beside them, such as folder.jpg
(`GET /api/v1/library/files/{id}/cover`). Settings → *Prefer your files' own tags and covers* shows matched music that
way too, wherever its files have them.

Each list is at the bottom of Review, where any file can be put back. The Scan library button's menu can also match
unmatched files again, or everything except what you matched yourself (`POST /api/v1/library/scan` with
`{ "rematch": "unmatched" }` or `"all"`), and format the whole library.

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
