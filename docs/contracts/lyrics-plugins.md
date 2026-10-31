# Lyrics plugins (contract v1.1)

Lyrics plugins find the words of songs, time-synced where the source has them, so the player can follow along. They sit
under Lyrics in Settings → Plugins. Code: `Melodex.Contracts.Plugins.Lyrics`.

## How the core uses them

1. After each scan, when **Settings → General → Lyrics** says to, the core queues a *Finding lyrics* task for every
   matched song in the library that has none. A song's page can also look for them at any time.
2. A file that already has lyrics, in an `.lrc` file beside it or its lyrics tag, keeps them, and no plugin is asked.
3. Otherwise the core asks the enabled lyrics plugins in the user's order, once per song however many files it has,
   and keeps the first answer.
4. The beets worker writes the lyrics where the settings say: an `.lrc` file beside each song with the same name (the
   default), the song's lyrics tag, or both. Synced lyrics are written as LRC; plain ones as they are.
5. When every plugin says it has nothing, the core remembers that for a month. When a plugin fails (it throws),
   nothing is remembered, and the next scan tries again.

On a song's page, someone with the Format permission can also search the plugins that declare `search` and choose
lyrics themselves, edit them, delete them, or exclude the song from lyrics matching so no task looks for them again.

Lyrics travel with their song: formatting that moves a song moves its `.lrc` too, and removing a song removes it.

## `ILyricsPlugin`

| Method | Purpose | Notes |
| --- | --- | --- |
| `FindAsync(query)` | The song's lyrics | `null` when the plugin has none. Throw on failures, so the core retries later. |
| `SearchAsync(search)` | Lyrics that might be the song, best first, to choose from (1.1) | Defaults to none, so 1.0 plugins load unchanged. Throw on failures. |
| `CheckHealthAsync()` | A `HealthReport` | Backs the Test button. |

`LyricsQuery` carries the song's title and artist credit, the title of the album its file is matched to, the file's
length (to tell versions apart), and the recording's IDs as `ProviderId`s, such as its MusicBrainz recording ID and
ISRCs.

`LyricsSearch` (1.1) has the `text` someone typed, which may be empty, and the song as a `LyricsQuery`, to search for
when nothing's typed and to put the closest first. Each `LyricsResult` has the title, artist, album and length the
source has, and the `FoundLyrics`.

`FoundLyrics` has `synced` lyrics in LRC format (`[mm:ss.xx] words`, one line each), `plain` lyrics, or both. A song
with no words is `instrumental`, with neither; the core remembers that and writes nothing.

## Capabilities

| Capability | Meaning |
| --- | --- |
| `synced` | Can return time-synced lyrics |
| `plain` | Can return plain lyrics |
| `search` | Can search, for someone choosing lyrics by hand (1.1) |

## Built-in plugins

| Plugin | Notes |
| --- | --- |
| LRCLIB | Set up by default; no account. Synced and plain lyrics from [LRCLIB](https://lrclib.net), an open, community-made database. Found by title, artist, album and length, then by searching for the title and artist and taking the closest in length (within 3 seconds). Searches LRCLIB for what's typed, or the song's title and artist, with those of the song's length first. Its server can be changed, for those who run their own copy. |
