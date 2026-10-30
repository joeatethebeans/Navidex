# Media-server connectors (contract v1.0)

Connectors tell a media server about new music and confirm it's there. They are the last step of the import
pipeline: once beets has filed a track, the connector triggers a scan, the core polls until the server has the
track, and the server's track ID is recorded so apps can switch to it. Code: `Melodex.Contracts.Plugins.Connectors`.

## Required: `IMediaServerConnector`

| Method | Purpose | Notes |
| --- | --- | --- |
| `TriggerScanAsync(paths)` | Ask the server to pick up changes | `paths` are library-relative. Servers that can't scan selectively may ignore them; declare `selective-scan` if you honour them. |
| `GetScanStatusAsync()` | Whether a scan is running, when the last one finished | Used to wait for a scan before resolving. |
| `ResolveTrackAsync(lookup)` | The server's track ID for a file or catalogue ID, or `null` | `null` means "not there yet"; the core keeps polling with backoff until a timeout. |
| `CheckHealthAsync()` | A `HealthReport` | Backs the Test button: check the URL and credentials. |

`TrackLookup` carries everything the core knows about the file. Try the most specific fields first: the path, then
the MusicBrainz recording ID or ISRC, then title, artist, album and duration.

## Optional capabilities

| Capability | Interface | Purpose |
| --- | --- | --- |
| `selective-scan` | — | `TriggerScanAsync` only scans the given paths |
| `full-scan` | `IFullScan` | Rescan everything, including unchanged files |
| `playlists` | `IPlaylistManager` | List, create and append to playlists |
| `favourites` | `IFavouritesManager` | Star and unstar tracks |
| `library-read` | `ILibraryReader` | Stream every track the server knows, to cross-check with the library scan |

The core and the API only offer actions the active connector declares. A listen-now track can't be starred or added
to a playlist until it's in the library and has a server track ID, whatever the connector supports.

## Reference connector: Navidrome

Through the Subsonic API: `startScan` and `getScanStatus` for scanning, `search3` to resolve tracks, plus
playlists and stars. Navidrome scans the whole library folder, so it doesn't declare `selective-scan`.

Navidrome's Subsonic responses don't report the real file path by default. `path` is synthesised from the tags,
for example `Portishead/Dummy/01-01 - Mysterons.flac` for a file at `Portishead/Dummy (1994)/01 Mysterons.flac`.
So the connector resolves tracks by MusicBrainz recording ID first (`musicBrainzId` on songs), then ISRC (`isrc`),
then title, artist, album and duration. It falls back to the path only when Navidrome is set to report real paths.
Seen on Navidrome 0.64.2 in the sandbox; see [docs/testing.md](../testing.md).

Navidrome can't match files that are retagged and moved in the same scan, so the adopt step for existing libraries
retags in place first, scans, then moves and scans again. The connector is what makes that two-pass flow possible.

Plex and Jellyfin connectors come after v1; nothing in the contract is Subsonic-specific.
