using ReportService.Admin.ViewModels;
using ReportService.Analytics;

namespace ReportService.Admin.Services;

/// <summary>Read-side facade for the Analytics (user tracking) dashboard page.</summary>
public interface IRSAAnalyticsDashboardService
{
    /// <summary>Builds the analytics dashboard scoped to <paramref name="scope"/> — any null axis
    /// (app / environment / client / platform) means "all" for that axis. The legacy report-store
    /// implementation only honours <see cref="RSCAnalyticsScope.Platform"/>.</summary>
    /// <remarks>The legacy report-store implementation is synchronous; analytics-store-backed
    /// implementations may block briefly on SQLite reads.</remarks>
    RSAAnalyticsDashboardVM Build(RSCAnalyticsScope scope = default);

    /// <summary>Async build path used by Razor pages so analytics-store implementations don't
    /// stall the request thread on SQLite reads. Default forwards to <see cref="Build"/> so legacy
    /// callers keep working without changes.</summary>
    ValueTask<RSAAnalyticsDashboardVM> BuildAsync(RSCAnalyticsScope scope, CancellationToken ct)
        => ValueTask.FromResult(Build(scope));
}
