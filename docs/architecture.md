# Architecture overview

## Components

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                         report-service repository                             │
│                                                                              │
│  ┌────────────────────┐     ┌──────────────────────────┐   ┌───────────────┐ │
│  │ ReportService.Core │ ◄── │ ReportService            │   │ ReportService │ │
│  │  (class library)   │     │ (ingestion API)          │   │ .Admin        │ │
│  │                    │     │ ─ /partners/api/v2       │   │ (Razor Pages  │ │
│  │ ─ Storage          │     │ ─ /api/v1/reports        │   │  CMS console) │ │
│  │ ─ Audit            │     │ ─ /api/v1/forced-reports │   │               │ │
│  │ ─ Security         │     │ ─ /api/health[+/ready]   │   │               │ │
│  │ ─ Validation       │     │                          │   │               │ │
│  │ ─ Hosting helpers  │     └──────────────────────────┘   └───────────────┘ │
│  │ ─ Migrations       │                  │                          │         │
│  └────────────────────┘                  ▼                          ▼         │
│                                  ┌────────────────────────────────────────┐  │
│                                  │   shared filesystem + SQLite           │  │
│                                  │                                        │  │
│                                  │   reports.db (problem_reports +        │  │
│                                  │                forced_reports)         │  │
│                                  │   audit.db   (audit_log)               │  │
│                                  │   auth-abuse.db (auth_abuse)           │  │
│                                  │   reports/<platform>/<file>.json[.gz]  │  │
│                                  └────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────┘
```

Two independent stacks (`reports-prod` on 8080/8081 and `reports-staging` on 18080/18081) can run side by side via `scripts/stack.sh`. They share the compose file but get isolated Docker volumes (so `down -v` on one never touches the other) and report their `Environment` label on `/api/health` + as a coloured badge in the admin layout.

## Storage model

**Files are the source of truth.** The SQLite index is a sidecar that accelerates the admin
search/filter/paginate endpoints. Every operation has a graceful path when SQLite is unavailable:

| Operation | If index is healthy | If index is broken |
|---|---|---|
| Ingest (write) | File + SQLite upsert (best effort) | File only — `RSCSqliteIndexingReportStore.TryIndexAsync` swallows the failure; rebuild later |
| List per platform | Index `SELECT … WHERE platform = …` | Disk fallback via `RSCFileSystemReportStore.List` |
| Filtered search | Index `SELECT … WHERE …` | Disk fallback (CMS shows "filesystem scan" badge) |
| Delete | File first, then SQLite row | File succeeds; index row stays until next rebuild |

Drift between disk and index is reconciled by the **Maintenance → Rebuild** action.

## Resilience layers

- `RSCResilientReportIndex` wraps `RSCSqliteReportIndex` with a lazy constructor + per-method
  exception swallowing. Failures are surfaced via `RSCComponentHealth` so the admin Dashboard /
  Status pages show a `degraded` badge — but ingestion never returns 5xx because of an index
  problem.
- `RSCResilientAuthAbuseTracker` falls **back, not open**: if the SQLite tracker can't construct,
  brute-force protection continues via a bounded in-memory tracker (LRU eviction past 10k
  sources). Bans persist for the lifetime of the process; they don't survive a restart, but the
  service refuses to silently let attackers through.
- `RSCSqliteAuditLog` degrades to no-op silently if it can't open its DB — the admin actions never
  block on the audit write succeeding.
- `RSCStatePaths.Resolve` anchors all three SQLite files under `ReportsRoot` when their paths are
  relative, so a read-only content root (Docker `read_only: true`, systemd `ProtectSystem=strict`)
  doesn't cause SQLITE_CANTOPEN.

## Schema management

All SQLite schemas are class-based migrations:

```
ReportService.Storage.Migrations.RSCISchemaMigration
                                    │
            ┌───────────────────────┴────────────────────────────────┐
            ▼                                                        ▼
    Reports/RSCM001_CreateProblemReports.cs              Audit/RSCM001_CreateAuditLog.cs
    Reports/RSCM002_AddIngestionChannel.cs
    Reports/RSCM003_AddCrashFingerprint.cs   (user_id, phone, top_frame columns)
    Reports/RSCM004_CreateForcedReports.cs   (allow-list table for forced captures)
    Reports/RSCM005_DropType.cs              (removes the unused 'type' column)
            │
            ▼
ReportService.Storage.Migrations.RSCSchemaRunner
  ─ reads PRAGMA user_version
  ─ applies pending migrations in order, each in its own transaction
  ─ on failure, rolls back and leaves the DB at the previous version
```

Adding a new schema = one new class in the appropriate `Migrations/…` folder + one line in the
service's `BuildMigrations()` factory.

## Crash bucketing & forced captures

When an incoming submission has `kind = "crash"` and an attached gzip log, `RSCSqliteIndexingReportStore.TryExtractTopFrame` decompresses the attachment in-process (capped at 256 lines), grabs the first JVM-style stack frame (`at <pkg.Class.method>(File.ext:line)`), and persists it as `top_frame` on the index. The admin **Errors** page groups occurrences by that frame, so an operator sees one row per fault site instead of one row per crash. Bucketing happens entirely on the server; the SDK never needs to compute a crash signature client-side. The legacy free-form `Type` column was removed in `RSCM005_DropType` once stack-frame grouping replaced it.

`/api/v1/forced-reports/{id}` is an operator-managed allow-list: the **Forced** admin page adds an id (clientId / userId / whatever the host app keys on); the mobile SDK polls the endpoint on every init and, on `forced=true`, programmatically submits a Report-a-Problem entry with the current logs. This is how operators trigger a capture for bugs that don't crash (UI hangs, broken flows reported out-of-band) without asking the user to tap anything.

## Ingest channels

The persisted `problem_reports.ingestion_channel` column records how each report reached the
service:

- `multipart` — `POST /partners/api/v2/report-problem` (the SDK contract).
- `json` — `POST /api/v1/reports` (the server-to-server API).

The CMS Reports page filters and groups by this column; both paths share the same idempotency
(content-hash filename) so a multipart upload and a JSON-API submission of the **same JSON** map
to the same on-disk file.

## CMS console

`ReportService.Admin` runs as its own process bound to host loopback. Key pages:

- `/` — Dashboard: tiles, per-platform totals, latest submissions, component health, index status.
- `/ProblemReports` — searchable + filterable + paginated list of user-submitted reports (non-crash).
  Filters: platform, pharmacy, userId, email, phone, app version, date range, filename contains.
- `/Errors` — error bucketing dashboard, grouped by `top_frame` (extracted at ingest from the
  gzip attachment) with a truncated-message fallback for non-crash submissions.
- `/ForcedReports` — operator-managed allow-list of identifiers that trigger automatic
  Report-a-Problem submissions on the next mobile-app init.
- `/Report/{platform}/{fileName}` — JSON preview (truncated at 512 KiB), download, delete.
- `/Status` — service version, uptime, all DB paths/sizes, schema version, drift counters.
- `/Maintenance` — rebuild index from files, integrity check, vacuum + analyze, atomic backup
  snapshot, CSV/JSON export. Each action requires an authenticated cookie + antiforgery token and
  writes an audit row.
- `/Audit` — the most recent 200 admin events.

## Where to read more

- The repo-root README — operations + deployment (lives outside this docs site).
- The **API reference** entry in the top navigation — generated from XML doc comments.
- `openapi/ingestion-v1.json` / `openapi/ingestion-v1.yaml` — generated from the ingestion service's compiled assembly.
