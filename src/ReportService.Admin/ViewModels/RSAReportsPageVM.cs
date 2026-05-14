namespace ReportService.Admin.ViewModels;

/// <summary>
/// Result of a Reports listing query: the rows for the current page, the total match count, and
/// which storage path produced them. <c>UsedIndex = false</c> means the SQLite index was missing
/// or degraded and the data came from a disk walk; the page surfaces this as a "filesystem scan"
/// badge so an operator can tell when filter accuracy may be limited.
/// </summary>
public sealed record RSAReportsPageVM(
    IReadOnlyList<RSAReportRowVM> Items,
    int TotalMatched,
    int PageNumber,
    int TotalPages,
    bool UsedIndex);
