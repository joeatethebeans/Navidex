#!/usr/bin/env bash
# A throwaway Navidrome + Navidex stack over a copy of the generated test library, so nothing touches a real library.
#
#   sandbox/sandbox.sh up       copy the test library, build what's missing, start everything, configure Navidex, scan
#   sandbox/sandbox.sh build    rebuild the Navidex images
#   sandbox/sandbox.sh status   show what Navidrome has indexed and how Navidex's plugins are doing
#   sandbox/sandbox.sh scan     ask Navidrome to rescan and wait for it
#   sandbox/sandbox.sh down     stop and remove the containers and their data
#   sandbox/sandbox.sh reset    down, and throw away the library copy and the inbox
#
# Works with podman or docker. Navidrome credentials are generated on first run into sandbox/.env.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SANDBOX="$ROOT/sandbox"
TESTLIB="$ROOT/tools/testlib/out/music"
# A writable copy: the beets worker files fetched music into it.
MUSIC="$SANDBOX/music"
INBOX="$SANDBOX/inbox"
ENV_FILE="$SANDBOX/.env"

NAVIDROME_IMAGE="docker.io/deluan/navidrome:0.64.2"
NETWORK="navidex-sandbox"
NAVIDROME_PORT="${NAVIDROME_PORT:-4533}"
NAVIDEX_PORT="${NAVIDEX_PORT:-8787}"
CONTAINERS=(navidex-sandbox-navidrome navidex-sandbox-core navidex-sandbox-beets navidex-sandbox-ytdlp navidex-sandbox-inbox)
VOLUMES=(navidex-sandbox-navidrome navidex-sandbox-config navidex-sandbox-staging)

# Image, then build context and Dockerfile.
IMAGES=(
  "localhost/navidex/core:dev|$ROOT|$ROOT/docker/core.Dockerfile"
  "localhost/navidex/beets-worker:dev|$ROOT/beets-worker|$ROOT/beets-worker/Dockerfile"
  "localhost/navidex/retriever-ytdlp:dev|$ROOT/plugins/retriever-ytdlp|$ROOT/plugins/retriever-ytdlp/Dockerfile"
  "localhost/navidex/retriever-inbox:dev|$ROOT/plugins/retriever-inbox|$ROOT/plugins/retriever-inbox/Dockerfile"
)

ENGINE="$(command -v podman || command -v docker || true)"
[[ -n "$ENGINE" ]] || { echo "needs podman or docker" >&2; exit 1; }

# The beets worker and the plugins run as you, so the library copy and the inbox stay yours.
if [[ "$(basename "$ENGINE")" == podman ]]; then
  AS_YOU=(--userns=keep-id)
else
  AS_YOU=(--user "$(id -u):$(id -g)")
fi

# Files a container writes carry its SELinux categories. Downloads pass from a plugin through beets to the core and
# Navidrome, so on SELinux hosts every container shares one level (elsewhere the option is ignored).
SHARED=(--network "$NETWORK" --security-opt label=level:s0:c100,c200)

if [[ ! -f "$ENV_FILE" ]]; then
  printf 'NAVIDROME_USER=admin\nNAVIDROME_PASSWORD=%s\n' "$(head -c 18 /dev/urandom | base64 | tr -d '/+=')" > "$ENV_FILE"
fi
# shellcheck source=/dev/null
source "$ENV_FILE"

navidrome_url="http://localhost:$NAVIDROME_PORT"
navidex_url="http://localhost:$NAVIDEX_PORT/api/v1"

# Calls the Subsonic API with token auth and prints the JSON response.
subsonic() {
  local method="$1"; shift
  local salt token
  salt="$(head -c 8 /dev/urandom | od -An -tx1 | tr -d ' \n')"
  token="$(printf '%s%s' "$NAVIDROME_PASSWORD" "$salt" | md5sum | cut -d' ' -f1)"
  curl -sf -G "$navidrome_url/rest/$method" \
    --data-urlencode "u=$NAVIDROME_USER" --data-urlencode "t=$token" --data-urlencode "s=$salt" \
    -d v=1.16.1 -d c=navidex-sandbox -d f=json "$@"
}

navidex() {
  local method="$1" path="$2"; shift 2
  curl -sf -X "$method" "$navidex_url$path" -H 'Content-Type: application/json' "$@"
}

wait_for() {
  local what="$1" url="$2"
  for _ in $(seq 1 60); do
    curl -sf -o /dev/null "$url" && return 0
    sleep 1
  done
  echo "$what didn't come up at $url" >&2
  exit 1
}

wait_for_scan() {
  for _ in $(seq 1 120); do
    if subsonic getScanStatus | grep -q '"scanning":false'; then return 0; fi
    sleep 1
  done
  echo "scan didn't finish" >&2
  exit 1
}

build() {
  local only_missing="${1:-}" entry image context file
  for entry in "${IMAGES[@]}"; do
    IFS='|' read -r image context file <<<"$entry"
    if [[ -n "$only_missing" ]] && "$ENGINE" image inspect "$image" >/dev/null 2>&1; then continue; fi
    echo "Building $image..."
    "$ENGINE" build -q -f "$file" -t "$image" "$context" >/dev/null
  done
}

