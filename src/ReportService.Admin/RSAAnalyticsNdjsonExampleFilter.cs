using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ReportService.Admin;

/// <summary>
/// Swashbuckle can't synthesise a body example for the NDJSON exports: the endpoints stream rows
/// straight to the response, so the generator only ever sees a bare <c>200</c> with no realistic
/// content (and the schema-derived example would carry placeholder <c>"string"</c>/<c>0</c> values).
/// This filter attaches one realistic, fully-populated sample row — and a description spelling out
/// the newline-delimited framing — to the <c>application/x-ndjson</c> response of the two
/// <c>/admin/api/analytics/*.ndjson</c> operations, so the Swagger UI shows what each line of a
/// download actually looks like. It keys off the <c>.ndjson</c> relative path, so it never touches
/// the JSON routes.
/// </summary>
/// <remarks>
/// The sample is rendered as a structured object (not a raw NDJSON string): because
/// <c>application/x-ndjson</c> contains "json", the Swagger UI would otherwise escape a string
/// example into an unreadable <c>"{\"eventId\":…}"</c> blob. The values mirror the worked examples
/// in <c>docs/guide/04-api-reference.md</c> (same hashes, session ids) so the rendered Swagger page
/// and the prose reference stay in lockstep.
/// </remarks>
internal sealed class RSAAnalyticsNdjsonExampleFilter : IOperationFilter
{
    private const string NdjsonContentType = "application/x-ndjson";
    private const string SampleAndroidHash = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2";

    private static OpenApiObject EventsExample() => new()
    {
        ["eventId"] = new OpenApiString("3f1a8b2c-9d0e-4a5b-8c6d-1e2f3a4b5c6d"),
        ["platform"] = new OpenApiString("android"),
        ["sessionId"] = new OpenApiString("s-android-abc"),
        ["anonymousIdHash"] = new OpenApiString(SampleAndroidHash),
        ["sequence"] = new OpenApiLong(7),
        ["occurredAt"] = new OpenApiString("2026-06-25T10:14:58.0000000Z"),
        ["type"] = new OpenApiString("action"),
        ["name"] = new OpenApiString("add_to_cart"),
        ["screen"] = new OpenApiString("ProductDetail"),
        ["feature"] = new OpenApiString("otc"),
        ["durationMs"] = new OpenApiLong(1240),
    };

    private static OpenApiObject SessionsExample() => new()
    {
        ["platform"] = new OpenApiString("android"),
        ["sessionId"] = new OpenApiString("s-android-abc"),
        ["anonymousIdHash"] = new OpenApiString(SampleAndroidHash),
        ["startedAt"] = new OpenApiString("2026-06-25T10:12:03.0000000Z"),
        ["lastSeenAt"] = new OpenApiString("2026-06-25T10:15:41.0000000Z"),
        ["eventCount"] = new OpenApiLong(23),
        ["screenCount"] = new OpenApiLong(6),
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath;
        if (path is null || !path.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase)) return;

        var isEvents = path.EndsWith("events.ndjson", StringComparison.OrdinalIgnoreCase);
        var rowNoun = isEvents ? "event" : "session";

        if (!operation.Responses.TryGetValue("200", out var ok) || ok is null)
        {
            ok = new OpenApiResponse();
            operation.Responses["200"] = ok;
        }

        ok.Description =
            $"NDJSON stream — one {rowNoun} object per line (`\\n`-terminated, **not** a JSON array), " +
            "up to `limit` rows. The example shows a single line.";

        // .Produces<T>(…, "application/x-ndjson") already registered the media type + row schema;
        // create it defensively in case the producer metadata ever changes, then attach the
        // realistic per-line example (overriding the placeholder one inferred from the schema).
        ok.Content ??= new Dictionary<string, OpenApiMediaType>();
        if (!ok.Content.TryGetValue(NdjsonContentType, out var media) || media is null)
        {
            media = new OpenApiMediaType();
            ok.Content[NdjsonContentType] = media;
        }
        media.Example = isEvents ? EventsExample() : SessionsExample();
    }
}
