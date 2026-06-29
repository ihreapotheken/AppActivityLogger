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
#
# SECURITY — the tunnel publishes the admin UI (problem reports, crash stacks, analytics PII, the
# NDJSON exports) to the public internet. It reaches the app over the compose network, bypassing
# the 127.0.0.1 host-port bind that the DevAutoSignIn login-bypass relies on. Therefore:
#   1. The target stack's --env-file MUST set ASPNETCORE_ENVIRONMENT=Production (or Staging) and
#      ADMIN_DEV_AUTO_SIGN_IN=false, so real cookie auth gates every admin request. The app also
#      gates DevAutoSignIn to loopback-only as a backstop, but run the tunnel as real Production.
#   2. Per-IP rate limiting + auth-abuse banning only work per-real-client when ProxyHeaders is
#      enabled (compose network in KnownNetworks) and cloudflared forwards X-Forwarded-For. Without
#      it, the tunnel collapses every public client to one source IP and those controls degenerate
#      to a single shared bucket. See the tunnel/proxy sections of .env.example.
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

# Loud preflight: refuse to silently publish an auth-bypassed admin UI. The tunnel escapes the
# loopback isolation that DevAutoSignIn assumes, so a Development / DevAutoSignIn=true host fronted
# by the tunnel would expose the admin UI + PII exports to the internet. The app gates the bypass
# to loopback as a backstop, but warn (and pause) so an operator can't do this by accident.
warn_if_unsafe() {
  local aspnet dev_signin
  # `${VAR:-default}` in the compose file means an UNSET value resolves to the dev default.
  aspnet=$(grep -E '^[[:space:]]*ASPNETCORE_ENVIRONMENT=' "$ENV_FILE" 2>/dev/null | tail -1 | cut -d= -f2- | tr -d '[:space:]')
  dev_signin=$(grep -E '^[[:space:]]*ADMIN_DEV_AUTO_SIGN_IN=' "$ENV_FILE" 2>/dev/null | tail -1 | cut -d= -f2- | tr -d '[:space:]')
  : "${aspnet:=Development}"      # compose default when unset
  : "${dev_signin:=true}"         # compose default when unset

  if [[ "$aspnet" != "Production" && "$aspnet" != "Staging" ]] || [[ "$dev_signin" != "false" ]]; then
    echo "WARNING: $ENV_FILE would run the tunnelled stack with ASPNETCORE_ENVIRONMENT=$aspnet" >&2
    echo "         and ADMIN_DEV_AUTO_SIGN_IN=$dev_signin. Publishing the admin UI to the public" >&2
    echo "         internet like this is unsafe — set ASPNETCORE_ENVIRONMENT=Production and" >&2
    echo "         ADMIN_DEV_AUTO_SIGN_IN=false in $ENV_FILE first (see .env.example)." >&2
    echo "         The app gates DevAutoSignIn to loopback as a backstop, but do not rely on it." >&2
    if [[ -t 0 ]]; then
      read -r -p "         Continue anyway? [y/N] " reply
      [[ "$reply" == "y" || "$reply" == "Y" ]] || { echo "aborted." >&2; exit 3; }
    fi
  fi
}

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
    warn_if_unsafe
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
