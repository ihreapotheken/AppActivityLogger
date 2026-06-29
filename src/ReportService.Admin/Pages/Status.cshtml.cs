using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Services;
using ReportService.Analytics;
using ReportService.Audit;
using ReportService.Observability;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.ApiKeys;
using ReportService.Storage.Retention;

namespace ReportService.Admin.Pages;

/// <summary>
/// Surfaces everything an operator needs before reaching for a maintenance action: file paths,
/// WAL presence, DB sizes, schema version, last-integrity / last-backup, drift counters, retention
/// stats, and a running view of <see cref="RSCComponentHealth"/>.
/// </summary>
public sealed class RSAStatusModel : PageModel
{
    private readonly RSCReportServiceOptions _options;
    private readonly RSCAnalyticsOptions _analyticsOptions;
    private readonly RSCComponentHealth _health;
    private readonly RSCServiceTelemetry _telemetry;
    private readonly RSCIAuditLog _audit;
    private readonly RSCRetentionService _retention;
    private readonly IRSAReportIndexAccessor _indexAccessor;
    private readonly RSCIAnalyticsStore _analyticsStore;
    private readonly RSCIApiKeyStore _apiKeyStore;

    public RSAStatusModel(
        RSCReportServiceOptions options,
        RSCAnalyticsOptions analyticsOptions,
        RSCComponentHealth health,
        RSCServiceTelemetry telemetry,
        RSCIAuditLog audit,
        RSCRetentionService retention,
        IRSAReportIndexAccessor indexAccessor,
        RSCIAnalyticsStore analyticsStore,
        RSCIApiKeyStore apiKeyStore)
    {
        _options = options;
        _analyticsOptions = analyticsOptions;
        _health = health;
        _telemetry = telemetry;
        _audit = audit;
        _retention = retention;
        _indexAccessor = indexAccessor;
        _analyticsStore = analyticsStore;
        _apiKeyStore = apiKeyStore;
    }

    public RSCIndexStatusReport? IndexStatus { get; private set; }
    public string ReportsRoot => Path.GetFullPath(_options.ReportsRoot);
    public string AuthAbuseDbPath => RSCStatePaths.Resolve(_options.AuthAbuseDbPath, _options.ReportsRoot);
    public string AuditDbPath => RSCStatePaths.Resolve(_options.AuditDbPath, _options.ReportsRoot);
    public string AnalyticsDbPath => RSCStatePaths.Resolve(_analyticsOptions.SqliteDbPath, _options.ReportsRoot);
    public string ApiKeysDbPath => RSCStatePaths.Resolve(_options.ApiKeysDbPath, _options.ReportsRoot);
    public string BackupRoot => RSCStatePaths.Resolve(_options.BackupRoot, _options.ReportsRoot);
    public long AuthAbuseDbSize => FileSize(AuthAbuseDbPath);
    public long AuditDbSize => FileSize(AuditDbPath);
    public long AnalyticsDbSize => FileSize(AnalyticsDbPath);
    public long ApiKeysDbSize => FileSize(ApiKeysDbPath);
    public int ActiveApiKeyCount { get; private set; }
    public IReadOnlyDictionary<string, RSCComponentHealth.Entry> Health => _health.Snapshot();
    public RSCRetentionStats Retention { get; private set; } = default!;
    public int AuditCount { get; private set; }
    public string Version => _telemetry.Version;
    public TimeSpan Uptime => TimeSpan.FromSeconds(_telemetry.UptimeSeconds);

    public bool AnalyticsEnabled => _analyticsOptions.Enabled;
    public RSCAnalyticsHealthSnapshot? AnalyticsSnapshot { get; private set; }
    public IReadOnlyList<RSCAnalyticsPlatformSummary> AnalyticsPlatforms { get; private set; } = Array.Empty<RSCAnalyticsPlatformSummary>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (_indexAccessor.Maintenance is { } maint)
        {
            try { IndexStatus = await maint.GetStatusAsync(ct).ConfigureAwait(false); }
            catch { /* health surfaces it */ }
        }
        AuditCount = await _audit.CountAsync(ct).ConfigureAwait(false);
        ActiveApiKeyCount = await _apiKeyStore.CountActiveAsync(ct).ConfigureAwait(false);
        Retention = _retention.GetStats();

        if (AnalyticsEnabled)
        {
            try
            {
                AnalyticsSnapshot = await _analyticsStore.GetHealthSnapshotAsync(5, ct).ConfigureAwait(false);
                AnalyticsPlatforms = await _analyticsStore.GetPlatformSummariesAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Analytics being unavailable shouldn't break the Status page. The health card
                // will simply show "n/a" so the operator notices in context.
            }
        }
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
