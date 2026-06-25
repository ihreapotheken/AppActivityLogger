using ReportService.Admin.ViewModels;

namespace ReportService.Admin.Services;

/// <summary>Read-side facade for the Analytics (user tracking) dashboard page.</summary>
public interface IRSAAnalyticsDashboardService
{
    /// <summary>Builds the analytics dashboard. When <paramref name="platform"/> is "ios" or "android",
    /// numbers are scoped to that platform; otherwise the combined view is returned.</summary>
    /// <remarks>The legacy report-store implementation is synchronous; analytics-store-backed
    /// implementations may block briefly on SQLite reads.</remarks>
    RSAAnalyticsDashboardVM Build(string? platform = null);

    /// <summary>Async build path used by Razor pages so analytics-store implementations don't
    /// stall the request thread on SQLite reads. Default forwards to <see cref="Build"/> so legacy
    /// callers keep working without changes.</summary>
    ValueTask<RSAAnalyticsDashboardVM> BuildAsync(string? platform, CancellationToken ct)
        => ValueTask.FromResult(Build(platform));
}
