# report-service

A security-hardened ASP.NET Core (.NET 8) service that ingests two kinds of telemetry from the
Android and iOS **IA SDKs** and gives operators a console to make sense of it:

1. **Report a Problem** submissions — user-initiated bug reports and automatic crash captures,
   persisted as JSON (plus an optional gzip log bundle) and indexed for fast browsing.
2. **Product analytics** — a v2 event pipeline that ingests batched SDK events and folds them into
   sessions, daily rollups, retention cohorts, and funnels.

A single merged process hosts both the SDK-facing ingestion endpoints and the operator console
(Razor Pages), gated by multi-scheme auth: an `apiKey` header for ingestion, a cookie for the
admin UI.

```text
                 Report a Problem ─┐                        ┌─ /Reports, /Errors, /Forced
 IA SDK ─────────────────────────►│                        │
 (Android / iOS)                  │   report-service  ──────┤─ /Analytics, /Retention, /Funnels
                 Analytics events ─┘   (merged host)        │
                                                            └─ /Stats, /Audit, /Maintenance
```

---

## Quick start

The supported way to run the service locally is **Docker Compose**. The `.env` file at the repo
root supplies the secrets (generate it with `./scripts/setup.sh` if it is missing).

```bash
# Build and start (HOST_PORT keeps the dev instance off the prod/staging ports).
HOST_PORT=8082 docker compose up --build -d

# Tail logs / stop
docker compose logs -f
docker compose down
```

Then open **http://localhost:8082** — the admin console. In Development the login screen is
bypassed (`Admin__DevAutoSignIn=true`); the SDK ingestion endpoint is `POST /api/v2/analytics/events`
and `POST /partners/api/v2/report-problem`.

See **[Quick start](docs/guide/02-quickstart.md)** for the full walkthrough (posting a report,
sending an analytics batch, running prod + staging side by side, and exposing the service off-network
via the optional [Cloudflare Tunnel](docs/guide/02-quickstart.md#26-public-access-via-cloudflare-tunnel-optional))
and **[Development](docs/guide/09-development.md)** for tests and the dev-data seeders.

---

## Documentation

The reference docs are split into focused chapters under [`docs/guide/`](docs/guide/). The admin
console renders all of them as one page at **`/Documentation`**.

The HTTP **endpoint reference** — every ingestion route with its payload schema, status codes, and
worked examples — lives in the **API docs**: the interactive Swagger/OpenAPI UI at **`/ApiDocs`**
(`/docs/`), generated from the routes themselves so it never drifts from the running service.

| Chapter | What's inside |
|---|---|
| [1. Overview](docs/guide/01-overview.md) | The two ingestion flows, what the SDKs send, the high-level data path. |
| [2. Quick start](docs/guide/02-quickstart.md) | Docker Compose, posting reports and analytics, prod/staging stacks. |
| [3. Configuration](docs/guide/03-configuration.md) | Every `ReportService`, `Analytics`, and `Admin` option plus env-var overrides. |
| [4. Architecture](docs/guide/04-architecture.md) | The merged host, the three projects, request and data flow, layout. |
| [5. Analytics pipeline](docs/guide/05-analytics.md) | Ingestion → aggregation → cohort → funnel workers, rollups, exports. |
| [6. Storage & privacy](docs/guide/06-storage-and-privacy.md) | On-disk layout, the SQLite indexes, PII handling, retention. |
| [7. Security model](docs/guide/07-security.md) | Auth, rate limiting, DoS caps, abuse tracking, hardening controls. |
| [8. Admin console](docs/guide/08-admin-console.md) | Every operator page, the NDJSON exports, and the dev-data seeders. |
| [9. Development & operations](docs/guide/09-development.md) | Build, test, deploy, generated API docs, project conventions. |

---

## At a glance

- **Two ingestion surfaces, one process** — `/partners/api/v2/report-problem` + `/api/v1/reports`
  for problem reports, `/api/v2/analytics/events` for analytics batches, all behind the same
  `apiKey`, rate limiter, and concurrency cap.
- **Crash bucketing** — `kind = "crash"` submissions get their top stack frame extracted at
  ingest; the **Errors** page groups occurrences by fault site.
- **Analytics workers** — aggregation, retention/cohort, and funnel background workers turn raw
  events into the dashboards under `/Analytics`.
- **Privacy by default** — raw emails are never indexed (only a SHA-256 digest); logs never carry
  payload contents; exception responses are RFC 7807 with a `traceId` and no internals.
- **Hardened ingestion** — constant-time key compare, per-IP + global concurrency limits,
  persisted auth-abuse banning, strict JSON parsing, path-traversal guards, atomic writes.

## Repository layout

```text
report-service/
├── docker-compose.yml        # Single merged service (ingestion + admin) on the reports volume
├── Dockerfile                # Multi-stage alpine build; ingestion + admin runtime targets
├── README.md                 # You are here
├── docs/                     # Hand-written guide (docs/guide/), contract, generated reference
├── scripts/                  # setup / run / stack / seed / generate-docs helpers
├── ops/                      # systemd unit + install/update/backup helpers
├── src/
│   ├── ReportService.Core/   # Domain models, storage, analytics store + workers, migrations
│   ├── ReportService/        # SDK-facing ingestion endpoints
│   └── ReportService.Admin/  # Merged host: admin Razor Pages + ingestion routes
└── tests/ReportService.Tests # xUnit integration tests against real in-memory SQLite
```

## License

Internal project — see repository owners for usage terms.
