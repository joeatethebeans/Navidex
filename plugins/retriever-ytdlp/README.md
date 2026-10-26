# YouTube (yt-dlp) retrieval plugin

A Navidex retrieval plugin (contract v1.0) that finds songs on YouTube Music with
[ytmusicapi](https://github.com/sigma67/ytmusicapi) and downloads their audio with [yt-dlp](https://github.com/yt-dlp/yt-dlp).

It lives in the Navidex repository for now and will move to its own repository later.

## What it does

- **Search.** For a track: an ISRC search when the ISRC is known, then songs, then music videos (ranked a little
  lower, since videos often have intros). Live takes, remixes, covers and other artists' songs of the same name rank
  well below the real thing. For an album: YouTube Music albums whose tracks cover the whole requested listing.
- **Fetch.** Downloads into the staging directory the core gives it, as `track.<ext>` for a track or
  `<disc>-<position>.<ext>` for each track of an album.
- **Cancel.** Stops a download between chunks.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `format` | `opus` | `opus` and `m4a` are YouTube's own audio, remuxed without re-encoding. `mp3` is re-encoded. |
| `bitrateKbps` | `320` | MP3 bitrate. Ignored for the other formats. |
| `includeVideos` | `true` | Also offer uploads that aren't listed as songs. |
| `minScore` | `0.5` | Results that match less well than this aren't offered. |
| `cookiesFile` | | A Netscape cookies file inside the container, for age-restricted songs. |

## Running it

```yaml
retriever-ytdlp:
  build: plugins/retriever-ytdlp
  volumes:
    - staging:/staging
```

Then add `http://retriever-ytdlp:8080` under Connections in Navidex (it's one of the templates under Add).

The container needs ffmpeg and deno, which the Dockerfile installs. It contacts `music.youtube.com`,
`www.youtube.com` and `*.googlevideo.com`. Environment variables: `STAGING_ROOT` (default `/staging`) and
`PARALLEL_FETCHES` (default `2`).

yt-dlp changes often to keep up with YouTube. Rebuild the image to pick up a newer version, then press Test in
Navidex to refresh the manifest.

You are responsible for complying with YouTube's terms and the law where you live.

## Development

```bash
python -m venv .venv && .venv/bin/pip install -r requirements.txt -r requirements-dev.txt
.venv/bin/pytest
STAGING_ROOT=/tmp/staging .venv/bin/uvicorn app.main:app --port 8080
```

The tests don't touch the network.
