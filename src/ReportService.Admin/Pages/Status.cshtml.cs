using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Services;
using ReportService.Audit;
using ReportService.Observability;
using ReportService.Options;
using ReportService.Storage;
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
    private readonly RSCComponentHealth _health;
    private readonly RSCServiceTelemetry _telemetry;
    private readonly RSCIAuditLog _audit;
    private readonly RSCRetentionService _retention;
    private readonly IRSAReportIndexAccessor _indexAccessor;

    public RSAStatusModel(
        RSCReportServiceOptions options,
        RSCComponentHealth health,
        RSCServiceTelemetry telemetry,
        RSCIAuditLog audit,
        RSCRetentionService retention,
        IRSAReportIndexAccessor indexAccessor)
    {
        _options = options;
        _health = health;
        _telemetry = telemetry;
        _audit = audit;
        _retention = retention;
        _indexAccessor = indexAccessor;
    }

    public RSCIndexStatusReport? IndexStatus { get; private set; }
    public string ReportsRoot => Path.GetFullPath(_options.ReportsRoot);
    public string AuthAbuseDbPath => RSCStatePaths.Resolve(_options.AuthAbuseDbPath, _options.ReportsRoot);
    public string AuditDbPath => RSCStatePaths.Resolve(_options.AuditDbPath, _options.ReportsRoot);
    public string BackupRoot => RSCStatePaths.Resolve(_options.BackupRoot, _options.ReportsRoot);
    public long AuthAbuseDbSize => FileSize(AuthAbuseDbPath);
    public long AuditDbSize => FileSize(AuditDbPath);
    public IReadOnlyDictionary<string, RSCComponentHealth.Entry> Health => _health.Snapshot();
    public RSCRetentionStats Retention { get; private set; } = default!;
    public int AuditCount { get; private set; }
    public string Version => _telemetry.Version;
    public TimeSpan Uptime => TimeSpan.FromSeconds(_telemetry.UptimeSeconds);

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (_indexAccessor.Maintenance is { } maint)
        {
            try { IndexStatus = await maint.GetStatusAsync(ct).ConfigureAwait(false); }
            catch { /* health surfaces it */ }
        }
        AuditCount = await _audit.CountAsync(ct).ConfigureAwait(false);
        Retention = _retention.GetStats();
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
