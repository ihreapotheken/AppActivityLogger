#!/usr/bin/env bash
# Run the cloudflared sibling defined in docker-compose.yml's `tunnel` profile alongside an
# already-running report-service stack. Quick-tunnel mode (the default) prints the ephemeral
# *.trycloudflare.com URL to stdout; named-tunnel mode just brings the container up — the URL
# is whatever DNS record you routed to the tunnel.
#
# Usage:
#   scripts/tunnel.sh up       [production|staging]    # bring the tunnel up + print URL
#   scripts/tunnel.sh url      [production|staging]    # print the URL (quick tunnel only)
#   scripts/tunnel.sh logs     [production|staging]
#   scripts/tunnel.sh down     [production|staging]
#
# The stack must already be running (./scripts/stack.sh <env> up). This script intentionally
# only manages the tunnel container so a `tunnel down` never takes the ingestion API with it.
set -euo pipefail

ACTION=${1:-up}
ENV_NAME=${2:-production}

case "$ENV_NAME" in
  production) ENV_FILE=".env"         ; PROJECT="reports-prod" ;;
  staging)    ENV_FILE=".env.staging" ; PROJECT="reports-staging" ;;
  *)
    echo "usage: $0 <up|url|logs|down> [production|staging]" >&2
    exit 2
    ;;
esac

cd "$(dirname "$0")/.."

if [[ ! -f "$ENV_FILE" ]]; then
  echo "missing env file: $ENV_FILE" >&2
  exit 1
fi

export ENV_FILE
DC=(docker compose --env-file "$ENV_FILE" -p "$PROJECT" --profile tunnel)

print_quick_url() {
  # Pull the most recent *.trycloudflare.com line out of the container's stdout. cloudflared
  # logs the URL once on connect; we retry to cover slow-start and image-pull cases.
  local url=""
  for _ in $(seq 1 30); do
    url=$("${DC[@]}" logs --no-color --tail=200 cloudflared 2>/dev/null \
      | grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' \
      | tail -1 || true)
    if [[ -n "$url" ]]; then
      echo "$url"
      return 0
    fi
    sleep 1
  done
  echo "no quick-tunnel URL appeared in 30s." >&2
  echo "  - named-tunnel mode never prints a URL here; check your DNS route instead." >&2
  echo "  - otherwise run '$0 logs $ENV_NAME' to see what cloudflared is doing." >&2
  return 1
}

case "$ACTION" in
  up)
    "${DC[@]}" up -d cloudflared
    print_quick_url || true
    ;;
  url)
    print_quick_url
    ;;
  logs)
    exec "${DC[@]}" logs -f cloudflared
    ;;
  down)
    "${DC[@]}" rm -sf cloudflared
    ;;
  *)
    echo "unknown action '$ACTION' (expected up|url|logs|down)" >&2
    exit 2
    ;;
esac
