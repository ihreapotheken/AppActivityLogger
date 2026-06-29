# 3. Configuration

Settings bind from three configuration sections at startup. In the Docker dev stack the values come
from the compose `environment:` block, `.env` (`env_file`), and
`src/ReportService.Admin/appsettings*.json`. ASP.NET Core maps environment variables with `__` as
the section separator, so any option below can be overridden without editing a file:
`ReportService__ReportsRoot=/srv/reports`, `Analytics__AggregationBatchSize=40000`, etc. Array
elements are indexed: `ReportService__AllowedPlatforms__0=android`.

## 3.1 `ReportService` section

Binds to [`RSCReportServiceOptions`](../../src/ReportService.Core/Options/RSCReportServiceOptions.cs).

| Option | Default | Purpose |
|---|---|---|
| `ApiKey` | `""` | Shared secret in the `apiKey` header — the permanent **root-admin** key (always valid, never expires, not stored in the DB). Bootstraps managed keys. Empty disables it (DB keys still work once minted). Must be set in any real deployment. |
| `ApiKeysDbPath` | `"api-keys.db"` | SQLite file for managed keys (admin/user, expiry, revocation, per-key limit). Only SHA-256 hashes stored. Resolved under `ReportsRoot` when relative. |
| `ApiKeyAdminRateLimitPerMinute` | `0` (⇒ `RateLimitPermitsPerMinute`) | Per-minute budget for admin-role keys (and the root key). Per-key override beats it. |
| `ApiKeyUserRateLimitPerMinute` | `0` (⇒ `RateLimitPermitsPerMinute`) | Per-minute budget for user-role keys. Per-key override beats it. |
| `Environment` | `"production"` | Free-text label surfaced on `/api/health` and the admin badge so you can tell prod from staging. |
| `ReportsRoot` | `"reports"` | Filesystem root for stored reports (absolute, or resolved against the working dir). Each allowed platform gets a `problem-reports/` child folder, created on startup. |
| `AllowedPlatforms` | `["android","ios"]` | Allow-list for the `platform` field and `{platform}` route params. Inbound values are lowercased before matching. |
| `MaxUploadBytes` | `524288000` (500 MiB) | Hard cap on the **entire multipart body**. Enforced by Kestrel and `FormOptions`. |
| `MaxAttachmentBytes` | `52428800` (50 MiB) | Hard cap on the **gzip `file` part alone**, checked after multipart parse. |
| `MaxJsonBytes` | `1048576` (1 MiB) | Hard cap on a JSON-only `/api/v1/reports` body. |
| `RateLimitPermitsPerMinute` | `120` | Per-partition sliding-window request budget — partition is the resolved API key when present, else source IP. Acts as the per-IP default and the fallback for the role tiers above. Excess → `429` + `Retry-After: 2`. |
| `IngestConcurrency` | `16` | Global cap on in-flight write-path requests across all IPs. The permit is acquired before the endpoint runs, so excess never touches parser/disk. |
| `IngestQueueLimit` | `16` | Waiting slots once `IngestConcurrency` is saturated; excess → `429` + `Retry-After: 2`. Keep small. |
| `IngestTimeoutSeconds` | `60` | Per-request wall-clock deadline; a stuck disk/client can't pin a permit past it. |
| `Storage` | `"FileSystem"` | `"FileSystem"` persists JSON + gzip on disk. `"SqliteIndex"` adds a metadata index for fast listing, crash grouping, and filters. The compose stack uses `SqliteIndex`. |
| `SqliteDbPath` | `"reports.db"` | Report index DB path (used when `Storage = SqliteIndex`). Relative paths anchor under `ReportsRoot`. |
| `SqliteCommandTimeoutSeconds` | `10` | Per-statement timeout for the report index DB. |
| `AuditDbPath` | `"audit.db"` | Operator audit-log DB path. |
| `AuthAbuseDbPath` | `"auth-abuse.db"` | Persisted auth-abuse tracker DB path. |
| `AuthAbuseMaxFailures` / `AuthAbuseWindowSeconds` / `AuthAbuseBanSeconds` | `10` / `60` / `300` | Failed-auth threshold, rolling window, and ban duration. |
| `BackupRoot` | `"backups"` | Destination for admin-triggered DB backups. |
| `RetentionEnabled` | `true` | Master switch for the report retention sweep (admin can still trigger a one-shot purge from `/Maintenance`). |
| `RetentionMaxBytes` | `10737418240` (10 GiB) | Total stored-report byte cap; oldest-first deletion brings usage back to ~95%. |
| `RetentionMaxAgeDays` | `30` | Reports older than this are deleted each sweep. `0` disables age-based deletion. |
| `RetentionScanIntervalSeconds` | `3600` | Report retention sweep cadence (floored at 60s). |
| `RetentionMinFreeDiskBytes` | `0` (off) | Disk-pressure guard: when the volume holding `ReportsRoot` has fewer than this many bytes free, the sweep evicts oldest reports first (+10% cushion). Catches growth the byte cap can't see (analytics/audit DBs, backups). |
| `RetentionMaxDiskUsagePercent` | `0` (off) | Disk-pressure guard by percent-used (`1..99`). Evaluated with `RetentionMinFreeDiskBytes`; the larger required eviction wins. Deleting reports only reclaims report blobs — DB/backup pressure still needs a manual VACUUM/cleanup. |

