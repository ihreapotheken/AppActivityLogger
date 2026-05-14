namespace ReportService.Storage;

/// <summary>Page of results plus the total match count so the UI can render a pagination bar.</summary>
public sealed record RSCReportPage(IReadOnlyList<RSCStoredReport> Items, int TotalMatched, int Limit, int Offset);
