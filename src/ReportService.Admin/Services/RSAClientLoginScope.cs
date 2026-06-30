using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace ReportService.Admin.Services;

/// <summary>
/// Confines a <b>client login</b> (a cookie principal carrying a <c>client_id</c> claim — see
/// <c>/ClientLogin</c>) to its own dashboards. Operators (cookie principals with no client claim) are
/// unaffected. Two jobs, applied by the pipeline middleware after authorization:
/// <list type="bullet">
///   <item>page allow-list — a client may only reach the per-client analytics dashboards; any other
///         admin page redirects to <c>/Analytics</c>;</item>
///   <item>scope pinning — the <c>client</c> query value is forced to the login's own client on every
///         dashboard request, so a client can't read another client's data by editing the URL.</item>
/// </list>
/// </summary>
public static class RSAClientLoginScope
{
    // The dashboards a client login may view. Each scopes its query by the client axis, so pinning
    // ?client= makes them safe. Operator-only surfaces (Health's cross-tenant DLQ samples, the
    // catalog/key admin pages, problem reports, exports) are deliberately absent.
    private static readonly HashSet<string> AllowedPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Analytics", "/AnalyticsSales", "/AnalyticsRetention", "/AnalyticsFunnels",
        "/AnalyticsSessions", "/AnalyticsSession", "/AnalyticsEvents",
        "/Scope", "/ClientLogin", "/Logout", "/Error", "/FeatureUnavailable",
    };

    /// <summary>True if a client login is allowed to reach <paramref name="path"/> (case-insensitive,
    /// trailing slash tolerated). Non-page infrastructure paths are handled by the caller.</summary>
    public static bool IsAllowedPage(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return false; // "/" → send clients to /Analytics
        return AllowedPages.Contains(trimmed);
    }

    /// <summary>Overwrites the request's <c>client</c> query value with the login's own client slug so
    /// the dashboard query can't be re-scoped to another tenant.</summary>
    public static void ForceClientScope(HttpContext ctx, string clientSlug)
    {
        var parsed = QueryHelpers.ParseQuery(ctx.Request.QueryString.Value ?? string.Empty);
        var qs = QueryString.Empty;
        foreach (var kv in parsed)
        {
            if (string.Equals(kv.Key, "client", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var v in kv.Value) qs = qs.Add(kv.Key, v ?? string.Empty);
        }
        qs = qs.Add("client", clientSlug);
        ctx.Request.QueryString = qs;
    }
}
