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

    /// <summary>
    /// Authorization policy for the key-management endpoints: requires the API-key scheme AND the
    /// <c>admin</c> role claim. Non-admin (<c>client</c>) keys authenticate but are rejected with 403.
    /// </summary>
    public const string ApiKeyAdminPolicy = "ApiKeyAdmin";

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
    // Operation descriptions are authored as Markdown — Swagger UI renders them (tables, code
    // fences, examples) as the operation body. This is the canonical API reference: it replaces
    // the former hand-written docs/guide API-reference chapter, so nothing lives in two places.
    private const string SubmitProblemReportDescription = """
        Accept a Report-a-Problem submission from the Android/iOS IA SDK.

        **Request** — `multipart/form-data`, `apiKey` header required.

        - **`json`** part — `application/json`, **required**. Deserialised into `RSCProblemReport` and validated by `RSCReportValidator`.
        - **`file`** part — `application/gzip`, optional. Gzip-compressed log bundle; the server checks the first two bytes against the gzip magic `1F 8B` and rejects non-gzip blobs.

        **`json` part fields**

        | Field | Type | Notes |
        |---|---|---|
        | `platform` | string | Required. `"Android"` / `"iOS"` (lowercased before the allow-list check). |
        | `message` | string | Required. User problem description (max 8 KiB). |
        | `title` | string? | Optional short title; for crashes, the exception class. |
        | `deviceModel` | string? | Optional device model. |
        | `email` | string? | Optional contact email. May embed a phone suffix, e.g. `foo@bar.com (phone: +49…)`. |
        | `phoneNumber` / `phone` | string? | Optional phone (`phone` is the iOS CardLink duplicate). |
        | `pharmacyId` | string? | Optional pharmacy identifier. |
        | `userId` | string? | SDK client identifier (Android `SdkSession.clientId`; iOS guest id). Drives the filter and forced-report list. |
        | `source` | string? | Source marker; SDKs send `"SDK"`. |
        | `appVersion` | string? | App version; SDKs emit `` `<host versionName> (SDK <sdk version>)` ``. |
        | `functionalityImportance` | string? | Android-only severity tag. |
        | `labels` | string[]? | Free-form labels (max 32, max 128 chars each). |
        | `kind` | string? | `"crash"` triggers server-side top-frame extraction. `"analytics"`/null for user reports. |
        | `stackTrace` / `eventProperties` / `occurredAt` | optional | Populated by the SDK analytics reporters for automatic captures. |

        Example `json` part:

        ```json
        {
          "platform": "Android",
          "deviceModel": "Pixel 7",
          "title": "java.lang.NullPointerException",
          "message": "Crash while opening cart after scanning a prescription QR.",
          "email": "kunde@example.com (phone: +491701234567)",
          "phoneNumber": "+491701234567",
          "pharmacyId": "DE-100123",
          "userId": "android-user-1003",
          "source": "SDK",
          "appVersion": "4.12.0 (SDK 2.3.30)",
          "functionalityImportance": "Schränkt mich häufig ein",
          "labels": ["SDKV2", "cardlink-client-42"],
          "kind": "crash"
        }
        ```

        **Success** — `201 Created`, `Location: /api/problem-reports/{platform}/{fileName}`, body is an `RSCStoredReport`:

        ```json
        {
          "platform": "android",
          "fileName": "problem-report_20260421-093941_3f1a8b2c9d0e.json",
          "sizeBytes": 612,
          "submittedAt": "2026-04-21T09:39:41.0000000+00:00",
          "attachmentFileName": "problem-report_20260421-093941_3f1a8b2c9d0e_a1b2c3d4e5f6.log.gz",
          "attachmentSizeBytes": 184320
        }
        ```

        **Failures** — `400` (missing/malformed `json`, validation failure, non-gzip attachment, malformed multipart), `401` (missing/invalid `apiKey`), `413` (body > `MaxUploadBytes` or attachment > `MaxAttachmentBytes`), `415` (not `multipart/form-data`), `429` (rate limit or ingest queue full). Error bodies follow RFC 7807 (`application/problem+json`) with a `traceId`; exception details are never echoed.
        """;

    private const string SubmitJsonReportDescription = """
        POST a single `RSCProblemReport` JSON document directly — no multipart, no attachment.

        Same auth (`apiKey`), concurrency limiter, validator (`RSCReportValidator`), and idempotency
        (content-hash filename) as the multipart `report-problem` endpoint; capped at `MaxJsonBytes`.
        See **POST `/partners/api/v2/report-problem`** for the full field table and the `RSCProblemReport`
        shape. Persisted rows are tagged `ingestionChannel = "json"` so the console can tell API
        submissions apart from SDK uploads.

        **Success** — `201 Created`, `Location: /api/problem-reports/{platform}/{fileName}`, body is an
        `RSCStoredReport` (same shape as the multipart endpoint).

        **Failures** — `400` (malformed/invalid document), `401` (missing/invalid `apiKey`),
        `413` (body > `MaxJsonBytes`), `415` (not `application/json`), `429` (rate limit or ingest queue
        full). Error bodies follow RFC 7807 (`application/problem+json`) with a `traceId`.
        """;

    private const string ListReportsDescription = """
        Returns an `RSCStoredReport[]` for the platform bucket, newest first. Unknown platforms `404`.
        Each element is the same shape returned on a successful submission; `attachmentFileName` /
        `attachmentSizeBytes` are `null` for reports with no log bundle.

        ```json
        [
          {
            "platform": "android",
            "fileName": "problem-report_20260421-093941_3f1a8b2c9d0e.json",
            "sizeBytes": 612,
            "submittedAt": "2026-04-21T09:39:41.0000000+00:00",
            "attachmentFileName": "problem-report_20260421-093941_3f1a8b2c9d0e_a1b2c3d4e5f6.log.gz",
            "attachmentSizeBytes": 184320
          },
          {
            "platform": "android",
            "fileName": "problem-report_20260420-181502_9b2c1d0e3f4a.json",
            "sizeBytes": 487,
            "submittedAt": "2026-04-20T18:15:02.0000000+00:00",
            "attachmentFileName": null,
            "attachmentSizeBytes": null
          }
        ]
        ```
        """;

    private const string DownloadReportDescription = """
        Streams one stored file. `fileName` is re-validated through `RSCSafePath`; `Content-Type` is
        `application/gzip` for a `.log.gz` attachment, else `application/json`. `404` when the platform
        is unknown or the file can't be resolved safely.
        """;

    private const string CheckForcedReportDescription = """
        The forced-report allow-list check. Mobile clients call it with their stable identifier on every
        backend fetch; returns `200 OK` with `{ id, forced }`. `forced` flips to `true` once an operator
        adds the id on the **Forced** admin page. Backed by a single indexed row read in `reports.db`.
        The response payload is intentionally tiny so the call is cheap to poll on every session start.

        ```json
        { "id": "android-user-1003", "forced": false }
        ```

        `400` for an empty or oversized id (>256 chars).
        """;

    public static IEndpointRouteBuilder MapProblemReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(HttpVerb.Post, "/partners/api/v2/report-problem", async (HttpRequest req, RSReportIngestionService svc, CancellationToken ct) =>
            {
                // Build-flag gate: ProblemReports can be compiled out (`-p:FeatureProblemReports=false`).
                if (!RSCFeatureFlags.ProblemReports)
                    return Results.Problem(RSCFeatureFlags.DisabledMessage, statusCode: RSCFeatureFlags.DisabledStatusCode);
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
            .WithDescription(SubmitProblemReportDescription)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<RSCStoredReport>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status406NotAcceptable)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        // Single-JSON ingest path. Same auth, same concurrency limiter, same Accept filter as the
        // multipart endpoint — but accepts `application/json` directly. Useful for partners that
        // can't easily emit multipart bodies (server-to-server integrations, generic webhooks).
        app.Map(HttpVerb.Post, "/api/v1/reports", async (HttpRequest req, RSReportIngestionService svc, CancellationToken ct) =>
            {
                if (!RSCFeatureFlags.ProblemReports)
                    return Results.Problem(RSCFeatureFlags.DisabledMessage, statusCode: RSCFeatureFlags.DisabledStatusCode);
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
            .WithDescription(SubmitJsonReportDescription)
            .Accepts<ReportService.Models.RSCProblemReport>("application/json")
            .Produces<RSCStoredReport>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status406NotAcceptable)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        var reports = app.MapGroup("/api/problem-reports")
            .Apply(EndpointModifier.RequireAuth, EndpointModifier.AcceptHeaderFilter);
        // Build-flag gate for every read route in the group: 503 when ProblemReports is compiled out.
        reports.AddEndpointFilter(async (ctx, next) =>
            RSCFeatureFlags.ProblemReports
                ? await next(ctx)
                : Results.Problem(RSCFeatureFlags.DisabledMessage, statusCode: RSCFeatureFlags.DisabledStatusCode));

        reports.Map(HttpVerb.Get, "/{platform}", (string platform, RSCIReportStore store, RSCReportServiceOptions opts) =>
                RSCPlatforms.TryCanonicalize(platform, opts) is { } p
                    ? Results.Ok(store.List(p))
                    : Results.NotFound())
            .WithTags("Reports")
            .WithName("ListProblemReports")
            .WithSummary("List stored problem reports for a platform")
            .WithDescription(ListReportsDescription)
            .Produces<IEnumerable<RSCStoredReport>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status406NotAcceptable);

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
            .WithDescription(DownloadReportDescription)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status406NotAcceptable);

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
            .WithDescription(CheckForcedReportDescription)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status406NotAcceptable)
            .Produces(StatusCodes.Status429TooManyRequests);

        return app;
    }
}
