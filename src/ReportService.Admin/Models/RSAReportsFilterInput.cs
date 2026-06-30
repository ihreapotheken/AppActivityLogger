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
    // Narrows a page's implicit kind scope to a single kind (e.g. "crash" or "error" on /Errors).
    // Ignored unless the page surfaces a kind dropdown, and a pick outside the page's scope is
    // dropped by the listing service — see RSAReportListingService.ResolveKindIn.
    [FromQuery(Name = "kind")] public string? Kind { get; set; }
    [FromQuery(Name = "topFrame")] public string? TopFrame { get; set; }
    [FromQuery(Name = "from")] public DateTime? From { get; set; }
    [FromQuery(Name = "until")] public DateTime? Until { get; set; }
    [FromQuery(Name = "page")] public int Page { get; set; } = 1;

    /// <summary>Shallow copy with a different <see cref="Page"/>; used to fetch the full matched set
    /// (page 1) for the on-page analytics summary without disturbing the user's current page.</summary>
    public RSAReportsFilterInput WithPage(int page) => new()
    {
        Platform = Platform, Q = Q, PharmacyId = PharmacyId, UserId = UserId, Email = Email,
        Phone = Phone, AppVersion = AppVersion, HasAttachment = HasAttachment, Channel = Channel,
        Kind = Kind, TopFrame = TopFrame, From = From, Until = Until, Page = page
    };

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
        Append(sb, "kind", Kind);
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
