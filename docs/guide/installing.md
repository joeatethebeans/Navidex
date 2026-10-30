# Installing

Melodex runs as a few containers: the core (the web app and API), the beets worker (the only thing that writes your
music folder), and one container per retrieval plugin. Navidrome isn't part of the stack: Melodex joins the one you
have.

## With Docker Compose

Download [`docker-compose.yml`](https://github.com/joeatethebeans/Melodex/blob/main/docker-compose.yml) into a folder
of its own, then start it, pointing it at the folder Navidrome reads:

```bash
MUSIC_PATH=/path/to/your/music PUID=$(id -u) PGID=$(id -g) docker compose up -d
```

| Variable | What | Default |
| --- | --- | --- |
| `MUSIC_PATH` | Your music folder, the same one Navidrome reads | `./music` |
| `PUID`, `PGID` | The user and group that own the music folder. The beets worker and plugins run as them. | `1000` |
| `MELODEX_PORT` | The port the web app listens on | `8787` |
| `MELODEX_VERSION` | Which release to run, such as `1.0.0`; `latest` follows each release | `latest` |
| `INBOX_PATH` | The folder the inbox plugin watches for files you drop in | `./inbox` |

You can put these in a `.env` file beside `docker-compose.yml` instead of typing them each time.

Then open `http://<your server>:8787`.

## The folders Melodex keeps

| Volume | Holds |
| --- | --- |
| `config` | The database, cached artwork, the keys that sign sessions, and the logs. Back this up. |
| `staging` | Downloads waiting to be checked and filed. Shared by the core, beets and the plugins. |
| `listen-now` | Songs fetched to listen to without adding them. Each is deleted after a while. |

The music folder is mounted read-only in the core: only the beets worker writes it, so run that as the folder's owner.

## SELinux and podman

On hosts with SELinux, files carry the label of the container that wrote them, and downloads pass from a plugin through
beets to the core and Navidrome. Give every Melodex container, and Navidrome, the same
`security_opt: ["label=level:s0:c100,c200"]` so they can read each other's files.

## From source

With the .NET 10 SDK:

```bash
git clone https://github.com/joeatethebeans/Melodex.git
cd Melodex
dotnet run --project src/Melodex.Core
```

This runs the core and web app only. Fetching needs the beets worker and a retrieval plugin; `docker compose -f
docker-compose.yml -f docker-compose.build.yml up --build` builds and runs them all from the source.

Next: [Getting started](getting-started.md).
