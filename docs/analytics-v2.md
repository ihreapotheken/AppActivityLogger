# v2 analytics pipeline

This is the implementation reference for the v2 analytics service introduced beside the existing
report/crash ingestion. Numbers, table names, and field names below are what's actually shipped in
this repository (Phase 1 + Phase 2 server work); the Android/iOS sections are forward plans for
the SDK repos to consume.

## Server surface

### Endpoint

```
POST /api/v2/analytics/events
Content-Type: application/json
apiKey: <shared secret>
```

Body shape (`RSCAnalyticsBatch`):

```json
{
  "schemaVersion": 1,
  "batchId": "uuid",
  "platform": "android",
  "sdkVersion": "1.0.0",
  "hostAppVersion": "5.6.7",
  "anonymousId": "stable-per-install",
  "clientId": "optional-pharmacy-id",
  "generatedAt": "2026-05-14T10:00:00Z",
  "events": [
    {
      "eventId": "uuid",
      "sessionId": "session-uuid",
      "sequence": 0,
      "occurredAt": "2026-05-14T10:00:00Z",
      "type": "screen | action | ecommerce | engagement | lifecycle | derived | error",
      "name": "home_viewed",
      "screen": "home",
      "feature": null,
      "durationMs": 100,
      "properties": { "key": "value" },
      "items": [
        { "itemId": "sku-1", "name": "...", "category": "...",
          "price": 1.23, "quantity": 1, "currency": "EUR" }
      ]
    }
  ]
}
```

Response is `202 Accepted` with `RSCAnalyticsBatchReceipt` when at least one event is stored:

```json
{
  "batchId": "uuid",
  "acceptedCount": 1,
  "rejectedCount": 0,
  "duplicateCount": 0,
  "batchRejected": false,
  "batchRejectReason": null
}
```

### Status codes the SDK must handle

- `202 Accepted` — the server persisted at least one event (or every event was an idempotent
  duplicate replay). Check the receipt for per-event outcomes.
- `400 Bad Request` — the request was rejected. Either a structural error (malformed JSON, missing
  `batchId`, unknown fields at the envelope/event level), or a **fully-rejected** batch where the
  validator wouldn't accept *any* event (`batchRejected: true` with a reason such as
  `schema_version_unsupported`, `batch_too_large`, `platform_unknown`, `app_unknown`,
  `client_unknown` — or every event dead-lettered). On a full rejection the `RSCAnalyticsBatchReceipt`
  is still returned as the body. Do not retry the same payload as-is.
- `401 Unauthorized` — wrong/missing apiKey. The SDK should not retry without operator action.
- `413 Payload Too Large` — body exceeded `MaxJsonBytes`. Split the batch and retry.
- `415 Unsupported Media Type` — non-`application/json` content type.
- `429 Too Many Requests` — concurrency/queue limiter said no. Honor `Retry-After: 2` and try
  again with the same `batchId` (idempotent retry).
- `503 Service Unavailable` — analytics is disabled server-side, or upstream timed out. Treat as
  a retriable failure; back off and retry the same `batchId`.

### Idempotency

The server enforces `UNIQUE(platform, event_id)`. Re-sending the same `batchId` with the same
`eventId` set is a no-op — the receipt reports `duplicateCount` for the dedup count. SDKs should
hold the batch in their outbox until they see a successful 2xx for that `batchId`.

### Validator rejections (dead-letter reasons)

Every rejected event lands in `analytics_dead_letters` with a reason from
`RSCAnalyticsDeadLetterReasons`:

| Reason                       | Trigger                                                  |
|------------------------------|----------------------------------------------------------|
| `schema_version_unsupported` | `schemaVersion` outside `[MinAccepted..MaxAccepted]`     |
| `batch_too_large`            | events > `MaxEventsPerBatch`                             |
| `event_too_large`            | reserved (currently checked via property limits)         |
| `property_too_large`         | a property key/value over the configured length          |
| `property_count_exceeded`    | properties > `MaxPropertiesPerEvent`                     |
| `missing_required_field`     | `eventId / sessionId / type / name / occurredAt` missing |
| `invalid_timestamp`          | `occurredAt` not ISO-8601 parseable                      |
| `clock_skew`                 | `\|occurredAt - now\|` > `MaxClockSkewSeconds`           |
| `type_unknown`               | event `type` not in `RSCAnalyticsEventKinds`             |
| `platform_unknown`           | batch `platform` not in `AllowedPlatforms`               |
| `pii_key_forbidden`          | property key in `ForbiddenPropertyKeys`                  |
| `duplicate_event_id`         | `eventId` repeats inside one batch                       |
| `empty_batch`                | `events` is null or empty                                |

