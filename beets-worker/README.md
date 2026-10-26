# Navidex beets worker

The only thing that writes the music library. Navidex sends it staged files with the MusicBrainz release and
recordings they are; beets tags them and works out where they go, and the worker moves them into the library.

The HTTP API is in [docs/import-worker.md](../docs/import-worker.md).

## Running it

```yaml
beets-worker:
  build: beets-worker
  user: "1000:1000"  # whoever owns the music library
  volumes:
    - staging:/staging
    - /path/to/music:/music
```

Navidex finds it at `http://beets-worker:8080` by default (`Navidex:ImportWorker:Url`).

## Development

```bash
python -m venv .venv && .venv/bin/pip install -r requirements.txt -r requirements-dev.txt
.venv/bin/pytest
NETWORK_TESTS=1 .venv/bin/pytest  # also imports a file for real, asking MusicBrainz
```
