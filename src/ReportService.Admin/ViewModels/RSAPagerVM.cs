namespace ReportService.Admin.ViewModels;

/// <summary>Model for the shared <c>_Pager</c> partial. The owning page pre-computes the prev/next
/// hrefs (<c>null</c> hides that link) and the centre label, so the one pager markup serves every
/// server-paginated listing regardless of how it builds page URLs.</summary>
public sealed record RSAPagerVM(
    string? PrevHref,
    string? NextHref,
    string Label,
    string AriaLabel = "Pagination",
    string? ExtraClass = null,
    // Appended (as a "#" fragment) to the prev/next hrefs so a full-page reload returns to the
    // listing instead of jumping to the top. The owning page must render an element with this id
    // just above its table (see the shared <c>list-anchor</c>). Set to null/"" to opt out.
    string AnchorId = "list");
