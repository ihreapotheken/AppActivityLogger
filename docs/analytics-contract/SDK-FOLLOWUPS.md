# SDK follow-ups (Android + iOS)

This is the cross-repo punch list that pairs with the server-side work landed in this commit. It
lives in `report-service` because the contract under `docs/analytics-contract/` is owned here; the
work itself happens in the other two repos. Nothing in this file edits the SDK repos — those
changes will go through their own PRs.

## Priority 1 — batchId reuse on retry (correctness)

Both clients generate a fresh `batchId` on every flush attempt. The server contract says retries
must reuse the same `batchId` so the envelope's `accepted_count` reflects the actual contribution
of the batch ([analytics-v2.md L75-83](../analytics-v2.md#L75-L83)). With the post-dedupe envelope
fix landed server-side ([RSCSqliteAnalyticsStore.cs:233-238](../../src/ReportService.Core/Analytics/RSCSqliteAnalyticsStore.cs#L233-L238)),
the over-count is bounded to the envelope row count, but the SDK contract is still violated.

### Android

[`V2AnalyticsSink.kt`](../../../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/analytics/v2/V2AnalyticsSink.kt) — the
current code calls `UUID.randomUUID().toString()` inside `flushOnce()`. Persist the batchId
alongside the queued event ids in the outbox: assign one on `enqueue`, keep it through every
retry, drop it from the outbox header on 2xx receipt. The JSONL outbox can carry the batchId on
the first line of each flush group.

### iOS

[`V2AnalyticsReporter.swift`](../../../IA-SDK-Dev-iOS/IOSKit/IOSKit/HTTPClientNetworking/V2AnalyticsReporter.swift) — the
default-arg `UUID()` on `V2AnalyticsBatchRequest.batchId` masks the bug. Force callers to supply
it, then plumb one through `AnalyticsV2Dispatcher.flush()` that's persisted next to the outbox
file's flush group.

## Priority 2 — wire into SDK init

### Android

The dispatcher already exposes `setRemoteSink(sink)` ([`AnalyticsDispatcher.kt:105`](../../../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/listener/AnalyticsDispatcher.kt#L105))
but nobody calls it. In `IaSdk.initialize()`, build a `V2AnalyticsSink(context, config, retrofitService)`
when `config.analytics.endpointUrl` is non-blank, then `analyticsDispatcher.setRemoteSink(sink)`.

Strip the `10.0.2.2` literal and hard-coded apiKey from the legacy `AnalyticsRemoteDataSource`
([`AnalyticsRemoteDataSource.kt:30`](../../../IA-SDK-Dev-Android/core/src/main/kotlin/de/ihreapotheken/sdk/core/data/remote/datasource/AnalyticsRemoteDataSource.kt#L30))
once the v2 path is live in every host app.

### iOS

`AnalyticsV2Dispatcher` exists ([`AnalyticsV2Dispatcher.swift:34`](../../../IA-SDK-Dev-iOS/IACore/IACore/Features/AnalyticsV2/AnalyticsV2Dispatcher.swift#L34))
but no call sites start it. In `IASDK.initialize`, start it when `analyticsEnabled` + consent
allow. Add the new files to `IACore.xcodeproj` and `V2AnalyticsReporter.swift` to
`IOSKit.xcodeproj` (the SwiftPM build already finds them — the missing piece is the Xcode build).

Replace the `BaseDataSource.swift:98` localhost routing with an environment-driven base URL.

## Priority 3 — automatic screen tracking

### iOS

Build `.analyticsScreen("home")` on top of the existing `.screenIdentifier("home")` modifier
([`View+ScreenIdentifier.swift:16`](../../../IA-SDK-Dev-iOS/IACore/IACore/CoreUI/Extensions/SwiftUI/View/View+ScreenIdentifier.swift#L16)).
Fire `screen_view` on appear, `screen_exit` on disappear with the dwell time in
`durationMs`. Names should match the `event-catalog.json` taxonomy.

### Android

Already has `AnalyticsPipeline` driving screen events. The taxonomy in
[`event-catalog.json`](event-catalog.json) lists the canonical names — align the existing emit
sites to the same names so the funnel matchers work without per-platform translation.

## Priority 4 — per-session sequence numbers ✓ DONE (Android)

`V2AnalyticsSink.kt` now maintains a `ConcurrentHashMap<String, AtomicLong>` keyed by session id.
`nextSequence(sessionId)` increments it on every `mapToDto()` call, so every event in a session
gets a unique, monotonically increasing sequence number. SDK-internal diagnostic events use a
separate `sdkDiagSequence` counter that never collides with user session counters.

iOS: add the same counter inside `AnalyticsV2Dispatcher` when the dispatcher init wiring lands
(Priority 2 above).

## Priority 5 — consent revocation drops the outbox

Both clients flush whatever was captured before consent was revoked. Required behaviour: on
consent revocation, call `outbox.clear()` and reject in-flight events.

- Android: `AnalyticsConsent` is a plain `var` without a setter callback. Add a listener slot and
  call `remoteSink.clearAll()` from `IaSdk.setAnalyticsConsent(false)`.
- iOS: move consent state inside `AnalyticsV2Dispatcher` instead of the host gating callers, and
  add a `revoke()` method that clears the outbox and stops the flush task.

## Priority 6 — outbox encryption

Both repos document this as a follow-up. JSONL today, Keystore/Keychain-backed AES-GCM is the
target. Threat model is durability over confidentiality — the validator strips PII before any row
reaches the wire, and the outbox lives in `noBackupFilesDir` / `applicationSupportDirectory`. The
encryption is hardening, not a bug fix.

## Priority 7 — instrument the documented event catalog

The taxonomy in [`event-catalog.json`](event-catalog.json) covers common, pharmacy, OTC,
prescription, CardLink, and appointments flows. Existing Android emit sites cover much of this
already; gap-fill against the catalog file. iOS is mostly empty — bridge CardLink `TrackResult`
into v2 first, then add screen view coverage via the new SwiftUI modifier.

## SDK non-fatal error tracking ✓ DONE (Android + iOS)

Both SDKs now emit `type=error, feature=sdk` events into a stable diagnostic session so
infrastructure failures are visible in the analytics admin without polluting user session data.

**Implemented** (see `docs/analytics-v2.md` for full details):
- `sdk_batch_http_error` — emitted on first failure per episode; `retryable` property distinguishes
  4xx drops from 5xx/429 retries.
- `sdk_batch_network_error` — emitted on network-layer failures (IOException, SocketTimeout,
  URLError).
- `sdk_api_http_error` — emitted from `ApiResponseCall.onApiError` (Android) /
  `URLSessionRequestService.onError` (iOS) for non-analytics HTTP errors.
- `sdk_outbox_parse_error` — emitted when the JSONL outbox contains malformed lines that had to
  be skipped; `lines_skipped` property gives the count per flush cycle.

**What remains**:
- Android: the `onApiError` callback fires for every API call error including expected ones
  (e.g. 401 after token expiry before refresh). Consider adding a filter for transient vs
  persistent errors before emitting so the error stream stays actionable.
- iOS: `URLSessionRequestService.onError` fires on `decodingError` too — review whether all
  decoding failures are worth tracking vs. only ones above a severity threshold.
- Both: `sdk_batch_http_error` events themselves are submitted in the next flush — if the outbox
  is full or the network is down, these events could be lost. The consecutive-failure guard limits
  loss to one event per episode, which is acceptable.

## Contract test parity

Both SDK repos should vendor or submodule `docs/analytics-contract/fixtures/` and run their
own contract test that:

1. Deserializes every `accept/*.json` into the SDK's `V2AnalyticsBatchRequest` (Kotlin) /
   `V2AnalyticsBatchRequest` (Swift) without losing fields.
2. Re-serializes the deserialized object and asserts the resulting JSON shape (after sorting
   keys) is structurally equal to the input. This catches casing / camelCase drift between
   server and SDK.
3. Asserts the SDK's local PII deny-list equals `event-catalog.json`'s `forbiddenPropertyKeys`.

The server side already runs `AnalyticsContractFixtureTests` against these files. SDK parity makes
the contract drift visible on whichever side ships next.
