using ReportService.Admin.ViewModels;

namespace ReportService.Admin.Services;

/// <summary>Read-side facade for the Dashboard page.</summary>
public interface IRSADashboardService
{
    /// <summary>Builds the dashboard, optionally scoped to one tenant <paramref name="clientId"/> /
    /// <paramref name="appId"/> (null = all). The global switcher scope is applied here so the landing
    /// page honours the top-left client/app selection like the other pages.</summary>
    RSADashboardVM Build(string? clientId = null, string? appId = null);
}
