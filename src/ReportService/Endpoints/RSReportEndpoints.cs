using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ReportService.Ingestion;
using ReportService.Options;
using ReportService.Security;
using ReportService.Storage;
using ReportService.Validation;

namespace ReportService.Endpoints;

public enum HttpVerb { Get, Post, Put, Delete, Patch }

public enum EndpointModifier
{
    RequireAuth,
    IngestRateLimit,
    AcceptHeaderFilter,
    DisableAntiforgery,
}

public static class RSEndpointConventions
{
    public static RouteHandlerBuilder Map(this IEndpointRouteBuilder app, HttpVerb verb, string pattern, Delegate handler) =>
        verb switch
        {
            HttpVerb.Get => app.MapGet(pattern, handler),
            HttpVerb.Post => app.MapPost(pattern, handler),
            HttpVerb.Put => app.MapPut(pattern, handler),
            HttpVerb.Delete => app.MapDelete(pattern, handler),
            HttpVerb.Patch => app.MapPatch(pattern, handler),
            _ => throw new ArgumentOutOfRangeException(nameof(verb)),
        };

    /// <summary>
    /// Named authorization policy that ingestion endpoints opt into. Both the standalone
    /// ingestion process and the merged admin process register this policy to bind
    /// <see cref="RSApiKeyAuthenticationOptions.Scheme"/> as the auth scheme — the merged admin
    /// uses Cookie as its default scheme for Razor pages, so we can't rely on
    /// <c>RequireAuthorization()</c> picking up the right scheme via the default policy.
    /// </summary>
    public const string ApiKeyPolicy = "ApiKey";

    public static T Apply<T>(this T builder, params EndpointModifier[] modifiers) where T : IEndpointConventionBuilder
    {
        foreach (var m in modifiers)
        {
            switch (m)
            {
                case EndpointModifier.RequireAuth:
                    builder.RequireAuthorization(ApiKeyPolicy);
                    break;
                case EndpointModifier.IngestRateLimit:
                    builder.RequireRateLimiting("ingest-concurrency");
                    break;
                case EndpointModifier.AcceptHeaderFilter:
                    builder.AddEndpointFilter<T, RSAcceptHeaderFilter>();
                    break;
                case EndpointModifier.DisableAntiforgery:
                    builder.DisableAntiforgery();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(modifiers), m, null);
            }
        }
        return builder;
    }
}

/// <summary>Minimal-API routes for the Report-a-Problem ingestion + read surface.</summary>
public static class RSReportEndpoints
{
    public static IEndpointRouteBuilder MapProblemReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(HttpVerb.Post, "/partners/api/v2/report-problem", async (HttpRequest req, RSReportIngestionService svc, CancellationToken ct) =>
            {
                var r = await svc.IngestAsync(req, ct);
                return r.Success
                    ? Results.Created($"/api/problem-reports/{r.Stored!.Platform}/{r.Stored.FileName}", r.Stored)
                    : Results.Problem(r.Error, statusCode: r.HttpStatus);
            })
            .Apply(
                EndpointModifier.RequireAuth,
                EndpointModifier.IngestRateLimit,
                EndpointModifier.AcceptHeaderFilter,
                EndpointModifier.DisableAntiforgery)
            .WithTags("Ingestion")
            .WithName("SubmitProblemReport")
            .WithSummary("Accept a Report-a-Problem submission from the IA SDK")
            .WithDescription("Multipart form with a required `json` part (application/json) and an optional `file` part (application/gzip). Matches the partner endpoint already called by the Android and iOS IA SDKs.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<RSCStoredReport>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status429TooManyRequests);

        // Single-JSON ingest path. Same auth, same concurrency limiter, same Accept filter as the
        // multipart endpoint — but accepts `application/json` directly. Useful for partners that
        // can't easily emit multipart bodies (server-to-server integrations, generic webhooks).
        app.Map(HttpVerb.Post, "/api/v1/reports", async (HttpRequest req, RSReportIngestionService svc, CancellationToken ct) =>
            {
                var r = await svc.IngestJsonAsync(req, ct);
                return r.Success
                    ? Results.Created($"/api/problem-reports/{r.Stored!.Platform}/{r.Stored.FileName}", r.Stored)
                    : Results.Problem(r.Error, statusCode: r.HttpStatus);
            })
            .Apply(
                EndpointModifier.RequireAuth,
                EndpointModifier.IngestRateLimit,
                EndpointModifier.AcceptHeaderFilter,
                EndpointModifier.DisableAntiforgery)
            .WithTags("Ingestion")
            .WithName("SubmitJsonReport")
            .WithSummary("Accept a single JSON-encoded problem report")
            .WithDescription("POST a `RSCProblemReport` JSON document directly. No multipart, no attachment. Same validation, size cap (`MaxJsonBytes`), and idempotency (content-hash filename) as the multipart path. The persisted row is tagged with channel = \"json\" so the admin console can tell API submissions apart from SDK uploads.")
            .Accepts<ReportService.Models.RSCProblemReport>("application/json")
            .Produces<RSCStoredReport>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status429TooManyRequests);

