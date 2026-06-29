using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Audit;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Retention;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Unit-level coverage for the retention sweep. Drives <see cref="RSCRetentionService"/> against a
/// hand-rolled in-memory <see cref="RSCIReportStore"/> so we can dictate timestamps and sizes
/// precisely (including attachment bytes, which must count toward the cap).
/// </summary>
public class RetentionServiceTests
{
    private static RSCReportServiceOptions Opts(long maxBytes, int maxAgeDays, bool enabled = true,
        long minFreeDiskBytes = 0, int maxDiskUsagePercent = 0) => new()
    {
        AllowedPlatforms = new[] { "android", "ios" },
        RetentionEnabled = enabled,
        RetentionMaxBytes = maxBytes,
        RetentionMaxAgeDays = maxAgeDays,
        RetentionMinFreeDiskBytes = minFreeDiskBytes,
        RetentionMaxDiskUsagePercent = maxDiskUsagePercent,
    };

    private static RSCRetentionService Build(RSCIReportStore store, RSCReportServiceOptions opts, RSCIDiskSpaceProbe? diskProbe = null) =>
        new(store, opts, new NullAuditLog(), NullLogger<RSCRetentionService>.Instance,
            diskProbe ?? new RSCDriveInfoDiskSpaceProbe());

    [Fact]
    public async Task Under_cap_and_within_age_is_a_noop()
    {
        var store = new FakeStore();
        store.Add("android", "a.json", 100, DateTimeOffset.UtcNow.AddDays(-1));
        store.Add("android", "b.json", 200, DateTimeOffset.UtcNow.AddDays(-2));

        var sut = Build(store, Opts(maxBytes: 10_000, maxAgeDays: 30));
        var report = await sut.SweepAsync("test", default);

        Assert.False(report.DidWork);
        Assert.Equal(0, report.DeletedTotal);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task Age_pass_deletes_anything_older_than_cutoff()
    {
        var store = new FakeStore();
        store.Add("android", "old1.json", 100, DateTimeOffset.UtcNow.AddDays(-45));
        store.Add("android", "old2.json", 100, DateTimeOffset.UtcNow.AddDays(-31));
        store.Add("android", "fresh.json", 100, DateTimeOffset.UtcNow.AddDays(-1));

        var sut = Build(store, Opts(maxBytes: 10_000, maxAgeDays: 30));
        var report = await sut.SweepAsync("test", default);

        Assert.Equal(2, report.DeletedByAge);
        Assert.Equal(0, report.DeletedBySize);
        Assert.Equal(1, store.Count);
        Assert.True(store.Has("android", "fresh.json"));
    }

    [Fact]
    public async Task Size_pass_deletes_oldest_first_until_under_target()
    {
        var store = new FakeStore();
        // 4 reports of 1000 bytes each, all within age window. Cap 3000 → must drop oldest.
        store.Add("android", "a.json", 1000, DateTimeOffset.UtcNow.AddHours(-4));
        store.Add("android", "b.json", 1000, DateTimeOffset.UtcNow.AddHours(-3));
        store.Add("android", "c.json", 1000, DateTimeOffset.UtcNow.AddHours(-2));
        store.Add("android", "d.json", 1000, DateTimeOffset.UtcNow.AddHours(-1));

        var sut = Build(store, Opts(maxBytes: 3000, maxAgeDays: 30));
        var report = await sut.SweepAsync("test", default);

        Assert.True(report.DeletedBySize >= 2, $"Expected >=2 deleted by size, got {report.DeletedBySize}");
        Assert.False(store.Has("android", "a.json"));
        Assert.True(store.Has("android", "d.json"), "newest must survive");
        Assert.True(report.BytesAfter <= (long)(3000 * 0.95));
    }

    [Fact]
    public async Task Combined_age_and_size_run_age_first_then_size()
    {
        var store = new FakeStore();
        // Two ancient + three recent-but-large. Cap 2000, age 30d.
        store.Add("android", "old1.json", 500, DateTimeOffset.UtcNow.AddDays(-60));
        store.Add("android", "old2.json", 500, DateTimeOffset.UtcNow.AddDays(-45));
        store.Add("android", "r1.json", 1000, DateTimeOffset.UtcNow.AddHours(-3));
        store.Add("android", "r2.json", 1000, DateTimeOffset.UtcNow.AddHours(-2));
        store.Add("android", "r3.json", 1000, DateTimeOffset.UtcNow.AddHours(-1));

        var sut = Build(store, Opts(maxBytes: 2000, maxAgeDays: 30));
        var report = await sut.SweepAsync("test", default);

        Assert.Equal(2, report.DeletedByAge);
        Assert.True(report.DeletedBySize >= 1, $"Expected size pass to also fire (got {report.DeletedBySize})");
        Assert.False(store.Has("android", "old1.json"));
        Assert.False(store.Has("android", "old2.json"));
        Assert.True(store.Has("android", "r3.json"));
    }

    [Fact]
    public async Task Attachment_bytes_count_toward_cap()
    {
        var store = new FakeStore();
        // JSON 100b + attachment 5000b each → 5100b/report. Cap 6000 → must drop oldest.
        store.Add("android", "a.json", 100, DateTimeOffset.UtcNow.AddHours(-2), attachment: 5000);
        store.Add("android", "b.json", 100, DateTimeOffset.UtcNow.AddHours(-1), attachment: 5000);

        var sut = Build(store, Opts(maxBytes: 6000, maxAgeDays: 30));
        var report = await sut.SweepAsync("test", default);

        Assert.True(report.DeletedBySize >= 1);
        Assert.Equal(5100, report.DeletedBytes);
        Assert.False(store.Has("android", "a.json"));
        Assert.True(store.Has("android", "b.json"));
    }

    [Fact]
    public async Task Disabled_flag_short_circuits_even_when_overcap()
    {
        var store = new FakeStore();
        store.Add("android", "huge.json", 50_000, DateTimeOffset.UtcNow.AddDays(-90));

        var sut = Build(store, Opts(maxBytes: 1000, maxAgeDays: 30, enabled: false));
        var report = await sut.SweepAsync("test", default);

        Assert.False(report.DidWork);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Disk_guard_evicts_oldest_first_when_free_below_floor()
    {
        var store = new FakeStore();
        // 10 recent, in-cap reports of 1000b. Byte cap is huge so only the disk guard can fire.
        for (var i = 0; i < 10; i++)
            store.Add("android", $"r{i:D2}.json", 1000, DateTimeOffset.UtcNow.AddMinutes(-(20 - i)));

        // Disk: 1000 free, floor 5000 → deficit 4000 +10% cushion = 4400 → evict 5 (5000b).
        var probe = new FakeDiskProbe(totalBytes: 100_000, freeBytes: 1_000);
        var sut = Build(store, Opts(maxBytes: 1_000_000, maxAgeDays: 30, minFreeDiskBytes: 5_000), probe);
        var report = await sut.SweepAsync("test", default);

        Assert.Equal(0, report.DeletedByAge);
        Assert.Equal(0, report.DeletedBySize);
        Assert.Equal(5, report.DeletedByDisk);
        Assert.Equal(5, store.Count);
        Assert.False(store.Has("android", "r00.json"), "oldest must be evicted first");
        Assert.True(store.Has("android", "r09.json"), "newest must survive");
    }

    [Fact]
    public async Task Disk_guard_evicts_when_usage_percent_exceeded()
    {
        var store = new FakeStore();
        for (var i = 0; i < 5; i++)
            store.Add("android", $"r{i:D2}.json", 1000, DateTimeOffset.UtcNow.AddMinutes(-(10 - i)));

        // total 10000, free 500 → 95% used. Cap at 80% → maxUsed 8000, deficit 1500 +10% = 1650 → evict 2.
        var probe = new FakeDiskProbe(totalBytes: 10_000, freeBytes: 500);
        var sut = Build(store, Opts(maxBytes: 1_000_000, maxAgeDays: 30, maxDiskUsagePercent: 80), probe);
        var report = await sut.SweepAsync("test", default);

        Assert.Equal(2, report.DeletedByDisk);
        Assert.False(store.Has("android", "r00.json"));
        Assert.True(store.Has("android", "r04.json"));
    }

    [Fact]
    public async Task Disk_guard_is_off_by_default_even_when_disk_full()
    {
        var store = new FakeStore();
        store.Add("android", "a.json", 1000, DateTimeOffset.UtcNow.AddHours(-1));

        var probe = new FakeDiskProbe(totalBytes: 100, freeBytes: 0); // bone dry
        // Defaults: minFreeDiskBytes = 0, maxDiskUsagePercent = 0 → guard disarmed.
        var sut = Build(store, Opts(maxBytes: 1_000_000, maxAgeDays: 30), probe);
        var report = await sut.SweepAsync("test", default);

        Assert.Equal(0, report.DeletedByDisk);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Disk_guard_skips_gracefully_when_free_space_unknown()
    {
        var store = new FakeStore();
        store.Add("android", "a.json", 1000, DateTimeOffset.UtcNow.AddHours(-1));

        var sut = Build(store, Opts(maxBytes: 1_000_000, maxAgeDays: 30, minFreeDiskBytes: 5_000),
            FakeDiskProbe.Unavailable());
        var report = await sut.SweepAsync("test", default);

        Assert.Equal(0, report.DeletedByDisk);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Disk_guard_frees_what_it_can_then_stops_when_out_of_reports()
    {
        var store = new FakeStore();
        store.Add("android", "a.json", 1000, DateTimeOffset.UtcNow.AddHours(-3));
        store.Add("android", "b.json", 1000, DateTimeOffset.UtcNow.AddHours(-2));
        store.Add("android", "c.json", 1000, DateTimeOffset.UtcNow.AddHours(-1));

        // Demand far more free space than report blobs can supply: it deletes all of them and stops.
        var probe = new FakeDiskProbe(totalBytes: 2_000_000, freeBytes: 0);
        var sut = Build(store, Opts(maxBytes: 1_000_000, maxAgeDays: 30, minFreeDiskBytes: 1_000_000), probe);
        var report = await sut.SweepAsync("test", default);

        Assert.Equal(3, report.DeletedByDisk);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Disk_guard_runs_after_age_and_size_passes()
    {
        var store = new FakeStore();
        store.Add("android", "ancient.json", 500, DateTimeOffset.UtcNow.AddDays(-60));   // age pass
        store.Add("android", "r1.json", 1000, DateTimeOffset.UtcNow.AddHours(-3));
        store.Add("android", "r2.json", 1000, DateTimeOffset.UtcNow.AddHours(-2));
        store.Add("android", "r3.json", 1000, DateTimeOffset.UtcNow.AddHours(-1));

        // Byte cap won't fire (3000 of in-window data, cap 1_000_000). Disk floor forces eviction
        // of the oldest in-window report after the age pass already removed "ancient".
        var probe = new FakeDiskProbe(totalBytes: 100_000, freeBytes: 1_000);
        var sut = Build(store, Opts(maxBytes: 1_000_000, maxAgeDays: 30, minFreeDiskBytes: 2_000), probe);
        var report = await sut.SweepAsync("test", default);

        Assert.Equal(1, report.DeletedByAge);
        Assert.Equal(0, report.DeletedBySize);
        Assert.True(report.DeletedByDisk >= 1);
        Assert.False(store.Has("android", "ancient.json"));
        Assert.False(store.Has("android", "r1.json"), "oldest in-window evicted by disk guard");
        Assert.True(store.Has("android", "r3.json"), "newest survives");
    }

    [Fact]
    public void Stats_reports_oldest_newest_and_total()
    {
        var store = new FakeStore();
        var oldest = DateTimeOffset.UtcNow.AddDays(-10);
        var newest = DateTimeOffset.UtcNow.AddHours(-1);
        store.Add("android", "a.json", 100, oldest);
        store.Add("ios", "b.json", 200, newest, attachment: 50);

        var sut = Build(store, Opts(maxBytes: 10_000, maxAgeDays: 30));
        var stats = sut.GetStats();

        Assert.Equal(350, stats.UsedBytes);
        Assert.Equal(2, stats.ReportCount);
        Assert.Equal(oldest, stats.Oldest);
        Assert.Equal(newest, stats.Newest);
        Assert.True(stats.Enabled);
        Assert.Equal(30, stats.MaxAgeDays);
    }

    private sealed class FakeStore : RSCIReportStore
    {
        private readonly Dictionary<(string Platform, string FileName), RSCStoredReport> _items = new();
        public int Count => _items.Count;

        public void Add(string platform, string fileName, long sizeBytes, DateTimeOffset submittedAt, long? attachment = null)
        {
            _items[(platform, fileName)] = new RSCStoredReport(
                Platform: platform,
                FileName: fileName,
                SizeBytes: sizeBytes,
                SubmittedAt: submittedAt,
                AttachmentFileName: attachment is null ? null : fileName + ".gz",
                AttachmentSizeBytes: attachment);
        }

        public bool Has(string platform, string fileName) => _items.ContainsKey((platform, fileName));

        public Task<RSCStoredReport> SaveAsync(RSCProblemReport report, ReadOnlyMemory<byte> jsonBytes, Stream? attachment, long? attachmentLength, string ingestionChannel, CancellationToken ct)
            => throw new NotSupportedException();

        public IReadOnlyList<RSCStoredReport> List(string platform) =>
            _items.Where(kv => kv.Key.Platform == platform).Select(kv => kv.Value).ToList();

        public Stream? OpenRead(string platform, string fileName) => null;

        public bool Delete(string platform, string fileName) => _items.Remove((platform, fileName));
    }

    private sealed class FakeDiskProbe : RSCIDiskSpaceProbe
    {
        private readonly (long TotalBytes, long FreeBytes)? _result;
        public FakeDiskProbe(long totalBytes, long freeBytes) => _result = (totalBytes, freeBytes);
        private FakeDiskProbe() => _result = null;
        public static FakeDiskProbe Unavailable() => new();
        public (long TotalBytes, long FreeBytes)? Probe(string path) => _result;
    }

    private sealed class NullAuditLog : RSCIAuditLog
    {
        public Task RecordAsync(RSCAuditEntry entry, CancellationToken ct) => Task.CompletedTask;
        public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<RSCAuditEntry>> RecentAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RSCAuditEntry>>(Array.Empty<RSCAuditEntry>());
    }
}
