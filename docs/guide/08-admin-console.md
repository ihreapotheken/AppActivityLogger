# 8. Admin console

The operator console is the Razor Pages surface of the merged host. It is reachable on the same
port as the ingestion API but gated by a separate cookie (the `AdminKey`), and bound to host
loopback only in the compose stack. In Development `Admin__DevAutoSignIn=true` skips the login
screen.

## 8.1 Pages

Routes follow the page name (`ProblemReports.cshtml` → `/ProblemReports`); `/` is the dashboard.

| Page | Route | Purpose |
|---|---|---|
| Dashboard | `/` | Platform overview — configured platforms, report counts, headline stats. |
| Problem reports | `/ProblemReports` | Newest-first list of user-submitted (non-crash) reports with filters (platform, pharmacy, search). |
| Report detail | `/Report/{platform}/{fileName}` | Renders the JSON body (truncated for display), downloads JSON or the `.log.gz` sibling, and deletes (confirmation dialog + antiforgery + cookie required). |
| Errors | `/Errors`, `/Error` | Crash reports grouped by extracted `top_frame` — one row per fault site with occurrence counts. |
| Forced | `/ForcedReports` | Manage the forced-capture allow-list (add/remove client ids with a note). Drives `GET /api/v1/forced-reports/{id}`. |
| Deep links | `/DeepLinks` | Manage deferred deep-link definitions (page pattern → redirect address, enable/disable) and watch recorded clicks. Drives `/dl/{slug}` and the `/api/v2/deeplinks/*` routes — see §8.6. |
| Analytics | `/Analytics` | Engagement tiles, per-platform rows, top screens, daily-active trend. |
| Analytics events | `/AnalyticsEvents` | Raw event search/filter (type, name, screen, feature, platform). |
| Sessions | `/AnalyticsSessions`, `/AnalyticsSession` | Session list and per-session event timeline. |
| Retention | `/AnalyticsRetention` | D1/D7/D30 cohort curves. |
| Funnels | `/AnalyticsFunnels` | Step-by-step conversion for each defined funnel. |
| Analytics health | `/AnalyticsHealth` | Ingestion lag, dead-letter reasons, aggregation backlog. |
| Stats | `/Stats` | Aggregate report stats over time. |
| Audit | `/Audit` | Operator action log (login, delete, export, vacuum, backup, integrity, rebuild). |
| Clients & apps | `/Clients` | Tenancy administration (admin-only): register clients and the apps each owns, mint/rotate access keys, archive/restore or delete tenants, and manage operator API keys. See §8.7. |
| Maintenance | `/Maintenance` | DB maintenance: one-shot retention purge, vacuum, integrity check, index rebuild, backup. |
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
matching" buttons on the listings (`/ProblemReports`, `/Errors`, `/Analytics`), and the
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

Exactly two roles, and role fixes the binding: **admin** keys are **unbound** — they read + write
across **all** clients and can manage keys (mint/list/revoke); **client** keys are **bound** to one
client (`clientId`) and ingest + read only that client's own data (they can't manage keys or reach
another tenant). There is no unbound non-admin key. The configured `ReportService:ApiKey` is the
permanent **root-admin** — always valid, never expires, not stored in the DB — and is how you
bootstrap the first managed keys. Managed keys live in `api-keys.db`; only their SHA-256 hash is
stored, so a minted secret is shown **once** and is never retrievable afterwards. Keys can be given an
expiry and a per-key rate-limit override at creation.

Rate limiting is **per key**: each key (and the root key) gets its own sliding-window budget — the
per-key override if set, else the role tier (`ApiKey{Admin,User}RateLimitPerMinute` — the `User`
option name is legacy and now sets the client tier), else `RateLimitPermitsPerMinute`. Requests with
no key fall back to a per-IP budget.

Mint **admin** keys from the **API keys** section on the `/Clients` page (operator cookie auth); a
client's own bound key is minted from that client's **issue key** button in the Clients section. Or
use the REST API (admin-key auth):

| Method | Route | Body / result |
|---|---|---|
| `POST` | `/api/v1/keys` | `{ role: 'admin'\|'client' (default 'client'), clientId? (required for 'client', forbidden for 'admin'), label?, expiresAt? (ISO) \| expiresInDays?, rateLimitPerMinute? }` → `201` with the one-time `key`. |
| `GET` | `/api/v1/keys` | Metadata for every key (no hashes/plaintext). |
| `DELETE` | `/api/v1/keys/{id}` | Revoke — subsequent auth with it returns `401`. `404` if no active key. |

