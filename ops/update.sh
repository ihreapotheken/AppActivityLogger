#!/usr/bin/env bash
#
# update.sh - rolling update of an installed report-service.
#
# Expects a fresh publish output at <script dir>/publish/ (produced via
# `dotnet publish src/ReportService.Admin -c Release -o ops/publish` — the
# merged host that serves both the admin UI and the ingestion routes).
#
# Flow:
#   1. snapshot the current /opt/report-service -> /opt/report-service.bak
#   2. stop the service
#   3. rsync --delete publish/ -> /opt/report-service/
#   4. start the service
#   5. poll /api/health for up to 30s
#   6. on failure: restore the snapshot and restart; exit non-zero
#   7. on success: print new build info (assembly version if available)
#
# Usage:
#   sudo ./update.sh
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
fi

if [[ $EUID -ne 0 ]]; then
    exec sudo -E bash "$0" "$@"
fi

SERVICE_USER="report-service"
SERVICE_GROUP="report-service"
INSTALL_DIR="/opt/report-service"
BACKUP_DIR="/opt/report-service.bak"
PUBLISH_DIR="${SCRIPT_DIR}/publish"
HEALTH_URL="http://127.0.0.1:8080/api/health"
HEALTH_TIMEOUT=30
MAIN_DLL="ReportService.Admin.dll"

log()  { printf '[update] %s\n' "$*"; }
fail() { printf '[update] ERROR: %s\n' "$*" >&2; exit 1; }

[[ -d "$PUBLISH_DIR" ]]  || fail "publish dir not found at ${PUBLISH_DIR}."
[[ -d "$INSTALL_DIR" ]]  || fail "${INSTALL_DIR} not found - run install.sh first."
command -v rsync     >/dev/null 2>&1 || fail "rsync is required."
command -v systemctl >/dev/null 2>&1 || fail "systemctl is required."
command -v curl      >/dev/null 2>&1 || fail "curl is required."

# --- snapshot current install -------------------------------------------
log "snapshotting ${INSTALL_DIR} -> ${BACKUP_DIR}"
rm -rf "$BACKUP_DIR"
# cp -a preserves perms/owners. rsync would also work; cp -a is sufficient.
cp -a "$INSTALL_DIR" "$BACKUP_DIR"

rollback() {
    log "rolling back from ${BACKUP_DIR}"
    systemctl stop report-service || true
    rsync -a --delete "${BACKUP_DIR}/" "${INSTALL_DIR}/"
    chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "$INSTALL_DIR"
    systemctl start report-service || true
}

# --- stop, swap, start --------------------------------------------------
log "stopping report-service"
systemctl stop report-service || true

log "syncing new build into ${INSTALL_DIR}"
if ! rsync -a --delete "${PUBLISH_DIR}/" "${INSTALL_DIR}/"; then
    rollback
    fail "rsync failed; rolled back."
fi
chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "$INSTALL_DIR"
chmod 0750 "$INSTALL_DIR"

log "starting report-service"
if ! systemctl start report-service; then
    rollback
    fail "systemctl start failed; rolled back."
fi

# --- health poll --------------------------------------------------------
log "polling ${HEALTH_URL} for up to ${HEALTH_TIMEOUT}s"
deadline=$(( $(date +%s) + HEALTH_TIMEOUT ))
healthy=0
while (( $(date +%s) < deadline )); do
    if curl -fsS --max-time 2 "$HEALTH_URL" >/dev/null 2>&1; then
        healthy=1
        break
    fi
    sleep 1
done

if (( healthy == 0 )); then
    log "health check failed after ${HEALTH_TIMEOUT}s"
    rollback
    fail "update rolled back; previous version restored."
fi

log "health OK"

# --- build info ---------------------------------------------------------
DLL_PATH="${INSTALL_DIR}/${MAIN_DLL}"
if [[ -f "$DLL_PATH" ]]; then
    # Prefer mtime + size since parsing an assembly version without .NET
    # tooling on the host is awkward. Operators who want the AssemblyVersion
    # can read it via `dotnet ${MAIN_DLL} --version` if the app supports it.
    MTIME="$(date -r "$DLL_PATH" '+%Y-%m-%d %H:%M:%S %z' 2>/dev/null || stat -c '%y' "$DLL_PATH")"
    SIZE="$(stat -c '%s' "$DLL_PATH" 2>/dev/null || wc -c < "$DLL_PATH")"
    log "new build: ${MAIN_DLL} size=${SIZE}B mtime=${MTIME}"
fi

log "update complete."