The Health admin page renders dead-letter counts grouped by reason plus a 20-row sample.

### Storage

Tables created by `RSCM001_CreateAnalyticsTables` (analytics DB, default `analytics.db`):

- `analytics_batches` — one row per envelope. Idempotent on `batch_id`.
- `analytics_events` — one row per accepted event. Idempotent on `(platform, event_id)`.
  `aggregated_at` starts NULL and is set when the worker has folded the row into rollups.
- `analytics_sessions` — per-session derived metadata: started/last-seen, event/screen count.
- `analytics_user_days` — distinct hashed users per (platform, day). Carries `hash_version`.
- `analytics_daily_rollups` — pre-aggregated DAU/WAU/MAU + event/session counts.
- `analytics_funnel_steps` — observation table for JSON-defined funnels.
- `analytics_dead_letters` — operational DLQ, capped by `DeadLetterRetentionDays`.

### Aggregation worker

`RSCAnalyticsAggregationWorker` is a `BackgroundService` that runs every
`AggregationIntervalSeconds`. It:

1. Reads up to `AggregationBatchSize` unaggregated events ordered by row id.
2. Groups them by `(platform, day)` and by `session_id`.
3. Upserts `analytics_sessions`, `analytics_user_days`, and `analytics_daily_rollups`.
4. Marks every event in the tick `aggregated_at = now`.

The worker is at-least-once: a crash between step 3 and step 4 replays the same events. User-day
rollups dedupe on `(platform, day, hash)`, but session and daily totals use SUM/MAX upsert
semantics — a replay can over-count those marginally. Acceptable trade vs. a per-event ledger.

### Retention worker

`RSCAnalyticsRetentionWorker` sweeps every hour:

- Deletes `analytics_events` rows older than `RawEventRetentionDays` (default 30).
- Deletes `analytics_dead_letters` rows older than `DeadLetterRetentionDays` (default 14).

Rollup tables are not trimmed — they're tiny and form the long-term operator-facing history.

### Identifier hashing

`anonymousId` and `clientId` are never persisted raw. `RSCAnalyticsIdentifierHasher` SHA-256s them
together with `IdentifierHashPepper`. The active `IdentifierHashVersion` is written into every
`analytics_user_days` row so a pepper rotation can be reconciled: future-you bumps the version,
the rollup worker writes new rows under the new version, and a rebuild script can detect the
boundary.

> Today the pepper is a single config value with no rotation mechanism in the codebase. Adding
> rotation is a follow-up — the schema is ready for it.

## Admin surface

### `/Analytics`

Reads from `RSCSqliteAnalyticsStore` (per `RSAAnalyticsStoreDashboardService`):

- DAU/MAU/Sessions today/Avg session length tiles.
- Per-platform rows.
- Top 5 screens (from raw `analytics_events`).
- Retention placeholder (D1/D7/D30 = 0 until the dedicated cohort worker lands).

If analytics is disabled (`Analytics:Enabled=false`), DI falls back to the legacy
`RSAReportStoreAnalyticsDashboardService` so the page still renders against stored
`kind = "analytics"` reports.

### `/AnalyticsHealth`

Operator visibility into pipeline correctness:

- Schema version + last-aggregated timestamp.
- Per-platform batch/event counts (accepted, rejected, last-received).
- Dead-letter totals grouped by reason.
- Top 20 SDK versions seen.
- 20-sample of recent dead-letter rows with reason + detail + raw JSON.

### `/Status`

A new "v2 analytics" card surfaces:

- Enabled / disabled / unavailable state.
- Last-aggregated timestamp.
- Dead-letter total.
- Per-platform batch summary.
- A link through to `/AnalyticsHealth`.

### `/Maintenance` — "Import legacy analytics"

