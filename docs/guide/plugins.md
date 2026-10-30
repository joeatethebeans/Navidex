# Plugins

Melodex is pluggable in five ways. Manage them under **Settings → Plugins**, where **Add** offers what's available and
**Test** checks each one.

| Kind | Does | Comes with Melodex |
| --- | --- | --- |
| **Catalogue** | Where artists, albums and songs come from | MusicBrainz (its public server, or your own mirror) |
| **Media server** | Told to scan when music arrives; also lends its covers | Navidrome |
| **Retrieval** | Fetches music | YouTube Music (yt-dlp) and the inbox folder, as containers in the compose file |
| **Artwork** | Covers and artist pictures | Cover Art Archive, Wikimedia Commons, Deezer, fanart.tv (with your own key) |
| **Lyrics** | The words of songs, synced where they can be | LRCLIB |

Retrieval, artwork and lyrics plugins are tried in order; move them up and down to change it. A retrieval plugin is a separate service that
speaks a small HTTP contract, so anyone can write one: see [the plugin contracts](../contracts/README.md).

## The inbox

The inbox plugin fetches nothing itself: it offers whatever you drop into its folder (`INBOX_PATH`). Drop in the files
for a request, then press **Retry** on it.

## Your responsibility

Melodex ships no media sources. You're responsible for the plugins you run, and for complying with each source's
terms.

Next: [Configuration](configuration.md).
