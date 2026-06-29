using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ReportService.Analytics;
using ReportService.Ingestion;
using ReportService.Models;

namespace ReportService.Endpoints;

/// <summary>
/// v2 analytics ingestion routes. Mounted by both the standalone ingestion process and the merged
/// admin process. Reuses the existing <see cref="RSEndpointConventions"/> for auth, the
/// ingest-concurrency rate-limiter, and the Accept-header filter — keeping the surface uniform
/// with the v1 report-problem routes.
/// </summary>
public static class RSAnalyticsEndpoints
{
    // Markdown operation descriptions — rendered by Swagger UI as the operation body. This is the
    // canonical API reference for the analytics ingestion surface (replaces the former hand-written
    // docs/guide API-reference chapter).
    private const string IngestAnalyticsBatchDescription = """
        The **mobile SDK** analytics path.

        JSON body shaped as `RSCAnalyticsBatch`. The server validates the batch, persists accepted
        events to `analytics_events` (idempotent on `platform + eventId`), and dead-letters rejected
        events with a documented reason. Returns `202 Accepted` with an `RSCAnalyticsBatchReceipt`.
        Clients should retry the same `batchId` on `5xx`/`429`. See the **Analytics pipeline** guide
        chapter for the batch shape and validation rules.

        Receipt body (`batchId` is echoed so a retry can be reconciled; counts let the SDK confirm the
        server saw what it sent):

        ```json
        {
          "batchId": "b-7f3a9c12",
          "acceptedCount": 18,
          "rejectedCount": 1,
          "duplicateCount": 2,
          "batchRejected": false,
          "batchRejectReason": null
        }
        ```

        `acceptedCount` + `rejectedCount` + `duplicateCount` equals the number of events submitted.
        When the whole batch is refused (e.g. an unsupported schema version) the response is still
        `202 Accepted`, but `batchRejected` is `true`, `batchRejectReason` names the cause, and every
        count is `0`:

        ```json
        {
          "batchId": "b-7f3a9c12",
          "acceptedCount": 0,
          "rejectedCount": 0,
          "duplicateCount": 0,
          "batchRejected": true,
          "batchRejectReason": "schema_version_unsupported"
        }
        ```
        """;