The `RSAAnalyticsLegacyImporter` walks the existing problem-report store, picks rows where
`Kind == "analytics"`, and converts each into a one-event v2 batch (`legacy-v1` SDK version,
`legacy-import` feature tag). Idempotent — re-running is safe because of
`UNIQUE(platform, event_id)`.

## Configuration

`appsettings.json` section `Analytics` (`RSCAnalyticsOptions`):

```json
{
  "Analytics": {
    "Enabled": true,
    "SqliteDbPath": "analytics.db",
    "MinAcceptedSchemaVersion": 1,
    "MaxAcceptedSchemaVersion": 1,
    "MaxEventsPerBatch": 250,
    "MaxPropertiesPerEvent": 64,
    "MaxPropertyValueLength": 2048,
    "MaxPropertyKeyLength": 64,
    "MaxClockSkewSeconds": 86400,
    "IdentifierHashPepper": "set-me-in-prod",
    "IdentifierHashVersion": 1,
    "ForbiddenPropertyKeys": [ "email", "phone", "..." ],
    "AggregationIntervalSeconds": 30,
    "AggregationBatchSize": 5000,
    "RawEventRetentionDays": 30,
    "DeadLetterRetentionDays": 14,
    "SqliteCommandTimeoutSeconds": 10
  }
}
```

In production, `IdentifierHashPepper` MUST be set to a non-empty operator-managed secret.

## Admin sub-pages (shipped)

- `/AnalyticsEvents` — paginated event search (filters: platform, type, name, screen, session,
  date window). Click-through from the session column drills into `/AnalyticsSession?platform=&id=`.
  Filter on `type=error` to see SDK diagnostic events; filter on `session=sdk-diag-*` to isolate
  the SDK's internal error stream from user sessions.
- `/AnalyticsSessions` — most-recently-active session list with platform filter + per-session
  drill-through.
