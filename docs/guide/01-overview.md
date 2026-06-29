# 1. Overview

`report-service` is the backend the Android and iOS **IA SDKs** talk to. It accepts two distinct
kinds of telemetry over one hardened HTTP surface and gives operators a console to triage them.

## 1.1 Report a Problem

Users submit bug reports from inside the host apps via the IA SDK "Report a Problem" feature:

- **Android** — `ReportAProblemUseCase` / `ReportProblemService`.
- **iOS** — `ReportProblemMapper` / `CardlinkReportProblemModel`.

Both SDKs POST `multipart/form-data` to `/partners/api/v2/report-problem` with a required `json`
part and an optional `file` part (a gzip-compressed log bundle). Partner integrations that can't
emit multipart can POST the JSON document alone to `/api/v1/reports`.

The service validates the payload, persists the JSON document (and the optional attachment) under
`<ReportsRoot>/<platform>/problem-reports/`, and — when the SQLite index is enabled — records a
metadata row so the console can list and filter reports without re-reading every file.

```text
<ReportsRoot>/
  android/problem-reports/
    problem-report_<yyyyMMdd-HHmmss>_<sha12>.json
    problem-report_<yyyyMMdd-HHmmss>_<sha12>_<attachSha12>.log.gz   # optional
  ios/problem-reports/
    problem-report_...
```

### Automatic crash reporting & forced captures

Two SDK-side flows ride on top of user-initiated reports:

1. **Automatic crash reporting** — the Android SDK installs an uncaught-exception handler at init
   (gated on `IaSdkConfiguration.analyticsEnabled`); on the next launch it drains the encrypted
   crash queue and POSTs each one with `kind = "crash"`. iOS uses MetricKit
   (`MXMetricManagerSubscriber`), which delivers crash diagnostics on the next launch (often
   delayed up to ~24h).
2. **Forced captures** — when no crash fires (a UI bug, or an out-of-band user complaint) an
   operator adds the affected client identifier on the **Forced** admin page. The next time that
   client calls `GET /api/v1/forced-reports/{id}` it sees `forced=true` and submits a report with
   the current logs, no user action required.

When an incoming submission has `kind = "crash"` and a gzip attachment, the ingestion path
decompresses it in-process and extracts the first stack frame as `top_frame`. The **Errors** page
groups occurrences by that frame, so an operator sees one row per fault site instead of one row per
crash.

## 1.2 Product analytics

The SDKs also batch product-analytics events and POST them to `/api/v2/analytics/events` as an
`RSCAnalyticsBatch`. The service validates each batch, stores accepted events (idempotent on
`platform + eventId`), and dead-letters rejected ones with a documented reason. Background workers
then fold raw events into sessions, daily rollups, retention cohorts, and funnel step observations,
which surface on the `/Analytics*` console pages.

See [Analytics pipeline](05-analytics.md) for the full data path.

## 1.3 High-level data flow

```text
IA SDK (Android / iOS)
   │   POST /partners/api/v2/report-problem   (apiKey, multipart)   ── problem reports
   │   POST /api/v1/reports                    (apiKey, JSON)        ──┘
   │   POST /api/v2/analytics/events           (apiKey, JSON batch)  ── analytics events
   ▼
report-service  (single merged host)
   ├─ ingestion: validate → persist → index
   ├─ analytics workers: aggregate → cohorts → funnels
   └─ admin console (cookie auth): browse reports, errors, analytics, audit, maintenance
```

Everything below assumes the merged deployment the bundled `docker-compose.yml` runs: one process,
one port, the SDK API behind an `apiKey` and the console behind an operator cookie. The standalone
`ReportService` ingestion project still exists for the test fixtures and as a fallback deployment
target, but the compose stack runs only the merged admin host.
