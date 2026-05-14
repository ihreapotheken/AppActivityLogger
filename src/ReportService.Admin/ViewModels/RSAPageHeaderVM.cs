namespace ReportService.Admin.ViewModels;

/// <summary>Tiny VM for the shared <c>_PageHeader</c> partial (title + optional subtitle).</summary>
public sealed record RSAPageHeaderVM(string Title, string? Subtitle = null);
