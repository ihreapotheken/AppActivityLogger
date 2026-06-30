using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ReportService.Audit;
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
    private const int MaxRetentionDays = 3650; // 10y — generous upper bound for the retention setting.

    // Device-identification signals. Any custom "X-DeepLink-*" request header is captured (e.g.
    // X-DeepLink-Screen: 1920x1080, X-DeepLink-Device-Time, X-DeepLink-Timezone), plus this curated
    // set of standard fingerprint headers that ride along on a plain browser navigation (no JS).
    private const string SignalHeaderPrefix = "X-DeepLink-";
    private static readonly (string Header, string Signal)[] StandardSignalHeaders =
    {
        ("Accept-Language", "language"),
        ("Sec-CH-UA", "browser"),
        ("Sec-CH-UA-Platform", "platform"),
        ("Sec-CH-UA-Platform-Version", "platform_version"),
        ("Sec-CH-UA-Mobile", "mobile"),
        ("Sec-CH-Viewport-Width", "viewport_width"),
        ("Sec-CH-Viewport-Height", "viewport_height"),
        ("Sec-CH-Width", "width"),
        ("Sec-CH-DPR", "dpr"),
        ("Sec-CH-Device-Memory", "device_memory"),
    };

    public static IEndpointRouteBuilder MapDeepLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(HttpVerb.Post, "/api/v2/deeplinks/clicks",
                (HttpContext ctx, RSDeepLinkClickRequest? body, RSCIDeferredDeepLinkStore store, RSCDeepLinkOptions opts, CancellationToken ct)
                    => RecordClickAsync(ctx, body, store, opts, ct))
            .Apply(
                EndpointModifier.RequireAuth,
                EndpointModifier.AcceptHeaderFilter,
                EndpointModifier.DisableAntiforgery)
            .WithTags("DeepLinks")
            .WithName("RecordDeepLinkClick")
            .WithSummary("Record a website visit for deferred deep linking")
            .WithDescription(
                "JSON body shaped as RSDeepLinkClickRequest. Records the visitor's IP and the page URL they were on, resolving the visit against the enabled deep-link definitions (longest matching page pattern wins). The IP defaults to the caller's connection address (honouring the configured forwarded-headers) but may be overridden in the body for trusted server-side callers that forward an end-user's address. Optional `params` (a string→string object of attribution/campaign query parameters) are captured with the click and forwarded onto the redirect address; they are capped at DeepLinks:MaxQueryParams entries and DeepLinks:MaxQueryParamLength characters each (excess dropped/truncated, never rejected). Optional `signals` (device-identification: screen dimensions, browser, timezone, device time, …) are captured too — merged with any `X-DeepLink-*` and standard client-hint request headers — and stored for extra match confidence (same caps). Returns 201 with whether the visit matched a configured link and, if so, the redirect address (with the params appended).")
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
                "Looks for a recent recorded click from the caller's IP (override with the `ip` query parameter) that resolved to an enabled deep link inside the configured match window. Returns 200 with `matched=true`, the originating page, the captured `params` object, the device-identification `signals` (screen/browser/timezone/…), and the redirect address (with those params appended) when one is found, otherwise `matched=false`. Unless `claim=false` is passed the matched click is consumed so it is handed out at most once — the typical app-first-launch call should claim.")
            .Produces<RSDeepLinkMatchResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status406NotAcceptable);

        // Hosted smart link. The single URL an operator hands out (in an ad, SMS, email): a visitor
        // opens it in a browser, the service records their IP + the page they came from, then
        // 302-redirects to the configured redirect address. Anonymous on purpose — a browser cannot
        // carry the API key — so it is rate-limited per IP by the global limiter like every route.
        app.MapGet("/dl/{slug}", RedirectSmartLinkAsync)
            .AllowAnonymous()
            .WithTags("DeepLinks")
            .WithName("DeepLinkSmartRedirect")
            .WithSummary("Hosted smart link: record the visitor's IP, then redirect")
            .WithDescription(
                "Public, browser-facing redirect for a configured deep link. Records a click for the visitor's IP (with the referring page and user-agent) bound to {slug}, then issues a 302 to the link's redirect address. Any query parameters on the smart-link URL (e.g. /dl/spring?utm_source=fb) are captured with the click and appended to the redirect — capped at DeepLinks:MaxQueryParams entries and DeepLinks:MaxQueryParamLength characters each, excess dropped so the redirect is never broken. Custom `X-DeepLink-*` headers and standard client-hint headers (Accept-Language, Sec-CH-UA*, Sec-CH-Viewport-*, …) are captured as device-identification signals alongside the IP. The recorded click is what the app's later GET /api/v2/deeplinks/match correlates against. Returns 404 for an unknown or disabled slug. Anonymous — no apiKey required.")
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status404NotFound);

        // Click-retention configuration. Admin-role API key only (same policy as key management):
        // changing how long the captured-IP click stream is retained is an operator/privacy decision,
        // not something a plain ingest key should touch. The admin console exposes the same setting
        // behind its cookie policy on the /DeepLinks page.
        var retention = app.MapGroup("/api/v2/deeplinks/click-retention")
            .RequireAuthorization(RSEndpointConventions.ApiKeyAdminPolicy)
            .WithTags("DeepLinks");

        retention.MapGet("", async (RSCIDeferredDeepLinkStore store, RSCDeepLinkOptions opts, CancellationToken ct) =>
            {
                var persisted = await store.GetClickRetentionDaysAsync(ct).ConfigureAwait(false);
                return Results.Ok(new RSDeepLinkRetentionResponse(persisted ?? opts.ClickRetentionDays, persisted is not null));
            })
            .WithName("GetDeepLinkClickRetention")
            .WithSummary("Get the deep-link click-retention period (admin key)")
            .WithDescription("Returns { retentionDays, overridden }. retentionDays is the effective value — the persisted operator override if set, otherwise the configured default (DeepLinks:ClickRetentionDays). Requires an admin-role API key.")
            .Produces<RSDeepLinkRetentionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        retention.MapPut("", async (RSDeepLinkRetentionRequest? body, HttpContext ctx,
                RSCIDeferredDeepLinkStore store, RSCIAuditLog audit, CancellationToken ct) =>
            {
                var days = body?.RetentionDays ?? 0;
                if (days < 1 || days > MaxRetentionDays)
                {
                    await audit.RecordAsync(ctx, "deeplink.retention.set", success: false, details: $"days={days} (out of range)").ConfigureAwait(false);
                    return Results.Problem($"retentionDays must be between 1 and {MaxRetentionDays}", statusCode: StatusCodes.Status400BadRequest);
                }
                await store.SetClickRetentionDaysAsync(days, ct).ConfigureAwait(false);
                await audit.RecordAsync(ctx, "deeplink.retention.set", success: true, details: $"days={days}").ConfigureAwait(false);
                return Results.Ok(new RSDeepLinkRetentionResponse(days, true));
            })
            .DisableAntiforgery()
            .WithName("SetDeepLinkClickRetention")
            .WithSummary("Set the deep-link click-retention period (admin key)")
            .WithDescription("Body { retentionDays }. Persists the retention period (1..3650 days); the background sweep deletes recorded clicks older than this on its next pass. Requires an admin-role API key. Writes an audit row (deeplink.retention.set).")
            .Accepts<RSDeepLinkRetentionRequest>("application/json")
            .Produces<RSDeepLinkRetentionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> RedirectSmartLinkAsync(
        HttpContext ctx, string slug, RSCIDeferredDeepLinkStore store, RSCDeepLinkOptions opts, CancellationToken ct)
    {
        var s = (slug ?? string.Empty).Trim();
        var link = string.IsNullOrEmpty(s) ? null : await store.GetLinkBySlugAsync(s, ct).ConfigureAwait(false);
        if (link is null || !link.Enabled)
            return Results.NotFound();

        // Capture the smart link's query parameters (utm_*, promo, …), bounded by the configured cap.
        // Computed before the IP check so the redirect carries them even when the IP is unavailable.
        var queryParams = RSCDeepLinkQuery.Normalize(
            ctx.Request.Query.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value.ToString())),
            opts.MaxQueryParams, opts.MaxQueryParamLength);
        var signals = CaptureSignals(ctx, bodySignals: null, opts);

        // Capture is best-effort: a missing IP (or a transient store hiccup) must never stop the
        // visitor from being forwarded on. Record, then always redirect.
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(ip))
        {
            // Page reference = where the visitor came from (Referer), else the smart-link URL itself
            // so there is always a recorded page.
            var referer = ctx.Request.Headers.Referer.ToString();
            var pageUrl = string.IsNullOrWhiteSpace(referer)
                ? $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path}{ctx.Request.QueryString}"
                : referer;
            if (pageUrl.Length > MaxPageUrlLength) pageUrl = pageUrl[..MaxPageUrlLength];

            var userAgent = ctx.Request.Headers.UserAgent.ToString();
            if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;
            else if (userAgent.Length > MaxUserAgentLength) userAgent = userAgent[..MaxUserAgentLength];

            await store.RecordClickForLinkAsync(link, ip, pageUrl, userAgent, queryParams, signals, DateTimeOffset.UtcNow, ct)
                .ConfigureAwait(false);
        }

        // Forward the captured params onto the redirect so attribution reaches the destination.
        return Results.Redirect(RSCDeepLinkQuery.Append(link.RedirectUrl, queryParams));
    }

    private static async Task<IResult> RecordClickAsync(
        HttpContext ctx, RSDeepLinkClickRequest? body, RSCIDeferredDeepLinkStore store,
        RSCDeepLinkOptions opts, CancellationToken ct)
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

        var queryParams = RSCDeepLinkQuery.Normalize(
            body?.Params?.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)),
            opts.MaxQueryParams, opts.MaxQueryParamLength);
        var signals = CaptureSignals(ctx, body?.Signals, opts);

        var click = await store.RecordClickAsync(ip, pageUrl, userAgent, queryParams, signals, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);

        // Echo back the redirect with the params appended when the visit matched a link.
        var redirect = click.RedirectUrl is null ? null : RSCDeepLinkQuery.Append(click.RedirectUrl, click.QueryParams);
        return Results.Json(
            new RSDeepLinkClickResponse(
                Recorded: true,
                Matched: click.LinkSlug is not null,
                Slug: click.LinkSlug,
                RedirectUrl: redirect),
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
            return Results.Json(new RSDeepLinkMatchResponse(false, null, null, null, null, null, null, null));

        return Results.Json(new RSDeepLinkMatchResponse(
            Matched: true,
            Slug: match.Slug,
            Name: match.Name,
            // Redirect carries the captured attribution params through to the app.
            RedirectUrl: RSCDeepLinkQuery.Append(match.RedirectUrl, match.QueryParams),
            PageUrl: match.PageUrl,
            ClickedAt: match.ClickedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Params: match.QueryParams,
            Signals: match.Signals));
    }

    /// <summary>
    /// Builds the device-identification signal map for a request: optional body-provided signals
    /// (highest precedence), then any custom <c>X-DeepLink-*</c> headers, then a curated set of
    /// standard fingerprint headers. The combined set is normalised + capped by the same limits as
    /// query parameters (<c>MaxQueryParams</c> / <c>MaxQueryParamLength</c>).
    /// </summary>
    private static IReadOnlyDictionary<string, string> CaptureSignals(
        HttpContext ctx, IReadOnlyDictionary<string, string>? bodySignals, RSCDeepLinkOptions opts)
    {
        var pairs = new List<KeyValuePair<string, string?>>();

        if (bodySignals is not null)
            foreach (var kv in bodySignals) pairs.Add(new(kv.Key, kv.Value));

        foreach (var h in ctx.Request.Headers)
        {
            if (h.Key.Length > SignalHeaderPrefix.Length &&
                h.Key.StartsWith(SignalHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = h.Key[SignalHeaderPrefix.Length..].Replace('-', '_').ToLowerInvariant();
                pairs.Add(new(name, h.Value.ToString()));
            }
        }

        foreach (var (header, signal) in StandardSignalHeaders)
        {
            var value = ctx.Request.Headers[header].ToString();
            if (!string.IsNullOrEmpty(value)) pairs.Add(new(signal, value));
        }

        return RSCDeepLinkQuery.Normalize(pairs, opts.MaxQueryParams, opts.MaxQueryParamLength);
    }
}

