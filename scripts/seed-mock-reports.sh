#!/usr/bin/env bash
#
# Seeds the locally-running report-service with a realistic mock dataset.
# Generates ~$COUNT reports spread across $DAYS days, mixing multipart submissions (with a gzipped
# logcat) and JSON-only submissions across multiple platforms / device models / pharmacies / app
# versions / crash types. Intended for the admin's Dashboard, Stats and log-filter screens.
#
# Usage:
#   scripts/seed-mock-reports.sh                # 200 reports over 60 days, default INGEST
#   COUNT=50 DAYS=14 scripts/seed-mock-reports.sh
#   INGEST=https://staging.example/svc API_KEY=... scripts/seed-mock-reports.sh

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

INGEST="${INGEST:-http://127.0.0.1:8080}"
COUNT="${COUNT:-200}"
DAYS="${DAYS:-60}"

if [[ -z "${API_KEY:-}" ]]; then
    if [[ -f "$REPO_ROOT/.env" ]]; then
        API_KEY=$(grep '^ReportService__ApiKey=' "$REPO_ROOT/.env" | head -1 | cut -d= -f2-)
    fi
fi
if [[ -z "${API_KEY:-}" ]]; then
    echo "API_KEY is unset and no ReportService__ApiKey found in .env" >&2
    exit 2
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

PLATFORMS=("android" "ios")
ANDROID_DEVICES=("Pixel 7" "Pixel 8 Pro" "Samsung SM-A536B" "Samsung SM-S918B" "Xiaomi Redmi 10" "OnePlus Nord 2" "Huawei P30" "Generic Android")
IOS_DEVICES=("iPhone 14" "iPhone 15 Pro" "iPhone SE (3rd)" "iPad Air" "iPhone 12")
PHARMACIES=("2163" "35742" "6001" "8842" "1109" "")
APP_VERSIONS=("Cardlink Android 2.3.3" "Cardlink Android 2.3.2" "Cardlink Android 2.3.1" "Cardlink iOS 2.3.3" "Cardlink iOS 2.3.2")
TYPES=("AppCrash" "AppError" "ANR" "OutOfMemory" "NetworkFailure" "ValidationFailure" "PermissionDenied")
TITLES=(
    "Prescription crash"
    "Pharmacy header OOM"
    "Sync stalled"
    "Detached fragment"
    "Network timeout on /reports"
    "Login retry loop"
    "Camera permission denied"
    "Cart total mismatch"
    "Map render timeout"
    "TLS handshake failed"
)
IMPORTANCE=("Blocks me sometimes" "Critical for my work" "Minor inconvenience" "Cannot use the app")

# ----- Logcat templates -------------------------------------------------------
make_logcat() {
    local platform="$1" type="$2" title="$3"
    local now; now=$(date -u +"%m-%d %H:%M:%S.000")
    if [[ "$platform" == "ios" ]]; then
        cat <<LOG
$now  iOS  $type  $title
$now  Thread 1: signal SIGABRT
$now    0   libsystem_kernel.dylib  __pthread_kill + 8
$now    1   libsystem_pthread.dylib  pthread_kill + 268
$now    2   IACardLink              CardlinkCoordinator.handle(_:) + 412
$now    3   IACore                  ReportProblemViewModel.submit() + 188
$now    4   IASDKDevDemo            SceneDelegate.scene(_:willConnectTo:) + 96
LOG
        return
    fi
    case "$type" in
        OutOfMemory)
            cat <<LOG
$now  E AndroidRuntime: java.lang.OutOfMemoryError: $title
$now  E AndroidRuntime: 	at android.graphics.Bitmap.nativeCreate(Native Method)
$now  E AndroidRuntime: 	at de.ihreapotheken.sdk.core.image.LogoCache.decode(LogoCache.kt:71)
LOG
            ;;
        ANR)
            cat <<LOG
