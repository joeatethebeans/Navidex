# Requests and listening

## Asking for music

Press the download button beside a missing track or album, **Get album** or **Get missing tracks** on an album, or
**Get everything missing** on an artist, to request it. Each request goes through
your retrieval plugins in turn, highest priority first:

1. The plugin searches its source and offers candidates.
2. Melodex checks each one: the right length, a real audio file, the quality you asked for under **Settings →
   General → Fetching**.
3. The beets worker tags it from the MusicBrainz release Melodex chose and files it in your music folder.
4. Navidrome is told to scan.

The **Activity** page shows every request and each attempt. A request no plugin could satisfy waits on **Review**:
retry it later, add another plugin, or pick one of the candidates found.

Requests run one at a time, in order. Move them up and down the queue, pause them or cancel them from Activity.

**Ignore** an album or song to stop counting it as missing.

## Listen now

Press **Listen** to hear something before deciding. It's fetched to a separate store, never your music folder, plays
in the player at the bottom of the page beside the music you have, and is deleted a while after you last play it (an
hour by default; change it under **Settings → General → Fetching**). **Add to library** files the same file without fetching it
again.

The **Downloads** page lists what you've fetched to listen to.

## The player

Play, queue and skip from any album, artist or track. The queue is kept in this browser. Songs you don't have are
fetched as the queue reaches them.

Next: [Your library](library.md).
