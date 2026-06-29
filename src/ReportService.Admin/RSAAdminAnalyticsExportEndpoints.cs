using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using ReportService.Analytics;
using ReportService.Models;

namespace ReportService.Admin;

/// <summary>
/// Admin-only NDJSON export of analytics events + sessions. Streams one row per line so a CSV
/// converter or jq pipeline can chew through the result without buffering the whole window in
/// memory. Filtered identically to <c>/AnalyticsEvents</c>; auth is the same cookie policy as
/// every other Razor page (the global fallback policy enforces it).
/// </summary>
internal static class RSAAdminAnalyticsExportEndpoints
{
    /// <summary>Hard cap on rows per response — protects the operator from accidentally hitting
    /// "export all of last quarter" on a busy DB. Operators paginate via <c>until</c>.</summary>
    private const int MaxExportRows = 50_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IEndpointRouteBuilder MapAdminAnalyticsExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/api/analytics/events.ndjson", async (
            HttpContext ctx, RSCIAnalyticsStore store, ILoggerFactory loggerFactory,
            string? platform, string? type, string? name, string? screen,
            string? session, DateTime? from, DateTime? until,
            int? limit,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("ReportService.Admin.AnalyticsExport");
            var filter = new RSCAnalyticsEventFilter(
                Platform: platform,
                Type: type,
                Name: name,
                Screen: screen,
                SessionId: session,
                From:  from  is { } f ? new DateTimeOffset(DateTime.SpecifyKind(f,  DateTimeKind.Utc)) : null,
                Until: until is { } u ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)) : null,
                Limit: Math.Clamp(limit ?? 5000, 1, MaxExportRows),
                Offset: 0);

            // The query runs BEFORE any byte of the response is committed, so a failure here can
            // still surface as a clean problem+json with the right status code.
            RSCAnalyticsEventPage page;
            try
            {
                page = await store.SearchEventsAsync(filter, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Analytics events export query failed before streaming began");
                await WriteQueryFailureAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            ctx.Response.Headers.Append("Content-Disposition", "attachment; filename=\"analytics-events.ndjson\"");

            var newline = Encoding.UTF8.GetBytes("\n");
            var written = 0;
            try
            {
                foreach (var row in page.Rows)
                {
                    var payload = new RSAAnalyticsEventExportRow(
                        row.EventId,
                        row.Platform,
                        row.SessionId,
                        row.AnonymousIdHash,
                        row.Sequence,
                        row.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        row.Type,
                        row.Name,
                        row.Screen,
                        row.Feature,
                        row.DurationMs);
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, payload, JsonOptions, ct).ConfigureAwait(false);
                    await ctx.Response.Body.WriteAsync(newline, ct).ConfigureAwait(false);
                    written++;
                }
                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await HandleStreamFailureAsync(ctx, log, ex, "events", written, page.Rows.Count, ct)
                    .ConfigureAwait(false);
            }
            return;
        })
        .WithTags("Analytics")
        .WithName("ExportAnalyticsEventsNdjson")
        .WithSummary("Stream analytics events as NDJSON (admin-cookie auth)")
        .Produces<RSAAnalyticsEventExportRow>(StatusCodes.Status200OK, "application/x-ndjson")
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapGet("/admin/api/analytics/sessions.ndjson", async (
            HttpContext ctx, RSCIAnalyticsStore store, ILoggerFactory loggerFactory,
            string? platform, int? limit,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("ReportService.Admin.AnalyticsExport");
            var capped = Math.Clamp(limit ?? 5000, 1, MaxExportRows);

            IReadOnlyList<RSCAnalyticsSessionRow> rows;
            try
            {
                rows = await store.ListSessionsAsync(platform, capped, offset: 0, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Analytics sessions export query failed before streaming began");
                await WriteQueryFailureAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            ctx.Response.Headers.Append("Content-Disposition", "attachment; filename=\"analytics-sessions.ndjson\"");

            var newline = Encoding.UTF8.GetBytes("\n");
            var written = 0;
            try
            {
                foreach (var s in rows)
                {
                    var payload = new RSAAnalyticsSessionExportRow(
                        s.Platform,
                        s.SessionId,
                        s.AnonymousIdHash,
                        s.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        s.LastSeenAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        s.EventCount,
                        s.ScreenCount);
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, payload, JsonOptions, ct).ConfigureAwait(false);
                    await ctx.Response.Body.WriteAsync(newline, ct).ConfigureAwait(false);
                    written++;
                }
                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await HandleStreamFailureAsync(ctx, log, ex, "sessions", written, rows.Count, ct)
                    .ConfigureAwait(false);
            }
            return;
        })
        .WithTags("Analytics")
        .WithName("ExportAnalyticsSessionsNdjson")
        .WithSummary("Stream analytics sessions as NDJSON (admin-cookie auth)")
        .Produces<RSAAnalyticsSessionExportRow>(StatusCodes.Status200OK, "application/x-ndjson")
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }

    /// <summary>
    /// The query failed before any header/byte was committed, so we can still emit a clean
    /// problem+json with a real error status. Guards against the (unexpected) case where the
    /// response has already started by simply returning — there is nothing safe left to do.
    /// </summary>
    private static async Task WriteQueryFailureAsync(HttpContext ctx, CancellationToken ct)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/problem+json; charset=utf-8";
        var problem = new
        {
            type = "about:blank",
            title = "Export failed",
            status = StatusCodes.Status500InternalServerError,
            detail = "The analytics export could not be produced.",
        };
        await JsonSerializer.SerializeAsync(ctx.Response.Body, problem, JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A failure mid-stream. If nothing has been written yet (and the response hasn't committed)
    /// we can still return a problem response. Once the 200 + attachment headers are on the wire
    /// the status can no longer change, so the only honest signal is to abort the connection so
    /// the client sees a broken transfer instead of a silently-truncated "complete" file — plus a
    /// Warning log recording how many rows of the expected total actually made it out.
    /// </summary>
    private static async Task HandleStreamFailureAsync(
        HttpContext ctx, ILogger log, Exception ex, string export, int written, int expected, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            // Client went away — not an error worth alarming on.
            log.LogDebug("Analytics {Export} export cancelled by client after {Written}/{Expected} rows",
                export, written, expected);
            return;
        }

        if (!ctx.Response.HasStarted)
        {
            log.LogError(ex, "Analytics {Export} export failed before any row was flushed", export);
            await WriteQueryFailureAsync(ctx, ct).ConfigureAwait(false);
            return;
        }

        log.LogWarning(ex,
            "Analytics {Export} export failed mid-stream after {Written}/{Expected} rows; aborting the connection so the truncated download is not mistaken for a complete file",
            export, written, expected);
        ctx.Abort();
    }
}

/// <summary>One line of the <c>events.ndjson</c> export — the wire shape of a single analytics event
/// row. Doubles as the OpenAPI response schema (via <c>.Produces&lt;&gt;</c>) so the documented shape
/// and the serialized payload can never drift apart. <see cref="OccurredAt"/> is a pre-formatted
/// ISO-8601 UTC string; identifiers are emitted only as the peppered <see cref="AnonymousIdHash"/>.</summary>
public sealed record RSAAnalyticsEventExportRow(
    string EventId,
    string Platform,
    string? SessionId,
    string? AnonymousIdHash,
    long Sequence,
    string OccurredAt,
    string Type,
    string Name,
    string? Screen,
    string? Feature,
    long? DurationMs);

/// <summary>One line of the <c>sessions.ndjson</c> export — the wire shape of a single aggregated
/// session row. <see cref="StartedAt"/>/<see cref="LastSeenAt"/> are pre-formatted ISO-8601 UTC
/// strings; the user is identified only by the peppered <see cref="AnonymousIdHash"/>.</summary>
public sealed record RSAAnalyticsSessionExportRow(
    string Platform,
    string SessionId,
    string? AnonymousIdHash,
    string StartedAt,
    string LastSeenAt,
    long EventCount,
    long ScreenCount);
