#!/usr/bin/env bash
#
# watchdog.sh - hang detector for report-service.
#
# Probes the health endpoint up to WATCHDOG_ATTEMPTS times. Exits 0 as soon as
# any attempt succeeds; exits non-zero only after ALL attempts fail. The
# bounded retry loop (no untimed `while true`) absorbs a single transient blip
# so a momentary GC pause or in-flight redeploy doesn't trigger a needless
# restart, mirroring the Docker healthcheck's `retries: 3`.
#
# Wired into report-service-watchdog.service, whose `OnFailure=` runs
# report-service-restart.service when this script exits non-zero. This script
# itself never restarts anything and runs unprivileged (the report-service
# user) — only systemd (root) issues the restart.
#
# Tunables (override via the service's EnvironmentFile):
#   WATCHDOG_ATTEMPTS    number of probes before declaring failure (default 3)
#   WATCHDOG_SLEEP_SECS  delay between probes                       (default 3)
#   REPORT_SERVICE_HEALTH_URL  passed through to healthcheck.sh
#
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROBE="${SCRIPT_DIR}/healthcheck.sh"

ATTEMPTS="${WATCHDOG_ATTEMPTS:-3}"
SLEEP_SECS="${WATCHDOG_SLEEP_SECS:-3}"

if [[ ! -x "$PROBE" ]]; then
    echo "watchdog: probe not found or not executable at ${PROBE}" >&2
    exit 2
fi

for (( attempt = 1; attempt <= ATTEMPTS; attempt++ )); do
    if "$PROBE"; then
        exit 0
    fi
    if (( attempt < ATTEMPTS )); then
        sleep "$SLEEP_SECS"
    fi
done

echo "watchdog: health probe failed ${ATTEMPTS}x (${SLEEP_SECS}s apart); signalling restart" >&2
exit 1
