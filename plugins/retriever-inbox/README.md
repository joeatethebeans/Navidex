# Inbox folder retrieval plugin

A Melodex retrieval plugin (contract v1.0) that offers audio files you drop into a folder. Use it for music you
bought, ripped or got from a friend: put the files in the inbox, then request the track or album in Melodex.

It lives in the Melodex repository for now and will move to its own repository later.

## How files are matched

Each search reads the inbox's tags (cached until a file changes):

- A MusicBrainz recording ID tag (as Picard writes) matching the request is a certain match. So is a matching ISRC.
- Otherwise the title, artist and length are compared, the same way as the yt-dlp plugin.
- For an album, each folder is a possible album. It's offered only if it has a file for every requested track.
  A matching MusicBrainz release ID tag makes it certain.

On fetch, the files are moved into staging (or copied, with **Keep originals**) and empty folders are tidied away.
Beets then tags and files them like anything else.

Melodex looks in the inbox when a request is made or retried. If you add files for a request that's waiting in
Review, press Retry on it.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `keepOriginals` | `false` | Copy files out of the inbox instead of moving them. |
| `minScore` | `0.6` | Files that match less well than this aren't offered. |

## Running it

```yaml
retriever-inbox:
  build: plugins/retriever-inbox
  volumes:
    - /path/to/inbox:/inbox
    - staging:/staging
```

Then add `http://retriever-inbox:8080` under Settings → Plugins in Melodex (it's one of the templates under Add). The plugin makes no network calls.
Environment variables: `INBOX_ROOT` (default `/inbox`), `STAGING_ROOT` (default `/staging`) and `PARALLEL_FETCHES`
(default `2`).

## Development

```bash
python -m venv .venv && .venv/bin/pip install -r requirements.txt -r requirements-dev.txt
.venv/bin/pytest
```

The tests make small FLAC files with ffmpeg, and are skipped without it.
