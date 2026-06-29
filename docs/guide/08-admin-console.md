# 8. Admin console

The operator console is the Razor Pages surface of the merged host. It is reachable on the same
port as the ingestion API but gated by a separate cookie (the `AdminKey`), and bound to host
loopback only in the compose stack. In Development `Admin__DevAutoSignIn=true` skips the login
screen.

## 8.1 Pages

Routes follow the page name (`Reports.cshtml` → `/Reports`); `/` is the dashboard.

| Page | Route | Purpose |
|---|---|---|
| Dashboard | `/` | Platform overview — configured platforms, report counts, headline stats. |
| Reports | `/Reports`, `/ProblemReports` | Newest-first report list with filters (platform, channel, search). |
| Report detail | `/Report/{platform}/{fileName}` | Renders the JSON body (truncated for display), downloads JSON or the `.log.gz` sibling, and deletes (confirmation dialog + antiforgery + cookie required). |
| Errors | `/Errors`, `/Error` | Crash reports grouped by extracted `top_frame` — one row per fault site with occurrence counts. |
| Forced | `/ForcedReports` | Manage the forced-capture allow-list (add/remove client ids with a note). Drives `GET /api/v1/forced-reports/{id}`. |
| Analytics | `/Analytics` | Engagement tiles, per-platform rows, top screens, daily-active trend. |
| Analytics events | `/AnalyticsEvents` | Raw event search/filter (type, name, screen, feature, platform). |
| Sessions | `/AnalyticsSessions`, `/AnalyticsSession` | Session list and per-session event timeline. |
| Retention | `/AnalyticsRetention` | D1/D7/D30 cohort curves. |
| Funnels | `/AnalyticsFunnels` | Step-by-step conversion for each defined funnel. |
| Analytics health | `/AnalyticsHealth` | Ingestion lag, dead-letter reasons, aggregation backlog. |
| Stats | `/Stats` | Aggregate report stats over time. |
| Audit | `/Audit` | Operator action log (login, delete, export, vacuum, backup, integrity, rebuild). |
| Maintenance | `/Maintenance` | DB maintenance: one-shot retention purge, vacuum, integrity check, index rebuild, backup — plus the **API keys** section (mint user/admin keys with optional expiry + per-key rate limit, and revoke them; the minted secret is shown **once**, mirroring the REST endpoints in §8.4). |
| Status | `/Status` | Component health — DB files, WAL state, sizes, schema versions, retention counters, active API-key count. |
| Documentation | `/Documentation` | This guide (README + `docs/guide/*` rendered as one page). |
| API docs | `/ApiDocs`, `/swagger` | Swagger UI / OpenAPI for the ingestion routes. |
| Login / Logout | `/Login`, `/Logout` | Cookie sign-in (constant-time `AdminKey` compare) and POST-only sign-out. |

## 8.2 Destructive operations

Delete is the main console write and flows through `RSCIReportStore.Delete`, which path-checks the
filename with `RSCSafePath`, removes the sibling `.log.gz` (best effort), deletes the JSON document
(its disappearance is the source of truth), then removes the index row (reconciled on the next list
if the index delete fails). Every delete is logged at Information with the operator identity, remote
address, and filename, and recorded in the audit log. No destructive operation runs without both an
authenticated cookie **and** an antiforgery token.

Every report-delete control — single delete on the detail page, the per-row and "delete all
matching" buttons on the listings (`/Reports`, `/ProblemReports`, `/Errors`, `/Analytics`), and the
"Wipe all data" action on `/Maintenance` — is additionally gated by a **confirmation dialog** that
names the scope (the filename, the exact match count, or the wipe warning) before the form submits.
The dialog is a shared modal driven by [`confirm-dialog.js`](../../src/ReportService.Admin/wwwroot/js/confirm-dialog.js):
any `<form data-confirm="…">` is intercepted on submit, and only an explicit click on the dialog's
accept button re-submits it; Cancel, Escape, or a backdrop click abort with no request. This is
served from an external script on purpose — the admin Content-Security-Policy is `script-src 'self'`
with no `'unsafe-inline'`, so an inline `onsubmit="confirm(…)"` attribute would be blocked by the
browser and never fire, leaving deletes ungated. The confirmation is a UX guard layered on top of
the cookie + antiforgery checks, not a replacement for them; the server still enforces both
regardless of what the client does.

## 8.3 Dev data

In Development the console is populated automatically before the first request by two seeders:

- [`RSAAnalyticsDevDataSeeder`](../../src/ReportService.Admin/Services/RSAAnalyticsDevDataSeeder.cs) —
  analytics events (sessions, retention cohorts, funnels).
