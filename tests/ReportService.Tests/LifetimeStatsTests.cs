using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Covers the "bank lifetime statistics before deleting" contract: every report removed through the
/// real deletion choke point (<see cref="RSCSqliteIndexingReportStore.Delete"/>) folds its count +
/// byte footprint into <c>lifetime_report_stats</c> in the same transaction that drops its row, so
/// the totals outlive the data. The drift-reconciliation delete (<see cref="RSCIReportIndex.DeleteAsync"/>)
/// deliberately does NOT accumulate.
/// </summary>
public class LifetimeStatsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rs-lifetime-{Guid.NewGuid():N}");
    private readonly RSCSqliteReportIndex _index;
    private readonly RSCIReportIndexMaintenance _maint;
    private readonly RSCSqliteIndexingReportStore _store;

    public LifetimeStatsTests()
    {
        Directory.CreateDirectory(_root);
        var options = new RSCReportServiceOptions
        {
            ReportsRoot = _root,
            SqliteDbPath = "lifetime.db",
            AllowedPlatforms = new[] { "android", "ios" }
        };
        var fileStore = new RSCFileSystemReportStore(options, NullLogger<RSCFileSystemReportStore>.Instance);
        _index = new RSCSqliteReportIndex(options, NullLogger<RSCSqliteReportIndex>.Instance);
        _maint = _index;
        _store = new RSCSqliteIndexingReportStore(fileStore, _index, NullLogger<RSCSqliteIndexingReportStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Deleting_reports_banks_their_footprint_into_lifetime_stats()
    {
        var noAttachment = await SaveAsync("{\"a\":1}");
        var withAttachment = await SaveAsync("{\"b\":2}", attachment: new byte[] { 1, 2, 3, 4, 5 });

        // Sanity: both are in the index before deletion.
        Assert.Equal(2, (await _index.ListAsync("android", default)).Count);
        Assert.Empty(await _maint.GetLifetimeStatsAsync(default));

        Assert.True(_store.Delete("android", noAttachment.FileName));
        Assert.True(_store.Delete("android", withAttachment.FileName));

        // Rows are gone…
        Assert.Empty(await _index.ListAsync("android", default));

        // …but their contribution survives in the lifetime rollup.
        var stats = await _maint.GetLifetimeStatsAsync(default);
        var android = Assert.Single(stats);
        Assert.Equal("android", android.Platform);
        Assert.Equal(2, android.DeletedReports);
        Assert.Equal(1, android.DeletedWithAttachment);
        Assert.Equal(noAttachment.SizeBytes + withAttachment.SizeBytes, android.DeletedJsonBytes);
        Assert.Equal(withAttachment.AttachmentSizeBytes ?? 0, android.DeletedAttachmentBytes);
        Assert.NotNull(android.FirstDeletedAt);
        Assert.NotNull(android.LastDeletedAt);
        Assert.True(android.LastDeletedAt >= android.FirstDeletedAt);
    }

    [Fact]
    public async Task Lifetime_totals_are_keyed_per_platform()
    {
        var android = await SaveAsync("{\"a\":1}", platform: "Android");
        var ios = await SaveAsync("{\"i\":1}", platform: "iOS");

        Assert.True(_store.Delete("android", android.FileName));
        Assert.True(_store.Delete("ios", ios.FileName));

        var stats = await _maint.GetLifetimeStatsAsync(default);
        Assert.Equal(2, stats.Count);
        Assert.All(stats, s => Assert.Equal(1, s.DeletedReports));
        Assert.Contains(stats, s => s.Platform == "android");
        Assert.Contains(stats, s => s.Platform == "ios");
    }

    [Fact]
    public async Task Drift_reconciliation_delete_does_not_accumulate()
    {
        var saved = await SaveAsync("{\"a\":1}");

        // The plain DeleteAsync is what RebuildAsync uses to prune stale index rows; it must not
        // inflate lifetime counters (the file was never a real, operator-/retention-driven delete).
        Assert.True(await _index.DeleteAsync("android", saved.FileName, default));

        Assert.Empty(await _index.ListAsync("android", default));
        Assert.Empty(await _maint.GetLifetimeStatsAsync(default));
    }

    private Task<RSCStoredReport> SaveAsync(string json, byte[]? attachment = null, string platform = "Android")
    {
        var report = new RSCProblemReport(
            Platform: platform, Message: "Test",
            Title: null, DeviceModel: null, Email: null, PhoneNumber: null, Phone: null,
            PharmacyId: null, Source: null, AppVersion: null, FunctionalityImportance: null, Labels: null);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        if (attachment is null)
        {
            return _store.SaveAsync(report, bytes, null, null, RSCIngestionChannels.Multipart, default);
        }
        var stream = new MemoryStream(attachment);
        return _store.SaveAsync(report, bytes, stream, attachment.Length, RSCIngestionChannels.Multipart, default);
    }
}
