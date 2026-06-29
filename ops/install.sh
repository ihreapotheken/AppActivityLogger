#!/usr/bin/env bash
#
# install.sh - install the ReportService systemd unit on a Linux host.
#
# Usage:
#   sudo ./install.sh [--help]
#
# Prerequisites:
#   1. Publish the app first, from the repository root:
#        dotnet publish src/ReportService -c Release -o ops/publish
#      This script expects the resulting directory at:
#        <script dir>/publish/
#   2. The .NET 8 runtime (or the self-contained publish output) must be
#      installed at /usr/bin/dotnet. Adjust ExecStart in the unit file if
#      your dotnet binary lives elsewhere.
#
# The script is idempotent: it only creates users, directories and the env
# file if they do not already exist. The binary tree and unit file are
# always refreshed.
#
# The service is enabled but NOT started automatically. After editing the
# environment file, start it manually with:
#   sudo systemctl start report-service
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
    sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    exit 0
fi

# Re-exec under sudo if not root.
if [[ $EUID -ne 0 ]]; then
    exec sudo -E bash "$0" "$@"
fi

SERVICE_USER="report-service"
SERVICE_GROUP="report-service"
INSTALL_DIR="/opt/report-service"
CONFIG_DIR="/etc/report-service"
ENV_FILE="${CONFIG_DIR}/report-service.env"
REPORTS_DIR="/srv/reports"
LOG_DIR="/var/log/report-service"
UNIT_SRC="${SCRIPT_DIR}/report-service.service"
UNIT_DST="/etc/systemd/system/report-service.service"
PUBLISH_DIR="${SCRIPT_DIR}/publish"
# Helper scripts the watchdog unit invokes, installed alongside the binaries at
# a stable path the unit files reference (/opt/report-service/ops/...).
OPS_DST="${INSTALL_DIR}/ops"
OPS_SCRIPTS=(healthcheck.sh watchdog.sh)
# Health-watchdog units installed next to the main unit. report-service.service
# is handled separately above so its presence is validated explicitly.
EXTRA_UNITS=(report-service-watchdog.service report-service-watchdog.timer report-service-restart.service)

log()  { printf '[install] %s\n' "$*"; }
fail() { printf '[install] ERROR: %s\n' "$*" >&2; exit 1; }

[[ -d "$PUBLISH_DIR" ]] || fail "publish dir not found at ${PUBLISH_DIR}. Run 'dotnet publish src/ReportService -c Release -o ops/publish' first."
[[ -f "$UNIT_SRC" ]]    || fail "unit file not found at ${UNIT_SRC}."
command -v rsync      >/dev/null 2>&1 || fail "rsync is required."
command -v systemctl  >/dev/null 2>&1 || fail "systemctl is required."

# --- user / group --------------------------------------------------------
if ! getent group  "$SERVICE_GROUP" >/dev/null; then
    log "creating group ${SERVICE_GROUP}"
    groupadd --system "$SERVICE_GROUP"
fi
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    log "creating user ${SERVICE_USER}"
    useradd --system \
            --gid "$SERVICE_GROUP" \
            --home-dir "$INSTALL_DIR" \
            --shell /usr/sbin/nologin \
            "$SERVICE_USER"
fi

# --- directories ---------------------------------------------------------
# /opt/report-service: binary tree, read-only to service user.
install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$INSTALL_DIR"
# /srv/reports: writable by service (group-writable for ops inspection).
install -d -m 0770 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$REPORTS_DIR"
# /var/log/report-service: writable by service.
install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$LOG_DIR"
# /etc/report-service: root-owned config dir, readable by service group.
install -d -m 0750 -o root            -g "$SERVICE_GROUP" "$CONFIG_DIR"

# --- binaries ------------------------------------------------------------
log "syncing publish/ -> ${INSTALL_DIR}"
TMP_STAGING="$(mktemp -d)"
trap 'rm -rf "$TMP_STAGING"' EXIT
rsync -a --delete "${PUBLISH_DIR}/" "${TMP_STAGING}/"
# Apply final ownership/perms in staging before swapping into place so we
# never leave a partially-chowned tree at the live path.
chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "$TMP_STAGING"
find "$TMP_STAGING" -type d -exec chmod 0750 {} +
find "$TMP_STAGING" -type f -exec chmod 0640 {} +
# Binaries and dotnet deps need exec on files that actually need it. The
# safest approach is to preserve whatever exec bits the publish output
# declared. Re-run rsync with -p to propagate permissions.
rsync -a --delete "${PUBLISH_DIR}/" "${INSTALL_DIR}/"
chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "$INSTALL_DIR"
chmod 0750 "$INSTALL_DIR"