```bash
# Mint a 30-day client key (bound to client 'acme') with the root key, then ingest with it:
curl -sX POST localhost:8082/api/v1/keys -H "apiKey: $ROOT" -H 'Content-Type: application/json' \
     -d '{"role":"client","clientId":"acme","label":"acme","expiresInDays":30}'
```

A client key calling the management routes gets `403`; an unknown/expired/revoked key gets `401`
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

## 8.6 Deferred deep linking

Deferred deep linking carries a visitor from a web page they saw *before* installing the app to the
right in-app destination *after* they install. The `/DeepLinks` page defines the links; three routes
drive the runtime, and recorded clicks (IP, page, matched link, claim time) are listed back on the page.

A **link definition** is a stable `slug`, a **page pattern**, and a **redirect address** — an absolute
URL, either an https universal link or a custom scheme such as `myapp://promo/spring` — plus an enabled
flag. Definitions and recorded clicks live in their own `deeplinks.db` (anchored under `ReportsRoot`),
so the feature works regardless of the report `Storage` mode and under a read-only content root.

{.deeplink-routes}
| Method | Route | Auth | Purpose |
|---|---|---|---|
| `GET` | `/dl/{slug}` | anonymous | Hosted **smart link** — the single URL you hand to visitors (ad, SMS, email). Opening it records the visitor's IP (plus the referring page and user-agent), then `302`-redirects to the link's redirect address. `404` for an unknown or disabled slug. Anonymous because a browser cannot carry the `apiKey`; still rate-limited per IP by the global limiter. |
| `POST` | `/api/v2/deeplinks/clicks` | apiKey | Record a visit from your own backend instead of the hosted link: `{ pageUrl, ip?, params?, signals? }`. The IP defaults to the connection address (resolved from `X-Forwarded-For`). The page is matched against the enabled links — the **longest** matching page-pattern substring wins — and the resolved redirect (with `params` appended) is returned. |
| `GET` | `/api/v2/deeplinks/match` | apiKey | Called by the app on first launch. Looks up a recent recorded click for the caller's IP (`?ip=` to override) and returns `{ matched, slug, name, redirectUrl, pageUrl, clickedAt, params, signals }`. `redirectUrl` already has the captured `params` appended. By default it **claims** the click so it is handed out at most once; pass `?claim=false` to peek without consuming. |
| `GET`/`PUT` | `/api/v2/deeplinks/click-retention` | **admin** apiKey | Get/set how many days recorded clicks are kept — see *Click retention* below. |

Matching is a best-effort heuristic: a website visit and the app's first launch are correlated by a
shared **IP** within `DeepLinks:MatchWindowHours` (default 24). Behind a proxy/tunnel the visitor's real
client IP must reach the service for both calls — `ForwardedHeaders` resolves it from `X-Forwarded-For`;
directly behind Docker's port forwarding the recorded address is the bridge gateway, not the real client.

### Query parameters

Attribution/campaign parameters ride along end to end. On the smart link they come from the URL's own
query string (`/dl/spring-promo?utm_source=newsletter&promo=ABC`); on the JSON capture route they come
from the optional `params` object. They are stored with the click, **appended to the redirect address**
(percent-encoded, after any query the redirect already carries, before any `#fragment`), and returned in
the match response — so a campaign tag set on the link a visitor clicked reaches the app on first launch.

The captured set is **bounded** so an over-decorated URL can never break the redirect or bloat storage:

| Option | Default | Effect |
|---|---|---|
| `DeepLinks:MaxQueryParams` | `16` | Maximum parameters captured per click. Extras are **dropped** (never an error). |
| `DeepLinks:MaxQueryParamLength` | `256` | Maximum characters per key and per value; longer ones are **truncated**. |

Repeated keys keep the first value seen, and blank keys are skipped. The same caps apply to both the
smart link and the JSON capture route. Operators see the captured params per click in the **Params**
column of the `/DeepLinks` page.

### Device signals

Alongside the IP, each click captures **device-identification signals** — screen dimensions, browser,
timezone, device time, language — to firm up the otherwise IP-only match. They come from:

- any custom **`X-DeepLink-*`** request header (e.g. `X-DeepLink-Screen: 1920x1080`,
  `X-DeepLink-Timezone: Europe/Berlin`, `X-DeepLink-Device-Time: …`) — the prefix is stripped and the
  remainder lower-cased into the signal key;
