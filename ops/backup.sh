#!/usr/bin/env bash
#
# backup.sh - archive /srv/reports to /var/backups/report-service.
#
# Non-destructive: never deletes source data. Retains the last 7 local
# archives; older ones are pruned. Optional offsite shipment via rsync.
#
# Usage:
#   sudo ./backup.sh
#   sudo ./backup.sh --rsync user@host:/backups/report-service/
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
fi

if [[ $EUID -ne 0 ]]; then
    exec sudo -E bash "$0" "$@"
fi

REPORTS_DIR="/srv/reports"
BACKUP_ROOT="/var/backups/report-service"
RETAIN=7
RSYNC_DEST=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rsync)
            RSYNC_DEST="${2:-}"
            [[ -n "$RSYNC_DEST" ]] || { echo "[backup] --rsync requires an argument" >&2; exit 2; }
            shift 2
            ;;
        *)
            echo "[backup] unknown arg: $1" >&2
            exit 2
            ;;
    esac
done

log()  { printf '[backup] %s\n' "$*"; }
fail() { printf '[backup] ERROR: %s\n' "$*" >&2; exit 1; }

[[ -d "$REPORTS_DIR" ]] || fail "${REPORTS_DIR} not found; nothing to back up."
command -v tar >/dev/null 2>&1 || fail "tar is required."

install -d -m 0700 -o root -g root "$BACKUP_ROOT"

TIMESTAMP="$(date -u +%Y%m%d-%H%M%S)"
ARCHIVE="${BACKUP_ROOT}/reports-${TIMESTAMP}.tar.gz"
TMP_ARCHIVE="${ARCHIVE}.partial"

log "creating ${ARCHIVE}"
# -C + basename keeps the archive layout relative (no leading /srv/).
if ! tar --warning=no-file-changed \
        -czf "$TMP_ARCHIVE" \
        -C "$(dirname "$REPORTS_DIR")" "$(basename "$REPORTS_DIR")"; then
    rm -f "$TMP_ARCHIVE"
    fail "tar failed"
fi
chmod 0600 "$TMP_ARCHIVE"
mv "$TMP_ARCHIVE" "$ARCHIVE"
log "archive size: $(stat -c '%s' "$ARCHIVE" 2>/dev/null || wc -c < "$ARCHIVE") bytes"

# --- retention (keep newest $RETAIN) ------------------------------------
# shellcheck disable=SC2012
mapfile -t ARCHIVES < <(ls -1t "${BACKUP_ROOT}"/reports-*.tar.gz 2>/dev/null || true)
if (( ${#ARCHIVES[@]} > RETAIN )); then
    for old in "${ARCHIVES[@]:RETAIN}"; do
        log "pruning ${old}"
        rm -f "$old"
    done
fi

# --- optional offsite ---------------------------------------------------
if [[ -n "$RSYNC_DEST" ]]; then
    command -v rsync >/dev/null 2>&1 || fail "rsync is required for --rsync."
    log "shipping to ${RSYNC_DEST}"
    # No --delete: offsite retention is the remote's concern.
    rsync -a "$ARCHIVE" "$RSYNC_DEST"
fi

log "backup complete: ${ARCHIVE}"
