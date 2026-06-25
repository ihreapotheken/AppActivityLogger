using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
            HttpContext ctx, RSCIAnalyticsStore store,
            string? platform, string? type, string? name, string? screen,
            string? session, DateTime? from, DateTime? until,
            int? limit,
            CancellationToken ct) =>
        {
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

            var page = await store.SearchEventsAsync(filter, ct).ConfigureAwait(false);

            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            ctx.Response.Headers.Append("Content-Disposition", "attachment; filename=\"analytics-events.ndjson\"");

            var newline = Encoding.UTF8.GetBytes("\n");
            foreach (var row in page.Rows)
            {
                var payload = new
                {
                    row.EventId,
                    row.Platform,
                    row.SessionId,
                    row.AnonymousIdHash,
                    row.Sequence,
                    OccurredAt = row.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    row.Type,
                    row.Name,
                    row.Screen,
                    row.Feature,
                    row.DurationMs,
                };
                await JsonSerializer.SerializeAsync(ctx.Response.Body, payload, JsonOptions, ct).ConfigureAwait(false);
                await ctx.Response.Body.WriteAsync(newline, ct).ConfigureAwait(false);
            }
        })
        .WithTags("Analytics")
        .WithName("ExportAnalyticsEventsNdjson")
        .WithSummary("Stream analytics events as NDJSON (admin-cookie auth)");

        app.MapGet("/admin/api/analytics/sessions.ndjson", async (
            HttpContext ctx, RSCIAnalyticsStore store,
            string? platform, int? limit,
            CancellationToken ct) =>
        {
            var capped = Math.Clamp(limit ?? 5000, 1, MaxExportRows);
            var rows = await store.ListSessionsAsync(platform, capped, offset: 0, ct).ConfigureAwait(false);

            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            ctx.Response.Headers.Append("Content-Disposition", "attachment; filename=\"analytics-sessions.ndjson\"");

            var newline = Encoding.UTF8.GetBytes("\n");
            foreach (var s in rows)
            {
                var payload = new
                {
                    s.Platform,
                    s.SessionId,
                    s.AnonymousIdHash,
                    StartedAt = s.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    LastSeenAt = s.LastSeenAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    s.EventCount,
                    s.ScreenCount,
                };
                await JsonSerializer.SerializeAsync(ctx.Response.Body, payload, JsonOptions, ct).ConfigureAwait(false);
                await ctx.Response.Body.WriteAsync(newline, ct).ConfigureAwait(false);
            }
        })
        .WithTags("Analytics")
        .WithName("ExportAnalyticsSessionsNdjson")
        .WithSummary("Stream analytics sessions as NDJSON (admin-cookie auth)");

        return app;
    }
}
