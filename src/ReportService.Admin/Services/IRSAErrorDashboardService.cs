using ReportService.Admin.ViewModels;

namespace ReportService.Admin.Services;

/// <summary>Read-side facade for the Error-reporting dashboard page.</summary>
public interface IRSAErrorDashboardService
{
    /// <summary>Builds the error dashboard. When <paramref name="platform"/> is "ios" or "android",
    /// numbers are scoped to that platform; otherwise the combined view is returned. The error-rate
    /// chart spans <paramref name="rateWindow"/> (bucketed by its <see cref="RSAErrorRateBucket"/>);
    /// when null it falls back to the rolling last 7 days, bucketed daily.</summary>
    RSAErrorDashboardVM Build(string? platform = null, RSAErrorRateWindow? rateWindow = null);

    /// <summary>
    /// Builds the dashboard "trending issues" list: crash fault sites ranked by occurrences in the
    /// last <paramref name="recentDays"/> days, each carrying the prior equal-length window's count
    /// so the view can show whether the issue is rising, falling, or new. Returns at most
    /// <paramref name="limit"/> rows, only for fault sites active in the recent window.
    /// </summary>
    IReadOnlyList<RSATrendingIssueVM> BuildTrending(int recentDays = 7, int limit = 5);
}
