namespace ReportService.Admin.ViewModels;

/// <summary>Model for the shared <c>_ScopeTabs</c> partial — the All / iOS / Android platform
/// selector shared by the Analytics and Errors dashboards. <paramref name="CurrentScope"/> is the
/// canonical platform (<c>null</c> = all) used to mark the active tab; <paramref name="PageName"/>
/// is the owning page's route so the links point back at the right dashboard.</summary>
public sealed record RSAScopeTabsVM(string PageName, string? CurrentScope);