# --- env file ------------------------------------------------------------
if [[ ! -f "$ENV_FILE" ]]; then
    log "creating ${ENV_FILE} (placeholder values, edit before start)"
    umask 077
    cat > "$ENV_FILE" <<'EOF'
# /etc/report-service/report-service.env
#
# Environment variables for report-service.service.
# ASP.NET Core configuration uses the double-underscore convention:
#   ReportService__<PropertyName>[__<NestedProperty>]=value
#
# Edit the placeholders below BEFORE starting the service.

# Bind URL (Kestrel). 127.0.0.1 is safest; front with nginx/caddy for TLS.
ASPNETCORE_URLS=http://127.0.0.1:8080
ASPNETCORE_ENVIRONMENT=Production
DOTNET_PRINT_TELEMETRY_MESSAGE=false

# Report service configuration. Values here override appsettings.json.
# ReportService__ApiKey: REQUIRED. Replace with a strong random secret.
ReportService__ApiKey=CHANGE_ME_TO_A_STRONG_SECRET
ReportService__ReportsRoot=/srv/reports
# Anchor writable SQLite state under ReportsRoot so ProtectSystem=strict doesn't block it.
ReportService__SqliteDbPath=/srv/reports/reports.db
ReportService__AuthAbuseDbPath=/srv/reports/auth-abuse.db
# ReportService__MaxUploadBytes=10485760
# ReportService__RateLimitPermitsPerMinute=60
EOF
    chown root:"$SERVICE_GROUP" "$ENV_FILE"
    chmod 0600 "$ENV_FILE"
else
    log "keeping existing ${ENV_FILE}"
    # Re-assert ownership/permissions in case someone relaxed them.
    chown root:"$SERVICE_GROUP" "$ENV_FILE"
    chmod 0600 "$ENV_FILE"
fi

# --- watchdog helper scripts --------------------------------------------
# Installed AFTER the publish rsync above (which uses --delete and would
# otherwise wipe this subdir) so they survive every re-run.
log "installing watchdog scripts -> ${OPS_DST}"
install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$OPS_DST"
for script in "${OPS_SCRIPTS[@]}"; do
    [[ -f "${SCRIPT_DIR}/${script}" ]] || fail "missing helper script ${SCRIPT_DIR}/${script}"
    install -m 0750 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "${SCRIPT_DIR}/${script}" "${OPS_DST}/${script}"
done

# --- unit files ----------------------------------------------------------
log "installing unit -> ${UNIT_DST}"
install -m 0644 -o root -g root "$UNIT_SRC" "$UNIT_DST"
for unit in "${EXTRA_UNITS[@]}"; do
    [[ -f "${SCRIPT_DIR}/${unit}" ]] || fail "missing unit ${SCRIPT_DIR}/${unit}"
    log "installing unit -> /etc/systemd/system/${unit}"
    install -m 0644 -o root -g root "${SCRIPT_DIR}/${unit}" "/etc/systemd/system/${unit}"
done

log "systemctl daemon-reload"
systemctl daemon-reload
log "systemctl enable report-service"
systemctl enable report-service >/dev/null
# Enable the hang-watchdog timer now (it's safe before the service starts —
# OnBootSec/first tick just probes and no-ops until the service is up). The
# restart/watchdog .service units are pulled in on demand and aren't enabled.
log "systemctl enable --now report-service-watchdog.timer"
systemctl enable --now report-service-watchdog.timer >/dev/null

cat <<EOF

report-service installed.

Next steps:
  1. Edit ${ENV_FILE} and replace the ApiKey placeholder.
  2. Start the service:
       sudo systemctl start report-service
  3. Check status / logs:
       systemctl status report-service
       journalctl -u report-service -f
  4. The health watchdog (restarts the service if it hangs) is already enabled:
       systemctl status report-service-watchdog.timer
       journalctl -u report-service-watchdog.service -f
EOF
