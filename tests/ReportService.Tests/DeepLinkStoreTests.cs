using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.DeepLinks;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Store-level coverage for the parts that scale and the retention plumbing: paginated/searchable
/// link listing, the in-memory enabled-link cache invalidation on writes, the persisted retention
/// setting, age-based click purge, and the retention worker sweep.
/// </summary>
public sealed class DeepLinkStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rs-dl-{Guid.NewGuid():N}");

    private RSCSqliteDeferredDeepLinkStore NewStore() => new(
        new RSCReportServiceOptions { ReportsRoot = _root },
        new RSCDeepLinkOptions { SqliteDbPath = $"deeplinks-{Guid.NewGuid():N}.db" },
        NullLogger<RSCSqliteDeferredDeepLinkStore>.Instance);

    private static Task Seed(RSCSqliteDeferredDeepLinkStore s, string slug, string pattern) =>
        s.UpsertLinkAsync(slug, slug, pattern, $"myapp://{slug}", enabled: true, CancellationToken.None);

    [Fact]
    public async Task Links_list_paginates_and_searches()
    {
        var s = NewStore();
        foreach (var name in new[] { "alpha", "beta", "gamma", "delta", "epsilon" })
            await Seed(s, name, $"/{name}");

        Assert.Equal(5, await s.CountLinksAsync(null, CancellationToken.None));

        var page1 = await s.ListLinksAsync(null, 2, 0, CancellationToken.None);
        var page2 = await s.ListLinksAsync(null, 2, 2, CancellationToken.None);
        var page3 = await s.ListLinksAsync(null, 2, 4, CancellationToken.None);
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Single(page3);
        // No row appears on two pages.
        Assert.Empty(page1.Select(l => l.Slug).Intersect(page2.Select(l => l.Slug)));

        Assert.Equal(1, await s.CountLinksAsync("alpha", CancellationToken.None));
        var hit = await s.ListLinksAsync("alph", 50, 0, CancellationToken.None);
        Assert.Equal("alpha", Assert.Single(hit).Slug);

        // LIKE metacharacters in the search term are treated literally (escaped), not as wildcards.
        Assert.Equal(0, await s.CountLinksAsync("%", CancellationToken.None));
    }

    [Fact]
    public async Task Clicks_filter_by_header_data()
    {
        var s = NewStore();
        await Seed(s, "promo", "/promo");
        var now = DateTimeOffset.UtcNow;

        // A: iPhone/Safari, German, on /promo → resolves to the seeded link (matched-eligible).
        await s.RecordClickAsync("203.0.113.5", "https://x.test/promo", "Mozilla/5.0 (iPhone) Safari",
            null, new Dictionary<string, string> { ["language"] = "de-DE", ["browser"] = "Safari" }, now, CancellationToken.None);
        // B: Android/Chrome, English, on /other → no link resolves (unmatched).
        await s.RecordClickAsync("198.51.100.9", "https://x.test/other", "Mozilla/5.0 (Android) Chrome",
            null, new Dictionary<string, string> { ["language"] = "en-US", ["browser"] = "Chrome" }, now, CancellationToken.None);

        // No filter → both.
        Assert.Equal(2, (await s.ListClicksAsync(new RSCDeepLinkClickFilter(), 50, CancellationToken.None)).Count);

        // IP substring.
        var byIp = await s.ListClicksAsync(new RSCDeepLinkClickFilter(Ip: "203.0.113"), 50, CancellationToken.None);
        Assert.Equal("203.0.113.5", Assert.Single(byIp).Ip);

        // User-Agent substring.
        var byUa = await s.ListClicksAsync(new RSCDeepLinkClickFilter(UserAgent: "iPhone"), 50, CancellationToken.None);
        Assert.Equal("203.0.113.5", Assert.Single(byUa).Ip);

        // Header free-text spans the signals JSON …
        var bySignal = await s.ListClicksAsync(new RSCDeepLinkClickFilter(Header: "de-DE"), 50, CancellationToken.None);
        Assert.Equal("203.0.113.5", Assert.Single(bySignal).Ip);
        // … and the User-Agent.
        var byUaHeader = await s.ListClicksAsync(new RSCDeepLinkClickFilter(Header: "Android"), 50, CancellationToken.None);
        Assert.Equal("198.51.100.9", Assert.Single(byUaHeader).Ip);

        // Matched state: A resolved to /promo, B did not.
        var matched = await s.ListClicksAsync(new RSCDeepLinkClickFilter(Matched: true), 50, CancellationToken.None);
        Assert.Equal("203.0.113.5", Assert.Single(matched).Ip);
        var unmatched = await s.ListClicksAsync(new RSCDeepLinkClickFilter(Matched: false), 50, CancellationToken.None);
        Assert.Equal("198.51.100.9", Assert.Single(unmatched).Ip);
    }

    [Fact]
    public async Task Resolution_sees_links_added_after_first_capture()
    {
        var s = NewStore();
        await Seed(s, "a", "/a");

        var first = await s.RecordClickAsync("203.0.113.1", "https://x/a/page", null, null, null, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal("a", first.LinkSlug);

        // Adding a more specific link must invalidate the cache so the next capture resolves it.
        await Seed(s, "ab", "/a/b");
        var second = await s.RecordClickAsync("203.0.113.2", "https://x/a/b/page", null, null, null, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal("ab", second.LinkSlug); // longest pattern wins, and it's visible immediately
    }

    [Fact]
    public async Task Click_retention_setting_roundtrips()
    {
        var s = NewStore();
        Assert.Null(await s.GetClickRetentionDaysAsync(CancellationToken.None));
        await s.SetClickRetentionDaysAsync(7, CancellationToken.None);
        Assert.Equal(7, await s.GetClickRetentionDaysAsync(CancellationToken.None));
        await s.SetClickRetentionDaysAsync(14, CancellationToken.None); // overwrite
        Assert.Equal(14, await s.GetClickRetentionDaysAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Purge_deletes_only_clicks_older_than_cutoff()
    {
        var s = NewStore();
        var now = DateTimeOffset.UtcNow;
        await s.RecordClickAsync("203.0.113.1", "https://x/old", null, null, null, now.AddDays(-10), CancellationToken.None);
        await s.RecordClickAsync("203.0.113.2", "https://x/new", null, null, null, now, CancellationToken.None);

        var deleted = await s.PurgeClicksOlderThanAsync(now.AddDays(-5), CancellationToken.None);
        Assert.Equal(1, deleted);

        var remaining = await s.ListRecentClicksAsync(100, CancellationToken.None);
        Assert.Equal("https://x/new", Assert.Single(remaining).PageUrl);
    }

    [Fact]
    public async Task Retention_worker_purges_using_persisted_period()
    {
        var s = NewStore();
        await s.SetClickRetentionDaysAsync(1, CancellationToken.None);
        await s.RecordClickAsync("203.0.113.1", "https://x/old", null, null, null, DateTimeOffset.UtcNow.AddDays(-2), CancellationToken.None);

        var worker = new RSCDeepLinkClickRetentionWorker(
            s, new RSCDeepLinkOptions(), NullLogger<RSCDeepLinkClickRetentionWorker>.Instance);
        var purged = await worker.SweepAsync(CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.Empty(await s.ListRecentClicksAsync(100, CancellationToken.None));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
