#!/usr/bin/env bash
# Load the repo .env (if present) and run one of the two apps.
#
# Usage:
#   ./scripts/run.sh                # runs the ingestion API (default)
#   ./scripts/run.sh ingestion      # same
#   ./scripts/run.sh admin          # runs the admin UI
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

target="${1:-ingestion}"
case "$target" in
    ingestion) PROJECT_DIR="$REPO_DIR/src/ReportService" ;;
    admin)     PROJECT_DIR="$REPO_DIR/src/ReportService.Admin" ;;
    *)
        echo "error: unknown target '$target' (expected 'ingestion' or 'admin')" >&2
        exit 2
        ;;
esac

ENV_FILE="$REPO_DIR/.env"
if [ -f "$ENV_FILE" ]; then
    set -a
    # shellcheck disable=SC1090
    source "$ENV_FILE"
    set +a
fi

exec dotnet run --project "$PROJECT_DIR"