/// <summary>Request body for <c>POST /api/v2/deeplinks/clicks</c>. <see cref="Ip"/> is optional —
/// when omitted the caller's connection address is used. <see cref="Params"/> is an optional
/// attribution/campaign map captured with the click and forwarded onto the redirect; <see cref="Signals"/>
/// is an optional device-identification map (screen size, browser, timezone, …) merged with any
/// <c>X-DeepLink-*</c>/fingerprint request headers. Both are capped by
/// <c>DeepLinks:MaxQueryParams</c> / <c>DeepLinks:MaxQueryParamLength</c>.</summary>
public sealed record RSDeepLinkClickRequest(
    string? PageUrl, string? Ip, Dictionary<string, string>? Params, Dictionary<string, string>? Signals);

/// <summary>Response for a recorded click: whether it resolved to a configured link and, if so, the
/// resolved slug + redirect address (with any captured params appended).</summary>
public sealed record RSDeepLinkClickResponse(bool Recorded, bool Matched, string? Slug, string? RedirectUrl);

/// <summary>Response for a deep-link match query. When <see cref="Matched"/> is false every other
/// field is null. <see cref="RedirectUrl"/> already has <see cref="Params"/> appended;
/// <see cref="Signals"/> are the captured device-identification signals for extra match confidence.</summary>
public sealed record RSDeepLinkMatchResponse(
    bool Matched, string? Slug, string? Name, string? RedirectUrl, string? PageUrl, string? ClickedAt,
    IReadOnlyDictionary<string, string>? Params, IReadOnlyDictionary<string, string>? Signals);

/// <summary>Request body for setting the click-retention period.</summary>
public sealed record RSDeepLinkRetentionRequest(int? RetentionDays);

/// <summary>Click-retention period in days. <see cref="Overridden"/> is true when an operator value
/// is persisted, false when the response reflects the configured default.</summary>
public sealed record RSDeepLinkRetentionResponse(int RetentionDays, bool Overridden);
