using ReportService.Admin.Models;

namespace ReportService.Admin.ViewModels;

/// <summary>
/// Inputs for the shared <c>_ReportsFilter</c> partial. Each per-category page renders the same
/// form by handing in its own <see cref="PageName"/> (e.g. <c>"/Reports"</c>, <c>"/ProblemReports"</c>)
/// and the bound filter values. Channel / attachment filters can be hidden when the page already
/// constrains them implicitly.
/// </summary>
public sealed record RSAReportsFilterPanelVM(
    string PageName,
    RSAReportsFilterInput Filter,
    IReadOnlyList<string> AvailablePlatforms,
    bool ShowChannelFilter = true,
    bool ShowAttachmentFilter = true);
