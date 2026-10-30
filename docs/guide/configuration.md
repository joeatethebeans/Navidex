# Configuration

Most settings are in the web app, under **Settings**. The rest are environment variables on the core container, written
`Melodex__Name`. The compose file sets the paths; you rarely need to change them.

| Variable | What | In the container |
| --- | --- | --- |
| `Melodex__DataPath` | The database, artwork cache, session keys and logs | `/config` |
| `Melodex__LibraryPath` | Your music, read-only | `/music` |
| `Melodex__StagingPath` | Downloads waiting to be filed | `/staging` |
| `Melodex__ListenNowPath` | Songs fetched to listen to | `/listen-now` |
| `Melodex__PluginsPath` | Extra in-process plugins (catalogue and media-server) | `/plugins` |
| `Melodex__ImportWorker__Url` | The beets worker | `http://beets-worker:8080` |
| `Melodex__ResetLogin` | `true` clears the admin's password at start: see [Users](users-and-permissions.md#a-lost-admin-password) | |

Variables named `Navidex__…`, from before the rename, still work; a `Melodex__` one wins where both are set.

## In the web app

| Settings section | Holds |
| --- | --- |
| **General** | Songs on more than one album, the missing view, matching, showing your music, the folder layout, and fetching (quality, and how long listen-now songs are kept) |
| **Log** | Recent activity, and how long logs are kept |
| **Plugins** | See [Plugins](plugins.md) |
| **Users**, **API**, **Webhooks** | See [Users and permissions](users-and-permissions.md) and the [client guide](../client-guide.md) |
| **Appearance** | See [Appearance](appearance.md) |
| **Help** | This guide |

Next: [Upgrading](upgrading.md).
