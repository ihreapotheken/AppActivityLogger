# v2 analytics wire-contract fixtures

This directory is the **source of truth** for the JSON shape carried by
`POST /api/v2/analytics/events`. Every fixture under `fixtures/` is a complete
`RSCAnalyticsBatch` payload that exercises one validator path. The server
consumes them from `AnalyticsContractFixtureTests`; the Android and iOS SDK
repos are expected to mirror this folder (or git-submodule it) and run the
same JSON against their `V2AnalyticsBatchRequest` types.

If the server-side `RSCAnalyticsBatch` / `RSCAnalyticsEvent` / `RSCAnalyticsItem`
records change shape, update the fixtures here in the same commit. SDK CI
then breaks until the SDK is updated to match.

## Layout

- `accept/` — batches the validator must accept in full. Filename describes
  the event kind exercised. Tests assert all events end up in
  `Validate(...).Accepted` with the documented dead-letter list empty.
- `reject/` — batches that must produce a specific dead-letter reason from
  `RSCAnalyticsDeadLetterReasons`. The expected reason and the per-event vs.
  batch-level scope are encoded in the filename
  (e.g. `reject/batch_too_large.json`).
- `event-catalog.json` — the canonical event taxonomy (names, types, screen
  / feature / properties shape). SDKs should code their event constants
  against this file rather than reinventing strings locally.

## Time-sensitive fields

Every fixture uses `"occurredAt": "2026-05-14T10:00:00.0000000+00:00"` and
`"generatedAt": "2026-05-14T10:00:00.0000000+00:00"`. The server enforces a
`MaxClockSkewSeconds` window relative to wall clock, so the test harness
loads fixtures with an explicit `receivedAt = 2026-05-14T10:00:00Z` and a
permissive skew option. Production code never sees these timestamps — they
exist purely so the wire shape stays diffable across SDK repos.

## Updating

1. Edit the fixture (or add a new one).
2. Run `dotnet test --filter FullyQualifiedName~AnalyticsContractFixture` —
   the test fails loud if a fixture no longer parses, fails an assertion, or
   if a new validator branch isn't covered.
3. Open matching SDK PRs that re-run their parser/serializer against the new
   shape. The Android repo's `V2AnalyticsContractTest` and the iOS repo's
   `AnalyticsV2ContractTests` consume the same fixtures.

## Identity & PII policy

The fixtures carry `anonymousId` and `clientId` as raw strings — the server
hashes them with `IdentifierHashPepper` before persistence. SDKs **must not**
send PII keys in the property bag; the canonical deny-list lives in
`event-catalog.json` under `"forbiddenPropertyKeys"`. The server's
`RSCAnalyticsOptions.ForbiddenPropertyKeys` default is the authoritative
list — the catalog file is generated from it on every server build (see
`AnalyticsContractFixtureTests.ForbiddenKeysCatalogIsInSync`).
