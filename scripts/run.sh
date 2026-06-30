#!/usr/bin/env bash
# Build + run the MERGED report-service host (admin UI + SDK ingestion routes in one process) via
# Docker Compose — the only supported local runtime. Per CLAUDE.md the service is ALWAYS run
# through Docker Compose, never `dotnet run` directly.
#
# This is the dev instance, pinned to HOST_PORT=8082 so it never collides with the prod (8080) or
# staging (18080) stacks. For the named prod/staging stacks use scripts/stack.sh instead.
#
# Usage:
#   ./scripts/run.sh                 # build (if needed) + start, detached, on :8082
#   ./scripts/run.sh up              # same
#   ./scripts/run.sh rebuild         # force a fresh build + recreate
#   ./scripts/run.sh logs            # tail logs
#   ./scripts/run.sh down            # stop (keep the reports volume)
#   ./scripts/run.sh <other>         # passed straight through to `docker compose`
set -euo pipefail

cd "$(dirname "$0")/.."

# The dev box lives on 8082 (8080=prod, 18080=staging). Honour an explicit override if the caller
# already exported HOST_PORT, otherwise default to the dev port.
: "${HOST_PORT:=8082}"
export HOST_PORT

if [[ ! -f .env ]]; then
    echo "error: .env not found — generate it first with ./scripts/setup.sh" >&2
    exit 1
fi

action="${1:-up}"
case "$action" in
    up)
        docker compose up --build -d
        echo "report-service (merged host) is up on http://localhost:${HOST_PORT}"
        ;;
    rebuild)
        docker compose up --build -d --force-recreate
        echo "report-service (merged host) rebuilt on http://localhost:${HOST_PORT}"
        ;;
    logs) docker compose logs -f ;;
    down) docker compose down ;;
    *)    docker compose "$action" "${@:2}" ;;
esac
