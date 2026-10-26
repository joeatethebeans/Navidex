# Artwork plugins (contract v1.0)

Artwork plugins find pictures: artist photos, album covers, and the covers of particular editions. They're part of
the catalogue alongside index plugins, and sit under Catalogue on the Connections page, but are a type of their own,
because a source of pictures usually can't answer the questions an index plugin must (fanart.tv can't search for
albums). Code: `Navidex.Contracts.Plugins.Artwork`.

## How the core uses them

The web app never loads artwork from a plugin or its upstream directly. Every picture is served by the core at
`GET /api/v1/artwork/{artist|album|edition}/{id}`:

1. The first time a picture is asked for, the core asks the enabled artwork plugins that declare the subject, in
   the user's order. Each returns candidates, best first; the core downloads them in turn and keeps the first that
   exists.
2. The image is stored under `data/artwork` and served from there from then on, so the browser never contacts the
   upstream, and pictures keep working offline.
3. When every plugin says it has nothing, the core remembers that for a fortnight and answers `404`, and the web
   app shows a placeholder. When a plugin fails (it throws), nothing is remembered, and the next request tries again.
4. Adding, removing, reordering, reconfiguring or switching an artwork plugin clears the cache, so the new order
   takes effect everywhere.

An album in the library is shown with the cover of the edition you have; one you don't have, with the album's.

## `IArtworkPlugin`

| Method | Purpose | Notes |
| --- | --- | --- |
| `FindAsync(query)` | Candidates for a subject, best first | Empty when there's none. Throw on failures, so the core retries later. |
| `DownloadAsync(candidate)` | The image's bytes and media type | `null` if the candidate turns out not to exist; the core tries the next. |
| `CheckHealthAsync()` | A `HealthReport` | Backs the Test button. |

`ArtworkQuery` carries the subject (`artist`, `album` or `edition`), its title or name, the artist credit, and the
IDs the core has for it, its album and its artist, as `ProviderId`s. For MusicBrainz those are the artist, release
group and release IDs. A candidate's `key` is the plugin's own reference, such as a URL; the core only hands it back.

## Capabilities

A plugin declares the subjects it can find pictures of, and is only asked about those:

| Capability | Subject |
| --- | --- |
| `artists` | Artist pictures |
| `albums` | An album's usual cover |
| `editions` | The cover of one edition |

## Borrowed settings

A plugin that works through a service another plugin already connects to can say so with `usesSettingsOf` in its
manifest, naming that plugin's ID. It then has no settings of its own: the core gives it the settings of the first
instance of that plugin, and only offers it once that plugin is set up.

## Built-in plugins

| Plugin | Subjects | Notes |
| --- | --- | --- |
| Cover Art Archive | albums, editions | Set up on first run. Covers MusicBrainz users have uploaded, by release and release group. |
| Navidrome cover art | albums, editions | Not set up by default. The artwork in your own files, through Navidrome; borrows the Navidrome connection. Albums you have only. |
| fanart.tv | artists | Needs a free personal API key. Artist pictures by MusicBrainz artist ID. |