    private const string IngestServerEventsDescription = """
        The **backend / server-to-server** analytics path — coexists with the SDK route; both stay open.
        Lets a trusted first-party service report analytics events directly (e.g. a confirmed purchase)
        instead of relying on the mobile client to emit them — more reliable, since the app may be
        backgrounded, the network may drop, or the event may be a purely server-side fact.

        Body shaped as `RSCServerAnalyticsRequest` — a lighter envelope where the backend supplies only
        what it knows. The server **synthesizes** the SDK-centric fields (sessionId, sequence, batchId,
        sdkVersion, generatedAt) and runs the events through the **same** validation → hashing → storage
        → aggregation → funnel pipeline as SDK batches.

        | Field | Type | Notes |
        |---|---|---|
        | `platform` | string? | Attribution. Defaults to `"backend"`; pass `"android"`/`"ios"` (with a matching `sessionId`) to join that platform's sessions and funnels. |
        | `subjectId` | string? | User/account id to attribute events to. Hashed with the pepper before storage — never stored raw (same as the SDK's `anonymousId`). |
        | `clientId` | string? | Optional secondary identifier; also hashed. |
        | `source` | string? | Free-text marker (e.g. `"order-service"`); recorded as the `sdkVersion` `server:<source>` so the Health page shows which service reported. |
        | `batchId` | string? | Optional; a `srv-<guid>` is generated when omitted. |
        | `events[]` | array | Each: required `name`; optional `type` (default `action`; use `ecommerce` for purchases), `eventId`, `sessionId`, `sequence`, `occurredAt` (default now), `screen`, `feature`, `durationMs`, `properties`, `items`. |

        **Idempotency** — supply a stable `eventId` per event derived from the business key
        (e.g. `"purchase-<orderId>"`). A retried report is then deduped on `UNIQUE(platform, eventId)`
        instead of double-counting. Per-event problems (unknown `type`, forbidden PII key, …) are
        dead-lettered and reflected in the receipt counts; an unknown `platform` rejects the whole batch
        (`platform_unknown`).

        Example — backend reports a completed OTC purchase (feeds the `otc_purchase` funnel's `purchase` step):

        ```json
        {
          "platform": "ios",
          "subjectId": "user-42",
          "source": "order-service",
          "events": [
            {
              "name": "purchase",
              "type": "ecommerce",
              "eventId": "purchase-ORDER-2001",
              "sessionId": "s-ios-abc",
              "occurredAt": "2026-06-25T10:15:00.0000000Z",
              "feature": "otc",
              "properties": { "order_id": "2001", "total": "19.98", "currency": "EUR" },
              "items": [ { "itemId": "pzn-00001", "name": "Ibuprofen 400mg", "price": 9.99, "quantity": 2, "currency": "EUR" } ]
            }
          ]
        }
        ```

        Returns `202 Accepted` with an `RSCAnalyticsBatchReceipt` (same shape as the SDK route). On the
        example request, a first delivery returns `acceptedCount: 1`; a retry with the same `eventId`
        returns `acceptedCount: 0, duplicateCount: 1`. An unknown `platform` rejects the whole batch with
        `batchRejected: true, batchRejectReason: "platform_unknown"`.

        Same auth (`apiKey`), rate limiter, and body cap (`MaxJsonBytes`) as the SDK route. `400` when the
        body carries no events. The set of accepted server platforms is `Analytics:ServerPlatforms`
        (default `["backend"]`) unioned with `ReportService:AllowedPlatforms` — analytics-only, so it
        never widens what the problem-report endpoints accept.
        """;

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(HttpVerb.Post, "/api/v2/analytics/events",
                async (HttpRequest req, RSAnalyticsIngestionService svc, CancellationToken ct) =>
                {
                    var r = await svc.IngestAsync(req, ct);
                    return r.Success
                        ? Results.Accepted(value: r.Receipt)
                        : Results.Problem(r.Error, statusCode: r.HttpStatus);
                })
            .Apply(
                EndpointModifier.RequireAuth,
                EndpointModifier.IngestRateLimit,
                EndpointModifier.AcceptHeaderFilter,
                EndpointModifier.DisableAntiforgery)
            .WithTags("Analytics")
            .WithName("IngestAnalyticsBatch")
            .WithSummary("Accept a v2 analytics event batch from the IA SDK")
            .WithDescription(IngestAnalyticsBatchDescription)
            .Accepts<RSCAnalyticsBatch>("application/json")
            .Produces<RSCAnalyticsBatchReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        // First-party / server-to-server reporting. Same auth, rate-limiter, and pipeline as the
        // SDK route above — a trusted backend can emit events (e.g. a confirmed purchase) directly
        // instead of relying on the mobile client to send them. Coexists with the SDK route; both
        // remain open.
        app.Map(HttpVerb.Post, "/api/v2/analytics/server-events",
                async (HttpRequest req, RSAnalyticsIngestionService svc, CancellationToken ct) =>
                {
                    var r = await svc.IngestServerAsync(req, ct);
                    return r.Success
                        ? Results.Accepted(value: r.Receipt)
                        : Results.Problem(r.Error, statusCode: r.HttpStatus);
                })
            .Apply(
                EndpointModifier.RequireAuth,
                EndpointModifier.IngestRateLimit,
                EndpointModifier.AcceptHeaderFilter,
                EndpointModifier.DisableAntiforgery)
            .WithTags("Analytics")
            .WithName("IngestServerAnalyticsEvents")
            .WithSummary("Report first-party analytics events from a trusted backend service")
            .WithDescription(IngestServerEventsDescription)
            .Accepts<RSCServerAnalyticsRequest>("application/json")
            .Produces<RSCAnalyticsBatchReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}
