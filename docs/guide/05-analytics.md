# 5. Analytics pipeline

The v2 analytics pipeline ingests batched SDK events and folds them into the aggregates the console
dashboards read. It lives in its own SQLite database (`analytics.db`) so its schema can evolve
independently of the report index.

## 5.1 Ingestion

Events reach the pipeline through **two coexisting endpoints**, both handled by
[`RSAnalyticsIngestionService`](../../src/ReportService/Ingestion/RSAnalyticsIngestionService.cs) and
landing in the same [`RSCSqliteAnalyticsStore.WriteBatchAsync`](../../src/ReportService.Core/Analytics/RSCSqliteAnalyticsStore.cs):

- **`POST /api/v2/analytics/events`** — the mobile **SDK** path. Body is a full
  **`RSCAnalyticsBatch`**: `schemaVersion`, `batchId`, `platform`, `sdkVersion`, `hostAppVersion`,
  `anonymousId` (+ optional `clientId`), `generatedAt`, and an `events[]` array.
- **`POST /api/v2/analytics/server-events`** — the **backend / server-to-server** path. Body is the
  lighter **`RSCServerAnalyticsRequest`**; the service synthesizes the SDK-centric envelope fields
  (sessionId, sequence, batchId, sdkVersion) and maps it onto an `RSCAnalyticsBatch` so it runs the
  **identical** validate/hash/store path. This lets the backend report first-party events — e.g. a
  confirmed purchase — directly instead of relying on the client to emit them. Server events default
  to platform `backend` (configurable via `Analytics:ServerPlatforms`) but may be attributed to
  `android`/`ios` to join a device session. See the **API docs** (`/ApiDocs` — Swagger UI) for the
  request shape and the per-field table.

Each **`RSCAnalyticsEvent`** carries `eventId`, `sessionId`, `sequence`, `occurredAt`, `type`,
`name`, optional `screen` / `feature` / `durationMs`, a `properties` bag, and optional `items`
(e-commerce).

[`RSCAnalyticsValidator`](../../src/ReportService.Core/Analytics/RSCAnalyticsValidator.cs) checks the
batch and routes failures to the dead-letter queue with a documented reason:

- `schemaVersion` outside `[Min,Max]AcceptedSchemaVersion`,
- more than `MaxEventsPerBatch` events,
- `|occurredAt − receivedAt|` over `MaxClockSkewSeconds`,
- property counts/keys/values over their caps,
- a property key in `ForbiddenPropertyKeys` → `pii_key_forbidden`.

Accepted events are written to `analytics_events`, **idempotent on `(platform, eventId)`** — a retry
of the same `batchId`/events is a no-op. The endpoint returns `202 Accepted` with an
`RSCAnalyticsBatchReceipt`; clients retry the same `batchId` on `5xx`/`429`. Identifiers
(`anonymousId`/`clientId`) are hashed with a peppered SHA-256 before storage — raw IDs are never
persisted.

## 5.2 Tables

| Table | Written by | Holds |
|---|---|---|
| `analytics_events` | ingestion | raw accepted events; `aggregated_at` NULL until folded |
| `analytics_batches` | ingestion | one row per accepted batch (provenance) |
| `analytics_dead_letters` | ingestion | rejected events + reason (surfaced on Health) |
| `analytics_sessions` | aggregation | one row per `(platform, session_id)`: start/last-seen, event/screen counts |
| `analytics_user_days` | aggregation | one row per `(platform, day, hashed user)`: event count + hash version |
| `analytics_daily_rollups` | aggregation | per `(day, platform)`: events, sessions, distinct users |
| `analytics_retention_cohorts` | cohort worker | per `(platform, install_day)`: cohort size + D1/D7/D30 retained |
| `analytics_funnel_definitions` | funnel seed/operator | named funnels and their ordered steps |
| `analytics_funnel_steps` | funnel worker | per-session step observations per funnel |

