# Import worker (beets)

The import worker is the only component that writes the music library. It runs [beets](https://beets.io) in its
own container, in `beets-worker/`. The core calls it after a fetched file passes verification.

Unlike retrieval plugins it's part of Navidex, not a plugin: there is one, and its API is internal. It's written
down here so it can be swapped or debugged.

## Why beets doesn't guess

By the time a file reaches the worker, Navidex knows which MusicBrainz release it belongs to and which recording
it is. The worker pins beets to that release (`search_ids`) and takes it whatever beets' match score says. It then
checks every file landed on the recording Navidex named, and refuses the import if not. So beets never files a
song under the wrong album because the tags looked close enough.

Navidex is the library's database, so beets' own library only exists for one import at a time. Nothing beets
remembers can disagree with Navidex. One side effect: `%aunique{}` never adds anything, because beets sees no other
albums to tell apart.

## `POST /v1/import`

```json
{
  "stagingDirectory": "0199f0c2-…",
  "release": { "musicBrainzId": "76df3287-…", "title": "Dummy", "artist": "Portishead", "date": "1994-08-22", "trackCount": 11 },
  "files": [
    { "path": "track.opus", "recordingId": "145f5c43-…", "trackId": null, "title": "Glory Box", "artist": "Portishead", "disc": 1, "position": 11 }
  ],
  "destinationDirectory": null,
  "pathTemplate": "$albumartist/$album%aunique{}/$track $title",
  "musicBrainzServer": "https://musicbrainz.org"
}
```

| Field | Meaning |
| --- | --- |
| `stagingDirectory` | The job's folder, relative to the staging volume (`/staging`). |
| `release` | The edition the files belong to. Its MusicBrainz ID pins the match. |
| `files` | Each file, relative to the staging folder, with the recording it is and its place on the release. |
| `destinationDirectory` | A folder relative to the library to put the files in, instead of the template's. Navidex sets it when you already own part of the album, so new tracks join the files you have wherever they live. Only the file name comes from the template. |
| `pathTemplate` | A [beets path format](https://beets.readthedocs.io/en/stable/reference/pathformat.html), set under Settings → Fetching. |
| `musicBrainzServer` | The server Navidex's MusicBrainz plugin uses, so a mirror applies to imports too. |

Steps:

1. Copy the files into a scratch folder and tag them with what Navidex sent (title, artist, album, disc and
   track number, MusicBrainz recording, track and release IDs), so beets maps each one onto the right track.
2. Run a beets import pinned to the release, writing tags in place.
3. Check each file matched its recording.
4. Move each file to `library/<template path>`, or `library/<destinationDirectory>/<file name>`. If a file is
   already there, add `.1`, `.2` and so on before the extension, as beets does.
5. Delete the staged originals.

Response `200`:

```json
{
  "files": [
    {
      "source": "track.opus",
      "path": "Portishead/Dummy/11 Glory Box.opus",
      "durationSeconds": 305.573,
      "format": "Opus",
      "tags": { "title": "Glory Box", "mb_trackid": "145f5c43-…", "mb_albumid": "76df3287-…", "isrc": "GBAQT9400066" }
    }
  ]
}
```

`tags` holds the beets fields that have values: titles, artists, track and disc numbers, dates, label, catalogue
number, ISRC, country, media, album type and the MusicBrainz IDs.

Errors are `{ "error": "message", "code": "..." }`: `422 import-failed` when beets can't import the files (for
example MusicBrainz didn't return the release, or a file didn't match its recording), and `400` for a malformed
request. Navidex shows the message on the Activity page and marks the request failed, without trying other
plugins, because another copy of the song would fail the same way.

## `GET /v1/health`

`{ "status": "healthy" | "unhealthy", "message": "..." }`. Unhealthy when the staging or library volume isn't
mounted, or the library isn't writable.

## Running

One import runs at a time, because beets' configuration is global. Mount the staging volume at `/staging` and the
library at `/music`, and run the container as the user that owns the library. Beets reads no config file, only its
defaults plus the settings above.
