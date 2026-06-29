using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Admin.Services;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Covers CODE-REVIEW finding #16 (HIGH): RSAAnalyticsLegacyImporter idempotency. The importer's
/// header documents "Idempotent on (platform, event_id). Re-running over the same source files only
/// inserts events that aren't already present." The fix made the synthesized event_id deterministic
/// (<c>legacy-{platform}-{fileName}</c>) so the store's UNIQUE(platform, event_id) actually dedupes
/// a second run. This pins that contract with a real SQLite analytics store + an in-memory fake
/// RSCIReportStore (no mocking framework).
/// </summary>
public class AnalyticsLegacyImporterTests : IDisposable
{
    private readonly string _root;
    private readonly RSCSqliteAnalyticsStore _store;
    private readonly RSAAnalyticsLegacyImporter _importer;

    public AnalyticsLegacyImporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rs-analytics-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var reportOptions = new RSCReportServiceOptions
        {
            ReportsRoot = _root,
            AllowedPlatforms = new[] { "android", "ios" }
        };
        var analyticsOptions = new RSCAnalyticsOptions
        {
            SqliteDbPath = "analytics-legacy.db",
            IdentifierHashPepper = "pepper-test"
        };
        _store = new RSCSqliteAnalyticsStore(reportOptions, analyticsOptions, NullLogger<RSCSqliteAnalyticsStore>.Instance);
        var validator = new RSCAnalyticsValidator(analyticsOptions, reportOptions);
        var hasher = new RSCAnalyticsIdentifierHasher(analyticsOptions);

        var reportStore = new FakeReportStore();
        // One analytics report (gets converted) + one non-analytics report (skipped).
        // OccurredAt must be within MaxClockSkewSeconds of now or the validator rejects it as
        // clock_skew (the importer stamps receivedAt = UtcNow).
        var occurredAt = DateTimeOffset.UtcNow.ToString("O");
        reportStore.Add("android", "analytics-1.json",
            $$"""{ "Kind": "analytics", "Title": "home_view", "Source": "home", "UserId": "u-1", "OccurredAt": "{{occurredAt}}" }""");
        reportStore.Add("android", "crash-1.json",
            """{ "Kind": "crash", "Title": "boom" }""");

        _importer = new RSAAnalyticsLegacyImporter(
            reportStore, _store, validator, hasher, reportOptions,
            NullLogger<RSAAnalyticsLegacyImporter>.Instance);
    }

    [Fact]
    public async Task First_import_converts_the_analytics_report_and_skips_the_rest()
    {
        var report = await _importer.ImportAsync(default);

        Assert.Equal(2, report.Scanned);
        Assert.Equal(1, report.Converted);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Failed);
        Assert.Equal(1, await CountEventsAsync());
    }

    [Fact]
    public async Task Second_import_over_identical_sources_converts_zero_and_does_not_duplicate()
    {
        var first = await _importer.ImportAsync(default);
        Assert.Equal(1, first.Converted);
        var afterFirst = await CountEventsAsync();
        Assert.Equal(1, afterFirst);

        // Deterministic event_id => UNIQUE(platform, event_id) dedupes the re-run entirely.
        var second = await _importer.ImportAsync(default);
        Assert.Equal(0, second.Converted);
        Assert.Equal(afterFirst, await CountEventsAsync());
    }

    private async Task<long> CountEventsAsync()
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _store.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM analytics_events;";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    /// <summary>Minimal in-memory RSCIReportStore: List + OpenRead are the only members the importer
    /// touches. Save/Delete throw — they are never reached on the import path.</summary>
    private sealed class FakeReportStore : RSCIReportStore
    {
        private readonly Dictionary<string, List<(RSCStoredReport Meta, byte[] Json)>> _byPlatform = new();

        public void Add(string platform, string fileName, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var meta = new RSCStoredReport(
                Platform: platform,
                FileName: fileName,
                SizeBytes: bytes.Length,
                SubmittedAt: DateTimeOffset.UtcNow,
                AttachmentFileName: null,
                AttachmentSizeBytes: null,
                Kind: null);
            if (!_byPlatform.TryGetValue(platform, out var list))
                _byPlatform[platform] = list = new();
            list.Add((meta, bytes));
        }

        public IReadOnlyList<RSCStoredReport> List(string platform) =>
            _byPlatform.TryGetValue(platform, out var list)
                ? list.Select(x => x.Meta).ToList()
                : Array.Empty<RSCStoredReport>();

        public Stream? OpenRead(string platform, string fileName)
        {
            if (!_byPlatform.TryGetValue(platform, out var list)) return null;
            foreach (var (meta, json) in list)
                if (meta.FileName == fileName)
                    return new MemoryStream(json, writable: false);
            return null;
        }

        public Task<RSCStoredReport> SaveAsync(RSCProblemReport report, ReadOnlyMemory<byte> jsonBytes,
            Stream? attachment, long? attachmentLength, string ingestionChannel, CancellationToken ct) =>
            throw new NotSupportedException();

        public bool Delete(string platform, string fileName) => throw new NotSupportedException();
    }
}
