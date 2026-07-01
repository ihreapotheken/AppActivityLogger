using Microsoft.Extensions.Logging;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Storage;

/// <summary>
/// The <see cref="RSCIReportStore"/> the host resolves from DI in the database-per-app model. It owns
/// no files of its own — it routes:
/// <list type="bullet">
///   <item><b><c>SaveAsync</c></b> stores the report under its own <c>(client, app)</c>'s tree
///         (attributed by the ingestion layer from the API key + <c>X-Report-App</c>).</item>
///   <item><b><see cref="List"/></b> fans out across every app's tree, stamps each row with its owning
///         <c>(client, app)</c>, and merges newest-first (capped). Admin pages filter the merged list
///         to the selected scope.</item>
///   <item><b><see cref="OpenRead"/> / <see cref="Delete"/></b> locate the file across apps (a report
///         is identified by platform + filename only).</item>
/// </list>
/// The <c>List</c>/<c>OpenRead</c>/<c>Delete</c> interface members are synchronous, so the (low
/// frequency, admin-only) catalog enumeration is awaited inline — the same sync-over-async shape the
/// existing indexing store already uses for its index calls.
/// </summary>
public sealed class RSCFanOutReportStore : RSCIReportStore
{
    private readonly RSCIReportStoreFactory _factory;
    private readonly RSCICatalog _catalog;
    private readonly RSCAnalyticsFanoutOptions _fanout;
    private readonly ILogger<RSCFanOutReportStore> _logger;

    public RSCFanOutReportStore(
        RSCIReportStoreFactory factory,
        RSCICatalog catalog,
        RSCAnalyticsFanoutOptions fanout,
        ILogger<RSCFanOutReportStore> logger)
    {
        _factory = factory;
        _catalog = catalog;
        _fanout = fanout;
        _logger = logger;
    }

    public Task<RSCStoredReport> SaveAsync(
        RSCProblemReport report, ReadOnlyMemory<byte> jsonBytes, Stream? attachment,
        long? attachmentLength, string ingestionChannel, CancellationToken ct)
        => SaveAsync(report, jsonBytes, attachment, attachmentLength, ingestionChannel, DateTimeOffset.UtcNow, ct);

    public async Task<RSCStoredReport> SaveAsync(
        RSCProblemReport report, ReadOnlyMemory<byte> jsonBytes, Stream? attachment,
        long? attachmentLength, string ingestionChannel, DateTimeOffset submittedAt, CancellationToken ct)
    {
        var client = Norm(report.ClientId);
        var app = Norm(report.AppId);
        var stored = await _factory.Get(client, app)
            .SaveAsync(report, jsonBytes, attachment, attachmentLength, ingestionChannel, submittedAt, ct).ConfigureAwait(false);
        // Stamp the owning tenant so the listing/detail/delete paths can scope + route.
        return stored with { ClientId = client, AppId = app };
    }

    public IReadOnlyList<RSCStoredReport> List(string platform)
    {
        var apps = AppList();
        var merged = new List<RSCStoredReport>();
        foreach (var (client, app) in apps)
        {
            try
            {
                foreach (var r in _factory.Get(client, app).List(platform))
                    merged.Add(r with { ClientId = client, AppId = app });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fan-out report list failed for app {Client}/{App}; skipping", client, app);
            }
        }
        return merged.OrderByDescending(r => r.SubmittedAt).ToList();
    }

    public Stream? OpenRead(string platform, string fileName)
    {
        foreach (var (client, app) in AppList())
        {
            try
            {
                var s = _factory.Get(client, app).OpenRead(platform, fileName);
                if (s is not null) return s;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fan-out report open failed for app {Client}/{App}; trying next", client, app);
            }
        }
        return null;
    }

    public bool Delete(string platform, string fileName)
    {
        foreach (var (client, app) in AppList())
        {
            try
            {
                if (_factory.Get(client, app).Delete(platform, fileName)) return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fan-out report delete failed for app {Client}/{App}; trying next", client, app);
            }
        }
        return false;
    }

    /// <summary>Active (client, app) pairs to fan out over, capped. Sync-over-async — List/OpenRead/
    /// Delete are synchronous interface members and this is a low-frequency admin path.</summary>
    private List<(string Client, string App)> AppList()
    {
        var apps = _catalog.ListAllAppsAsync(includeArchived: false, CancellationToken.None).GetAwaiter().GetResult();
        if (_fanout.IsTruncated(apps.Count))
        {
            _logger.LogWarning("Fan-out report read truncated to {Cap} of {Total} apps", _fanout.MaxAppsPerRead, apps.Count);
            apps = apps.Take(_fanout.MaxAppsPerRead).ToList();
        }
        return apps.Select(a => (a.ClientSlug, a.Slug)).ToList();
    }

    private static string Norm(string? v) => string.IsNullOrWhiteSpace(v) ? string.Empty : v.Trim().ToLowerInvariant();
}
