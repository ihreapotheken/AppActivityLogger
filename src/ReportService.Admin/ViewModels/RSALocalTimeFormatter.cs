using System.Globalization;
using Microsoft.AspNetCore.Html;

namespace ReportService.Admin.ViewModels;

/// <summary>
/// Renders a UTC timestamp as a <c>&lt;time datetime="..."&gt;</c> element. The wwwroot script
/// <c>local-time.js</c> rewrites each element's text to the browser's local-time format on
/// page load; with JS disabled the UTC fallback inside the tag stays visible.
/// </summary>
public static class RSALocalTimeFormatter
{
    public static IHtmlContent AsLocalTime(this DateTimeOffset dt)
    {
        var iso = dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var fallback = dt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return new HtmlString($"<time datetime=\"{iso}\" class=\"local-time\">{fallback}</time>");
    }

    public static IHtmlContent AsLocalTime(this DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        return new DateTimeOffset(utc, TimeSpan.Zero).AsLocalTime();
    }

    public static IHtmlContent AsLocalTimeOrDash(this DateTimeOffset? dt)
        => dt is null ? new HtmlString("<span class=\"muted\">—</span>") : dt.Value.AsLocalTime();
}
