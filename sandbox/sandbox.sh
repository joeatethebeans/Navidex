#!/usr/bin/env bash
# A throwaway Navidrome + Navidex stack over the generated test library, so nothing touches a real library.
#
#   sandbox/sandbox.sh up       generate the test library if needed, start both containers, scan, report
#   sandbox/sandbox.sh status   show what Navidrome has indexed
#   sandbox/sandbox.sh scan     ask Navidrome to rescan and wait for it
#   sandbox/sandbox.sh down     stop and remove the containers and their data
#
# Works with podman or docker. Navidrome credentials are generated on first run into sandbox/.env.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SANDBOX="$ROOT/sandbox"
MUSIC="$ROOT/tools/testlib/out/music"
ENV_FILE="$SANDBOX/.env"

NAVIDROME_IMAGE="docker.io/deluan/navidrome:0.64.2"
CORE_IMAGE="localhost/navidex/core:dev"
NETWORK="navidex-sandbox"
NAVIDROME_PORT="${NAVIDROME_PORT:-4533}"
NAVIDEX_PORT="${NAVIDEX_PORT:-8787}"

ENGINE="$(command -v podman || command -v docker || true)"
[[ -n "$ENGINE" ]] || { echo "needs podman or docker" >&2; exit 1; }

if [[ ! -f "$ENV_FILE" ]]; then
  printf 'NAVIDROME_USER=admin\nNAVIDROME_PASSWORD=%s\n' "$(head -c 18 /dev/urandom | base64 | tr -d '/+=')" > "$ENV_FILE"
fi
# shellcheck source=/dev/null
source "$ENV_FILE"

navidrome_url="http://localhost:$NAVIDROME_PORT"

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

status() {
  local scan
  scan="$(subsonic getScanStatus)"
  echo "Navidrome: $navidrome_url (user $NAVIDROME_USER, password in sandbox/.env)"
  echo "  tracks indexed: $(sed -n 's/.*"count":\([0-9]*\).*/\1/p' <<<"$scan")"
  echo "  artists: $(subsonic getArtists | grep -o '"albumCount"' | wc -l)"
  echo "Navidex:   http://localhost:$NAVIDEX_PORT/api/v1/health -> $(curl -sf "http://localhost:$NAVIDEX_PORT/api/v1/health" || echo 'not running')"
}

up() {
  if [[ ! -d "$MUSIC" ]]; then
    echo "Generating the test library..."
    python3 -I "$ROOT/tools/testlib/generate.py"
  fi
  if ! "$ENGINE" image exists "$CORE_IMAGE" 2>/dev/null && ! "$ENGINE" image inspect "$CORE_IMAGE" >/dev/null 2>&1; then
    echo "Building the Navidex image..."
    "$ENGINE" build -q -f "$ROOT/docker/core.Dockerfile" -t "$CORE_IMAGE" "$ROOT" >/dev/null
  fi

  "$ENGINE" network inspect "$NETWORK" >/dev/null 2>&1 || "$ENGINE" network create "$NETWORK" >/dev/null

  "$ENGINE" rm -f navidex-sandbox-navidrome navidex-sandbox-core >/dev/null 2>&1 || true
  "$ENGINE" run -d --name navidex-sandbox-navidrome --network "$NETWORK" --network-alias navidrome \
    -p "$NAVIDROME_PORT:4533" \
    -v "$MUSIC:/music:ro,z" -v navidex-sandbox-navidrome:/data \
    -e ND_LOGLEVEL=warn -e ND_SCANNER_SCHEDULE=0 -e ND_ENABLEINSIGHTSCOLLECTOR=false \
    "$NAVIDROME_IMAGE" >/dev/null
  "$ENGINE" run -d --name navidex-sandbox-core --network "$NETWORK" --network-alias navidex \
    -p "$NAVIDEX_PORT:8080" \
    -v "$MUSIC:/music:ro,z" -v navidex-sandbox-config:/config \
    "$CORE_IMAGE" >/dev/null

  wait_for Navidrome "$navidrome_url/ping"
  # First run only: Navidrome accepts creating the admin while it has no users.
  curl -sf -o /dev/null -X POST "$navidrome_url/auth/createAdmin" -H 'Content-Type: application/json' \
    -d "{\"username\":\"$NAVIDROME_USER\",\"password\":\"$NAVIDROME_PASSWORD\"}" || true
  wait_for Navidex "http://localhost:$NAVIDEX_PORT/api/v1/health"

  subsonic startScan >/dev/null
  wait_for_scan
  status
}

case "${1:-}" in
  up) up ;;
  status) status ;;
  scan) subsonic startScan >/dev/null; wait_for_scan; status ;;
  down)
    "$ENGINE" rm -f navidex-sandbox-navidrome navidex-sandbox-core >/dev/null 2>&1 || true
    "$ENGINE" volume rm -f navidex-sandbox-navidrome navidex-sandbox-config >/dev/null 2>&1 || true
    "$ENGINE" network rm "$NETWORK" >/dev/null 2>&1 || true
    echo "Sandbox removed." ;;
  *) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
