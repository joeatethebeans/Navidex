# Getting started

## Making the admin account

The first time you open Melodex it asks for the admin account. The admin may do everything, and only they can change
their own account. Everyone signs in, even at home; tick **Remember me** to stay signed in on that browser for 30
days.

## The setup steps

After the admin account, Melodex walks through what it needs. Each step can be skipped and done later from Settings.

1. **Your music folder.** Melodex checks it can read the folder and counts what's there. If it finds nothing, check
   `MUSIC_PATH` and the folder's permissions.
2. **Navidrome.** Melodex tells Navidrome to scan when music arrives, and shows the covers from your own files. Give it
   Navidrome's address and a Navidrome user.
3. **Fetching.** Choose where music comes from: YouTube Music (yt-dlp), and an inbox folder you drop files into.
   Without a retrieval plugin you can still browse and see what's missing.
4. **The first scan.** Melodex reads every file and matches it against MusicBrainz.

## The first scan

Matching is quick, but filling in the catalogue (each album's editions, to tell official albums from bootlegs and to
see which songs you have on other albums) costs about one MusicBrainz request per album, at one request a second. A
big library takes a while. Pages fill in as it goes, and later scans only refresh what's stale: discographies weekly
and editions monthly.

## What the library shows

Each artist's albums are one of:

| Status | Means |
| --- | --- |
| **Owned** | You have every song |
| **Partly owned** | You have some of the songs |
| **Covered** | You have every song, but on other albums, such as a compilation |
| **Missing** | You have none of it |

Live albums, compilations, remixes and the like are hidden by default; show them from the artist page. Files Melodex
couldn't match confidently wait on the **Review** page: see [Your library](library.md).

Next: [Requests and listening](requests-and-listening.md).
