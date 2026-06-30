using ReportService.Admin.Models;

namespace ReportService.Admin.ViewModels;

/// <summary>
/// Inputs for the shared <c>_ReportsFilter</c> partial. Each per-category page renders the same
/// form by handing in its own <see cref="PageName"/> (e.g. <c>"/ProblemReports"</c>, <c>"/Errors"</c>)
/// and the bound filter values. Channel / attachment filters can be hidden when the page already
/// constrains them implicitly.
/// </summary>
public sealed record RSAReportsFilterPanelVM(
    string PageName,
    RSAReportsFilterInput Filter,
    IReadOnlyList<string> AvailablePlatforms,
    bool ShowChannelFilter = true,
    bool ShowAttachmentFilter = true,
    IReadOnlyList<RSAReportsFilterKindOption>? KindOptions = null);

/// <summary>
/// One entry in the optional Kind dropdown. <see cref="Value"/> is the kind written to the
/// <c>kind=</c> query (e.g. <c>"crash"</c>); <see cref="Label"/> is the human caption. The dropdown
/// only renders when a page passes a non-empty option list — pages with a single implicit kind
/// scope (ProblemReports, Analytics) leave it null and the field is hidden.
/// </summary>
public sealed record RSAReportsFilterKindOption(string Value, string Label);