$now  W ActivityManager: ANR in de.ihreapotheken.sdk.iasdkdemo.staging
$now  W ActivityManager: Reason: $title
$now  W ActivityManager: 	at de.ihreapotheken.sdk.core.network.SyncBlockingInterceptor.intercept(SyncBlockingInterceptor.kt:23)
LOG
            ;;
        NetworkFailure)
            cat <<LOG
$now  W OkHttp: $title — SocketTimeoutException after 15.0s
$now  W OkHttp: 	at okhttp3.internal.http.RealInterceptorChain.proceed(RealInterceptorChain.kt:109)
LOG
            ;;
        ValidationFailure)
            cat <<LOG
$now  W ReportValidator: $title (field=email; reason=length>512)
$now  W ReportValidator: 	at de.ihreapotheken.sdk.core.validation.ReportValidator.validate(ReportValidator.kt:88)
LOG
            ;;
        PermissionDenied)
            cat <<LOG
$now  E PermissionManager: $title — android.permission.CAMERA denied at runtime
$now  E PermissionManager: 	user denied dialog after 2 prompts
LOG
            ;;
        *)
            cat <<LOG
$now  E AndroidRuntime: FATAL EXCEPTION: main
$now  E AndroidRuntime: java.lang.NullPointerException: $title
$now  E AndroidRuntime: 	at de.ihreapotheken.sdk.cardlink.ui.PrescriptionScreen.onPrescriptionLoaded(PrescriptionScreen.kt:142)
$now  E AndroidRuntime: 	at de.ihreapotheken.sdk.cardlink.ui.PrescriptionViewModel.loadPrescriptions\$1.invokeSuspend(PrescriptionViewModel.kt:88)
LOG
            ;;
    esac
}

# Pick a random element from the variadic list.
pick() { local arr=("$@"); echo "${arr[RANDOM % ${#arr[@]}]}"; }

# ----- Submission helpers -----------------------------------------------------
submitted=0
submit_multipart() {
    local platform="$1" type="$2" title="$3" device="$4" pharmacy="$5" version="$6" importance="$7" message="$8"
    local json_path="$TMP/payload.json" log_path="$TMP/logcat.log"
    cat > "$json_path" <<JSON
{
  "platform": "$platform",
  "type": "$type",
  "title": "$title",
  "message": "$message",
  "deviceModel": "$device",
  "email": "qa+${submitted}@example.com",
  "phoneNumber": "+49 1789 26${submitted}",
  "phone": null,
  "pharmacyId": "$pharmacy",
  "source": "SDK",
  "appVersion": "$version",
  "functionalityImportance": "$importance",
  "labels": ["SDKV2","mock-seed"]
}
JSON
    make_logcat "$platform" "$type" "$title" > "$log_path"
    gzip -f "$log_path"

    code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$INGEST/partners/api/v2/report-problem" \
        -H "apiKey: $API_KEY" \
        -F "json=@$json_path;type=application/json" \
        -F "file=@$log_path.gz;type=application/gzip;filename=logcat.log.gz")
    [[ "$code" =~ ^20[01]$ ]] || echo "  !! multipart failed: $code  ($title)" >&2
}

submit_json() {
    local platform="$1" type="$2" title="$3" device="$4" pharmacy="$5" version="$6" importance="$7" message="$8"
    local body_path="$TMP/json.json"
    cat > "$body_path" <<JSON
{
  "platform": "$platform",
  "type": "$type",
  "title": "$title",
  "message": "$message",
  "deviceModel": "$device",
  "email": "ops+${submitted}@example.com",
  "phoneNumber": "+49 30 555 ${submitted}",
  "phone": null,
  "pharmacyId": "$pharmacy",
  "source": "SDK",
  "appVersion": "$version",
  "functionalityImportance": "$importance",
  "labels": ["SDKV2","json-channel","mock-seed"]
}
JSON
    code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$INGEST/api/v1/reports" \
        -H "apiKey: $API_KEY" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_path")
    [[ "$code" =~ ^20[01]$ ]] || echo "  !! json failed: $code  ($title)" >&2
}

