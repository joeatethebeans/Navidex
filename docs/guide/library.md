# Your library

## Scanning

**Scan library**, at the top right, reads new and changed files and matches them. Its menu has more:

- **Scan for changes**: the same as the button.
- **Match unmatched files again**: files under review and deferred ones, as well as changes.
- **Match everything again**: every file from scratch, except those you matched, excluded or hid yourself. Over a big
  library this takes a while.
- **Format the whole library**: see [Formatting](#formatting).

Scans and other long jobs show on **Activity**, where they can be paused, cancelled or moved in the queue.

## Review

Files Melodex couldn't match confidently wait on **Review**. They don't count as owned until they're matched. For each:

- **Approve** the suggested match, or **Search** for the right one.
- **Exclude** it: it's never matched, and the library shows it as its own tags say. Use this for music no catalogue
  knows, such as your own recordings.
- **Defer** it: it leaves the list, and each scan tries it again, matching it only when it's confident.
- **Hide** it: it leaves the library altogether.

Every file stays in your music folder whichever you choose. The lists at the bottom of Review (Excluded, Incomplete
metadata, Deferred and Hidden) put any file back.

## Your own music

Excluded files with a title and an artist are listed as their tags say: under the artist the library already has by
that name, or one of their own, with the picture embedded in the file or beside it (such as `folder.jpg`). Excluded
files without them are listed under *Incomplete metadata* on Review.

**Settings → General → Prefer your files' own tags and covers** shows matched music that way too, wherever its files
have them.

## Formatting

**Format** rewrites files' tags and covers from the catalogue, and moves them to where your naming scheme says. It's on
each artist, album and track page, and for the whole library in the Scan menu. It shows what will change first, and
nothing is written until you confirm. Set the folder layout under **Settings → General**.

## Removing music

The Library page can remove an artist, album or track. Either the files are deleted (with any folder left holding
only artwork) and Navidrome is told, or they stay and are hidden, so Melodex stops counting them until they're put
back on Review.

Next: [Users and permissions](users-and-permissions.md).