- [`RSAProblemReportDevDataSeeder`](../../src/ReportService.Admin/Services/RSAProblemReportDevDataSeeder.cs) —
  problem reports + crashes (with backdated timestamps, attachments, and extracted top frames),
  a handful of forced-report entries, and audit-log rows. It runs only under `Storage=SqliteIndex`
  (the compose dev stack), so the FileSystem-backed test hosts are unaffected.

Sizing knobs (`ANALYTICS_SEED_SCALE`, `REPORTS_SEED_SCALE`) and idempotency details are in
[Development](09-development.md#dev-data-seeders).

## 8.4 API keys

Two roles: **admin** keys can manage keys (mint/list/revoke) **and** ingest; **user** keys can only
ingest. The configured `ReportService:ApiKey` is the permanent **root-admin** — always valid, never
expires, not stored in the DB — and is how you bootstrap the first managed keys. Managed keys live in
`api-keys.db`; only their SHA-256 hash is stored, so a minted secret is shown **once** and is never
retrievable afterwards. Keys can be given an expiry and a per-key rate-limit override at creation.

Rate limiting is **per key**: each key (and the root key) gets its own sliding-window budget — the
per-key override if set, else the role tier (`ApiKey{Admin,User}RateLimitPerMinute`), else
`RateLimitPermitsPerMinute`. Requests with no key fall back to a per-IP budget.

Manage keys from the **API keys** section on the `/Maintenance` page (operator cookie auth) or the
REST API (admin-key auth):

| Method | Route | Body / result |
|---|---|---|
| `POST` | `/api/v1/keys` | `{ role: 'user'\|'admin', label?, expiresAt? (ISO) \| expiresInDays?, rateLimitPerMinute? }` → `201` with the one-time `key`. |
| `GET` | `/api/v1/keys` | Metadata for every key (no hashes/plaintext). |
| `DELETE` | `/api/v1/keys/{id}` | Revoke — subsequent auth with it returns `401`. `404` if no active key. |

```bash
# Mint a 30-day user key with the root key, then ingest with it:
curl -sX POST localhost:8082/api/v1/keys -H "apiKey: $ROOT" -H 'Content-Type: application/json' \
     -d '{"role":"user","label":"acme","expiresInDays":30}'
```

A user key calling the management routes gets `403`; an unknown/expired/revoked key gets `401`
(and repeated failures trip the per-IP auth-abuse ban). Every mint/revoke writes an audit row
(`apikey.create` / `apikey.revoke`).

## 8.5 NDJSON exports

Two operator export endpoints stream analytics data as [NDJSON](https://github.com/ndjson/ndjson-spec)
(`Content-Type: application/x-ndjson; charset=utf-8`, one JSON object per `\n`-terminated line). They
use **cookie auth** — the same authenticated-operator policy as the Razor pages, **not** the `apiKey`
header — so they are operator tooling rather than part of the ingestion OpenAPI spec. Both send
`Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`, and stream so a `jq` or CSV
pipeline can consume them without buffering the whole window.

| Route | Query params |
|---|---|
| `GET /admin/api/analytics/events.ndjson` | `platform`, `type`, `name`, `screen`, `session`, `from`, `until`, `limit` (same filter as `/AnalyticsEvents`). |
| `GET /admin/api/analytics/sessions.ndjson` | `platform`, `limit`. |

Each row is capped at `limit` (default `5000`, max `50000`); page through a large window with `from` /
`until`. Identifiers are emitted **only** as `anonymousIdHash` (peppered hash) — the raw
`anonymousId`/`subjectId` is never stored and never exported. Timestamps are ISO-8601 UTC.

`events.ndjson` — each line:

```json
{"eventId":"3f1a8b2c-9d0e-4a5b-8c6d-1e2f3a4b5c6d","platform":"android","sessionId":"s-android-abc","anonymousIdHash":"a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2","sequence":7,"occurredAt":"2026-06-25T10:14:58.0000000Z","type":"action","name":"add_to_cart","screen":"ProductDetail","feature":"otc","durationMs":1240}
```

`sessions.ndjson` — each line:

```json
{"platform":"android","sessionId":"s-android-abc","anonymousIdHash":"a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2","startedAt":"2026-06-25T10:12:03.0000000Z","lastSeenAt":"2026-06-25T10:15:41.0000000Z","eventCount":23,"screenCount":6}
```

**Failure semantics differ from the JSON routes.** If the query fails *before* the first byte is
flushed, the response is a clean `500` `application/problem+json`. If it fails *mid-stream* — after the
`200` and attachment headers are already on the wire — the status can no longer change, so the server
**aborts the connection** rather than silently truncating. A broken transfer is the signal; do not
treat a partial file as complete.
