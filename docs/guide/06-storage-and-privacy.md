# 6. Storage & privacy

## 6.1 On-disk layout

```text
<ReportsRoot>/<platform-lower>/problem-reports/
    problem-report_<yyyyMMdd-HHmmss>_<sha12>.json
    problem-report_<yyyyMMdd-HHmmss>_<sha12>_<attachSha12>.log.gz   # optional sibling
```

- `yyyyMMdd-HHmmss` is UTC at persist time.
- `<sha12>` is the first 12 hex chars of `SHA-256(jsonBytes)`; when an attachment is present its own
  `<attachSha12>` is appended so different attachments with identical JSON don't collide.
- JSON and its attachment share the same base name, so enumerating `*.json` locates the matching
  `.log.gz`.
- Writes are atomic: each file is written to a temp path then `File.Move(overwrite: true)`, so a
  crash mid-write never leaves a half-written file visible.

In the Docker stack all writable state lives on the `reports` volume (the container root FS is
read-only): `reports/` (JSON + attachments), `reports.db` (report index), `analytics.db`,
`audit.db`, `auth-abuse.db`, `api-keys.db` (managed API keys), and `backups/`.

## 6.2 SQLite report index (optional)

When `Storage = "SqliteIndex"`, [`RSCSqliteIndexingReportStore`](../../src/ReportService.Core/Storage/RSCSqliteIndexingReportStore.cs)
decorates the file-system store: the file is persisted first, then the index row is upserted. Index
failures are logged at **Warning** and swallowed — the upload is already durable on disk and a
stale index can be rebuilt from the files. `List` unions index rows with files on disk so a
swallowed upsert can't hide a persisted report.

```sql
CREATE TABLE IF NOT EXISTS problem_reports (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  platform         TEXT NOT NULL,
  file_name        TEXT NOT NULL,
  submitted_at     TEXT NOT NULL,           -- ISO 8601 round-trip
  device_model     TEXT,
  title            TEXT,
  email_hash       TEXT,                    -- SHA-256 hex of UTF-8 email; raw email never indexed
  pharmacy_id      TEXT,
  app_version      TEXT,
  has_attachment   INTEGER NOT NULL,
  size_bytes       INTEGER NOT NULL,
  attachment_bytes INTEGER,
  labels_json      TEXT,
  -- later migrations add: ingestion_channel, top_frame, user_id, phone, log_summary_json, kind
  UNIQUE(platform, file_name)
);
```

`journal_mode=WAL` is set once at bootstrap; `synchronous=NORMAL` is applied per connection.
Transient `SQLITE_BUSY`/`SQLITE_LOCKED` errors are retried with exponential backoff. Both the report
index and the analytics DB evolve through versioned migrations under
[`Storage/Migrations/`](../../src/ReportService.Core/Storage/Migrations/), driven by
`PRAGMA user_version`.

## 6.3 Privacy & PII

The `json` part can contain personal data (`email`, `phoneNumber`, `phone`, `pharmacyId`, and
anything typed into `message`/`title`). Handling:

- The JSON is stored **verbatim** on disk. Operators own retention, at-rest encryption, and disk
  access control on `ReportsRoot`.
- The report index stores only a **SHA-256 digest of the email** (`email_hash`) — never the raw
  address.
- Analytics identifiers (`anonymousId`/`clientId`) are stored only as a **peppered SHA-256 hash**;
  the validator dead-letters events whose property keys look like PII (`email`, `phone`, `token`,
  `iban`, …) with reason `pii_key_forbidden`.
- Managed API keys (`api-keys.db`) store only a **SHA-256 hash** of each key (256-bit CSPRNG secret,
  so an unsalted digest is sufficient — GitHub-PAT style). The plaintext is shown once at creation
  and never persisted; revocation/expiry are enforced on every auth via the in-memory cache.
- Logs never include the message body, email, pharmacy id, request headers, or attachment bytes —
  only metadata (platform, file name, byte counts, attachment presence).
- Exception responses are RFC 7807 with a `traceId`; exception messages are never echoed.

## 6.4 Retention

- **Reports** — the report retention sweep (hosted in-process, hourly by default) deletes by total
  byte cap (`RetentionMaxBytes`, oldest-first to ~95%) and by age (`RetentionMaxAgeDays`, default
  30). The admin **Maintenance** page can trigger a one-shot purge.
- **Disk-pressure guard** — `RetentionMaxBytes` only bounds report blobs. The analytics/audit
  SQLite DBs, the backups directory, un-VACUUMed slack, and anything else sharing the volume are
  *not* counted. The opt-in `RetentionMinFreeDiskBytes` / `RetentionMaxDiskUsagePercent` knobs make
  the same sweep watch the **actual filesystem** holding `ReportsRoot` and evict oldest reports
  first (+10% cushion) when it gets tight, so unbounded non-report growth can't silently fill the
  disk and 503 ingestion. Because eviction only frees report blobs, pressure originating in the
  DBs/backups is logged as a warning and needs a manual VACUUM/cleanup. The `/Status` page shows
  live volume free/used so you can size `RetentionMaxBytes` against real capacity (it does **not**
  auto-scale to disk size — set it per host).
- **Analytics** — the analytics retention worker trims raw `analytics_events` older than
  `RawEventRetentionDays` (prod 30, dev 365) and dead letters older than `DeadLetterRetentionDays`.
  Rollups, cohorts, and funnels are kept indefinitely, so dashboards retain history even after raw
  events are trimmed.
