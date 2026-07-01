using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Admin.Models;
using ReportService.Admin.Services;
using ReportService.Options;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Covers the Kind filter on the /Errors listing. The page scopes its listing to the fault
/// population (<c>KindIn: ["crash", "error"]</c>) and the Kind dropdown narrows to one kind. The
/// resolution lives in <see cref="RSAReportListingService"/> (ResolveKindIn): an in-scope pick
/// narrows to that kind, an out-of-scope pick is ignored so a hand-edited <c>kind=</c> query can't
/// widen a listing past its page boundary.
/// </summary>
public class ErrorsKindFilterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rs-kindfilter-{Guid.NewGuid():N}");
    private readonly RSCReportServiceOptions _options;
    private readonly RSCSqliteReportIndex _index;
    private readonly RSAReportListingService _listing;

    // Mirrors RSAErrorsModel.Scope — the full fault population the Errors page lists.
    private static readonly RSAReportListingScope ErrorsScope = new(KindIn: new[] { "crash", "error" });

    public ErrorsKindFilterTests()
    {
        Directory.CreateDirectory(_root);
        _options = new RSCReportServiceOptions { ReportsRoot = _root, SqliteDbPath = "kindfilter.db", Storage = "SqliteIndex" };
        _index = new RSCSqliteReportIndex(_options, NullLogger<RSCSqliteReportIndex>.Instance);
        var fileStore = new RSCFileSystemReportStore(_options, NullLogger<RSCFileSystemReportStore>.Instance);
        // The per-app read path lists through an indexing store (a file store + its own SQLite index),
        // the same shape the fan-out store wraps per app — so List() returns index-backed rows incl. Kind.
        var store = new RSCSqliteIndexingReportStore(fileStore, _index, NullLogger<RSCSqliteIndexingReportStore>.Instance);
        _listing = new RSAReportListingService(store, _options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Errors_listing_spans_crashes_and_errors_but_excludes_other_kinds()
    {
        await SeedAsync();

        var page = await _listing.ListAsync(new RSAReportsFilterInput(), 100, ErrorsScope, default);

        // The per-app read path reads the store (not a single global index), so UsedIndex is false.
        Assert.False(page.UsedIndex);
        // 2 crashes + 2 errors in scope; the analytics + null-kind rows are excluded.
        Assert.Equal(4, page.TotalMatched);
        Assert.All(page.Items, r => Assert.Contains(r.Kind, new[] { "crash", "error" }));
    }

    [Fact]
    public async Task Kind_pick_narrows_the_listing_to_a_single_kind()
    {
        await SeedAsync();

        var crashes = await _listing.ListAsync(new RSAReportsFilterInput { Kind = "crash" }, 100, ErrorsScope, default);
        Assert.Equal(2, crashes.TotalMatched);
        Assert.All(crashes.Items, r => Assert.Equal("crash", r.Kind));

        var errors = await _listing.ListAsync(new RSAReportsFilterInput { Kind = "error" }, 100, ErrorsScope, default);
        Assert.Equal(2, errors.TotalMatched);
        Assert.All(errors.Items, r => Assert.Equal("error", r.Kind));
    }

    [Fact]
    public async Task Out_of_scope_kind_pick_is_ignored_and_the_page_scope_holds()
    {
        await SeedAsync();

        // "analytics" is not part of the Errors page scope — the pick must be dropped, leaving the
        // full crash+error population rather than leaking analytics rows in.
        var page = await _listing.ListAsync(new RSAReportsFilterInput { Kind = "analytics" }, 100, ErrorsScope, default);

        Assert.Equal(4, page.TotalMatched);
        Assert.All(page.Items, r => Assert.Contains(r.Kind, new[] { "crash", "error" }));
    }

    private async Task SeedAsync()
    {
        var n = 0;
        async Task AddAsync(string? kind)
        {
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            await _index.UpsertAsync(new RSCReportMetadata(
                Platform: "android",
                FileName: $"problem-report_20260101-0000{n++:D2}_{suffix}.json",
                SubmittedAt: DateTimeOffset.UtcNow,
                DeviceModel: null, Title: null, EmailHash: null, PharmacyId: null, AppVersion: null,
                HasAttachment: false, SizeBytes: 1, AttachmentSizeBytes: null, LabelsJson: null,
                Kind: kind), default);
        }

        await AddAsync("crash");
        await AddAsync("crash");
        await AddAsync("error");
        await AddAsync("error");
        await AddAsync("analytics");
        await AddAsync(null);
    }
}