# Adds the plugins to Navidex, once: Navidrome as the media server, then the two retrieval plugins.
configure() {
  local plugins
  plugins="$(navidex GET /plugins)"
  if ! grep -q '"navidex.connector.navidrome"' <<<"$plugins"; then
    navidex POST /plugins -o /dev/null -d "{\"pluginId\":\"navidex.connector.navidrome\",\"settings\":{\"url\":\"http://navidrome:4533\",\"username\":\"$NAVIDROME_USER\",\"password\":\"$NAVIDROME_PASSWORD\"}}"
  fi
  for endpoint in http://retriever-inbox:8080 http://retriever-ytdlp:8080; do
    grep -q "\"$endpoint\"" <<<"$plugins" && continue
    # The plugin may still be starting.
    for _ in $(seq 1 30); do
      navidex POST /plugins -o /dev/null -d "{\"endpoint\":\"$endpoint\"}" && continue 2
      sleep 1
    done
    echo "couldn't add the plugin at $endpoint" >&2
    exit 1
  done
  # Test each, so Settings shows how they're doing.
  for id in $(navidex GET /plugins | grep -o '"id":"[^"]*"' | cut -d'"' -f4); do
    navidex POST "/plugins/$id/test" -o /dev/null || true
  done
}

status() {
  local scan
  scan="$(subsonic getScanStatus)"
  echo "Navidrome: $navidrome_url (user $NAVIDROME_USER, password in sandbox/.env)"
  echo "  tracks indexed: $(sed -n 's/.*"count":\([0-9]*\).*/\1/p' <<<"$scan")"
  echo "  artists: $(subsonic getArtists | grep -o '"albumCount"' | wc -l)"
  echo "Navidex:   http://localhost:$NAVIDEX_PORT -> $(curl -sf "$navidex_url/health" || echo 'not running')"
  navidex GET /plugins 2>/dev/null | python3 -I -c '
import json, sys
for p in json.load(sys.stdin):
    message = p.get("healthMessage")
    print("  " + p["name"] + ": " + (p.get("health") or "not tested") + (" (" + message + ")" if message else ""))' || true
  echo "Library copy: $MUSIC"
  echo "Inbox:        $INBOX (drop files here, then Retry the request)"
}

remove() {
  "$ENGINE" rm -f "${CONTAINERS[@]}" >/dev/null 2>&1 || true
}

up() {
  if [[ ! -d "$TESTLIB" ]]; then
    echo "Generating the test library..."
    python3 -I "$ROOT/tools/testlib/generate.py"
  fi
  if [[ ! -d "$MUSIC" ]]; then
    echo "Copying the test library to $MUSIC..."
    cp -r "$TESTLIB" "$MUSIC"
  fi
  mkdir -p "$INBOX"
  build only-missing

  "$ENGINE" network inspect "$NETWORK" >/dev/null 2>&1 || "$ENGINE" network create "$NETWORK" >/dev/null
  remove

  "$ENGINE" run -d --name navidex-sandbox-navidrome "${SHARED[@]}" --network-alias navidrome \
    -p "$NAVIDROME_PORT:4533" \
    -v "$MUSIC:/music:ro,z" -v navidex-sandbox-navidrome:/data \
    -e ND_LOGLEVEL=warn -e ND_SCANNER_SCHEDULE=0 -e ND_ENABLEINSIGHTSCOLLECTOR=false \
    "$NAVIDROME_IMAGE" >/dev/null
  # The core first, so the staging volume takes its ownership from the core's image.
  "$ENGINE" run -d --name navidex-sandbox-core "${SHARED[@]}" --network-alias navidex \
    -p "$NAVIDEX_PORT:8080" \
    -v "$MUSIC:/music:ro,z" -v navidex-sandbox-config:/config -v navidex-sandbox-staging:/staging \
    localhost/navidex/core:dev >/dev/null
  "$ENGINE" run -d --name navidex-sandbox-beets "${SHARED[@]}" --network-alias beets-worker "${AS_YOU[@]}" \
    -v "$MUSIC:/music:z" -v navidex-sandbox-staging:/staging \
    localhost/navidex/beets-worker:dev >/dev/null
  "$ENGINE" run -d --name navidex-sandbox-ytdlp "${SHARED[@]}" --network-alias retriever-ytdlp "${AS_YOU[@]}" \
    -v navidex-sandbox-staging:/staging \
    localhost/navidex/retriever-ytdlp:dev >/dev/null
  "$ENGINE" run -d --name navidex-sandbox-inbox "${SHARED[@]}" --network-alias retriever-inbox "${AS_YOU[@]}" \
    -v "$INBOX:/inbox:z" -v navidex-sandbox-staging:/staging \
    localhost/navidex/retriever-inbox:dev >/dev/null

  wait_for Navidrome "$navidrome_url/ping"
  # First run only: Navidrome accepts creating the admin while it has no users.
  curl -sf -o /dev/null -X POST "$navidrome_url/auth/createAdmin" -H 'Content-Type: application/json' \
    -d "{\"username\":\"$NAVIDROME_USER\",\"password\":\"$NAVIDROME_PASSWORD\"}" || true
  wait_for Navidex "$navidex_url/health"
  configure

  subsonic startScan >/dev/null
  wait_for_scan
  status
}

case "${1:-}" in
  up) up ;;
  build) build ;;
  status) status ;;
  scan) subsonic startScan >/dev/null; wait_for_scan; status ;;
  down|reset)
    remove
    "$ENGINE" volume rm -f "${VOLUMES[@]}" >/dev/null 2>&1 || true
    "$ENGINE" network rm "$NETWORK" >/dev/null 2>&1 || true
    if [[ "$1" == reset ]]; then
      # Files the containers wrote may belong to a mapped user; let the engine remove them if needed.
      rm -rf "$MUSIC" "$INBOX" 2>/dev/null || "$ENGINE" unshare rm -rf "$MUSIC" "$INBOX"
    fi
    echo "Sandbox removed." ;;
  *) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
