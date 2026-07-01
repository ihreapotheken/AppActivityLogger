using Microsoft.AspNetCore.Http;

namespace ReportService.Admin.Services;

/// <summary>
/// The persistent global tenant scope, carried in a cookie (<c>rsc_scope</c>) so a selected
/// client/app/environment sticks across every admin page rather than living only in one page's
/// query string. The header switcher writes it (via <c>/Scope</c>); the scope-fill middleware reads
/// it and fills any missing <c>?client/?app/?env</c> on a page request so the dashboards bind it.
/// Value format is <c>client|app|env</c> (each part optional/empty = "all" for that axis).
/// </summary>
public static class RSAScopeCookie
{
    public const string Name = "rsc_scope";

    /// <summary>
    /// Set once at startup from <c>Admin:EmbedAllowedAncestors</c>. When true the scope cookie is
    /// written <c>SameSite=None; Secure; Partitioned</c> so it survives the cross-site Confluence
    /// iframe; otherwise it stays <c>SameSite=Strict</c>. (A static is enough — this is a process-wide
    /// build/deploy setting, not a per-request value.)
    /// </summary>
    public static bool CrossSiteEmbed { get; set; }

    public readonly record struct Scope(string? Client, string? App, string? Env);

    private static string? Clean(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToLowerInvariant();

    public static Scope Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(Name, out var raw) || string.IsNullOrWhiteSpace(raw))
            return default;
        var parts = raw.Split('|');
        return new Scope(
            Client: parts.Length > 0 ? Clean(parts[0]) : null,
            App: parts.Length > 1 ? Clean(parts[1]) : null,
            Env: parts.Length > 2 ? Clean(parts[2]) : null);
    }

    public static void Write(HttpResponse response, string? client, string? app, string? env)
    {
        var value = $"{Clean(client)}|{Clean(app)}|{Clean(env)}";
        var options = new CookieOptions
        {
            HttpOnly = true,
            SameSite = CrossSiteEmbed ? SameSiteMode.None : SameSiteMode.Strict,
            Secure = CrossSiteEmbed,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(30),
            Path = "/",
        };
        if (CrossSiteEmbed)
            options.Extensions.Add("Partitioned");
        response.Cookies.Append(Name, value, options);
    }
}
