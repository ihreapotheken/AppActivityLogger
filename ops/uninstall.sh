#!/usr/bin/env bash
#
# uninstall.sh - stop, disable and (optionally) remove the report-service.
#
# Usage:
#   sudo ./uninstall.sh                  # stop + disable + remove unit file
#   sudo ./uninstall.sh --purge          # also remove /opt/report-service,
#                                        # /etc/report-service and the
#                                        # report-service user/group.
#   sudo ./uninstall.sh --purge --purge-data
#                                        # additionally remove /srv/reports.
#                                        # THIS DELETES INGESTED REPORTS.
#
# /var/log/report-service is left alone by default so post-mortem logs
# survive an uninstall. Remove it by hand if you want it gone.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Re-exec under sudo if not root.
if [[ $EUID -ne 0 ]]; then
    exec sudo -E bash "$0" "$@"
fi

PURGE=0
PURGE_DATA=0
for arg in "$@"; do
    case "$arg" in
        --purge)       PURGE=1 ;;
        --purge-data)  PURGE_DATA=1 ;;
        -h|--help)
            sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *) printf '[uninstall] unknown arg: %s\n' "$arg" >&2; exit 2 ;;
    esac
done

if (( PURGE_DATA == 1 && PURGE == 0 )); then
    echo "[uninstall] --purge-data requires --purge." >&2
    exit 2
fi

SERVICE_USER="report-service"
SERVICE_GROUP="report-service"
INSTALL_DIR="/opt/report-service"
CONFIG_DIR="/etc/report-service"
REPORTS_DIR="/srv/reports"
UNIT_DST="/etc/systemd/system/report-service.service"

log() { printf '[uninstall] %s\n' "$*"; }

# Stop + disable (best effort; do not fail if already absent).
if systemctl list-unit-files report-service.service >/dev/null 2>&1; then
    if systemctl is-active --quiet report-service; then
        log "stopping report-service"
        systemctl stop report-service || true
    fi
    if systemctl is-enabled --quiet report-service 2>/dev/null; then
        log "disabling report-service"
        systemctl disable report-service || true
    fi
fi

if [[ -f "$UNIT_DST" ]]; then
    log "removing ${UNIT_DST}"
    rm -f "$UNIT_DST"
fi

log "systemctl daemon-reload"
systemctl daemon-reload
systemctl reset-failed report-service 2>/dev/null || true

if (( PURGE == 1 )); then
    if [[ -d "$INSTALL_DIR" ]]; then
        log "removing ${INSTALL_DIR}"
        rm -rf "$INSTALL_DIR"
    fi
    if [[ -d "$CONFIG_DIR" ]]; then
        log "removing ${CONFIG_DIR}"
        rm -rf "$CONFIG_DIR"
    fi
    if id -u "$SERVICE_USER" >/dev/null 2>&1; then
        log "removing user ${SERVICE_USER}"
        userdel "$SERVICE_USER" || true
    fi
    if getent group "$SERVICE_GROUP" >/dev/null; then
        log "removing group ${SERVICE_GROUP}"
        groupdel "$SERVICE_GROUP" 2>/dev/null || true
    fi
    if (( PURGE_DATA == 1 )) && [[ -d "$REPORTS_DIR" ]]; then
        log "removing ${REPORTS_DIR} (ingested reports deleted)"
        rm -rf "$REPORTS_DIR"
    elif [[ -d "$REPORTS_DIR" ]]; then
        log "keeping ${REPORTS_DIR} (pass --purge-data to remove)"
    fi
fi

log "done."
