using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ReportService.DeepLinks;
using ReportService.Options;

namespace ReportService.Endpoints;

/// <summary>
/// Deferred deep-linking routes. A website records a visitor's IP + the page they were on
/// (<c>POST /api/v2/deeplinks/clicks</c>); the mobile app, on first launch, asks whether its IP
/// matches a recent recorded click and, if so, which deep link to open
/// (<c>GET /api/v2/deeplinks/match</c>). Both opt into the same API-key auth, Accept-header filter,
/// and antiforgery posture as the analytics ingestion routes — the link definitions (page pattern →
/// redirect address) are managed by operators on the admin <c>/DeepLinks</c> page.
/// </summary>
public static class RSDeepLinkEndpoints
{
    private const int MaxPageUrlLength = 2048;
    private const int MaxIpLength = 64;
    private const int MaxUserAgentLength = 1024;

    public static IEndpointRouteBuilder MapDeepLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(HttpVerb.Post, "/api/v2/deeplinks/clicks",
                (HttpContext ctx, RSDeepLinkClickRequest? body, RSCIDeferredDeepLinkStore store, CancellationToken ct)
                    => RecordClickAsync(ctx, body, store, ct))
            .Apply(
                EndpointModifier.RequireAuth,
                EndpointModifier.AcceptHeaderFilter,
                EndpointModifier.DisableAntiforgery)
            .WithTags("DeepLinks")
            .WithName("RecordDeepLinkClick")
            .WithSummary("Record a website visit for deferred deep linking")
            .WithDescription(
                "JSON body shaped as RSDeepLinkClickRequest. Records the visitor's IP and the page URL they were on, resolving the visit against the enabled deep-link definitions (longest matching page pattern wins). The IP defaults to the caller's connection address (honouring the configured forwarded-headers) but may be overridden in the body for trusted server-side callers that forward an end-user's address. Returns 201 with whether the visit matched a configured link and, if so, the redirect address.")
            .Accepts<RSDeepLinkClickRequest>("application/json")
            .Produces<RSDeepLinkClickResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status406NotAcceptable)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        app.Map(HttpVerb.Get, "/api/v2/deeplinks/match",
                (HttpContext ctx, RSCIDeferredDeepLinkStore store, RSCDeepLinkOptions opts,
                 string? ip, bool? claim, CancellationToken ct)
                    => MatchAsync(ctx, store, opts, ip, claim, ct))
            .Apply(
                EndpointModifier.RequireAuth,
                EndpointModifier.AcceptHeaderFilter)
            .WithTags("DeepLinks")
            .WithName("MatchDeepLinkForIp")
            .WithSummary("Resolve a deferred deep link for the caller's IP")
            .WithDescription(
                "Looks for a recent recorded click from the caller's IP (override with the `ip` query parameter) that resolved to an enabled deep link inside the configured match window. Returns 200 with `matched=true` and the redirect address + originating page when one is found, otherwise `matched=false`. Unless `claim=false` is passed the matched click is consumed so it is handed out at most once — the typical app-first-launch call should claim.")
            .Produces<RSDeepLinkMatchResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status406NotAcceptable);

        return app;
    }

    private static async Task<IResult> RecordClickAsync(
        HttpContext ctx, RSDeepLinkClickRequest? body, RSCIDeferredDeepLinkStore store, CancellationToken ct)
    {
        var pageUrl = body?.PageUrl?.Trim();
        if (string.IsNullOrEmpty(pageUrl))
            return Results.Problem("pageUrl is required", statusCode: StatusCodes.Status400BadRequest);
        if (pageUrl.Length > MaxPageUrlLength)
            return Results.Problem("pageUrl exceeds the maximum length", statusCode: StatusCodes.Status400BadRequest);

        // Prefer an explicit, trusted, caller-forwarded address (a website backend reporting on
        // behalf of an end user); otherwise fall back to the connection's remote address, which the
        // forwarded-headers middleware has already resolved to the real client when behind a proxy.
        var ip = body?.Ip?.Trim();
        if (string.IsNullOrEmpty(ip))
            ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (string.IsNullOrEmpty(ip))
            return Results.Problem("could not determine client IP", statusCode: StatusCodes.Status400BadRequest);
        if (ip.Length > MaxIpLength)
            return Results.Problem("ip exceeds the maximum length", statusCode: StatusCodes.Status400BadRequest);

        var userAgent = ctx.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;
        else if (userAgent.Length > MaxUserAgentLength) userAgent = userAgent[..MaxUserAgentLength];

        var click = await store.RecordClickAsync(ip, pageUrl, userAgent, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);

        return Results.Json(
            new RSDeepLinkClickResponse(
                Recorded: true,
                Matched: click.LinkSlug is not null,
                Slug: click.LinkSlug,
                RedirectUrl: click.RedirectUrl),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> MatchAsync(
        HttpContext ctx, RSCIDeferredDeepLinkStore store, RSCDeepLinkOptions opts,
        string? ip, bool? claim, CancellationToken ct)
    {
        var effectiveIp = string.IsNullOrWhiteSpace(ip) ? ctx.Connection.RemoteIpAddress?.ToString() : ip.Trim();
        if (string.IsNullOrEmpty(effectiveIp))
            return Results.Problem("could not determine client IP", statusCode: StatusCodes.Status400BadRequest);

        var window = TimeSpan.FromHours(Math.Max(1, opts.MatchWindowHours));
        var match = await store.FindMatchForIpAsync(effectiveIp, window, claim ?? true, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);

        if (match is null)
            return Results.Json(new RSDeepLinkMatchResponse(false, null, null, null, null, null));

        return Results.Json(new RSDeepLinkMatchResponse(
            Matched: true,
            Slug: match.Slug,
            Name: match.Name,
            RedirectUrl: match.RedirectUrl,
            PageUrl: match.PageUrl,
            ClickedAt: match.ClickedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
    }
}

/// <summary>Request body for <c>POST /api/v2/deeplinks/clicks</c>. <see cref="Ip"/> is optional —
/// when omitted the caller's connection address is used.</summary>
public sealed record RSDeepLinkClickRequest(string? PageUrl, string? Ip);

/// <summary>Response for a recorded click: whether it resolved to a configured link and, if so, the
/// resolved slug + redirect address.</summary>
public sealed record RSDeepLinkClickResponse(bool Recorded, bool Matched, string? Slug, string? RedirectUrl);

/// <summary>Response for a deep-link match query. When <see cref="Matched"/> is false every other
/// field is null.</summary>
public sealed record RSDeepLinkMatchResponse(
    bool Matched, string? Slug, string? Name, string? RedirectUrl, string? PageUrl, string? ClickedAt);
