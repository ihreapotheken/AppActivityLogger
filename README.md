# report-service

A security-hardened ASP.NET Core (.NET 8) ingestion API that accepts **Report a Problem** submissions from the Android and iOS IA SDKs. The service validates the multipart payload, persists the JSON document (and its optional gzip log attachment) under `reports/<platform>/problem-reports/`, and optionally indexes the metadata in SQLite for fast listing.

## 1. Overview

The service receives problem reports that mobile users submit from inside the Android and iOS apps via the **IA SDK "Report a Problem" feature**:

- Android: `ReportAProblemUseCase` / `ReportProblemService`.
- iOS: `ReportProblemMapper` / `CardlinkReportProblemModel`.

Both SDKs POST `multipart/form-data` to `/partners/api/v2/report-problem` with a required `json` part and an optional `file` part (gzip-compressed log bundle). The service persists the payload under `<ReportsRoot>/<platform>/problem-reports/`:

```text
<ReportsRoot>/
  android/
    problem-reports/
      problem-report_<yyyyMMdd-HHmmss>_<sha12>.json
      problem-report_<yyyyMMdd-HHmmss>_<sha12>.log.gz   # optional
  ios/
    problem-reports/
      problem-report_...
```

High-level flow:

```text
IA SDK (Android / iOS) ──multipart/form-data──▶ report-service
                                                   │
                                                   ▼
                                     reports/<platform>/problem-reports/
                                                   │
                                     (optional) SQLite metadata index
```

## 2. Quick start

Prerequisites:

- .NET 8 SDK (`dotnet --version` should report `8.x`)
- Write access to the target `ReportsRoot` (default: `reports`, resolved against the process working directory)

Set the API key (double-underscore is the `IConfiguration` convention for nested keys):

```bash
export ReportService__ApiKey="choose-a-long-random-secret"
```

Run the ingestion service (from the repository root):

```bash
dotnet run --project src/ReportService
```

or use the helper script that additionally loads `.env`:

```bash
./scripts/run.sh            # ingestion (default)
./scripts/run.sh admin      # admin UI (separate process — see §13)
```

In Development, Kestrel binds to `https://localhost:8443` (see [appsettings.Development.json](src/ReportService/appsettings.Development.json)). In Production, the URL is controlled by `ASPNETCORE_URLS` / the `Kestrel:Endpoints` configuration section (no HTTPS endpoint is baked in, on the assumption that TLS terminates upstream).

Liveness check (anonymous):

```bash
curl -k https://localhost:8443/api/health
```

POST a problem report with an attached gzip log bundle:

```bash
curl -k -X POST https://localhost:8443/partners/api/v2/report-problem \
  -H "apiKey: $ReportService__ApiKey" \
  -F "json=@report.json;type=application/json" \
  -F "file=@logs.log.gz;type=application/gzip"
```

On success the service replies `201 Created` with a `Location:` header pointing at `/api/problem-reports/{platform}/{fileName}` and a [RSCStoredReport](src/ReportService.Core/Storage/RSCStoredReport.cs) body describing the persisted file.

### Production + staging side-by-side

Two named Docker stacks share the compose file but get isolated host ports, isolated volumes, and a distinct `apiKey` / `Environment` label. Operators promote a release by rebuilding *staging* first, smoke-testing, then rebuilding *production* — neither `down -v` ever touches the other's data.

| Stack | Env file | Host ports (ingest / admin) | Project name | Volume |
|-------|----------|-----------------------------|--------------|--------|
| production | `.env` | `8080` / `127.0.0.1:8081` | `reports-prod` | `reports-prod_reports` |
| staging | `.env.staging` | `18080` / `127.0.0.1:18081` | `reports-staging` | `reports-staging_reports` |

```bash
# bring up production (.env)
./scripts/stack.sh production rebuild

# bring up staging (.env.staging) alongside it
./scripts/stack.sh staging rebuild

# inspect one without touching the other
./scripts/stack.sh staging logs
./scripts/stack.sh staging down --volumes   # wipes the staging volume only
```

Each stack reports its environment label on `/api/health` and in a coloured badge at the top-left of the admin UI, so it's obvious which one a request is hitting.

### Public access via Cloudflare Tunnel (optional)

For demos, mobile-SDK testing from off-network devices, or any other case where the loopback bind isn't enough, the compose file ships a `cloudflared` sibling service behind a `tunnel` profile. It only starts when explicitly requested, so the default `up` keeps the host-loopback posture intact.

```bash
./scripts/stack.sh production up      # ingestion API on 127.0.0.1:8080 (unchanged)
./scripts/tunnel.sh up                # adds cloudflared and prints the public URL
./scripts/tunnel.sh down              # removes the tunnel, leaves report-service running
```

The sibling container reaches `report-service` over the compose network at `http://report-service:8080`, so no host-port hop is involved. Two modes:

- **Quick tunnel (default)** — ephemeral `*.trycloudflare.com` URL, no Cloudflare account required, hostname changes on every restart. Useful for short-lived demos.
- **Named tunnel** — stable hostname on your own Cloudflare-managed domain. Set `CLOUDFLARED_COMMAND=tunnel --no-autoupdate run --token <token>` in `.env` (see [.env.example](.env.example) for the `cloudflared tunnel login` / `create` / `token` sequence), then `./scripts/tunnel.sh up`. Survives restarts and host reboots.

`scripts/tunnel.sh logs` follows the cloudflared container output; in quick-tunnel mode `scripts/tunnel.sh url` reprints the last URL parsed from those logs.

## 3. Configuration

All settings live in the `ReportService` section of [appsettings.json](src/ReportService/appsettings.json) and bind to [RSCReportServiceOptions](src/ReportService.Core/Options/RSCReportServiceOptions.cs) at startup.