        var reports = app.MapGroup("/api/problem-reports")
            .Apply(EndpointModifier.RequireAuth, EndpointModifier.AcceptHeaderFilter);

        reports.Map(HttpVerb.Get, "/{platform}", (string platform, RSCIReportStore store, RSCReportServiceOptions opts) =>
                RSCPlatforms.TryCanonicalize(platform, opts) is { } p
                    ? Results.Ok(store.List(p))
                    : Results.NotFound())
            .WithTags("Reports")
            .WithName("ListProblemReports")
            .WithSummary("List stored problem reports for a platform")
            .WithDescription("Returns RSCStoredReport metadata for all persisted problem reports in the platform bucket, newest first.")
            .Produces<IEnumerable<RSCStoredReport>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        reports.Map(HttpVerb.Get, "/{platform}/{fileName}", (string platform, string fileName, RSCIReportStore store, RSCReportServiceOptions opts) =>
            {
                if (RSCPlatforms.TryCanonicalize(platform, opts) is not { } p) return Results.NotFound();
                var s = store.OpenRead(p, fileName);
                if (s is null) return Results.NotFound();

                var contentType = fileName.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase)
                    ? "application/gzip"
                    : "application/json";
                return Results.Stream(s, contentType, fileName);
            })
            .WithTags("Reports")
            .WithName("DownloadProblemReport")
            .WithSummary("Download a specific stored problem report")
            .WithDescription("Streams either the JSON document or its sibling gzip attachment for the named file.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        // Forced-report check. Mobile apps call this on every backend fetch with their stable
        // identifier (clientId / userId — whatever they already key on); operators add specific
        // IDs through the admin UI to instruct that client to forcefully submit a Report-a-Problem
        // entry on the next opportunity. Cheapest possible read path: a single indexed row lookup
        // with the same apiKey + per-IP rate limiter as the rest of the public surface.
        app.Map(HttpVerb.Get, "/api/v1/forced-reports/{id}", async (string id, RSCIForcedReportStore store, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256)
                {
                    return Results.BadRequest(new { error = "id must be 1..256 chars" });
                }
                var forced = await store.ContainsAsync(id, ct);
                return Results.Ok(new { id, forced });
            })
            .Apply(EndpointModifier.RequireAuth, EndpointModifier.AcceptHeaderFilter)
            .WithTags("ForcedReports")
            .WithName("CheckForcedReport")
            .WithSummary("Tells the mobile client whether it should force a Report-a-Problem submission")
            .WithDescription("Returns `{ id, forced }` where `forced` is true iff an operator added this id to the allow-list via the admin UI. Intended to be polled on each backend fetch; the response payload is intentionally tiny so the call is cheap to make on every session start.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);

        return app;
    }
}
