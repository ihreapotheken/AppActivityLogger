#!/usr/bin/env bash
# Spin a named report-service stack — production or staging — without forcing operators to
# remember the project-name + env-file dance every time. Both stacks share the compose file but
# get isolated Docker volumes (via -p) and isolated host ports (via the env file), so they run
# side-by-side and a `down -v` on one never touches the other.
#
# Usage:
#   scripts/stack.sh <env> <up|down|logs|ps|rebuild>
#   scripts/stack.sh production up
#   scripts/stack.sh staging   rebuild
#   scripts/stack.sh staging   down --volumes        # drops only the staging volume
set -euo pipefail

ENV_NAME=${1:-}
ACTION=${2:-up}
shift 2 || true

case "$ENV_NAME" in
  production) ENV_FILE=".env"           ; PROJECT="reports-prod"    ;;
  staging)    ENV_FILE=".env.staging"   ; PROJECT="reports-staging" ;;
  *)
    echo "usage: $0 <production|staging> <up|down|logs|ps|rebuild> [extra-args]" >&2
    exit 2
    ;;
esac

cd "$(dirname "$0")/.."

if [[ ! -f "$ENV_FILE" ]]; then
  echo "missing env file: $ENV_FILE" >&2
  exit 1
fi

# --env-file controls variable substitution (HOST_PORT, ADMIN_HOST_PORT). ENV_FILE is also
# exported so the compose file's `env_file: ${ENV_FILE:-.env}` directive picks the right one
# for the container environment.
export ENV_FILE
DC=(docker compose --env-file "$ENV_FILE" -p "$PROJECT")

case "$ACTION" in
  up)      "${DC[@]}" up -d "$@" ;;
  rebuild) "${DC[@]}" up -d --build "$@" ;;
  down)    "${DC[@]}" down "$@" ;;
  logs)    "${DC[@]}" logs -f "$@" ;;
  ps)      "${DC[@]}" ps "$@" ;;
  *)       "${DC[@]}" "$ACTION" "$@" ;;
esac
