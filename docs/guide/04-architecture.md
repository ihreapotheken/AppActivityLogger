# 4. Architecture

## 4.1 Three projects, one running process

| Project | Role |
|---|---|
| [`ReportService.Core`](../../src/ReportService.Core/) | Shared class library: domain models, validation, storage (file system + SQLite index), the analytics store and background workers, schema migrations, security primitives, observability. Referenced by both apps. |
| [`ReportService`](../../src/ReportService/) | The SDK-facing ingestion web API — endpoint maps, the ingestion services, and the `apiKey` auth handler. Runs standalone for tests / as a fallback target. |
| [`ReportService.Admin`](../../src/ReportService.Admin/) | The **merged host** the compose stack runs: it mounts the ingestion routes *and* the Razor Pages operator console in one process, wired with multi-scheme auth. |

The previous two-container split (public ingestion + loopback admin) is gone. Today a single
process binds one port; cookie auth + auto-sign-in gate the Razor pages (`/`, `/Reports`,
`/Analytics`, …), `apiKey` gates the ingestion routes (`/partners/api/v2/report-problem`,
`/api/v1/reports`, `/api/v2/analytics/events`, …), and the network binding (host loopback only)
bounds exposure.

## 4.2 Directory layout

```text
src/
├── ReportService.Core/             # Shared library
│   ├── Models/                     # RSCProblemReport, RSCAnalytics{Batch,Event,…}
│   ├── Options/                    # RSCReportServiceOptions, RSCAnalyticsOptions
│   ├── Validation/                 # RSCReportValidator (+ analytics validator)
│   ├── Security/                   # RSCSafePath, auth-abuse tracker, secret validation
│   ├── Storage/
│   │   ├── RSCIReportStore.cs / RSCFileSystemReportStore.cs
│   │   ├── RSCSqliteIndexingReportStore.cs   # decorator: file store + index
│   │   ├── RSCSqliteReportIndex.cs           # report metadata index (WAL)
│   │   ├── RSCSqliteForcedReportStore.cs     # forced-report allow-list
│   │   ├── Migrations/{Reports,Analytics}/   # versioned SQLite schema migrations
│   │   └── Retention/                        # report retention sweep
│   ├── Analytics/                  # store + RSCAnalytics{Aggregation,Cohort,Funnel,Retention}Worker
│   ├── Audit/                      # RSCSqliteAuditLog + migrations
│   └── Observability/              # crash handler, correlation-id middleware, telemetry
│
├── ReportService/                  # Ingestion web API
│   ├── Endpoints/                  # RSReportEndpoints, RSAnalyticsEndpoints
│   ├── Ingestion/                  # RSReportIngestionService, RSAnalyticsIngestionService
│   └── Security/                   # apiKey auth handler, Accept-header filter
│
└── ReportService.Admin/            # Merged host
    ├── Program.cs                  # Composition root: DI, auth, rate limit, routes, dev seeders
    ├── appsettings*.json
    ├── Pages/                      # Razor Pages console (see chapter 9)
    ├── Services/                   # dashboard/stats/docs services + dev-data seeders
    ├── ViewModels/
    └── wwwroot/css|js              # tokens.css, layout.css, components.css
```

## 4.3 Request flow (ingestion)

```text
IA SDK ──POST (apiKey, multipart/JSON)──▶ Kestrel (MaxRequestBodySize, AddServerHeader=false)
   │
   ▼
Exception handler → Security headers → Correlation-id → RateLimiter (per-IP)
   → Authentication (apiKey) → Authorization → RateLimiter (ingest-concurrency)
   │
   ▼
Endpoint (RSReportEndpoints / RSAnalyticsEndpoints, antiforgery disabled for SDK clients)
   │
   ▼
Ingestion service: ReadForm/Deserialize → validate → store
   │
   ▼
RSCFileSystemReportStore  (atomic .tmp → File.Move)  ──▶ (optional) SQLite index upsert
   │                                                       (analytics: WriteBatchAsync → analytics_events)
   ▼
201 Created (RSCStoredReport)   /   202 Accepted (RSCAnalyticsBatchReceipt)
```

## 4.4 Class responsibilities

- **`RSCReportServiceOptions` / `RSCAnalyticsOptions`** — strongly-typed config.
- **`RSApiKeyAuthenticationHandler`** — constant-time `apiKey` compare.
- **`RSCSafePath`** — turn an untrusted filename into a safe absolute path or refuse.
- **`RSCReportValidator`** — field bounds, platform allow-list, attachment size + gzip magic.
- **`RSReportIngestionService` / `RSAnalyticsIngestionService`** — orchestrate parse → validate →
  store for one inbound submission/batch.
- **`RSCIReportStore` + decorators** — persistence seam; the file system is the source of truth and
  SQLite is an accelerator index that can be rebuilt from disk.
- **`RSCIAnalyticsStore`** — the analytics DB seam (ingest, aggregation upserts, dashboard queries).
- **Analytics workers** — background services that fold raw events into rollups (chapter 6).
- **Razor Page models** — HTTP surface for the console; they call dashboard/stats services, never
  storage directly.
