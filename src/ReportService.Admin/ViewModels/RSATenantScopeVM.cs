using ReportService.Analytics;

namespace ReportService.Admin.ViewModels;

/// <summary>
/// Helpers for turning the bound <c>?client</c>/<c>?app</c>/<c>?platform</c> query axes (set by the
/// header tenant switcher via the <c>rsc_scope</c> cookie + scope-fill middleware) into a canonical
/// <see cref="RSCAnalyticsScope"/>. The analytics pages no longer carry their own client/app dropdown
/// — the single top-left switcher owns the selection — so only the scope-building helpers remain.
/// </summary>
public static class RSATenantScopes
{
    /// <summary>Trim + lowercase; null/blank ⇒ null ("all" for that axis).</summary>
    public static string? Norm(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>Build a scope from the bound axes (any of which may be null/blank = all). Environment
    /// is folded into the app slug, so the scope's environment axis is always "all" (null).</summary>
    public static RSCAnalyticsScope Build(string? app, string? client, string? platform) =>
        new(Norm(app), null, Norm(client), Norm(platform));
}