| Option | Default | Purpose |
|---|---|---|
| `ApiKey` | `""` | Shared secret clients send in the `apiKey` header. Empty disables authentication (every authenticated request fails). Must be set in any real deployment. |
| `ReportsRoot` | `"reports"` | Filesystem root (absolute or resolved against the process working directory). Each allowed platform gets a lowercase subdirectory with a `problem-reports/` child folder, both created on startup. |
| `MaxUploadBytes` | `524288000` (500 MiB) | Hard upper bound on the **entire multipart request body** (JSON part plus optional gzip attachment plus multipart framing overhead). Enforced by Kestrel (`MaxRequestBodySize`) and by ASP.NET Core `FormOptions.MultipartBodyLengthLimit`. |
| `MaxAttachmentBytes` | `52428800` (50 MiB) | Hard upper bound on the **optional `file` (gzip) part alone**. Checked after multipart parsing; separate from `MaxUploadBytes` so operators can raise the envelope without enlarging the attachment cap. |
| `AllowedPlatforms` | `["android", "ios"]` | Allow-list for the `platform` field in the JSON payload and for `{platform}` route parameters on the read endpoints. Inbound values are canonicalized with `ToLowerInvariant()` before matching. |
| `RateLimitPermitsPerMinute` | `120` | Fixed-window per-remote-IP request budget. Excess requests are rejected with `429`. |
| `IngestConcurrency` | `16` | **Global** concurrency cap on the write path — total in-flight `POST /partners/api/v2/report-problem` requests across all source IPs. Complements the per-IP limit above: a distributed DoS (many IPs, each within their per-IP budget) still cannot push more than this many uploads through the storage pipeline simultaneously. The permit is acquired **before** the endpoint delegate runs, so requests over the cap never consume parser buffers or disk spool space. |
| `IngestQueueLimit` | `16` | Number of waiting slots once `IngestConcurrency` is saturated. Excess requests are rejected immediately with `429 Too Many Requests` and `Retry-After: 2`. Keep this small — a long queue converts a burst into a latency tail instead of shedding it. |
| `Storage` | `"FileSystem"` | Backend selector. `"FileSystem"` persists JSON + gzip on disk. `"SqliteIndex"` wraps the file-system store with a SQLite metadata index for fast listing. |
| `SqliteDbPath` | `"reports.db"` | SQLite database path used when `Storage = "SqliteIndex"`. Admin-supplied and trusted (not traversal-validated). Ignored for `FileSystem`. |
| `RetentionEnabled` | `true` | Master switch for the background retention sweep hosted in the ingestion process. The admin can still trigger a one-shot purge from `/Maintenance` when this is `false`. |
| `RetentionMaxBytes` | `10737418240` (10 GiB) | Hard cap on total stored-report bytes (JSON + gzip attachments). When exceeded, the sweep deletes oldest-first until usage is back to ~95% of the cap. |
| `RetentionMaxAgeDays` | `30` | Reports older than this are deleted on every sweep regardless of size. `0` disables age-based deletion. |
| `RetentionScanIntervalSeconds` | `3600` | How often the background sweep runs. Floored at 60s. |

### Env var overrides

ASP.NET Core maps env vars using `__` as the section separator. Every option above can be overridden without touching any file:

```bash
export ReportService__ApiKey="..."
export ReportService__ReportsRoot="/var/lib/ia-problem-reports"
export ReportService__MaxUploadBytes=1073741824
export ReportService__MaxAttachmentBytes=104857600
export ReportService__RateLimitPermitsPerMinute=60
export ReportService__IngestConcurrency=16
export ReportService__IngestQueueLimit=16
export ReportService__Storage=SqliteIndex
export ReportService__SqliteDbPath=/var/lib/ia-problem-reports/reports.db

# Arrays are indexed:
export ReportService__AllowedPlatforms__0=android
export ReportService__AllowedPlatforms__1=ios
```

Kestrel binding can likewise be overridden, for example `Kestrel__Endpoints__Https__Url=https://0.0.0.0:8443`.

## 4. API reference

