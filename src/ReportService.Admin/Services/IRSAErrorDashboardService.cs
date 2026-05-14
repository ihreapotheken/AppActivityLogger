using ReportService.Admin.ViewModels;

namespace ReportService.Admin.Services;

/// <summary>Read-side facade for the Error-reporting dashboard page.</summary>
public interface IRSAErrorDashboardService
{
    /// <summary>Builds the error dashboard. When <paramref name="platform"/> is "ios" or "android",
    /// numbers are scoped to that platform; otherwise the combined view is returned.</summary>
    RSAErrorDashboardVM Build(string? platform = null);
}
