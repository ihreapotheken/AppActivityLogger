using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace ReportService.Admin.Models;

/// <summary>
/// Bound from the query string by the Reports page. Encapsulates the nine filter fields the
/// listing accepts so the page model exposes one cohesive value instead of nine
/// <c>[BindProperty]</c> properties, and so a single <see cref="ToQueryString"/> serialises the
/// active filter back into pagination links.
/// </summary>
public sealed class RSAReportsFilterInput
{
    [FromQuery(Name = "platform")] public string? Platform { get; set; }
    [FromQuery(Name = "q")] public string? Q { get; set; }
    [FromQuery(Name = "pharmacyId")] public string? PharmacyId { get; set; }
    [FromQuery(Name = "userId")] public string? UserId { get; set; }
    [FromQuery(Name = "email")] public string? Email { get; set; }
    [FromQuery(Name = "phone")] public string? Phone { get; set; }
    [FromQuery(Name = "appVersion")] public string? AppVersion { get; set; }
    [FromQuery(Name = "hasAttachment")] public bool? HasAttachment { get; set; }
    [FromQuery(Name = "channel")] public string? Channel { get; set; }
    [FromQuery(Name = "topFrame")] public string? TopFrame { get; set; }
    [FromQuery(Name = "from")] public DateTime? From { get; set; }
    [FromQuery(Name = "until")] public DateTime? Until { get; set; }
    [FromQuery(Name = "page")] public int Page { get; set; } = 1;

    /// <summary>Rebuilds the query string with a different page number, preserving every active filter.</summary>
    public string ToQueryString(int page)
    {
        var sb = new StringBuilder("?");
        Append(sb, "platform", Platform);
        Append(sb, "q", Q);
        Append(sb, "pharmacyId", PharmacyId);
        Append(sb, "userId", UserId);
        Append(sb, "email", Email);
        Append(sb, "phone", Phone);
        Append(sb, "appVersion", AppVersion);
        if (HasAttachment is not null) Append(sb, "hasAttachment", HasAttachment.Value ? "true" : "false");
        Append(sb, "channel", Channel);
        Append(sb, "topFrame", TopFrame);
        if (From is not null) Append(sb, "from", From.Value.ToString("yyyy-MM-ddTHH:mm"));
        if (Until is not null) Append(sb, "until", Until.Value.ToString("yyyy-MM-ddTHH:mm"));
        Append(sb, "page", page.ToString());
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (sb.Length > 1) sb.Append('&');
        sb.Append(key).Append('=').Append(Uri.EscapeDataString(value));
    }
}
