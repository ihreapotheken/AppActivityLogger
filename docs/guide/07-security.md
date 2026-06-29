# 7. Security model

The merged host applies the same hardening to the SDK-facing routes as the standalone ingestion
service, and adds cookie-based controls for the operator console.

## 7.1 Ingestion & transport

| Control | Where | Defends against |
|---|---|---|
| `apiKey` header + constant-time compare | `RSApiKeyAuthenticationHandler` via `CryptographicOperations.FixedTimeEquals` | Unauthenticated access; timing-based key recovery. |
| Per-IP fixed-window rate limit (120/min) | `AddRateLimiter` in `Program.cs` | Brute-force key guessing, dumb DoS, noisy SDKs. `429` returns before auth/ingestion run. |
| Global write-path concurrency cap (`IngestConcurrency`=16, `IngestQueueLimit`=16) | `AddConcurrencyLimiter("ingest-concurrency")` on the POST endpoints | Distributed DoS: even within per-IP budgets, no more than `IngestConcurrency` uploads are in flight. Permits are tied to request lifetime, so a disconnect frees capacity. |
| `MaxUploadBytes` at Kestrel + `FormOptions`; `MaxAttachmentBytes` post-parse; `MaxJsonBytes` for JSON bodies | `Program.cs`, ingestion services | Memory exhaustion via oversized bodies/attachments. |
| Per-request wall-clock timeout (`IngestTimeoutSeconds`=60) | ingestion service via a linked `CancellationTokenSource` | A stuck disk/client holding an ingest permit; fires `503` with a logged `traceId`. |
| Gzip magic-byte probe (`1F 8B`) | `RSCReportValidator` | Storing arbitrary blobs under a `.log.gz` name. |
| Strict JSON parser (`MaxDepth=32`, no comments/trailing commas, `UnmappedMemberHandling=Disallow`) | ingestion `JsonOptions` | Stack exhaustion; parser-differential attacks; smuggling unknown fields past validation. |
| `RSCSafePath.TryCombine` on ingest + download | `Security/RSCSafePath.cs` | Path traversal (`..`), absolute paths, NUL bytes, invalid filename chars. |
| Persisted auth-abuse tracking (10 failures / 60s → 300s ban) | `RSCSqliteAuthAbuseTracker`, hooked in the apiKey handler and `/Login` | Online brute-force against the shared secrets. State survives restarts in its own DB; success clears the counter, repeat abuse extends the ban. |
| Startup secret validation (fail-fast) | `RSCSecretValidation.RequireInProduction` after `builder.Build()` | Booting Production with a missing/weak/placeholder `ApiKey` / `AdminKey`. |
| Kestrel min data rates + request-header timeout | `ConfigureKestrel` | Slowloris trickle-body / trickle-read clients. |
| `Accept`-header filter on JSON endpoints | `RSAcceptHeaderFilter` | Serving a mismatched body — incompatible `Accept` gets `406`. |
| Per-statement SQLite command timeout | report index + analytics store | Long-running queries holding the DB file or request permits. |

## 7.2 Console & responses

| Control | Where | Defends against |
|---|---|---|
| Cookie auth (HttpOnly, SameSite=Strict, Secure over HTTPS, constant-time `AdminKey` compare) | admin `Program.cs` + `/Login` | Console access with the SDK key; CSRF on session. |
| Antiforgery on every console POST (delete, maintenance actions) | Razor Pages | Cross-site request forgery of destructive actions. |
| Content-Security-Policy (`default-src 'self'`, no inline scripts, no frame ancestors) | admin middleware | XSS, clickjacking of the console. |
| Security headers on every response (`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`) | `Program.cs` middleware | MIME sniffing, clickjacking, referrer leakage, proxy caching. |
| `Kestrel.AddServerHeader = false` | `Program.cs` | Server fingerprinting. |
| Correlation-id middleware (`X-Correlation-ID` / `X-Request-ID`) | `RSCCorrelationIdMiddleware` | Links every log line for a request; rejects client ids with control bytes or >128 chars. |
| HSTS + HTTPS redirect when an HTTPS endpoint is bound | `Program.cs` | Protocol downgrade (skipped when TLS terminates upstream). |
| `UseForwardedHeaders` | `Program.cs` | Correct `Request.Scheme` / `RemoteIpAddress` behind a reverse proxy. |
| Centralized RFC 7807 exception handler with `traceId` | `UseExceptionHandler` | Stack-trace / internal-path disclosure. |
| Atomic writes (`.tmp` → `File.Move`) | `RSCFileSystemReportStore` | Operators reading half-written files. |
| Loopback-only default + read-only container FS | compose `127.0.0.1:` binding, `read_only: true` | Public exposure of the console; tampering with the image. |

## 7.3 Two secrets, two scopes

- The **`apiKey`** grants upload-only access to the ingestion routes.
- The **`AdminKey`** grants read + delete + maintenance on the console.

They are distinct: compromising the SDK key cannot sign into the console, and the console key cannot
ingest. The console is loopback-only by default — put it behind a reverse proxy with mTLS or an SSH
tunnel for remote operator access.