- a curated set of standard fingerprint headers that ride along on a plain browser navigation, no JS
  required: `Accept-Language` → `language`, and the client hints `Sec-CH-UA*`,
  `Sec-CH-Viewport-Width`/`Height`, `Sec-CH-Width`, `Sec-CH-DPR`, `Sec-CH-Device-Memory`;
- the optional `signals` object on the JSON capture route (highest precedence).

Signals are stored with the click, shown in the **Signals** column of the `/DeepLinks` page, and
returned in the match response so the app can use them as extra match confidence. Unlike query
parameters they are **not** forwarded onto the redirect. They are bounded by the same
`MaxQueryParams` / `MaxQueryParamLength` caps.

### Click retention

Recorded clicks (the captured IP stream) are purged after a configurable age by a background sweep; the
**link definitions are never purged**. This bounds the growth of a public smart link's click stream and
limits how long captured IPs are retained.

The period seeds from `DeepLinks:ClickRetentionDays` (default 30) and is overridable at runtime — the
override is persisted in `deeplinks.db` and survives restarts. Set it two ways:

- **REST** (automation): `GET`/`PUT /api/v2/deeplinks/click-retention`, gated by an **admin-role** API
  key (same policy as key management). `PUT { "retentionDays": 14 }` (1..3650) persists it and writes
  an audit row (`deeplink.retention.set`); `GET` returns `{ retentionDays, overridden }`.
- **Admin page**: the *Click retention* control on `/DeepLinks` (operator cookie auth).

The sweep runs every `DeepLinks:RetentionScanIntervalSeconds` (default 1h, floored at 60s).

### Scale

The feature is built for **thousands** of link definitions. The smart link and match are slug-/IP-indexed
(`O(log n)`); the capture route's longest-page-pattern resolution scans only the **enabled** set, which is
cached in memory (write-invalidated, with a short TTL backstop) so a high-volume capture stream doesn't
reload definitions from SQLite on every hit. The admin links list is **paginated** (`DeepLinks:LinksPageSize`,
default 50) and **searchable** (slug/name/page-pattern substring), so the page stays responsive at scale.

## 8.7 Clients & apps {#clients-apps}

Tenancy administration lives on one **admin-only** screen, `/Clients` ("Clients & apps") — it is hidden
from client logins and folds together what were once three places (a separate apps page and the API-keys
block on `/Maintenance`). The **client is the top-level tenant, identified by its API access key**; each
client owns a list of **apps**, and every app is its own dashboard backed by its own database.
Environment is folded into the app slug — create `app-a-qa` / `app-a-prod` rather than adding a separate
environment axis.

The page has three sections:

1. **Clients** — register a client (which mints its first `client`-role access key, shown **once**),
   rename it, `issue key` to mint more of its keys (e.g. to rotate), and archive/restore or delete it.
   The slug is a non-PII business key (e.g. a pharmacy id), stored verbatim. Traffic sent with an
   unbound/root key falls back to the seeded `default` client.
2. **Apps** — pick a client (`?client=`) to create/rename/archive/restore/delete its apps. A slug (the
   `appId` the SDK sends) is unique only *within* its client, so two clients may each own an app with
   the same slug. Admins manage any client's apps; a client login manages only its own.
3. **API keys** — mint/revoke **admin** operator keys only (unbound, all-clients, can manage keys). A
   client's own bound key is minted from its `issue key` button in the Clients section, not here — see §8.4.

### Archive vs delete

Both clients and apps support two lifecycle actions, and the destructive one is gated by a confirmation
dialog (§8.2):

- **Archive** is a reversible soft-disable: ingestion is rejected and the tenant drops out of every
  dashboard, but its rows and on-disk data are kept, and **restore** brings it back exactly as it was.
  An app is only "active" when its owning client is active too, so archiving a client hides all of its
  apps without mutating the app rows.
- **Delete** is a permanent hard-delete: it revokes the client's bound keys, removes the catalog rows,
  and recursively wipes the on-disk tree (`{ReportsRoot}/apps/{client}[/{app}]/` — analytics + report
  databases). This cannot be undone.

Both actions refuse the seeded **default** client/app (the fallback for attribution-omitting traffic).
Every lifecycle change is audited (`client.{archive,unarchive,delete}`, `app.{archive,unarchive,delete}`,
`apikey.{create,revoke}`).

### Client self-service & login

A client can also manage its own apps over JSON with its key — `GET/POST/DELETE /api/v2/apps` (an
unbound key gets `403`). Clients log into the console at `/ClientLogin` (paste the key); that session is
confined to its own per-app dashboards — its analytics and its own problem/error reports — and pinned to
its own `?client=`, so it can never read another tenant's data by editing the URL.
