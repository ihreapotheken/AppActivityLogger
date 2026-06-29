using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Options;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Verifies the resilience contract around the SQLite metadata index:
///   - when the index throws on every call, ingestion still returns 201 (files land on disk),
///   - listing continues to work via the disk fallback,
///   - rebuild from disk restores the SQLite rows afterwards.
/// </summary>
public class IndexDownResilienceTests
{
    [Fact]
    public async Task Ingestion_succeeds_when_index_is_broken()
    {
        await using var app = new BrokenIndexFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        using var form = new MultipartFormDataContent();
        var json = new StringContent(
            "{\"platform\":\"Android\",\"message\":\"still-works\"}",
            Encoding.UTF8, "application/json");
        form.Add(json, "json", "report.json");

        var res = await client.PostAsync("/partners/api/v2/report-problem", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        // Listing still sees the file: the storage decorator unions the disk fallback with the
        // (here, always-empty) index result, so metadata for the just-ingested file must appear.
        var list = await client.GetAsync("/api/problem-reports/android");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await list.Content.ReadAsStringAsync();
        Assert.Contains("problem-report_", body); // canonical basename prefix
        Assert.Contains("\"platform\":\"android\"", body);
    }

    [Fact]
    public async Task Rebuild_reconciles_missing_and_stale_rows()
    {
        var options = new RSCReportServiceOptions
        {
            ReportsRoot = Path.Combine(Path.GetTempPath(), $"rs-rebuild-{Guid.NewGuid():N}"),
            SqliteDbPath = "reports.db",
            Storage = "SqliteIndex"
        };
        Directory.CreateDirectory(options.ReportsRoot);

        try
        {
            var fileStore = new RSCFileSystemReportStore(options, NullLogger<RSCFileSystemReportStore>.Instance);
            var index = new RSCSqliteReportIndex(options, NullLogger<RSCSqliteReportIndex>.Instance);

            // 1. Write a real report to disk via the file store.
            var report = new ReportService.Models.RSCProblemReport(
                Platform: "android", Message: "rebuild-me",
                Title: null, DeviceModel: null, Email: null, PhoneNumber: null, Phone: null,
                PharmacyId: null, Source: null, AppVersion: null, FunctionalityImportance: null, Labels: null);
            var saved = await fileStore.SaveAsync(report,
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"platform\":\"android\",\"message\":\"rebuild-me\"}")),
                null, null, RSCIngestionChannels.Multipart, default);

            // 2. Inject a stale row into the index (points at a filename that doesn't exist on disk).
            await index.UpsertAsync(new RSCReportMetadata(
                Platform: "android",
                FileName: "problem-report_20200101-000000_deadbeefcafe.json",
                SubmittedAt: DateTimeOffset.UtcNow.AddYears(-1),
                DeviceModel: null, Title: null,
                EmailHash: null, PharmacyId: null, AppVersion: null,
                HasAttachment: false, SizeBytes: 42, AttachmentSizeBytes: null, LabelsJson: null), default);

            // Pre-state: one real row (from disk, not in index yet) + one stale row in index.
            RSCIReportIndexMaintenance maint = index;
            var preRebuild = await maint.SummarizeAsync(default);
            Assert.Single(preRebuild);

            var report2 = await maint.RebuildAsync(fileStore, new[] { "android" }, default);
            Assert.Equal(1, report2.Inserted);
            Assert.Equal(1, report2.StaleRemoved);

            // Post-rebuild: the disk report is in the index, the stale row is gone.
            var postRebuild = await index.ListAsync("android", default);
            Assert.Single(postRebuild);
            Assert.Equal(saved.FileName, postRebuild[0].FileName);
        }
        finally
        {
            try { Directory.Delete(options.ReportsRoot, recursive: true); } catch { }
        }
    }

    private sealed class BrokenIndex : RSCIReportIndex
    {
        public Task UpsertAsync(RSCReportMetadata metadata, CancellationToken ct) => throw new InvalidOperationException("broken");
        public Task<IReadOnlyList<RSCStoredReport>> ListAsync(string platform, CancellationToken ct) => throw new InvalidOperationException("broken");
        public Task<IReadOnlyList<RSCStoredReport>> ListAsync(string platform, int limit, int offset, CancellationToken ct) => throw new InvalidOperationException("broken");
        public Task<bool> DeleteAsync(string platform, string fileName, CancellationToken ct) => throw new InvalidOperationException("broken");
        public Task<bool> RecordLifetimeAndDeleteAsync(string platform, string fileName, CancellationToken ct) => throw new InvalidOperationException("broken");
    }

    /// <summary>
    /// Factory that wires the broken index through the resilient decorator — the same pipeline
    /// Program.cs uses in production, just with a stub that always throws.
    /// </summary>
    private sealed class BrokenIndexFactory : IngestionAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Switch to SqliteIndex semantics so the decorator chain runs.
                services.RemoveAll<RSCIReportStore>();
                services.AddSingleton<RSCFileSystemReportStore>();
                services.RemoveAll<RSCIReportIndex>();
                services.AddSingleton<RSCComponentHealth>();
                services.AddSingleton<RSCIReportIndex>(sp => new RSCResilientReportIndex(
                    () => new BrokenIndex(),
                    sp.GetRequiredService<RSCComponentHealth>(),
                    new NullLogger<RSCResilientReportIndex>()));
                services.AddSingleton<RSCIReportStore, RSCSqliteIndexingReportStore>();
            });
        }
    }
}
