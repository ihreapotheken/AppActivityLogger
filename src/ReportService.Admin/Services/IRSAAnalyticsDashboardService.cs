using ReportService.Admin.ViewModels;

namespace ReportService.Admin.Services;

/// <summary>Read-side facade for the Analytics (user tracking) dashboard page.</summary>
public interface IRSAAnalyticsDashboardService
{
    /// <summary>Builds the analytics dashboard. When <paramref name="platform"/> is "ios" or "android",
    /// numbers are scoped to that platform; otherwise the combined view is returned.</summary>
    RSAAnalyticsDashboardVM Build(string? platform = null);
}