- `/AnalyticsSession` — full event timeline for one session, ordered by sequence then occurredAt.
- `/AnalyticsRetention` — D1/D7/D30 retention by install cohort. Cohorts younger than the window
  are excluded (e.g. a cohort installed yesterday can't have D7 data yet). Cohort-weighted summary
  at the top; full per-cohort table below.
- `/AnalyticsFunnels` — step-by-step session counts for operator-defined funnels. Conversion is
  relative to step 0. Filter by platform to surface Android vs iOS drop-off differences. Funnel
  definitions are editable in the admin DB and seeded from `Analytics:SeedFunnels` on first start.
- `/AnalyticsHealth` — pipeline correctness dashboard: schema version, last-aggregated timestamp,
  per-platform batch/event counts, dead-letter totals by reason, SDK versions seen, and a 20-row
  sample of recent dead-letter rows. SDK error events (type=error, feature=sdk) appear in the
  Events and Sessions pages, not in dead letters — dead letters are validator rejections.

## Pepper rotation (shipped — partial)

The maintenance page now has a **Rotate identifier-hash pepper** action that purges
`analytics_user_days` rows under the old `hash_version`. The actual pepper value lives in
`Analytics:IdentifierHashPepper` and is read at startup, so the operator workflow is:

1. Update `Analytics:IdentifierHashPepper` to a new value in config.
2. Bump `Analytics:IdentifierHashVersion`.
3. Restart the service.
4. Click **Rotate identifier-hash pepper** on `/Maintenance` (confirm-token `ROTATE`).

This is *destructive to retention cohort continuity*: raw anonymous IDs are never stored, so old
hashes cannot be reconciled with new ones. The orphaned rows are deleted; cohorts start fresh
under the new hash version. The schema already carries `hash_version` per row, so a future
implementation can keep dual-version data side-by-side if needed.

## SDK non-fatal error tracking (Android + iOS — IMPLEMENTED)

Both SDKs now capture non-fatal infrastructure errors as `type=error` events with `feature=sdk`
and route them through the same analytics outbox. They land in a stable per-process diagnostic
session (`sessionId = "sdk-diag-XXXX"`) so they don't inflate user session metrics.

### Error event catalog

| Event name               | When emitted                                                                          |
|--------------------------|---------------------------------------------------------------------------------------|
| `sdk_batch_http_error`   | Batch POST returned a non-2xx status. `retryable=true` for 429/5xx, `false` for 4xx. |
| `sdk_batch_network_error`| Batch POST failed at the network layer (IOException, SocketTimeout, Swift URLError).  |
| `sdk_api_http_error`     | Any non-analytics HTTP call through `ApiResponseCall` / `URLSessionRequestService` returned an error. |
| `sdk_outbox_parse_error` | Malformed JSONL lines found in the on-disk outbox during a flush.                     |

### Consecutive-failure guard

The first failure in a run emits one event; subsequent failures in the same episode are silent.
The counter resets when a batch succeeds. This prevents a sustained network outage from flooding
the outbox with thousands of identical error events.

### Android implementation

- [`V2AnalyticsSink.kt`](../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/analytics/v2/V2AnalyticsSink.kt)
  — `emitSdkError()` method; `consecutiveFailures` counter; `sdkDiagSessionId`; wires
  `ApiResponseCall.onApiError` in `start()`/`stop()`.
- [`AnalyticsOutbox.kt`](../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/analytics/v2/AnalyticsOutbox.kt)
  — `skippedLinesSinceLastCheck: AtomicInteger` incremented on each `JsonSyntaxException` in
  `parseOrNull()`; read and reset by the sink before each flush.
- [`ApiResponseCall.kt`](../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/remote/response/ApiResponseCall.kt)
  — `companion object { var onApiError }` added; invoked in every error branch of `onResponse`
  (non-2xx HTTP) and `onFailure` (SocketTimeout, HttpException, IOException, other).

### iOS implementation

- [`URLSessionRequestService.swift`](../IA-SDK-Dev-iOS/IOSKit/IOSKit/HTTPClientNetworking/RequestService/URLSessionRequestService.swift)
  — `public nonisolated(unsafe) static var onError` callback; invoked before `badStatusCode` and
  `decodingError` throws.
- [`AnalyticsV2Dispatcher.swift`](../IA-SDK-Dev-iOS/IACore/IACore/Features/AnalyticsV2/AnalyticsV2Dispatcher.swift)
  — `sdkDiagSessionId`, `sdkDiagSequence`, `consecutiveFailures` properties; `tryFlushOnce()`
  emits `sdk_batch_http_error` / `sdk_batch_network_error`; `emitSdkError()` writes directly into
  the outbox; `start()`/`stop()` wire `URLSessionRequestService.onError`.

### Mock data

The dev seeder (`RSAAnalyticsDevDataSeeder.SeedSdkDiagnosticsAsync`) seeds 9 episodes per
platform covering all 4 error kinds over a 7-day window. Each episode is its own batch with a
`receivedAt` close to the event timestamp so the validator's 24-hour clock-skew check passes.
View them at `/AnalyticsEvents` with filter `type=error` or `session=sdk-diag-*`.

## Android (IA-SDK-Dev-Android — IMPLEMENTED)

Shipped on `feature/IASDK-2413/error-reporting`:

- [`V2AnalyticsConfig.kt`](../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/analytics/v2/V2AnalyticsConfig.kt) — endpoint URL, apiKey, sdkVersion, hostAppVersion, anonymousId/clientId, batch size, flush interval, backoff caps, outbox size cap.
- [`V2WireTypes.kt`](../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/analytics/v2/V2WireTypes.kt) — `V2AnalyticsBatchRequest`, `V2AnalyticsEventDto`, `V2AnalyticsItemDto`, `V2AnalyticsBatchReceipt` (Gson-tagged, camelCase wire shape).
- [`V2AnalyticsService.kt`](../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/analytics/v2/V2AnalyticsService.kt) — Retrofit interface with `@Url` indirection so the host can repoint per environment.
- [`AnalyticsOutbox.kt`](../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/analytics/v2/AnalyticsOutbox.kt) — file-backed JSONL outbox in `noBackupFilesDir`, with size-cap-driven oldest-first eviction, atomic drain via temp-file + rename.
- [`V2AnalyticsSink.kt`](../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/analytics/v2/V2AnalyticsSink.kt) — `RemoteAnalyticsSink` implementation that maps the in-memory `AnalyticsEvent` to wire DTOs, persists to outbox, and drains via the Retrofit service with exponential backoff. Handles 2xx/4xx/5xx drop-vs-retry semantics (401/403/429 stay, rest of 4xx is dropped).
- [`AnalyticsDispatcher.kt`](../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/listener/AnalyticsDispatcher.kt) — gained a `remoteSink` slot wired into `deliverEvent` so the v2 sink fires in parallel with the host listener. `setRemoteSink(sink)` starts/stops the sink lifecycle.

**Tests** (10/10 green) — outbox enqueue/peek/drain/clear/malformed-line + mapper coverage for
screen/action/lifecycle/ecommerce/engagement/derived events.

**Wiring left to the host**: build a `V2AnalyticsSink` in SDK init (passing context, config, and a
Retrofit `V2AnalyticsService` built off the existing `OkHttpClient`), then
`analyticsDispatcher.setRemoteSink(sink)`.

**Android follow-ups**:
- Replace clear-text outbox with Keystore-backed AES-GCM. Threat model today is durability, not
  confidentiality (validator strips PII), but adding encryption is straightforward given the JSONL
  format.
- Hook `AnalyticsConsent` revocation into `RemoteAnalyticsSink.clearAll()` — the current consent
  field is a plain `var` without a setter callback.
- Replace the legacy `AnalyticsRemoteDataSource` with the v2 sink once production has switched
  over.

## iOS (IA-SDK-Dev-iOS — IMPLEMENTED, additive only)

Shipped on `develop` (working alongside the in-progress `feature/IASDK-2413/error-reporting`
modifications without touching them):

- [`V2AnalyticsReporter.swift`](../IA-SDK-Dev-iOS/IOSKit/IOSKit/HTTPClientNetworking/V2AnalyticsReporter.swift)
  — `V2AnalyticsBatchRequest` + DTOs + `URLSessionV2AnalyticsReporter`. Sendable + Codable. Lives
  in IOSKit alongside the legacy `URLSessionAnalyticsReporter` so both can coexist.
- [`AnalyticsV2Config.swift`](../IA-SDK-Dev-iOS/IACore/IACore/Features/AnalyticsV2/AnalyticsV2Config.swift)
  — config struct separate from `SDKConfiguration` to avoid merge conflicts with the
  forced-report work in flight.
- [`AnalyticsV2Event.swift`](../IA-SDK-Dev-iOS/IACore/IACore/Features/AnalyticsV2/AnalyticsV2Event.swift)
  — in-memory event model + item.
- [`AnalyticsV2Outbox.swift`](../IA-SDK-Dev-iOS/IACore/IACore/Features/AnalyticsV2/AnalyticsV2Outbox.swift)
  — JSONL outbox under no-backup application-support dir, with size-cap, atomic drain. Mirrors
  the Android semantics.
- [`AnalyticsV2Dispatcher.swift`](../IA-SDK-Dev-iOS/IACore/IACore/Features/AnalyticsV2/AnalyticsV2Dispatcher.swift)
  — `actor` with `start(config:)`, `track(event:)`, convenience methods for
  screen/action/lifecycle, and a background flush task with exponential backoff.

`swiftc -typecheck` confirms the four IACore files compile under iOS 15 target; the SwiftPM
`Package.swift` already groups them into the right targets (IOSKit, IACore) because the file
includes are path-based.

**iOS follow-ups**:
- **Xcode project files**: The new files compile via SwiftPM but the modified `.xcodeproj`
  package on `develop` does not list them yet. Once the in-progress `feature/IASDK-2413` work
  lands, add the four files to `IACore.xcodeproj` and `V2AnalyticsReporter.swift` to
  `IOSKit.xcodeproj` so xcodebuild for the demo apps picks them up.
- Bridge the existing CardLink `TrackResult` (and any other feature-level event sites) into
  `AnalyticsV2Dispatcher.shared.track(_:)` — currently the dispatcher only fires when host code
  calls it.
- A SwiftUI modifier on top of the existing `.screenIdentifier(...)` helper so screen events are
  automatic.
- Keychain-backed AES-GCM outbox encryption (same threat-model footnote as Android).
- `scenePhase` / foreground/background notifications for session boundaries — currently the host
  has to call `renewSession()` manually.

## Android plan (IA-SDK-Dev-Android — historical, see "IMPLEMENTED" above for what shipped)

The existing `AnalyticsDispatcher`, `AnalyticsPipeline`, screen/session tracking, and
ecommerce/engagement event hierarchy stay as-is. The mobile-side work is to add a new sink that
talks to `/api/v2/analytics/events` and to harden the producer side.

1. **Turn `AnalyticsRemoteDataSource` into a real `AnalyticsSink`.** Today it's hard-coded to a
   local URL. Make it a first-class destination on the dispatcher's fanout alongside the debug
   observer and the host listener.

2. **Push endpoint config into `IaSdkConfiguration`.** Endpoint URL, apiKey, environment-derived
   defaults, sampling rate, consent gate, and upload policy. Strip the `10.0.2.2` literal.

3. **Add a file-backed encrypted outbox.** Batches flush on a timer + on app foreground + on
   network availability. Exponential backoff on 429/503 honoring `Retry-After`. Dedupe by
   `eventId` so a crash between persist and ACK doesn't double-send. The current in-memory queue
   is fine for the host-listener path but not for remote durability.

4. **Map the existing Kotlin `AnalyticsEvent` hierarchy onto the v2 wire shape.** Screen/session,
   ecommerce items, engagement, lifecycle, derived events all have direct equivalents. PII
   stripping happens client-side too so the request doesn't even leave the device with banned
   keys.

5. **Broaden coverage.** Apofinder searches/filters, OTC product/search/detail/cart, prescription
   scanner outcomes, CardLink, public API entry points, errors.

6. **Contract tests against the server schema.** Reuse the same fixtures the server validator
   ships with, so a schema change in the server's `RSCAnalyticsValidator` automatically breaks
   the SDK CI until both sides ship together.

## iOS plan (IA-SDK-Dev-iOS — NOT EDITED HERE)

iOS doesn't have an SDK-wide analytics dispatcher equivalent to Android. The plan is to build one
from the existing pieces.

1. **`AnalyticsDispatcher` actor in IACore.** Session id, anonymous id, screen/action/ecommerce/
   engagement/lifecycle/derived events, consent gate, property sanitizer, host listener/delegate,
   remote sink, debug/log observer.

2. **Wire it to `SDKConfiguration.analyticsEnabled`.** Public API parity with Android —
   `IASDK.setAnalyticsListener` and `setAnalyticsConsent`.

3. **SwiftUI integration.** A `.analyticsScreen(...)` modifier on top of the existing
   `.screenIdentifier(...)` helper so screen views/exits are automatic instead of hand-coded per
   ViewModel.

4. **Convert `URLSessionAnalyticsReporter`.** From single-event report-shaped POSTs into the same
   v2 batch sink as Android, with the same outbox semantics. The multipart crash/report uploader
   stays separate.

5. **Bridge CardLink `TrackResult` into the dispatcher.** Keep the host-callback path for
   backwards compatibility, with an explicit deprecation date — otherwise the dual path lives
   forever.

6. **`scenePhase`/foreground lifecycle events** for session boundaries. MetricKit-based crash
   diagnostics is a later phase; user-initiated problem reports stay on the existing repository.

## Cross-cutting

- **Schema versioning policy.** Server today accepts only `schemaVersion = 1` (configurable
  range). Minor additive fields land in the events' `properties` bag (`UnmappedMemberHandling`
  only rejects unknown fields at envelope/event/item level). Major increments require both sides
  to ship; an SDK shipping ahead of the server is rejected on purpose to avoid silent truncation.

