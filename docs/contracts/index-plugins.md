# Index plugins (contract v1.0)

Index plugins answer **what exists**: artists, their albums, each album's editions and track listings. They fill
the catalogue side of the comparison that produces the missing view. Code: `Melodex.Contracts.Plugins.Index`.

## Entities

| Contract type | Meaning | Typical ID (MusicBrainz) |
| --- | --- | --- |
| `IndexArtist` | A performer or group | Artist MBID |
| `IndexAlbum` | The album as people think of it, across all editions | Release group MBID |
| `IndexEdition` | One specific release: deluxe, remaster, regional pressing | Release MBID, plus UPC |
| `IndexTrack` | A recording, which can appear on several editions | Recording MBID, plus ISRCs |
| `IndexTrackListing` | A recording's place on one medium of one edition | — |

In search results, an `IndexTrack` may also carry the `Album` it's best known from, so the track can be shown with
its cover. The core only imports it when it doesn't know the album yet, so a partial date or status there doesn't
overwrite better details.

Every entity carries its own `Id` as a `ProviderId` in the plugin's scheme (for example `musicbrainz:5b11f4ce-…`)
and may carry `ExternalIds` in other schemes when the plugin knows them. Durations are `TimeSpan`; dates are
strings as precise as known (`yyyy`, `yyyy-MM` or `yyyy-MM-dd`).

Albums carry a primary `AlbumType` (Album, Ep, Single, Broadcast, Other) and a list of `AlbumSecondaryType`s
(Compilation, Live, Remix, Demo, Soundtrack…), mirroring MusicBrainz release-group types. The core uses these to
hide live albums, compilations, remixes, demos and soundtracks from the missing view by default.

## Required: `IIndexPlugin`

| Method | Returns | Notes |
| --- | --- | --- |
| `SearchAsync(query)` | Artists, albums and tracks, each ordered by relevance | `query.Kinds` narrows the kinds; empty means all. Respect `query.Limit` per kind. |
| `GetArtistAsync(artistId)` | One artist, or `null` | |
| `GetArtistAlbumsAsync(artistId)` | Every album of the artist, of every type | Don't filter; the core applies the user's filters. |
| `GetAlbumAsync(albumId)` | The album with all editions, media and track listings, or `null` | This is what ownership is computed against. Set `HasOfficialRelease` on the album if the source knows release statuses, so bootleg-only albums can be hidden. Keep it to as few upstream requests as the source allows: the core calls it for every album the missing view shows. |
| `CheckHealthAsync()` | A `HealthReport` | Backs the Test button. Make one cheap upstream call. |

Error handling: return `null` (or an empty list) when the entity doesn't exist; throw for anything else (network
failures, upstream errors, rate-limit responses). The core retries and backs off.

## Optional capabilities

Each is an extra interface on the same class, declared in the manifest by name.

| Capability | Interface | Purpose |
| --- | --- | --- |
| `isrc-lookup` | `IIsrcLookup` | Recordings by ISRC; lets the core match files tagged with only an ISRC |
| `upc-lookup` | `IUpcLookup` | Editions by barcode |
| `external-ids` | — | The plugin fills `ExternalIds` with other providers' IDs |
| `popularity` | `IPopularityProvider` | Track popularity from 0 to 1, for ordering |

## Errors

Return `null` or an empty list for things that don't exist. When the source can't answer right now (overloaded,
rate-limiting, down), throw `IndexUnavailableException`. The core rests the plugin for a while and moves that work to
the back of the queue. It tries the work again up to the number of times set under "When MusicBrainz is busy" in
Settings (default 3), and then leaves it pending for the next scan. After every five such answers in a row it waits
five minutes. After fifteen in a row it stops asking for the rest of the scan. A plain `HttpRequestException` or a
timeout counts the same way. Anything else is logged as a failure.

## What the core does for you

- **Caching.** Every response is cached in the catalogue tables and the crosswalk. Plugins must not cache.
- **Retries.** The plugin's `HttpClient` retries 503 and 429 responses (honouring `Retry-After`) and dropped GET
  connections, waiting up to 30 seconds between attempts, before the plugin sees the response.
- **Rate limiting.** The core never exceeds `maxRequestsPerSecond` from the manifest. MusicBrainz asks for about
  one request per second, so the MusicBrainz plugin declares `1` and accepts a custom server URL in its settings
  for users running a mirror.
- **Several plugins at once.** Index plugins can run side by side. The core merges results by crosswalk IDs
  (ISRC for recordings, UPC for editions), with a per-field priority order set by the user.

## Reference plugin

MusicBrainz, built on [MetaBrainz.MusicBrainz](https://www.nuget.org/packages/MetaBrainz.MusicBrainz). Deezer and
Discogs are the next candidates after v1.
