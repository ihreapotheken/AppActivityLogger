# 2. Quick start

## 2.1 Prerequisites

- Docker + Docker Compose (the supported local runtime).
- A `.env` file at the repo root with the secrets. It is gitignored; generate one with:

  ```bash
  ./scripts/setup.sh          # writes .env with random keys (won't clobber an existing one)
  ```

The compose file declares `env_file` as required, so it refuses to start without `.env` rather
than booting with no secrets.

## 2.2 Run it

```bash
# Build + start. HOST_PORT keeps this instance off the prod (8080) / staging (18080) ports.
HOST_PORT=8082 docker compose up --build -d

# Already built — just (re)start
HOST_PORT=8082 docker compose up -d

docker compose logs -f          # tail
docker compose down             # stop (keep data)
docker compose down -v          # stop + wipe the reports volume
```

The container binds host loopback only (`127.0.0.1:${HOST_PORT}:8080`), so SDK clients on the host
(Android emulator via `10.0.2.2`, iOS simulator via `localhost`) reach it but other machines can't.

Open **http://localhost:8082**. In Development the admin login is bypassed
(`Admin__DevAutoSignIn=true` in the compose env), so the console opens straight to the dashboard.

Liveness / readiness (anonymous):

```bash
curl http://localhost:8082/api/health
curl http://localhost:8082/api/health/ready
```

> **Dev data.** In Development the service auto-seeds synthetic data before the first request:
> analytics events ([`RSAAnalyticsDevDataSeeder`](../../src/ReportService.Admin/Services/RSAAnalyticsDevDataSeeder.cs))
> and problem reports / crashes / forced-report entries / audit rows
> ([`RSAProblemReportDevDataSeeder`](../../src/ReportService.Admin/Services/RSAProblemReportDevDataSeeder.cs)).
> Volume sizes are tunable with `ANALYTICS_SEED_SCALE` and `REPORTS_SEED_SCALE` — see
> [Development](09-development.md#dev-data-seeders).

## 2.3 Post a problem report

```bash
API_KEY=$(grep '^ReportService__ApiKey=' .env | cut -d= -f2-)

# Multipart (SDK path) with a gzip log bundle:
curl -X POST http://localhost:8082/partners/api/v2/report-problem \
  -H "apiKey: $API_KEY" \
  -F "json=@report.json;type=application/json" \
  -F "file=@logs.log.gz;type=application/gzip"

# JSON-only (partner path):
curl -X POST http://localhost:8082/api/v1/reports \
  -H "apiKey: $API_KEY" -H "Content-Type: application/json" \
  --data @report.json
```

On success the service replies `201 Created` with a `Location:` header pointing at
`/api/problem-reports/{platform}/{fileName}` and an `RSCStoredReport` body. See the **API docs**
(`/ApiDocs` — Swagger UI) for the `report.json` shape, the full field table, and status codes.

To seed a batch of reports against a running instance (instead of the in-process dev seeder), use
[`scripts/seed-mock-reports.sh`](../../scripts/seed-mock-reports.sh):

```bash
COUNT=200 DAYS=60 INGEST=http://127.0.0.1:8082 scripts/seed-mock-reports.sh
```

## 2.4 Send an analytics batch

```bash
curl -X POST http://localhost:8082/api/v2/analytics/events \
  -H "apiKey: $API_KEY" -H "Content-Type: application/json" \
  --data @batch.json
```

`batch.json` is an `RSCAnalyticsBatch`; the service replies `202 Accepted` with an
`RSCAnalyticsBatchReceipt`. See the **API docs** (`/ApiDocs` — Swagger UI) for the batch and receipt
shapes, and [Analytics pipeline](05-analytics.md) for the validation rules.

## 2.5 Production + staging side by side

Two named stacks share the compose file but get isolated host ports, isolated volumes, and a
distinct `apiKey` / `Environment` label. [`scripts/stack.sh`](../../scripts/stack.sh) wraps the
project-name + env-file dance:

| Stack | Env file | Host port | Project name | Volume |
|---|---|---|---|---|
| production | `.env` | `8080` | `reports-prod` | `reports-prod_reports` |
| staging | `.env.staging` | `18080` | `reports-staging` | `reports-staging_reports` |

```bash
./scripts/stack.sh production rebuild        # build + start prod
./scripts/stack.sh staging   rebuild         # start staging alongside it
./scripts/stack.sh staging   logs
./scripts/stack.sh staging   down --volumes  # wipes the staging volume only
```

Promote a release by rebuilding **staging** first, smoke-testing, then rebuilding **production** —
neither `down -v` ever touches the other's data. Each stack reports its environment label on
`/api/health` and in a coloured badge in the admin UI, so it's obvious which one a request hits.

## 2.6 Public access via Cloudflare Tunnel (optional)

For demos or mobile-SDK testing from off-network devices — anywhere the loopback bind isn't enough
— the compose file ships a `cloudflared` sibling service behind a `tunnel` profile. It only starts
when explicitly requested, so the default `up` keeps the host-loopback posture intact.

```bash
./scripts/stack.sh production up      # ingestion API on 127.0.0.1:8080 (unchanged)
./scripts/tunnel.sh up                # adds cloudflared and prints the public URL
./scripts/tunnel.sh logs              # follow the cloudflared container output
./scripts/tunnel.sh url               # (quick-tunnel mode) reprint the last parsed URL
./scripts/tunnel.sh down              # remove the tunnel, leave report-service running
```

The sibling container reaches `report-service` over the compose network at
`http://report-service:8080` — no host-port hop, no NAT. Two modes:

- **Quick tunnel (default)** — an ephemeral `*.trycloudflare.com` URL, no Cloudflare account
  required; the hostname changes on every restart. Good for short-lived demos.
- **Named tunnel** — a stable hostname on your own Cloudflare-managed domain. Set
  `CLOUDFLARED_COMMAND=tunnel --no-autoupdate run --token <token>` in `.env` (see
  [`.env.example`](../../.env.example) for the `cloudflared tunnel login` / `create` / `token`
  sequence), then `./scripts/tunnel.sh up`. Survives restarts and host reboots.

Equivalently with raw compose: `docker compose --env-file .env --profile tunnel up -d`.