- **Consent revocation.** Outbox-buffered events MUST be dropped, not flushed, when consent is
  revoked between capture and send. This is a client-side policy — both SDKs need to honor it
  identically.

- **Pepper rotation.** Stored alongside every `analytics_user_days` row via `hash_version`. A
  rotation will look like a retention-cohort discontinuity until the rollup tables are rebuilt
  under the new hash. Plan migrations accordingly.

## Contract fixtures

`docs/analytics-contract/` is the single source of truth for the wire shape:

- `fixtures/accept/` — canonical JSON batches per event kind. Server tests assert validator
  acceptance; SDK repos run their serializer round-trip against the same files.
- `fixtures/reject/` — one fixture per documented `RSCAnalyticsDeadLetterReasons` value.
  `AnalyticsContractFixtureTests` covers each branch.
- `event-catalog.json` — canonical event taxonomy (names, types, owners, properties) plus the
  authoritative `forbiddenPropertyKeys` list. Two of the fixture tests keep this file in sync
  with `RSCAnalyticsOptions.ForbiddenPropertyKeys` and `RSCAnalyticsEventKinds.Known` — a server
  change that drifts them breaks `dotnet test`.
- `CONTRACT.md` — operator-facing readme that explains how SDK repos should vendor or submodule
  the directory.

