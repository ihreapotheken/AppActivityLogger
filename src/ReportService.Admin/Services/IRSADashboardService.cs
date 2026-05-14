using ReportService.Admin.ViewModels;

namespace ReportService.Admin.Services;

/// <summary>Read-side facade for the Dashboard page.</summary>
public interface IRSADashboardService
{
    RSADashboardVM Build();
}