echo "Seeding $COUNT mock reports across $DAYS days against $INGEST"
multipart_pct=70

for ((i = 0; i < COUNT; i++)); do
    submitted=$i
    platform=$(pick "${PLATFORMS[@]}")
    if [[ "$platform" == "android" ]]; then
        device=$(pick "${ANDROID_DEVICES[@]}")
    else
        device=$(pick "${IOS_DEVICES[@]}")
    fi
    pharmacy=$(pick "${PHARMACIES[@]}")
    version=$(pick "${APP_VERSIONS[@]}")
    type=$(pick "${TYPES[@]}")
    title=$(pick "${TITLES[@]}")
    importance=$(pick "${IMPORTANCE[@]}")
    message="$title — generated by the mock seeder (#$i, $type, $platform)."

    if (( RANDOM % 100 < multipart_pct )); then
        submit_multipart "$platform" "$type" "$title" "$device" "$pharmacy" "$version" "$importance" "$message"
    else
        submit_json "$platform" "$type" "$title" "$device" "$pharmacy" "$version" "$importance" "$message"
    fi

    # Pace the burst so we don't trip the per-IP rate limit. ~10/sec stays well under the
    # 600/min cap docker-compose sets locally and any sane production value.
    sleep 0.1

    if (( (i + 1) % 25 == 0 )); then
        echo "  …submitted $((i + 1))"
    fi
done

# Backfill submitted_at across $DAYS days so the Stats page has a real time series. The ingestion
# service stamps "now" on every row, so a fresh seed all clusters at the current minute. Spread
# the rows by mutating submitted_at directly inside the SQLite index — files on disk keep their
# canonical timestamp; the index column is what /Stats and /Reports query.
# Spread submitted_at across $DAYS days so the Stats page sees a real time series. Ingestion
# stamps "now" on every row, so a fresh seed clusters all rows at the current minute.
#
# We need exclusive access to the SQLite file here: a running ingestion service holds the WAL,
# and a sidecar alpine container would only see a stale pre-WAL snapshot. So we briefly stop
# both compose services, run sqlite3 in an alpine sidecar against the volume, then bring them
# back up.
if command -v docker >/dev/null 2>&1; then
    VOLUME_NAME="report-service_reports"
    echo "Spreading submitted_at across $DAYS days (volume: $VOLUME_NAME)…"
    echo "  stopping services to release SQLite WAL…"
    docker compose -f "$REPO_ROOT/docker-compose.yml" stop report-admin report-service >/dev/null 2>&1 || true
    docker run --rm -v "$VOLUME_NAME:/data" alpine:3.20 sh -c "
        apk add --no-cache sqlite >/dev/null 2>&1
        sqlite3 /data/reports.db <<SQL
WITH ranked AS (
  SELECT id, ROW_NUMBER() OVER (ORDER BY id) AS rn, COUNT(*) OVER () AS total
  FROM problem_reports
  WHERE labels_json LIKE '%mock-seed%'
)
UPDATE problem_reports
SET submitted_at = strftime('%Y-%m-%dT%H:%M:%fZ',
    datetime('now', '-' || CAST(($DAYS * 86400.0) * (1.0 - (ranked.rn * 1.0 / ranked.total)) AS INTEGER) || ' seconds'))
FROM ranked
WHERE problem_reports.id = ranked.id;
SELECT 'rows touched: ' || COUNT(*) FROM problem_reports WHERE labels_json LIKE '%mock-seed%';
SQL
    " 2>&1 | sed 's/^/  /' || echo "  (timestamp spread skipped — sqlite3 unavailable)"
    echo "  starting services back up…"
    docker compose -f "$REPO_ROOT/docker-compose.yml" up -d >/dev/null 2>&1 || true
fi

echo ""
echo "Done. Open the admin at http://127.0.0.1:8081/Reports or /Stats."
