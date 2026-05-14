#!/usr/bin/env bash
#
# healthcheck.sh - probe the report-service /api/health endpoint.
#
# Exits 0 on HTTP 2xx, non-zero otherwise. Designed for use from a
# systemd .timer, cron, or external monitor. Quiet on success; curl's
# error is surfaced on stderr on failure.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

HEALTH_URL="${REPORT_SERVICE_HEALTH_URL:-http://127.0.0.1:8080/api/health}"

exec curl -fsS --max-time 5 -o /dev/null "$HEALTH_URL"
