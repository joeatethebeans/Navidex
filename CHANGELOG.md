# Changelog

What changed in each release of Melodex. Versions follow [semantic versioning](https://semver.org).

## Unreleased

Everything on the way to 1.0.

### New

- **Melodex**: Navidex has a new name, a logo and a pink theme. Installs from before upgrade in place; see
  [upgrading](docs/guide/upgrading.md#from-navidex).
- **Themes**: pink, green, blue, grey, purple and amber, each light or dark or following your device, a black mode
  for OLED screens, and high contrast with colour-blind-safe status colours. Each user chooses their own under
  Settings → Appearance.
- **The guide**, in `docs/guide` and in the web app under Settings → Help.
- **Users**: accounts for everyone, with permissions to browse, request, listen, review, format, remove, scan, change
  settings, make API keys and manage users. Everyone signs in; Remember me keeps a browser signed in for 30 days.
- **API keys** belong to the user who made them and can do no more than they may.
- **Review**: exclude, defer or hide files, and put them back from the lists at the bottom.
- **Your own music**: excluded files show as their tags say, with their own artist and album pages and covers.
- **Formatting**: rewrite files' tags, covers and folders from the catalogue, for a track, album, artist or the whole
  library.
- **Scan menu**: match unmatched files again, or everything, and format the library.
- **Tasks** can be paused, cancelled and reordered; tracks have pages of their own.
- **About**, under Settings: the version, what it was built from, and links to GitHub.
- **Missing view**: each kind of release (live, compilations, remixes, demos, soundtracks, bootlegs and the rest) has
  its own choice of Show, Hide or **New songs only**, which lists a release just for the songs found nowhere else.
- **File metadata**: your files' tags and their cover art can each be preferred on their own.
- **Search remembers**: the last search, its results and where you'd scrolled stay for a while after you leave the
  Search page (5 minutes by default; each user sets their own under Settings → Appearance).
- **Choosing a cover**: an album's or song's cover can be picked from every release it's on, its files' own, or a picture
  you upload. It's written into the files straight away (and an album's folder gets it as `cover.jpg`), and Melodex
  shows it from then on.
- **Lyrics**: a new kind of plugin, with LRCLIB set up out of the box. Scans find lyrics for songs without them and
  write them as `.lrc` files beside each song, into its tags, or both. Each song's page says whether it has synced
  lyrics, shows them, and can look for them again.
- **The full-screen player**: the cover large over a blur of itself, a visualiser that moves with the music, and the
  lyrics lit line by line as they're sung (click one to play from it), or the queue. The player bar is laid out as
  music apps have it, with a volume control.
- **Playlists**: each user's own, of songs in the library and files of your own, in folders made by a separator in their
  names (`/` by default, set on your account page). Add to them from a song's or album's page or the player. With
  **Sync playlists** ticked in the Navidrome plugin, and each user's Navidrome login linked on their account page (kept
  encrypted), they're kept the same in Navidrome both ways, and formatting that moves files doesn't break them there.
- **Getting your library ready**: just after starting, the top bar and the Library page say the library's lists are
  being worked out, which takes a while the first time on a big library.

### Fixed

- Browsers could keep an old copy of the web app's page, showing *Loading Navidex* and failing to change the theme.
- The ownership meters and legend use brighter colours in light themes.
- The Log page's Info, Warnings and Errors toggles have tick boxes in their own colours, not black.
- Status pills on Downloads, Activity and elsewhere have room to breathe.
- The Library page's controls wait while a view loads, so changes don't pile up behind it.
- Choosing a cover offers each album, EP or single once, not every edition of it, and no picture twice.
- In the full-screen player, where it's playing from is in the middle of the page, and the lyrics sit beside the cover
  with no box, fading at the top and bottom, as tall as the cover and controls.
- The Navidrome plugin asks plainly for an admin's username and password; the account page only mentions Navidrome
  when it can sync playlists.
- Album, song and playlist pages line the cover up with the buttons, and an album's Get button is the size of the rest.
  A song's page shows each album it's on with its status, as the artist's page does.
- *Fetch once, keep it with the smallest release* now does that: a song fetched on its own goes with a single before an
  album. It used to behave as *keep it with the main album*.

### Changed

- The media-server connector contract is 1.1, adding `IPlaylistSync` for each user's playlists. Connectors built for
  1.0 still load.

- Artist and album pages open at once with what Melodex knows, and fill in as MusicBrainz answers, rather than waiting
  for it. What they fetch goes ahead of catalogue updates running in the background. `GET /artists/{id}` and
  `GET /albums/{id}` take `quick=true` to answer this way, with `fetching` set while the rest is on its way.
- The library's lists of artists, albums and tracks are kept on disk, so they show at once after a restart, and carry
  an ETag, so switching between them only downloads what changed. Views already loaded show at once.
- Settings → General reads more easily: songs on more than one release are described for every kind of release, and
  numbers have − and + buttons that keep every line the same height.
- `LibrarySettings` has `showing`, `preferFileTags` and `preferFileCovers` in place of `shownTypes`,
  `hideSecondaryTypes`, `hideUnofficial` and `preferFileMetadata`; saved settings carry over.
- API keys start `mdx_`; `ndx_` keys still work.
- Webhook headers are `Melodex-Signature`, `Melodex-Event` and `Melodex-Delivery`.
- The local network is no longer let in without signing in.
