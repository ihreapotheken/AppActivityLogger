using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.ViewModels;
using ReportService.Analytics;
using ReportService.Options;

namespace ReportService.Admin.Pages;

public sealed class RSAAnalyticsHealthModel : PageModel
{
    private const int SampleSize = 20;

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCAnalyticsOptions _options;
    private readonly RSCSqliteAnalyticsStore? _sqliteStore;

    public RSAAnalyticsHealthModel(
        RSCIAnalyticsStore store,
        RSCAnalyticsOptions options)
    {
        _store = store;
        _options = options;
        // The concrete type carries its bootstrap schema version. Cast is safe today (only one
        // implementation); if a second backend lands, expose SchemaVersion through the interface.
        _sqliteStore = store as RSCSqliteAnalyticsStore;
    }

    public RSAAnalyticsHealthVM VM { get; private set; } = default!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var snapshot = await _store.GetHealthSnapshotAsync(SampleSize, ct).ConfigureAwait(false);
        var platforms = await _store.GetPlatformSummariesAsync(ct).ConfigureAwait(false);

        VM = new RSAAnalyticsHealthVM(
            AnalyticsEnabled: _options.Enabled,
            SchemaVersion: _sqliteStore?.SchemaVersion ?? 0,
            LastAggregatedAt: snapshot.LastAggregatedAt,
            Snapshot: snapshot,
            PlatformSummaries: platforms);
    }
}
