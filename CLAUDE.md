# report-service — Claude Code instructions

## Running the service

**Always use Docker Compose. Never use `dotnet run` directly.**

```bash
# First time / after code changes — use HOST_PORT=8082 (8080=prod, 18080=staging)
HOST_PORT=8082 docker compose up --build -d

# Already built, just restart
HOST_PORT=8082 docker compose up -d

# Tail logs
docker compose logs -f

# Stop
docker compose down
```

The service listens on **http://localhost:8082** (dev instance). The admin UI opens at `/` — no login prompt (DevAutoSignIn is on for the compose stack). The SDK ingestion endpoint is `/api/v2/analytics/events`.

Port assignments on this machine:
- 8080 → prod (`reports-prod` compose project)
- 18080 → staging (`reports-staging` compose project)
- **8082 → this dev instance** (`HOST_PORT=8082 docker compose up -d`)

Always pass `HOST_PORT=8082` when starting so the dev container doesn't clash with the existing prod/staging containers.

```bash
HOST_PORT=8082 docker compose up --build -d
```

The `.env` file at the repo root supplies all secrets (API key, admin key, paths). It must exist before starting. It is already present and gitignored.

## Dev data

The analytics seeder (`RSAAnalyticsDevDataSeeder`) runs automatically at startup when `ASPNETCORE_ENVIRONMENT=Development` (which the compose file sets). It inserts 30 days of synthetic events across both platforms and seeds funnel definitions. The seeder is idempotent — restarting the container does not duplicate data.

## Tests

```bash
dotnet test
```

All tests run against a real in-memory SQLite store; no mocks, no Docker needed.

## Build (compile check only)

```bash
dotnet build
```

## Project layout

| Path | Purpose |
|---|---|
| `src/ReportService.Core/` | Domain models, analytics store interface + SQLite impl, workers, migrations |
| `src/ReportService/` | SDK-facing ingestion endpoints only |
| `src/ReportService.Admin/` | Merged host: Razor Pages admin UI + ingestion routes wired together |
| `tests/ReportService.Tests/` | Integration tests — uses `TestHost` with real SQLite |
| `docs/analytics-contract/` | Shared event catalog, contract fixtures, SDK follow-up punch list |

## Analytics pipeline