## 3.2 `Analytics` section

Binds to [`RSCAnalyticsOptions`](../../src/ReportService.Core/Options/RSCAnalyticsOptions.cs).

| Option | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Master switch. When `false`, the events endpoint returns `503` and the workers idle. |
| `SqliteDbPath` | `"analytics.db"` | Analytics DB path (separate file from the report index). Relative paths anchor under `ReportsRoot`. |
| `MinAcceptedSchemaVersion` / `MaxAcceptedSchemaVersion` | `1` / `1` | Accepted batch `schemaVersion` range; out-of-range batches are dead-lettered. |
| `MaxEventsPerBatch` | `250` | Batches over this are rejected whole. |
| `MaxPropertiesPerEvent` / `MaxPropertyKeyLength` / `MaxPropertyValueLength` | `64` / `64` / `2048` | Per-event property bounds. |
| `MaxClockSkewSeconds` | `86400` (24h) | Max `\|occurredAt − receivedAt\|`; beyond it the event is dead-lettered. |
| `ForbiddenPropertyKeys` | email/phone/token/… | Property keys that dead-letter the event with `pii_key_forbidden` to catch PII leakage. |
| `IdentifierHashPepper` / `IdentifierHashVersion` | `""` / `1` | Pepper + version for hashing `anonymousId` / `clientId`. Raw IDs are never stored. |
| `AggregationIntervalSeconds` | `30` (dev `5`) | Aggregation worker cadence (floored at 5s). |
| `AggregationBatchSize` | `5000` (dev `40000`) | Max events folded per aggregation tick. |
| `RawEventRetentionDays` | `30` (dev `365`) | Retention for raw `analytics_events` rows; rollups/cohorts are kept indefinitely. |
| `DeadLetterRetentionDays` | `14` | Retention for dead-letter rows. |
| `CohortIntervalSeconds` | `3600` (dev `15`) | Retention/cohort worker cadence (floored at 60s in prod). |
| `FunnelIntervalSeconds` | `600` (dev `15`) | Funnel worker cadence. |
| `SeedFunnels` | OTC + CardLink | Funnel definitions inserted on first run if absent (operator edits aren't overwritten). |

> The dev values come from
> [`appsettings.Development.json`](../../src/ReportService.Admin/appsettings.Development.json), which
> ships in the image and loads only under `ASPNETCORE_ENVIRONMENT=Development`. See
> [Analytics pipeline](05-analytics.md) for why the dev cadence/batch differ.

## 3.3 `Admin` section

Binds to [`RSAAdminOptions`](../../src/ReportService.Admin/Options/RSAAdminOptions.cs).

| Option | Default | Purpose |
|---|---|---|
| `AdminKey` | `""` | Operator sign-in secret. Constant-time compared. **Not** the same as `ReportService:ApiKey`. Generate with `openssl rand -hex 32`. |
| `SessionMinutes` | `60` | Cookie lifetime (sliding). Cookie is HttpOnly, SameSite=Strict, Secure over HTTPS. |
| `DevAutoSignIn` | `false` | When `true`, every request is treated as a signed-in `dev-operator` (skips `/Login`). The compose dev stack sets it via `Admin__DevAutoSignIn=true`. **Never set this in committed Development config** — the test hosts run in Development and would then bypass auth, breaking the auth tests. |
| `DocsRoot` | `"admin-docs"` | Where `/Documentation` reads `README.md` + `guide/*.md` from (resolved against the binary dir). |

## 3.4 Production secret guard

`RSCSecretValidation.RequireInProduction` runs right after `builder.Build()` and refuses to boot a
Production process when `ApiKey` / `AdminKey` is empty, shorter than 32 chars, or matches a known
placeholder (`CHANGE_ME`, `PLACEHOLDER`, `YOUR_SECRET`, …). The `.env.example` placeholders are
built from those markers, so copying it without editing cannot yield a running production process.