## Aggregation tick atomicity (Phase 3)

The aggregation worker now hands its precomputed deltas plus the source event IDs to a single
`RSCIAnalyticsStore.WriteAggregationTickAsync(...)` call. That method applies every session /
user-day / daily-rollup upsert and marks the events `aggregated_at = now` inside one SQLite
transaction. A crash before the commit leaves the unaggregated pool unchanged; the next tick
replays the same events with no net change in the rollup tables. The previous at-least-once
caveat — "session and daily totals may marginally over-count on replay" — no longer applies.

## Retention cohort worker (`RSCAnalyticsCohortWorker`)

Runs every `Analytics:CohortIntervalSeconds` (default 3600s). Walks `analytics_user_days`, derives
first-seen day per (platform, hashed-id), then for every (platform, install_day) inside a 90-day
window counts how many of those users were active again at install_day + 1, 7, 30 days. Writes
`analytics_retention_cohorts` (one row per (platform, install_day), upserted). The
`/AnalyticsRetention` admin page renders the cohort table plus a cohort-weighted D1/D7/D30
summary (cohorts younger than the window are excluded from each metric).

The Analytics dashboard's D1/D7/D30 tile now reads from `GetRetentionSummaryAsync` instead of
the hard-coded zeros — fresh installs still show zero until the worker has run at least once.

