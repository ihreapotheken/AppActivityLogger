using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Services;
using ReportService.Admin.ViewModels;
using ReportService.Observability;
using ReportService.Storage;

namespace ReportService.Admin.Pages;

/// <summary>
/// Dashboard handler. Composition is delegated to <see cref="IRSADashboardService"/>; this class
/// does only HTTP concerns (binding, exposing the result, choosing where to render). The optional
/// SQLite index status is fetched via the typed accessor instead of a service-locator lookup.
/// </summary>
public sealed class RSAIndexModel : PageModel
{
    private readonly IRSADashboardService _dashboard;
    private readonly RSCComponentHealth _health;
    private readonly RSCServiceTelemetry _telemetry;
    private readonly IRSAReportIndexAccessor _indexAccessor;

    public RSAIndexModel(
        IRSADashboardService dashboard,
        RSCComponentHealth health,
        RSCServiceTelemetry telemetry,
        IRSAReportIndexAccessor indexAccessor)
    {
        _dashboard = dashboard;
        _health = health;
        _telemetry = telemetry;
        _indexAccessor = indexAccessor;
    }

    public RSADashboardVM Dashboard { get; private set; } = default!;
    public RSCIndexStatusReport? IndexStatus { get; private set; }
    public string Version => _telemetry.Version;
    public TimeSpan Uptime => TimeSpan.FromSeconds(_telemetry.UptimeSeconds);
    public IReadOnlyDictionary<string, RSCComponentHealth.Entry> Health => _health.Snapshot();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Dashboard = _dashboard.Build();

        if (_indexAccessor.Maintenance is { } maint)
        {
            try { IndexStatus = await maint.GetStatusAsync(ct).ConfigureAwait(false); }
            catch { /* surfaced via RSCComponentHealth */ }
        }
    }
}