Every `/partners/api/v2/*`, `/api/v1/*`, and `/api/problem-reports/*` endpoint requires the `apiKey` request header. `/api/health` and `/api/health/ready` are `AllowAnonymous` (and now also surface the configured `Environment` label so an operator can tell which stack they're hitting). All endpoints (anonymous included) are subject to the global per-IP rate limiter.

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `POST` | `/partners/api/v2/report-problem` | apiKey | Ingest a Report-a-Problem submission (multipart). |
| `POST` | `/api/v1/reports` | apiKey | Ingest a JSON-only submission (no attachment). |
| `GET`  | `/api/v1/forced-reports/{id}` | apiKey | Tells a mobile client whether an operator has marked it for a forced report. |
| `GET`  | `/api/problem-reports/{platform}` | apiKey | List persisted problem reports for a platform. |
| `GET`  | `/api/problem-reports/{platform}/{fileName}` | apiKey | Stream a stored JSON document or its `.log.gz` sibling. |
| `GET`  | `/api/health` | anonymous | Liveness probe. |
| `GET`  | `/api/health/ready` | anonymous | Readiness probe (verifies `ReportsRoot` is writable). |

### `POST /partners/api/v2/report-problem`

Multipart form with:

- **`json`** part, `Content-Type: application/json`, required. Deserialised into [RSCProblemReport](src/ReportService.Core/Models/RSCProblemReport.cs) and validated by [RSCReportValidator](src/ReportService.Core/Validation/RSCReportValidator.cs).
- **`file`** part, `Content-Type: application/gzip`, optional. Gzip-compressed log bundle (Android bundles encrypted logcat + network + analytics + crash logs; iOS bundles the logger's log file). The server checks the first two bytes against the gzip magic `1F 8B` and rejects non-gzip blobs.

`json` part fields:

| Field | Type | Notes |
|---|---|---|
| `platform` | `string` | Required. `"Android"` or `"iOS"` (canonicalized to lowercase before the allow-list check). |
| `message` | `string` | Required. User-supplied problem description (max 8 KiB). |
| `title` | `string?` | Optional short title. |
| `deviceModel` | `string?` | Optional device model string. |
| `email` | `string?` | Optional contact email. May embed a phone suffix, e.g. `"foo@bar.com (phone: +49…)"`. |
| `phoneNumber` | `string?` | Optional phone number. |
| `phone` | `string?` | Optional CardLink iOS duplicate of `phoneNumber`. |
| `pharmacyId` | `string?` | Optional pharmacy identifier. |
| `userId` | `string?` | Optional SDK-supplied client identifier (Android: `SdkSession.clientId`; iOS: guest-user identifier). Used by the admin filter and the forced-report allow-list. |
| `source` | `string?` | Optional source marker; the SDKs send `"SDK"`. |
| `appVersion` | `string?` | Optional app version string. SDKs emit `<host versionName> (SDK <SDK version>)`. |
| `functionalityImportance` | `string?` | Optional severity tag emitted by the Android SDK only. |
| `labels` | `string[]?` | Optional free-form labels (max 32 entries, max 128 chars each). |
| `kind` | `string?` | Optional SDK-supplied kind. `"crash"` triggers server-side stack-frame extraction at ingest. |

Example Android `json` part:

```json
{
  "deviceModel": "Pixel 7 (sdk_gphone64_arm64)",
  "message": "Crash while opening cart after scanning a prescription QR.",
  "title": "java.lang.RuntimeException",
  "email": "kunde@example.com (phone: +491701234567)",
  "phoneNumber": "+491701234567",
  "pharmacyId": "DE-123456",
  "userId": "android-user-9999",
  "platform": "Android",
  "source": "SDK",
  "appVersion": "4.12.0 (SDK 2.3.30)",
  "functionalityImportance": "Schränkt mich häufig ein",
  "labels": ["SDKV2", "cardlink-client-42"],
  "kind": "crash"
}
```

Example iOS `json` part:

```json
{
  "deviceModel": "iPhone 15 Pro",
  "message": "Login screen is stuck after biometric prompt.",
  "title": "Biometric login hang",
  "email": "kunde@example.com",
  "phoneNumber": "+491701234567",
  "phone": "+491701234567",
  "pharmacyId": "DE-123456",
  "userId": "ios-user-9999",
  "platform": "iOS",
  "source": "SDK",
  "appVersion": "4.12.0 (SDK 2.3.30)",
  "labels": ["SDKV2", "cardlink-client-42"]
}
```

Success response: `201 Created` with `Location: /api/problem-reports/{platform}/{fileName}` and a [RSCStoredReport](src/ReportService.Core/Storage/RSCStoredReport.cs) body:

```json
{
  "platform": "android",
  "fileName": "problem-report_20260421-093941_3f1a8b2c9d0e.json",
  "sizeBytes": 612,
  "submittedAt": "2026-04-21T09:39:41.0000000+00:00",
  "attachmentFileName": "problem-report_20260421-093941_3f1a8b2c9d0e.log.gz",
  "attachmentSizeBytes": 184320
}
```

`attachmentFileName` and `attachmentSizeBytes` are `null` when no `file` part was sent.

Failure status codes:

- `400 Bad Request` — missing `json` part, malformed JSON, validation failure, non-gzip attachment, malformed multipart body.
- `401 Unauthorized` — missing or invalid `apiKey` header.
- `413 Payload Too Large` — request body exceeds `MaxUploadBytes` or attachment exceeds `MaxAttachmentBytes`.
- `415 Unsupported Media Type` — request is not `multipart/form-data`.
- `429 Too Many Requests` — per-IP rate limit tripped.

Error bodies follow RFC 7807 (`application/problem+json`) with a `traceId`; exception details are never echoed.

### `POST /api/v1/reports`

Same auth, same concurrency limiter, same validator as the multipart endpoint. Accepts a single `RSCProblemReport` JSON document directly — no multipart, no attachment. Useful for partner integrations that can't easily emit multipart bodies. Persisted rows are tagged with `ingestionChannel = "json"` so the admin console can tell API submissions apart from SDK uploads.

### `GET /api/v1/forced-reports/{id}`

The forced-report allow-list check. Mobile clients call this on every backend fetch with their stable identifier (Android: `SdkSession.clientId`; iOS: guest-user identifier). Returns `200 OK` with `{ "id": "...", "forced": true|false }`.

When an operator adds an id through the **Forced** admin page, this endpoint flips to `forced=true` for that id. The SDKs respond by automatically posting a Report-a-Problem submission with the current logs attached, so an operator can collect a fresh capture without asking the end user to tap anything. Once the operator removes the id from the dashboard, the next call returns `forced=false` and the client stops re-submitting.

The store is a single-column SQLite table sharing `reports.db` (no extra DB file to back up). Lookup is one indexed row read; intended to be cheap enough that the mobile app can poll on every session start.

Failure modes: `400` for empty / oversized id (>256 chars); `401` on bad apiKey; `429` on rate limit.

### `GET /api/problem-reports/{platform}`

Returns a `RSCStoredReport[]` for the given platform, newest first. Unknown platforms yield `404 Not Found`. The SQLite backend serves this from its metadata table; the file-system backend enumerates `problem-report_*.json` files on disk.

### `GET /api/problem-reports/{platform}/{fileName}`

Streams one stored file. `fileName` is re-validated through [RSCSafePath](src/ReportService.Core/Security/RSCSafePath.cs). `Content-Type` is `application/gzip` when the name ends in `.log.gz`, otherwise `application/json`. Returns `404` when the platform or file cannot be resolved safely.

### `GET /api/health`

Anonymous. `200 OK` with `status`, `environment`, `startedAt`, `uptimeSeconds`, `version`. The `environment` field reflects `ReportService:Environment` (default `"production"`) so a curl on the wrong port immediately reveals which stack you reached.

### `GET /api/health/ready`

Anonymous. Writes, reads, and deletes a probe file inside `ReportsRoot`. `200 OK` when writable; `503 Service Unavailable` otherwise. Response body mirrors `/api/health` (including `environment`).

## 4a. Automatic crash reporting & forced captures

The service is the upload target for two SDK-side flows on top of user-initiated Report-a-Problem submissions:

1. **Automatic crash reporting** — the Android SDK installs an uncaught-exception handler at SDK init (gated on `IaSdkConfiguration.analyticsEnabled`). When the app crashes, the trace is encrypted to disk; on the next launch the SDK drains the queue and POSTs each crash to `/partners/api/v2/report-problem` with `kind = "crash"`. iOS uses MetricKit (`MXMetricManagerSubscriber`) which delivers crash diagnostics on the next launch, often delayed up to ~24h.
2. **Forced captures** — when a crash isn't enough (e.g. UI bug with no exception, or a specific user reports a problem out-of-band), an operator adds the affected user's identifier on the admin **Forced** page. The next time that client's SDK initializes, it queries `GET /api/v1/forced-reports/{id}`; on `forced=true` it programmatically submits a Report-a-Problem entry with the current logs, no user action required.

Server-side, when an incoming submission has `kind = "crash"` and an attached gzip log file, the ingestion path decompresses the attachment in-process and extracts the first JVM-style stack frame as `top_frame`. The admin **Errors** page groups occurrences by that frame, so an operator sees one row per fault site instead of one row per crash. Bucketing happens entirely on the server; the SDK never needs to compute a crash signature client-side.

## 5. Architecture

### Directory layout

```text
report-service/
├── ReportService.sln                   # Solution file for all three projects
├── Dockerfile                          # Multi-stage alpine build; `ingestion` + `admin` runtime targets
├── docker-compose.yml                  # Stack: public ingestion + loopback-only admin
├── .dockerignore
├── .env.example                        # Template for local dev secrets/overrides
├── README.md
├── scripts/                            # Developer tooling (run, setup)
├── ops/                                # Systemd unit + install/update/backup helpers
└── src/
    ├── ReportService.Core/             # Shared class library — referenced by both apps
    │   ├── Models/
    │   │   └── RSCProblemReport.cs        # The `json` part payload
    │   ├── Options/
    │   │   └── RSCReportServiceOptions.cs # Strongly-typed config POCO
    │   ├── Security/
    │   │   └── RSCSafePath.cs             # Path-traversal guard for filenames
    │   ├── Validation/
    │   │   ├── RSCReportValidator.cs      # Field bounds + gzip magic byte check
    │   │   └── RSCValidationResult.cs     # Ok / Fail value type
    │   ├── Storage/
    │   │   ├── RSCIReportStore.cs         # SaveAsync / List / OpenRead / Delete
    │   │   ├── RSCFileSystemReportStore.cs
    │   │   ├── RSCIReportIndex.cs         # Upsert / ListAsync / DeleteAsync
    │   │   ├── RSCSqliteReportIndex.cs    # SQLite index (WAL, parameterised, retry on busy)
    │   │   ├── RSCSqliteIndexingReportStore.cs # Decorator: file store + index
    │   │   ├── RSCStoredReport.cs         # Response metadata record
    │   │   └── RSCReportMetadata.cs       # Index-friendly projection of RSCProblemReport
    │   └── Observability/
    │       ├── RSCCrashHandler.cs         # AppDomain + TaskScheduler unhandled-exception hooks
    │       └── RSCServiceTelemetry.cs     # startedAt / uptime / version
    │
    ├── ReportService/                  # Ingestion web API (public)
    │   ├── Program.cs                  # Composition root: DI, auth, rate limit, HSTS, headers, routes
    │   ├── ReportService.csproj
    │   ├── appsettings.json            # Production defaults
    │   ├── appsettings.Development.json
    │   ├── Endpoints/
    │   │   └── RSReportEndpoints.cs      # Minimal API: /partners/api/v2/report-problem + /api/problem-reports
    │   ├── Ingestion/
    │   │   ├── RSReportIngestionService.cs
    │   │   └── RSIngestionResult.cs
    │   └── Security/
    │       ├── RSApiKeyAuthenticationOptions.cs
    │       └── RSApiKeyAuthenticationHandler.cs  # Constant-time apiKey header compare
    │
    └── ReportService.Admin/            # Operator console (separate process, internal port)
        ├── Program.cs                  # Cookie auth, Razor Pages, shared storage wiring
        ├── ReportService.Admin.csproj
        ├── appsettings.json            # Defaults to http://127.0.0.1:8081
        ├── appsettings.Development.json
        ├── Options/
        │   └── RSAAdminOptions.cs         # AdminKey, SessionMinutes
        ├── Pages/
        │   ├── _Layout.cshtml          # Shared chrome + sign-out form
        │   ├── Login.cshtml(.cs)       # Constant-time AdminKey compare → cookie sign-in
        │   ├── Logout.cshtml.cs        # POST-only sign-out
        │   ├── Index.cshtml(.cs)       # RSCPlatforms overview
        │   ├── Platform.cshtml(.cs)    # Per-platform report list
        │   ├── Report.cshtml(.cs)      # View JSON / download / DELETE
        │   └── Error.cshtml(.cs)
        └── wwwroot/site.css
```

### Class responsibilities

- `RSCReportServiceOptions` — hold configuration.
- `RSApiKeyAuthenticationHandler` — constant-time compare of the `apiKey` header against the configured key.
- `RSCSafePath` — turn an untrusted filename into a safe absolute path or refuse.
- `RSCReportValidator` — enforce field bounds, platform allow-list, attachment size cap, and gzip magic.
- `RSReportIngestionService` — orchestrate parse → validate → store for one inbound submission.
- `RSCIReportStore` / `RSCFileSystemReportStore` / `RSCSqliteIndexingReportStore` — persistence seam; file system is the source of truth, SQLite is an accelerator index.
- `RSCIReportIndex` / `RSCSqliteReportIndex` — metadata index over persisted reports.
- `RSReportEndpoints` — HTTP surface only (routing, authorization, response shape).

### Data flow

```text
IA SDK (Android / iOS)
   │  POST /partners/api/v2/report-problem   (apiKey, multipart/form-data)
   ▼
Kestrel  (MaxRequestBodySize = MaxUploadBytes, AddServerHeader=false)
   │
   ▼
Exception handler → Security headers → RateLimiter (per-IP fixed-window) → Authentication → Authorization → RateLimiter (ingest-concurrency)
   │
   ▼
RSReportEndpoints  (DisableAntiforgery for SDK clients)
   │
   ▼
RSReportIngestionService.IngestAsync
   ├─ request.ReadFormAsync        (multipart parse; 413 on InvalidDataException, 400 on BadHttpRequestException)
   ├─ JSON.Deserialize<RSCProblemReport>  (MaxDepth=32, strict)
   ├─ RSCReportValidator.ValidateReport   (required fields, length caps, platform allow-list)
   ├─ RSCReportValidator.ValidateAttachment  (MaxAttachmentBytes, gzip magic 0x1F 0x8B)
   └─ RSCIReportStore.SaveAsync
         │
         ▼
RSCFileSystemReportStore
   ├─ RSCSafePath.TryCombine  (platform folder + problem-report_<ts>_<sha12>.{json,log.gz})
   ├─ Write JSON to <file>.tmp.<guid>, File.Move(overwrite: true)  — atomic publish; per-writer temp avoids concurrent collisions
   └─ Write attachment stream to <file>.log.gz.tmp, File.Move(overwrite: true)
         │
         ▼
(optional) RSCSqliteIndexingReportStore upserts the RSCReportMetadata row
         │
         ▼
201 Created → RSCStoredReport { Platform, FileName, SizeBytes, SubmittedAt, AttachmentFileName?, AttachmentSizeBytes? }
```

## 6. Storage layout

```text
<ReportsRoot>/<platform-lower>/problem-reports/
    problem-report_<yyyyMMdd-HHmmss>_<sha12>.json
    problem-report_<yyyyMMdd-HHmmss>_<sha12>.log.gz   # optional, sibling to the JSON
```

- `yyyyMMdd-HHmmss` is UTC at persist time.
- `sha12` is the first 12 hex characters of `SHA-256(jsonBytes)`; together with the timestamp this yields a stable, collision-resistant base name.
- JSON and its attachment always share the same base name so enumeration by `.json` locates the matching `.log.gz`.
- Writes are atomic: each file is written to `<path>.tmp` and then renamed with `File.Move(overwrite: true)` so a crash mid-write cannot leave a half-written file visible.

## 7. SQLite index (optional)

When `Storage = "SqliteIndex"`, `RSCSqliteIndexingReportStore` decorates the file-system store. The file is persisted first; the index upsert runs afterwards. Index failures are logged at **Warning** level and swallowed — the upload has already been durably stored on disk, and a stale index can be rebuilt from the files.

Schema (see [RSCSqliteReportIndex.cs](src/ReportService.Core/Storage/RSCSqliteReportIndex.cs)):

```sql
CREATE TABLE IF NOT EXISTS problem_reports (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  platform         TEXT NOT NULL,
  file_name        TEXT NOT NULL,
  submitted_at     TEXT NOT NULL,           -- ISO 8601 round-trip format
  device_model     TEXT,
  type             TEXT,
  title            TEXT,
  email_hash       TEXT,                    -- SHA-256 hex of UTF-8 email; raw email is never indexed
  pharmacy_id      TEXT,
  app_version      TEXT,
  has_attachment   INTEGER NOT NULL,
  size_bytes       INTEGER NOT NULL,
  attachment_bytes INTEGER,
  labels_json      TEXT,
  UNIQUE(platform, file_name)
);
CREATE INDEX IF NOT EXISTS idx_problem_reports_platform_submitted
  ON problem_reports(platform, submitted_at DESC);
```

`journal_mode=WAL` is set once at bootstrap (it persists at the database level); `synchronous=NORMAL` is applied per connection because it is a connection-scoped pragma. Transient `SQLITE_BUSY` / `SQLITE_LOCKED` errors are retried with exponential backoff. `List` unions index rows with files on disk so that a swallowed index upsert cannot hide a persisted report.

## 8. Privacy and PII

The `json` part can contain personal data: `email`, `phoneNumber`, `phone`, `pharmacyId`, and anything the user typed into `message` / `title`. Handle with care:

- The JSON is stored **verbatim** on disk inside `problem-report_*.json`. Operators are responsible for retention, at-rest encryption, and disk access controls on `ReportsRoot`.
- The SQLite index stores only a **SHA-256 hex digest of the email** (`email_hash`) — never the raw email.
- Logs produced by the service never include the message body, email, pharmacy id, request headers, or the attachment bytes. Only metadata (platform, file name, byte counts, attachment presence) is logged.
- Exception responses are RFC 7807 with a `traceId`; exception messages are never echoed.

## 9. Security model

| Control | Where | Defends against |
|---|---|---|
| `apiKey` header + constant-time compare | [RSApiKeyAuthenticationHandler.cs](src/ReportService/Security/RSApiKeyAuthenticationHandler.cs) via `CryptographicOperations.FixedTimeEquals` | Unauthenticated access; timing-based key recovery. |
| Per-IP fixed-window rate limit (120/min default) | [Program.cs](src/ReportService/Program.cs) `AddRateLimiter` | Brute-force key guessing, dumb DoS, noisy-neighbor SDKs. `429` is returned before auth or ingestion run. |
| Global write-path concurrency cap (`IngestConcurrency`=16, `IngestQueueLimit`=16 by default) | [Program.cs](src/ReportService/Program.cs) `AddConcurrencyLimiter("ingest-concurrency")` applied via `RequireRateLimiting` on the POST endpoint | Distributed DoS across many source IPs: even if every attacker IP stays within its per-IP budget, no more than `IngestConcurrency` uploads can be in flight through the storage pipeline at once. Permits are tied to request lifetime (client disconnect releases the permit), so the cap does not leak capacity. |
| `MaxUploadBytes` enforced at Kestrel and `FormOptions` | [Program.cs](src/ReportService/Program.cs) | Memory exhaustion via oversized multipart bodies. |
| `MaxAttachmentBytes` enforced after multipart parse | [RSReportIngestionService.cs](src/ReportService/Ingestion/RSReportIngestionService.cs) | Oversized gzip parts that fit inside the envelope. |
| Gzip magic-byte probe (`1F 8B`) | [RSCReportValidator.cs](src/ReportService.Core/Validation/RSCReportValidator.cs) | Storing arbitrary blobs under a `.log.gz` name. |
| `RSCSafePath.TryCombine` | [Security/RSCSafePath.cs](src/ReportService.Core/Security/RSCSafePath.cs) | Path traversal (`..`), absolute paths, NUL bytes, invalid filename chars — on both ingest and download. |
| Strict JSON parser (`MaxDepth=32`, no comments, no trailing commas, `UnmappedMemberHandling=Disallow`) | `JsonOptions` in `RSReportIngestionService` | Stack exhaustion; parser-differential attacks, including smuggling unknown fields past validation. |
| Per-request wall-clock timeout (`IngestTimeoutSeconds`=60 by default) | [RSReportIngestionService.cs](src/ReportService/Ingestion/RSReportIngestionService.cs) via a linked `CancellationTokenSource` | A stuck disk or misbehaving client can't hold an `IngestConcurrency` permit past the deadline; fires as 503 with a logged `traceId`. |
| SQLite command timeout (`SqliteCommandTimeoutSeconds`=10) | [RSCSqliteReportIndex.cs](src/ReportService.Core/Storage/RSCSqliteReportIndex.cs) | Long-running queries blocking the index database file or holding request permits. |
| Persisted auth-abuse tracking (`AuthAbuseMaxFailures`=10 / window 60s / ban 300s by default) | [RSCSqliteAuthAbuseTracker.cs](src/ReportService.Core/Security/RSCSqliteAuthAbuseTracker.cs), hooked in both [RSApiKeyAuthenticationHandler.cs](src/ReportService/Security/RSApiKeyAuthenticationHandler.cs) and the admin `/Login` page | Online brute-force against the shared secrets. State is kept in its own SQLite file so it survives a restart — an attacker that bursts across process restarts keeps their ban. Success auths clear the counter; repeated abuse extends the ban. |
| Startup secret validation (fail-fast) | [RSCSecretValidation.cs](src/ReportService.Core/Security/RSCSecretValidation.cs), called after `builder.Build()` in both apps | Launching a Production process with a missing or obviously-weak (<32 char) `ApiKey` / `AdminKey`. |
| Kestrel min data rates (`MinRequestBodyDataRate` / `MinResponseDataRate`) + request header timeout | [Program.cs](src/ReportService/Program.cs) `ConfigureKestrel` | Slowloris: a trickle-body or trickle-read client is dropped after the grace period. |
| `Accept`-header filter on JSON endpoints | [RSAcceptHeaderFilter.cs](src/ReportService/Security/RSAcceptHeaderFilter.cs) | Clients whose `Accept` header excludes every media type we can produce get `406` instead of being served a mismatched body. |
| `405 Method Not Allowed` for defined paths hit with the wrong verb | Default routing behaviour — tests assert `DELETE /partners/api/v2/report-problem` → 405 | Probing for unexpected verbs masked as 404s. |
| Correlation-id middleware (`X-Correlation-ID` / `X-Request-ID`) | [RSCCorrelationIdMiddleware.cs](src/ReportService.Core/Observability/RSCCorrelationIdMiddleware.cs) | Links every log line for a request (via a logger scope) and echoes the id to the client; rejects client-supplied ids that contain control bytes or exceed 128 chars. |
| Bounded field validation | [RSCReportValidator.cs](src/ReportService.Core/Validation/RSCReportValidator.cs) | Log injection, DoS via huge payloads. |
| HSTS (1 year, include subdomains) + HTTPS redirect when an HTTPS endpoint is bound | [Program.cs](src/ReportService/Program.cs) | Protocol downgrade. Skipped when TLS terminates upstream so the middleware does not misfire. |
| `UseForwardedHeaders` (`X-Forwarded-Proto`, `X-Forwarded-For`) | [Program.cs](src/ReportService/Program.cs) | Ensures `Request.Scheme` / `RemoteIpAddress` reflect the client-facing values when fronted by a reverse proxy. |
| Security headers on every response | [Program.cs](src/ReportService/Program.cs) middleware: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` | MIME sniffing, clickjacking, referrer leakage, proxy caching. |
| `Kestrel.AddServerHeader = false` | [Program.cs](src/ReportService/Program.cs) | Server fingerprinting. |
| Atomic writes (`.tmp` then `File.Move(overwrite: true)`) | [RSCFileSystemReportStore.cs](src/ReportService.Core/Storage/RSCFileSystemReportStore.cs) | Half-written files being read by operators. |
| Centralized RFC 7807 exception handler with `traceId` | [Program.cs](src/ReportService/Program.cs) `UseExceptionHandler` | Stack trace / internal path disclosure. |

## 10. Observability

- **RSCCrashHandler** ([Observability/RSCCrashHandler.cs](src/ReportService.Core/Observability/RSCCrashHandler.cs)) hooks `AppDomain.CurrentDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` so background faults are logged with a stable correlation id before the process exits.
- **RSCServiceTelemetry** ([Observability/RSCServiceTelemetry.cs](src/ReportService.Core/Observability/RSCServiceTelemetry.cs)) records `StartedAt` and exposes `UptimeSeconds` and the informational assembly version; surfaced through `/api/health` and `/api/health/ready`.
- Structured logs are emitted through `ILogger<T>` and carry platform + file name + byte counts, never payload contents.

## 11. How to extend

### Accept a new platform

1. Add the identifier (lowercase) to `AllowedPlatforms` — in [appsettings.json](src/ReportService/appsettings.json) or via `ReportService__AllowedPlatforms__N`. Example: `"web"`.
2. Ship a client that POSTs with `"platform": "<name>"` in the JSON part.
3. No code changes required: `RSCFileSystemReportStore` creates `reports/<name>/problem-reports/` on startup and the read endpoints accept the new value.

### Plug a different `RSCIReportStore`

1. Create e.g. `src/ReportService.Core/Storage/S3ReportStore.cs` implementing [RSCIReportStore](src/ReportService.Core/Storage/RSCIReportStore.cs) (SaveAsync, List, OpenRead, Delete).
2. Replace the registration in [Program.cs](src/ReportService/Program.cs):

   ```csharp
   builder.Services.AddSingleton<RSCIReportStore, S3ReportStore>();
   ```

3. Validation, auth, rate limiting, and the endpoints keep working — they only depend on the interface.

### Add a new `RSCProblemReport` field

1. Add the property to [Models/RSCProblemReport.cs](src/ReportService.Core/Models/RSCProblemReport.cs) and any length cap in [Validation/RSCReportValidator.cs](src/ReportService.Core/Validation/RSCReportValidator.cs).
2. If it should be queryable, extend [Storage/RSCReportMetadata.cs](src/ReportService.Core/Storage/RSCReportMetadata.cs), the SQLite schema in [Storage/RSCSqliteReportIndex.cs](src/ReportService.Core/Storage/RSCSqliteReportIndex.cs) (table + migration), and the mapping in [Storage/RSCSqliteIndexingReportStore.cs](src/ReportService.Core/Storage/RSCSqliteIndexingReportStore.cs).
3. On-disk JSON is stored verbatim, so existing files remain readable without migration.

## 12. Deployment

- **Local dev** — see [scripts/setup.sh](scripts/setup.sh) for a one-shot bootstrap (.NET SDK check, NuGet restore, `.env` generation, dev-cert trust). Then `./scripts/run.sh` for ingestion or `./scripts/run.sh admin` for the admin UI.
- **Docker** — see [Dockerfile](Dockerfile) and [docker-compose.yml](docker-compose.yml) for a containerised build and a compose stack that runs the ingestion service on the configured public port and the admin UI bound to host loopback only. **First-run flow** (see §12.1 below).
- **systemd** — see [ops/](ops/) for unit files and deploy notes suitable for a long-running host-installed ingestion service. The ops scripts intentionally only manage the ingestion binary; install the admin UI as a sibling unit if you need it under systemd, pointed at the same `ReportsRoot`.

This README does not duplicate those artifacts; follow the links for the authoritative steps.

### 12.1 First-run flow (Docker)

The compose file declares `env_file: .env` as required on both services. That is deliberate: it refuses to start the stack at all when `.env` is missing, rather than booting with no secrets. So the first-run order is:

```bash
# 1. Generate .env with real random keys (idempotent; won't clobber an existing .env).
./scripts/setup.sh

# 2. Build + start the stack. Ingestion on ${HOST_PORT:-8080}, admin on 127.0.0.1:${ADMIN_HOST_PORT:-8081}.
docker compose up -d --build
```

If you forget step 1, compose prints:

```
env file .../report-service/.env not found: stat .../.env: no such file or directory
```

— no silent fallback, no placeholder-secret boot. This is intentional: a deployment that starts with the placeholder values copied from `.env.example` would be backdoored from the first request. **Production boot refuses placeholder values regardless of compose** — see §12.2.

State files (`reports/`, `reports.db`, `auth-abuse.db`) all live on the `reports` named volume inside the container. The container root filesystem is mounted read-only (`read_only: true`), so the compose file pins `ReportService__ReportsRoot`, `ReportService__SqliteDbPath`, and `ReportService__AuthAbuseDbPath` to `/srv/reports/*` explicitly. If you deploy outside compose, set at least `ReportService__ReportsRoot` to a writable directory — relative SQLite paths will be anchored under it automatically by `RSCStatePaths.Resolve`.

### 12.2 Placeholder-secret guard

`RSCSecretValidation.RequireInProduction` runs immediately after `builder.Build()` and throws (preventing the app from booting) when `ApiKey` / `AdminKey`:

- is empty, or
- is shorter than 32 characters, or
- matches a known placeholder pattern: `CHANGE_ME`, `CHANGEME`, `REPLACE_ME`, `GENERATE_WITH`, `PLACEHOLDER`, `YOUR_SECRET`, `EXAMPLE_KEY`, `INSECURE_DEV_ONLY`, `SAMPLE_KEY` (case-insensitive substring match).

The `.env.example` values (`CHANGE_ME_RUN_openssl_rand_hex_32_AND_REPLACE_THIS_VALUE`) are deliberately built from those markers, so copying `.env.example` → `.env` without replacing values cannot result in a running production process.

## 13. Admin UI (`ReportService.Admin`)

A second ASP.NET Core project — [src/ReportService.Admin/](src/ReportService.Admin/) — exposes an operator console for browsing and deleting stored problem reports. It is deliberately a **separate process** and a **separate secret**, so the attack surface of the public ingestion service stays as narrow as it was: the admin UI is never reachable from the internet by default, and the SDK-facing API key cannot be used to sign in.

### What it does

| Page | Purpose |
|---|---|
| `GET  /Login` | Operator sign-in form (anonymous). |
| `POST /Login` | Constant-time compare against `Admin:AdminKey`; issues an HttpOnly SameSite=Strict cookie. |
| `POST /Logout` | Clears the auth cookie. |
| `GET  /`      | Platform overview — lists configured platforms and current report counts. |
| `GET  /Platform/{platform}` | Newest-first list of reports for a platform (file name, size, submitted-at, attachment badge). |
| `GET  /Report/{platform}/{fileName}` | Renders the JSON body (truncated at 512 KiB for display; full file available via download). |
| `GET  /Report/{platform}/{fileName}?handler=DownloadJson` | Streams the stored JSON as `application/json`. |
| `GET  /Report/{platform}/{fileName}?handler=DownloadAttachment` | Streams the sibling `.log.gz` as `application/gzip`. |
| `POST /Report/{platform}/{fileName}?handler=Delete` | Deletes the JSON **and** its sibling attachment; redirects back to `/Platform/{platform}`. Requires an antiforgery token and an authenticated cookie. |
| `GET  /healthz` | Anonymous liveness probe (used by the container healthcheck). |

### Configuration

Bound the same way as the ingestion service — all settings live in [src/ReportService.Admin/appsettings.json](src/ReportService.Admin/appsettings.json). The admin reuses the `ReportService` section (to locate the same `ReportsRoot` and — when enabled — the same SQLite index) and adds an `Admin` section of its own:

| Option | Default | Purpose |
|---|---|---|
| `Admin:AdminKey` | `""` | Operator sign-in secret. Must be set in any real deployment; empty disables login. Generate with `openssl rand -hex 32`. **Not** the same as `ReportService:ApiKey`. |
| `Admin:SessionMinutes` | `60` | Cookie lifetime in minutes (sliding). The cookie is HttpOnly, SameSite=Strict, and `Secure` when the request arrived over HTTPS. |

Kestrel binding: `Urls` in `appsettings.json` defaults to `http://127.0.0.1:8081` so a bare `dotnet run` never exposes the admin beyond loopback. Override via `ASPNETCORE_URLS` if you need to bind elsewhere.

### Running it

```bash
# Local — alongside the ingestion service, loading the same .env
./scripts/run.sh admin

# Inside Docker — admin container, bound to host loopback only
docker compose up -d report-service report-admin
# then browse to http://127.0.0.1:8081 from the host
```

Because the admin project reads from the same `ReportsRoot` (and optional `SqliteDbPath`) as the ingestion service, both must be pointed at the same volume. The bundled [docker-compose.yml](docker-compose.yml) does this by mounting the shared `reports` volume into both containers.

### Destructive operations

Delete is the only write the admin performs and it flows through `RSCIReportStore.Delete`, which:

1. Path-checks the file name with `RSCSafePath.TryCombine` so `..` / absolute / null-byte names are refused before any filesystem call.
2. Deletes the sibling gzip attachment (best effort — a failure is logged but does not abort the JSON delete).
3. Deletes the JSON document. Its disappearance is the source of truth for "the report is gone".
4. For the SQLite backend, then removes the index row. If the index delete fails, the next list cycle reconciles via the drift-fallback union with disk (see §7).

Every delete is logged at Information level with the operator identity, remote address, and filename:

```
info: ReportService.Admin.Pages.RSAReportModel[0]
      Operator operator deleted android/problem-report_20260423-072406_c22dfe13ea7f.json from 127.0.0.1
```

### Security posture

- **Separate process, separate secret** — compromising the SDK-facing API key grants upload-only access; compromising the admin key grants read+delete.
- **Loopback-only default** — both the appsettings default (`Urls=http://127.0.0.1:8081`) and the compose mapping (`127.0.0.1:8081:8081`) keep the UI off public interfaces. Put it behind a reverse proxy with mTLS or an SSH tunnel if operators need remote access.
- **Cookie auth** — HttpOnly, SameSite=Strict, constant-time key compare (`CryptographicOperations.FixedTimeEquals`), antiforgery required on every POST.
- **CSP** — `default-src 'self'`, no inline scripts, no frame ancestors.
- **Same security headers** as the ingestion service (`X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, `Cache-Control: no-store`) plus a strict `Content-Security-Policy`.
- **No destructive operation without an authenticated cookie AND an antiforgery token.**

## 14. Tests

An xUnit test project at [tests/ReportService.Tests/](tests/ReportService.Tests/) exercises the security posture end-to-end. Each integration test boots the real ingestion service via `WebApplicationFactory<Program>` with an isolated temp `ReportsRoot` and throwaway auth-abuse DB, so runs never collide. Unit tests cover the primitives that don't need a host.

Run with:

```bash
dotnet test tests/ReportService.Tests
```

Coverage (36 cases across 6 files):

- **[SafePathTests](tests/ReportService.Tests/SafePathTests.cs)** — accepts ordinary leaf names, rejects `..`, absolute paths, null bytes, empty / whitespace. The single gate between attacker-supplied filenames and the filesystem call.
- **[SecretValidationTests](tests/ReportService.Tests/SecretValidationTests.cs)** — Production refuses to start with empty or <32-char `ApiKey` / `AdminKey`; non-Production tolerates weak values so fixtures can run.
- **[AuthAbuseTrackerTests](tests/ReportService.Tests/AuthAbuseTrackerTests.cs)** — threshold-driven ban, success clears the counter, ban state survives a "restart" (reopened tracker against the same DB file), sources are isolated from each other.
- **[PathTraversalTests](tests/ReportService.Tests/PathTraversalTests.cs)** — traversal-style file names on the download endpoint come back as `404`, never a file outside `ReportsRoot`.
- **[IngestionEndpointTests](tests/ReportService.Tests/IngestionEndpointTests.cs)** — unauthenticated `401`, happy path `201`, unknown JSON fields `400`, disallowed platform `400`, non-gzip attachment `400`, non-multipart request `415`, wrong verb `405`, incompatible `Accept` `406`, wildcard `Accept` accepted, correlation id echoed / generated, identical uploads produce the same filename (idempotency).
- **[ConcurrencyLimiterTests](tests/ReportService.Tests/ConcurrencyLimiterTests.cs)** — replaces the real `RSCIReportStore` with a gated stub so a pinning request deterministically holds the single permit; verifies a 20-way storm is shed with `429` + `Retry-After: 2`.

Tests use only `xunit` + `Microsoft.AspNetCore.Mvc.Testing` + `Microsoft.NET.Test.Sdk` — no mocking framework.

## 15. Documentation

The repo ships **hand-written orientation under [`docs/`](docs/)** (architecture, this README) plus
two flavors of generated reference, all wired up by [`scripts/generate-docs.sh`](scripts/generate-docs.sh):

```bash
./scripts/generate-docs.sh
```

What it produces:

- **`docs/openapi/ingestion-v1.json`** + `docs/openapi/ingestion-v1.yaml` — Swagger 2.0 / OpenAPI
  spec dumped from the compiled `ReportService.dll` via Swashbuckle CLI. Covers every public
  ingestion route (`/api/health`, `/partners/api/v2/report-problem`, `/api/v1/reports`,
  `/api/problem-reports/{platform}`, `/api/problem-reports/{platform}/{fileName}`) plus
  `RSCProblemReport` / `RSCStoredReport` schemas. Drop into Swagger UI / Insomnia / Postman / Redocly.
- **`docs/_site/index.html`** — full HTML reference site built by [DocFX](https://dotnet.github.io/docfx/).
  One page per public type (Core / ingestion / Admin), wired from the projects' XML doc comments.
- **`docs/api/`** — DocFX's intermediate YAML metadata (consumed by the site build).

The script restores both tools from [`.config/dotnet-tools.json`](.config/dotnet-tools.json) on
first run, so the only prerequisite on a fresh clone is the .NET 8 SDK. Generated outputs
(`docs/openapi/`, `docs/_site/`, `docs/api/`) are gitignored — the canonical source is the code.