## Funnel worker (`RSCAnalyticsFunnelWorker`)

Runs every `Analytics:FunnelIntervalSeconds` (default 600s). For every enabled row in
`analytics_funnel_definitions`, walks the last 14 days of `analytics_events`, matches each
session's event stream against the step list in order, and writes step observations into
`analytics_funnel_steps` (`INSERT OR IGNORE` on `(funnel_key, session_id, step_index)`).

Definitions are operator-overridable via the admin DB; the worker seeds defaults from
`Analytics:SeedFunnels` (currently `otc_purchase`, `cardlink_activation`) on first start, and
will never overwrite an operator edit because the seed only inserts on missing keys.

`/AnalyticsFunnels` shows step-by-step session counts and per-step conversion (relative to the
first step) over the last 30 days. Step matchers today are `{name, eventName, eventType?}` —
exact-match on `eventName`, optional type filter.

## NDJSON exports

Admin-only endpoints behind the cookie auth fallback policy:

- `GET /admin/api/analytics/events.ndjson?platform&type&name&screen&session&from&until&limit`
- `GET /admin/api/analytics/sessions.ndjson?platform&limit`

Capped at 50,000 rows per response so a single click doesn't tar-pit the admin. Streams one JSON
object per line so a CSV converter or `jq` pipeline can chew through the result without buffering.

## Out of scope (still deferred)

These items remain follow-ups even after the latest round:

- **Crash-after-screen correlation** between analytics events and the report/crash store.
- **Android Keystore-backed outbox encryption** (clear-text JSONL today).
- **iOS Keychain-backed outbox encryption** (clear-text JSONL today).
- **iOS Xcode project file updates** for the new `IACore/Features/AnalyticsV2/` files and the
  `IOSKit/HTTPClientNetworking/V2AnalyticsReporter.swift` — additive entries that should land
  *after* the in-progress `feature/IASDK-2413/error-reporting` work commits, to avoid
  pbxproj merge conflicts.
- **Consent-revocation hook** that clears the SDK outboxes when the user opts out — currently the
  outbox keeps draining anything that was captured before consent was revoked.
- **Mobile batch-id reuse on retry.** Both Android (`V2AnalyticsSink.kt`) and iOS
  (`V2AnalyticsReporter.swift`) generate a fresh batch UUID per flush attempt. The server now
  records post-dedupe event counts, so the inflation is bounded to the envelope row count, but
  per the contract section above the SDKs should persist the batchId in their outbox and reuse it
  across retries.
