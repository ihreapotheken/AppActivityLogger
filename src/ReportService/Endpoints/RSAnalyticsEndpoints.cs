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
            .WithDescription(
                "JSON body shaped as RSCAnalyticsBatch. The server validates the batch, persists accepted events to analytics_events (idempotent on platform + eventId), and dead-letters rejected events with a documented reason. Returns 202 with an RSCAnalyticsBatchReceipt — clients should retry the same batch with the same batchId on 5xx/429.")
            .Accepts<RSCAnalyticsBatch>("application/json")
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
