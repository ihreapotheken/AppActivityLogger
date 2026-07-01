using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Services;
using ReportService.Admin.ViewModels;
using ReportService.Analytics;
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
    private readonly IRSAAnalyticsDashboardService _analytics;
    private readonly IRSAErrorDashboardService _errors;
    private readonly RSCIAnalyticsStore _analyticsStore;
    private readonly RSCComponentHealth _health;
    private readonly RSCServiceTelemetry _telemetry;
    private readonly IRSAReportIndexAccessor _indexAccessor;

    public RSAIndexModel(
        IRSADashboardService dashboard,
        IRSAAnalyticsDashboardService analytics,
        IRSAErrorDashboardService errors,
        RSCIAnalyticsStore analyticsStore,
        RSCComponentHealth health,
        RSCServiceTelemetry telemetry,
        IRSAReportIndexAccessor indexAccessor)
    {
        _dashboard = dashboard;
        _analytics = analytics;
        _errors = errors;
        _analyticsStore = analyticsStore;
        _health = health;
        _telemetry = telemetry;
        _indexAccessor = indexAccessor;
    }

    // Global tenant scope from the top-left switcher (rsc_scope cookie → ?client/?app, filled by the
    // scope-fill middleware). Null = all. Applied so the landing page filters like the other pages.
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }
    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }

    public RSADashboardVM Dashboard { get; private set; } = default!;
    public RSCIndexStatusReport? IndexStatus { get; private set; }

    /// <summary>
    /// Per-platform cumulative totals for reports deleted over the service's lifetime, banked at
    /// deletion time so they survive retention purges. Empty until the first deletion (or when the
    /// SQLite index isn't wired up).
    /// </summary>
    public IReadOnlyList<RSCLifetimeReportStats> LifetimeStats { get; private set; } = Array.Empty<RSCLifetimeReportStats>();

    /// <summary>True once anything has ever been deleted — gates the "Lifetime totals" section.</summary>
    public bool HasLifetimeStats => LifetimeStats.Count > 0;

    /// <summary>Reports deleted across all platforms over the service's lifetime.</summary>
    public long LifetimeDeletedReports => LifetimeStats.Sum(s => s.DeletedReports);

    /// <summary>JSON + attachment bytes reclaimed by deletions over the service's lifetime.</summary>
    public long LifetimeDeletedBytes => LifetimeStats.Sum(s => s.DeletedTotalBytes);

    /// <summary>Currently-retained reports plus everything ever deleted — the true lifetime intake.</summary>
    public long LifetimeReportsReceived => Dashboard.TotalCount + LifetimeDeletedReports;
    public string Version => _telemetry.Version;
    public TimeSpan Uptime => TimeSpan.FromSeconds(_telemetry.UptimeSeconds);
    public IReadOnlyDictionary<string, RSCComponentHealth.Entry> Health => _health.Snapshot();

    // Headline metrics for the analytics "Submissions" tiles. These categories track events, not
    // stored reports, so they carry their own signal instead of a report count. Best-effort: a
    // SQLite hiccup leaves them at their defaults rather than breaking the dashboard.
    public int DailyActiveUsers { get; private set; }
    public double RetentionDay1 { get; private set; }
    public int FunnelCount { get; private set; }

    /// <summary>Crash fault sites trending over the last 7 days (vs the prior 7). Empty if none/offline.</summary>
    public IReadOnlyList<RSATrendingIssueVM> TrendingIssues { get; private set; } = Array.Empty<RSATrendingIssueVM>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Dashboard = _dashboard.Build(Client, App);

        try { TrendingIssues = _errors.BuildTrending(recentDays: 7, limit: 5, clientId: Client, appId: App); }
        catch { /* report store hiccup — section just renders empty */ }

        try
        {
            var scope = RSATenantScopes.Build(App, Client, platform: null);
            var a = await _analytics.BuildAsync(scope, ct).ConfigureAwait(false);
            DailyActiveUsers = a.DailyActiveUsers;
            RetentionDay1 = a.Retention.Day1;
        }
        catch { /* analytics offline — tiles fall back to defaults */ }

        try
        {
            var funnels = await _analyticsStore.ListFunnelDefinitionsAsync(onlyEnabled: false, ct).ConfigureAwait(false);
            FunnelCount = funnels.Count;
        }
        catch { /* analytics offline — tile falls back to 0 */ }

        if (_indexAccessor.Maintenance is { } maint)
        {
            try { IndexStatus = await maint.GetStatusAsync(ct).ConfigureAwait(false); }
            catch { /* surfaced via RSCComponentHealth */ }

            try { LifetimeStats = await maint.GetLifetimeStatsAsync(ct).ConfigureAwait(false); }
            catch { /* lifetime section just stays hidden on a SQLite hiccup */ }
        }
    }
}
