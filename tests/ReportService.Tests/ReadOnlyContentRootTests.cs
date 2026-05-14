using System.Net;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Simulates the Docker <c>read_only: true</c> / systemd <c>ProtectSystem=strict</c> topology by
/// pointing the content root at a directory we chmod to 0555 for the duration of the test, while
/// <c>ReportsRoot</c> + the SQLite files live on a separate writable path. Proves that
/// <c>/api/health</c> and an authenticated list request still return 200 — regressing the
/// SQLITE_CANTOPEN class of failure that knocks health endpoints over when the abuse tracker can't
/// create its DB file.
/// </summary>
public sealed class ReadOnlyContentRootTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"rs-ro-{Guid.NewGuid():N}");
    private readonly string _reportsRoot = Path.Combine(Path.GetTempPath(), $"rs-ro-reports-{Guid.NewGuid():N}");

    public ReadOnlyContentRootTests()
    {
        Directory.CreateDirectory(_contentRoot);
        Directory.CreateDirectory(_reportsRoot);
        MakeReadOnly(_contentRoot);
    }

    public void Dispose()
    {
        MakeWritable(_contentRoot);
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_reportsRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Health_and_authenticated_list_succeed_under_read_only_content_root()
    {
        await using var app = new ReadOnlyFactory(_contentRoot, _reportsRoot);

        var client = app.CreateClient();

        // /api/health must work anonymously — it goes through authentication middleware, which
        // resolves the RSCIAuthAbuseTracker. If the tracker can't open its DB, we get a 500 here.
        var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // Authenticated list: exercises the storage read path + the abuse tracker's "clear" call.
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        var list = await client.GetAsync("/api/problem-reports/android");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        // Nothing may have been written into the read-only content root.
        foreach (var entry in Directory.EnumerateFileSystemEntries(_contentRoot))
            Assert.Fail($"unexpected write into the read-only content root: {entry}");

        // The state files must live under the writable ReportsRoot.
        Assert.True(File.Exists(Path.Combine(_reportsRoot, "auth-abuse.db")),
            "auth-abuse.db should have been created under ReportsRoot");
    }

    private static void MakeReadOnly(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            new DirectoryInfo(path).Attributes |= FileAttributes.ReadOnly;
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute
                                  | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                  | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void MakeWritable(string path)
    {
        if (!Directory.Exists(path)) return;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            new DirectoryInfo(path).Attributes &= ~FileAttributes.ReadOnly;
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                  | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                  | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private sealed class ReadOnlyFactory : IngestionAppFactory
    {
        private readonly string _contentRoot;
        private readonly string _reportsRootOverride;

        public ReadOnlyFactory(string contentRoot, string reportsRoot)
        {
            _contentRoot = contentRoot;
            _reportsRootOverride = reportsRoot;
            Configure = baseline => baseline with
            {
                ReportsRoot = reportsRoot,
                // Relative paths — exactly the values a default appsettings.json would ship with.
                // Resolution should anchor them under ReportsRoot, not under the read-only CWD.
                SqliteDbPath = "reports.db",
                AuthAbuseDbPath = "auth-abuse.db",
                Storage = "FileSystem"
            };
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(_contentRoot);
            base.ConfigureWebHost(builder);
        }
    }
}