Rollup/cohort/funnel tables are kept indefinitely (they're tiny); only raw `analytics_events` are
trimmed by retention.

## 5.3 Background workers

All four run inside the merged host (cadences from [Configuration](03-configuration.md#32-analytics-section); dev
values in parentheses):

- **Aggregation** (`30s`, dev `5s`) — pulls up to `AggregationBatchSize` unaggregated events,
  groups them by `(platform, day)` and by session, upserts the session / user-day / daily-rollup
  deltas **and** marks the source events `aggregated_at = now` in one transaction (exactly-once per
  tick; a crash before commit just replays them).
- **Cohort / retention** (`1h`, dev `15s`) — recomputes D1/D7/D30 retention from
  `analytics_user_days` into `analytics_retention_cohorts`.
- **Funnel** (`10min`, dev `15s`) — walks events and records per-session step observations for each
  defined funnel; seeds the OTC-purchase and CardLink-activation funnels on first tick
  (INSERT-only, so operator edits survive).
- **Retention** (`1h`) — purges raw `analytics_events` older than `RawEventRetentionDays`
  (prod `30`, dev `365`) and dead letters older than `DeadLetterRetentionDays`.

> **Aggregation performance.** Marking events used `WHERE event_id = ?`, but `event_id` was only
> covered by the composite `UNIQUE(platform, event_id)` index (leading column `platform`), so each
> mark fell back to a full table scan — `O(rows × batch)` per tick. Migration `RSCM003` adds a
> standalone index on `event_id`, making each mark an `O(log n)` lookup. The dev stack also raises
> `AggregationBatchSize` to `40000` so a large seeded backlog drains in a handful of sub-second
> ticks instead of grinding for hours.

## 5.4 Dashboards & exports

Console pages (see [Admin console](08-admin-console.md)): `/Analytics` (engagement tiles, per-platform
rows, top screens, daily trend), `/AnalyticsEvents` (raw event search), `/AnalyticsSessions` +
`/AnalyticsSession` (session list + timeline), `/AnalyticsRetention` (cohort curves),
`/AnalyticsFunnels` (funnel step conversion), `/AnalyticsHealth` (ingestion lag, dead letters,
aggregation backlog).

Operator NDJSON exports (cookie-gated): `GET /admin/api/analytics/events.ndjson` and
`GET /admin/api/analytics/sessions.ndjson`.

## 5.5 Dev data

In Development, [`RSAAnalyticsDevDataSeeder`](../../src/ReportService.Admin/Services/RSAAnalyticsDevDataSeeder.cs)
runs before the first request and inserts a deterministic, idempotent set of realistic events
across both platforms (power/regular/casual/churned/new cohorts, plus SDK diagnostics). The
population scales with `ANALYTICS_SEED_SCALE` (code default `1`; the docker dev stack sets `10` in
`.env`). See [Development](09-development.md#dev-data-seeders) for sizing and the retention
interaction.

## 5.6 Reading the engagement dashboard

The `/Analytics` page answers a simple question: how many people are using the app, and how heavily?
Every tile and table on it is computed from accepted analytics events, not from raw report files.

- **Daily active users (DAU)** — the number of distinct people seen in the last 24 hours. "Distinct
  people" means distinct hashed anonymous IDs, so one person who opens the app five times still
  counts once.
- **Monthly active users (MAU)** — the same count over a rolling 30-day window (not the calendar
  month).
- **Sessions today** — how many app visits started since midnight. A visit is bracketed by the SDK's
  `session_start` and `session_end` lifecycle events.
- **Average session length** — the mean gap between `session_start` and `session_end` across those
  visits, i.e. roughly how long a typical visit lasts.

The two donut charts and the **Platforms** table break the same DAU/MAU/session figures down by
platform; clicking a platform name narrows the whole dashboard to iOS or Android. **Top screens**
ranks views by the SDK's `screen_view` events alongside the mean dwell time per visit. The
**Retention** block repeats the headline D1/D7/D30 numbers — the per-cohort detail lives on the
Retention page (see [§5.7](#reading-retention)). The **Analytics submissions** table at the
bottom is a different thing entirely: it lists the raw analytics report files kept on disk (the
legacy `kind = "analytics"` store), not the aggregated pipeline described above.

## 5.7 Reading retention

Retention measures whether new users come back. We group every user by the day their first event
arrived — that group is a **cohort** — then ask how many of them returned later.

- **D1** — of everyone whose first event fell on a given install day, the fraction who came back
  with at least one event the *next* day.
- **D7** and **D30** — the same question, 7 and 30 days after install.

The **weighted summary** at the top averages each window across every cohort old enough to have been
observed: a cohort that installed yesterday is left out of D7 because it physically cannot have
reached day 7 yet. The *cohorts used* figure beneath each number tells you how many install-day
groups went into that average — a small number means the percentage rests on little data. A cohort
where nobody came back is still shown: zero retention is real data (those users churned), not a gap.

One subtlety worth knowing: the **hash version** records which pepper key was active when a cohort
was computed. Anonymous IDs are stored as peppered hashes, so rotating the pepper changes every
hash — cohorts computed before and after a rotation aren't directly comparable until the history is
rebuilt.

## 5.8 Reading funnels

A funnel tracks how many sessions make it through an ordered sequence of steps — for example *view
product → add to cart → purchase* — and shows where people fall away.

- Each step's count is the number of distinct sessions that completed **that step and every step
  before it** within the window.
- Steps must happen **in order**: a session has to emit step 0's event before step 1's, and so on
  (matched by event name and type). Events that arrive out of order don't advance the funnel.
- **Conversion** is measured against step 0 — the full-width bar — so you can see how much of the
  starting audience survives to each step.
- **vs previous** is the step-over-step rate, and it's usually the most useful column: it points at
  the *single* step where people are leaving.

Use the platform filter to compare Android against iOS — a step that converts far worse on one
platform often signals a platform-specific UX problem or bug. Funnel definitions come from the
`Analytics:SeedFunnels` configuration and are seeded once on first start; any edits an operator
makes afterwards survive restarts, because the seeder only inserts and never overwrites.
