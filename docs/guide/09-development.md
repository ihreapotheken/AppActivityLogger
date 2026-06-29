# 9. Development & operations

## 9.1 Build & test

```bash
dotnet build                     # compile all three projects
dotnet test                      # run the xUnit suite
```

The test project ([`tests/ReportService.Tests/`](../../tests/ReportService.Tests/)) boots the real
hosts via `WebApplicationFactory` against **real in-memory SQLite** with an isolated temp
`ReportsRoot` per test — no mocking framework, no Docker. It exercises the security posture
end-to-end (auth, path traversal, rate/concurrency limits, secret validation, abuse tracking) plus
the analytics store, validator, aggregation replay, funnel/cohort math, and the admin UI.

> Test hosts run in the Development environment but with `Storage=FileSystem` and
> `Admin:DevAutoSignIn` unset, so they see the real auth flow and the index-gated problem-report
> seeder is a no-op there. Keep it that way: don't put `Admin:DevAutoSignIn=true` in committed
> Development config, and keep dev-seeder code defaults small.

## 9.2 Dev-data seeders

Two seeders run before `app.Run()` when `ASPNETCORE_ENVIRONMENT=Development`:

| Seeder | Populates | Scale knob (env) | Code default | Docker dev (`.env`) |
|---|---|---|---|---|
| `RSAAnalyticsDevDataSeeder` | analytics events, sessions, cohorts, funnels | `ANALYTICS_SEED_SCALE` | `1` | `10` |
| `RSAProblemReportDevDataSeeder` | problem reports, crashes, forced entries, audit rows | `REPORTS_SEED_SCALE` | `1` | (set as desired) |

Both are **idempotent**: the analytics seeder uses deterministic IDs (`INSERT OR IGNORE`), and the
report seeder skips when the index already lists reports. The code defaults are intentionally small
so the test host (which also runs in Development) stays fast; the Docker stack opts into a large
dataset via `.env`.

Sizing notes:

- `ANALYTICS_SEED_SCALE=10` submits ~520k events. With the dev `RawEventRetentionDays=365` the full
  history (~110 days) stays visible; under the prod default of 30 days the retention sweep would
  trim everything older than 30 days. Seed onto a **fresh** volume (`docker compose down -v`) so the
  one-time backlog aggregates exactly once without double-counting.
- The report seeder writes back-dated JSON files (and gzip attachments for crashes) directly,
  mirroring the store's `problem-report_<ts>_<sha12>[_<attachSha12>]` naming, and upserts the index
  row itself — because `RSCFileSystemReportStore.SaveAsync` always stamps `SubmittedAt = UtcNow` and
  can't produce a back-dated spread.

To seed against an already-running instance instead, use
[`scripts/seed-mock-reports.sh`](../../scripts/seed-mock-reports.sh) (`COUNT`, `DAYS`, `INGEST`,
`API_KEY`).

## 9.3 Extending the service

**Accept a new platform** — add the lowercase identifier to `AllowedPlatforms`
(`ReportService__AllowedPlatforms__N`). No code change: the store creates
`reports/<name>/problem-reports/` on startup and the read endpoints accept it.

**Plug a different report store** — implement
[`RSCIReportStore`](../../src/ReportService.Core/Storage/RSCIReportStore.cs) (SaveAsync / List /
OpenRead / Delete) and swap the DI registration in `Program.cs`. Validation, auth, rate limiting,
and the endpoints depend only on the interface.

**Add an `RSCProblemReport` field** — add the property to
[`RSCProblemReport`](../../src/ReportService.Core/Models/RSCProblemReport.cs) and any cap in
`RSCReportValidator`. To make it queryable, extend `RSCReportMetadata`, add a migration under
`Storage/Migrations/Reports/`, and map it in `RSCSqliteIndexingReportStore`. On-disk JSON is stored
verbatim, so existing files stay readable.

**Evolve a SQLite schema** — add a versioned migration under
[`Storage/Migrations/`](../../src/ReportService.Core/Storage/Migrations/); the runner applies
anything with a version above the DB's `PRAGMA user_version` on startup, so it rolls forward on
existing volumes automatically.

## 9.4 Deployment

- **Docker (recommended)** — [`docker-compose.yml`](../../docker-compose.yml) runs the merged host
  on the `reports` volume with a read-only root FS. First-run requires `.env`
  (`./scripts/setup.sh`); the compose file refuses to boot without it. Run prod and staging
  side-by-side with [`scripts/stack.sh`](../../scripts/stack.sh) (see
  [Quick start](02-quickstart.md#25-production--staging-side-by-side)).
- **systemd** — [`ops/`](../../ops/) has unit files and install/update/backup helpers for a
  host-installed deployment.

Production refuses to boot with a missing/weak/placeholder secret — see
[Configuration §3.4](03-configuration.md#34-production-secret-guard).

## 9.5 Generated API reference

[`scripts/generate-docs.sh`](../../scripts/generate-docs.sh) produces, from the compiled binaries:

- `docs/openapi/ingestion-v1.{json,yaml}` — OpenAPI for the ingestion routes (Swagger CLI).
- `docs/_site/` — a full HTML reference site (DocFX) from the projects' XML doc comments.
- `docs/api/` — DocFX's intermediate metadata.

Tools are restored from [`.config/dotnet-tools.json`](../../.config/dotnet-tools.json) on first run,
so a fresh clone only needs the .NET 8 SDK. The generated outputs are gitignored — the code is the
canonical source.
