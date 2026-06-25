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

- **Ingestion**: `POST /api/v2/analytics/events` → `RSAnalyticsIngestionService` → `RSCSqliteAnalyticsStore.WriteBatchAsync`
- **Aggregation worker**: runs every 5 s in dev (30 s prod), atomically upserts sessions + daily rollups, marks events aggregated
- **Cohort worker**: runs every 15 s in dev (1 h prod), computes D1/D7/D30 retention from `analytics_user_days`
- **Funnel worker**: runs every 15 s in dev (10 min prod), walks unaggregated events and records step observations per defined funnel
- **Admin pages**: `/Analytics`, `/AnalyticsEvents`, `/AnalyticsSessions`, `/AnalyticsRetention`, `/AnalyticsFunnels`, `/AnalyticsHealth`
- **NDJSON exports**: `GET /admin/api/analytics/events.ndjson`, `GET /admin/api/analytics/sessions.ndjson`

## Key constraints

- SQLite analytics DB lives at `/srv/reports/analytics.db` inside the container (on the `reports` volume).
- The container filesystem is **read-only** — all writable state goes through the `reports` volume.
- The seeder runs before `app.Run()`, so the DB is populated before the first HTTP request.
- Funnel definitions are seeded by `RSCAnalyticsFunnelWorker` on its first tick (INSERT-only, won't overwrite operator edits).
- Cross-repo SDK changes (Android `IA-SDK-Dev-Android`, iOS `IA-SDK-Dev-iOS`) are tracked in `docs/analytics-contract/SDK-FOLLOWUPS.md` — do not edit those repos here.