- **Database-per-app**: each `(client, app)` owns its own SQLite database at `{ReportsRoot}/apps/{client}/{app}/analytics.db`, provisioned + migrated once on first use by `RSCSqliteAnalyticsStoreFactory` (a cached `(client,app)→store` factory; `RSCIAnalyticsStoreFactory`). The `RSCM00x` migration ladder is unchanged — it just runs per app DB; the `app_id`/`client_id` columns still exist (harmless) but the **file** is the isolation boundary. Control-plane DBs (`catalog.db`, `api-keys.db`, `audit.db`, `deeplinks.db`) stay global.
- **Ingestion**: `POST /api/v2/analytics/events` → `RSAnalyticsIngestionService` → `factory.Get(client, app).WriteBatchAsync` (routes to that app's DB).
- **Workers** (aggregation 5 s/30 s, cohort 15 s/1 h, funnel 15 s/10 min, retention-purge hourly): each tick **fans out over every registered app** (`catalog.ListAllAppsAsync`), running the per-store body against `factory.Get(app)` with a per-app try/catch so one bad DB can't starve the rest. The per-store logic is an `internal static TickStoreAsync(store, options, logger, ct)` (tests drive it directly).
- **Dashboard reads** go through `RSCFanOutAnalyticsStore` (the DI `RSCIAnalyticsStore`): a **scoped** read (client+app selected) delegates to that one app's DB; an **unscoped/client-only** read fans out across app DBs (bounded parallelism, capped at `Analytics:Fanout:MaxAppsPerRead`=200, per-store errors logged+skipped) and merges in memory. Cross-app distinct-user counts are summed (double-count, documented); retention re-pools from raw cohort counts.
- **Admin pages**: `/Analytics`, `/AnalyticsEvents`, `/AnalyticsSessions`, `/AnalyticsRetention`, `/AnalyticsFunnels`, `/AnalyticsHealth`, `/AnalyticsSales`
- **NDJSON exports**: `GET /admin/api/analytics/events.ndjson`, `GET /admin/api/analytics/sessions.ndjson`

## Multi-tenancy (client → app · environment · platform)

The **client is the top-level tenant, identified by its API access key.** A client owns a list of **apps** (each with its own **environments**), and each app is surfaced as its own dashboard. Rows are keyed `(client_id, app_id, environment, platform)` — `app_id` is scoped *within* `client_id` (two clients may each have an app with the same slug). Every read can be scoped by any combination (a null axis = "all").

- **Catalog** (`catalog.db`, `RSCSqliteCatalog` / `RSCICatalog`): the registry of **clients** and the **apps each client owns** (`apps.client_id` + `UNIQUE(client_id, slug)`, migration `RSCMA002`). Own migration ladder `RSCMA0xx`, in-memory cache keyed by `(clientSlug, appSlug)`, self-seeds the `default` client + its `default` app/`production` env so key-less/attribution-omitting traffic still resolves. App methods (`IsValidApp`/`CreateAppAsync`/… ) are all client-scoped; `ListAllAppsAsync` is the admin cross-client view.
- **Client = the access key.** A managed API key can be **bound to a client** (`api_keys.client_id`, migration `RSCMK002`); the auth handler emits a `rsc:client_id` claim (`RSCTenantClaims.ClientId`). On ingestion the client is taken from the authenticated key — the body/header `clientId` can't override it. Only an **unbound** key (the static root key, or a legacy managed key) falls back to header → body → `default`. App + environment still resolve header → body → default and are validated against *that client's* apps. Unknown client/app/env ⇒ whole batch rejected (`client_unknown` / `app_unknown` / `environment_unknown`) when `Catalog:Enabled` (default true).
- **Admin pages** (Operations nav): `/Clients` registers a client **and mints its first access key** (shown once; `issue key` mints more) — keys are `user`-role keys bound to the client. `/Apps` is **client-scoped** CRUD (pick a client → manage its apps); admins can manage any client's apps. The analytics pages carry a **client → app → environment** selector (`_TenantScope`, `?client=&app=&env=`) — the app dropdown lists only the chosen client's apps.
- **Client self-service**: a client uses its key to manage its own apps over JSON — `GET/POST/DELETE /api/v2/apps` (`RSAppManagementEndpoints`, an unbound key gets 403). Clients also **log into the admin UI** at `/ClientLogin` (paste the key); the `RSAClientLoginScope` middleware confines that cookie session to its own per-app dashboards and pins `?client=` to its own client.
- **Persistent global scope**: the header **tenant/app switcher** (`RSATenantSwitcherViewComponent`, replaces the old env chip in `_Layout`) POSTs the chosen `client|app` to `/Scope`, which sets the `rsc_scope` cookie and 303-redirects back to the **current** page (no bounce to Analytics). A scope-fill middleware (after the client-login pin in `Program.cs`) fills any missing `?client/?app/?env` on each page request from that cookie, so the selection sticks across all pages; an explicit query value wins, and a client login's `?client=` pin always wins over the cookie.
- `clientId` (e.g. a pharmacy id) is a business/tenant key, stored **verbatim**, NOT user PII — and is no longer hashed (the legacy `client_id_hash` column is unpopulated). The per-user `anonymousId` stays hashed.
- Dev demo seeds: clients `pharmacy-42`/`pharmacy-99` each owning an app (`app-a`/`app-b`) via `Catalog:SeedClients` / `Catalog:SeedApps` (with `ClientSlug`) in `appsettings.Development.json`. Mint a demo client key from `/Clients` to exercise client login + the app API.
- **Problem reports are per-app too** (database-per-app): each `(client, app)` owns its own report tree + `reports.db` at `{ReportsRoot}/apps/{client}/{app}/…` (`RSCReportStoreFactory` builds a per-app indexing store; `RSCFanOutReportStore` is the DI `RSCIReportStore` — routes writes per-app, merges/scopes reads — registered in **both** `FileSystem` and `SqliteIndex` modes). Attribution mirrors analytics: **client from the API-key claim**, **app from the `X-Report-App` header** (or `appId`/`environment` body fields), catalog-validated (`app ∈ client`, else `400`). The `/ProblemReports` + `/Errors` admin pages filter by the global scope; their `Stats`/`Index` aggregates are a follow-up. (Legacy single-DB data import is a planned opt-in `/Maintenance` action.)

## Optional features (build flags)

The "Submissions" areas are optional, decided at **build time** via `Directory.Build.props` flags surfaced through `RSCFeatureFlags` (Core):

```bash
dotnet build -p:FeatureAnalytics=false        # compile analytics off
dotnet build -p:FeatureProblemReports=false   # compile problem reports off
```

A disabled feature is **gated, not removed**: its ingestion endpoints return **HTTP 503 Service Unavailable** with a clear message (a deliberate build configuration, not an unexpected `500` — and consistent with the runtime `Analytics:Enabled=false` kill-switch, which also returns 503), its admin pages redirect to `/FeatureUnavailable` ("Not enabled — contact your administrator"), the nav entry is muted, and its workers/seeders don't run. Defaults are ON. `RSCFeatureFlags.{Analytics,ProblemReports}` is the single runtime source of truth (with `RSCFeatureFlags.DisabledStatusCode` = 503 the one place the status is defined); the `RSAFeatureGateFilter` gates the admin pages.

## Key constraints

- SQLite analytics DB lives at `/srv/reports/analytics.db` inside the container (on the `reports` volume); the tenancy catalog lives alongside it at `/srv/reports/catalog.db`.
- The container filesystem is **read-only** — all writable state goes through the `reports` volume.
- The seeder runs before `app.Run()`, so the DB is populated before the first HTTP request.
- Funnel definitions are seeded by `RSCAnalyticsFunnelWorker` on its first tick (INSERT-only, won't overwrite operator edits).
- Cross-repo SDK changes (Android `IA-SDK-Dev-Android`, iOS `IA-SDK-Dev-iOS`) are tracked in `docs/analytics-contract/SDK-FOLLOWUPS.md` — do not edit those repos here.
