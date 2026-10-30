# Playlists

Each user has their own playlists, under **Playlists** at the top. They hold songs from your library, files of your
own that no catalogue knows, and, when they're synced, songs only your media server has. You need the **Listen**
permission to have them.

## Making them

Make one with **New playlist**, or from the playlist button on a song's or album's page, beside each song you have on
an album, or in the player for the song that's playing. Adding a song that's in the playlist already leaves it out.

On a playlist's page, play it, shuffle it or add it to the queue, move songs up and down, take them out, and rename it
or give it a comment. Deleting a playlist leaves its songs in your library.

A song that leaves your library stays in the playlist, greyed out, so it's there again if the song comes back.

## Folders

Media servers have no folders, so a folder is part of a playlist's name: with the separator `/`, a playlist named
`Moods/Calm` is **Calm** in a folder **Moods**, and `Moods/Evening/Calm` is a folder within that. Change the
separator, or turn folders off, under **Playlists** on your account page.

On the Playlists page, open and close folders, make a playlist in one with its **+**, and rename a folder to rename
everything in it. Renaming a folder with the separator in the new name moves it into another; leaving the name empty
takes what's in it out of the folder.

## Syncing with your media server

With Navidrome, your playlists can be the same in both places, both ways: make or change one in Melodex and it's
changed in Navidrome, make or change one in Navidrome (or an app that uses it) and it's changed in Melodex.

1. Someone who may change settings ticks **Sync playlists** in the Navidrome plugin's settings, under
   **Settings → Plugins**.
2. Each user links their own Navidrome login under **Playlists** on their account page. It's checked first, then
   kept encrypted with Melodex's data protection keys. Someone who may manage users can link a login for someone else
   from **Settings → Users**.

Syncing happens a few seconds after you change a playlist, every 10 minutes for changes made in Navidrome, and when you
click the sync button on the Playlists page. Each user only ever sees and changes their own playlists: Navidrome's
public playlists from other users aren't brought over.

- **Changed in one place** since the last sync: copied to the other.
- **Changed in both**: the later change wins.
- **Deleted in one place**: deleted in the other, unless it's been changed there since, in which case it's kept.
- **Songs Navidrome has that your library doesn't** stay in the playlist, marked *only on the media server*, and go
  back to Navidrome with it.

Formatting can move files, which can give them new IDs in Navidrome. Playlists with songs that moved look for them
again once Navidrome has scanned them, and aren't sent until it's found them all, so nothing drops out of the
Navidrome copy in the meantime.

Unlinking your login stops syncing. Your playlists stay in both places, but no longer change together. Linking a
different login starts afresh with that login's playlists.

Next: [Your library](library.md).
