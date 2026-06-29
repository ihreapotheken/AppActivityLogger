namespace ReportService.Admin.ViewModels;

/// <summary>Model for the shared <c>_Pager</c> partial. The owning page pre-computes the prev/next
/// hrefs (<c>null</c> hides that link) and the centre label, so the one pager markup serves every
/// server-paginated listing regardless of how it builds page URLs.</summary>
public sealed record RSAPagerVM(
    string? PrevHref,
    string? NextHref,
    string Label,
    string AriaLabel = "Pagination",
    string? ExtraClass = null);
