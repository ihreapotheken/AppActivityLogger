using ReportService.Storage;

namespace ReportService.Admin.ViewModels;

/// <summary>Bound model for the <c>_StatsBucket</c> partial. The parent page supplies the page slice
/// metadata + a function that builds pager URLs preserving cross-bucket state.</summary>
public sealed record RSAStatsBucketArgs(
    string Title,
    IReadOnlyList<RSCStatsBucket> Items,
    string FilterParam,
    int Page,
    int PageSize,
    Func<int, string> BuildHref);
