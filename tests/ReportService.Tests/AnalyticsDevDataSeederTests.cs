using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Analytics;
using ReportService.Options;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Covers CODE-REVIEW finding #32 (MEDIUM): RSAAnalyticsDevDataSeeder must be idempotent. The
/// seeder runs on every Development startup before app.Run(); CLAUDE.md and its own header promise
/// "all IDs are deterministic, so re-runs produce zero duplicate rows." This pins that contract so
/// a future edit that makes a seeded id non-deterministic (e.g. Guid.NewGuid) trips a red test
/// instead of duplicating 30 days of synthetic data on every restart.
///
/// The seeder is <c>internal static</c> in ReportService.Admin (no InternalsVisibleTo to the test
/// assembly), so we invoke SeedAsync via reflection against a real SQLite store + validator +
/// hasher wired into a minimal service provider — no mocks. NOTE: the seeder's id determinism is
/// the subject under test; the problem-report seeder is a separate type and is not exercised here.
/// </summary>
public class AnalyticsDevDataSeederTests : IDisposable
{
    private readonly string _root;
    private readonly RSCSqliteAnalyticsStore _store;
    private readonly ServiceProvider _sp;
    private readonly string? _priorSeedScale;

    public AnalyticsDevDataSeederTests()
    {
        // Synthetic seeding is opt-in (DefaultSeedScale = 0): with no ANALYTICS_SEED_SCALE the seeder
        // writes nothing. This suite is specifically testing what the seeder emits, so enable it for
        // the class's lifetime and restore the prior value in Dispose.
        _priorSeedScale = Environment.GetEnvironmentVariable("ANALYTICS_SEED_SCALE");
        Environment.SetEnvironmentVariable("ANALYTICS_SEED_SCALE", "1");

        _root = Path.Combine(Path.GetTempPath(), $"rs-analytics-seeder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var reportOptions = new RSCReportServiceOptions
        {
            ReportsRoot = _root,
            AllowedPlatforms = new[] { "android", "ios" }
        };
        var analyticsOptions = new RSCAnalyticsOptions
        {
            SqliteDbPath = "analytics-seeder.db",
            IdentifierHashPepper = "pepper-test"
        };
        _store = new RSCSqliteAnalyticsStore(reportOptions, analyticsOptions, NullLogger<RSCSqliteAnalyticsStore>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<RSCIAnalyticsStore>(_store);
        services.AddSingleton(new RSCAnalyticsValidator(analyticsOptions, reportOptions, RSATestCatalog.Permissive, new ReportService.Options.RSCCatalogOptions()));
        services.AddSingleton(new RSCAnalyticsIdentifierHasher(analyticsOptions));
        _sp = services.BuildServiceProvider();
    }

    private static async Task SeedAsync(IServiceProvider sp)
    {
        var adminAsm = typeof(ReportService.Admin.AdminProgram).Assembly;
        var seederType = adminAsm.GetType("ReportService.Admin.Services.RSAAnalyticsDevDataSeeder", throwOnError: true)!;
        var method = seederType.GetMethod("SeedAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, new object?[] { sp, NullLogger.Instance, CancellationToken.None })!;
        await task;
    }

    [Fact]
    public async Task Seeding_twice_inserts_zero_new_rows_on_the_second_run()
    {
        await SeedAsync(_sp);
        var afterFirst = await CountEventsAsync();
        Assert.True(afterFirst > 0, "seeder should insert at least one event on the first run");

        await SeedAsync(_sp);
        var afterSecond = await CountEventsAsync();

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task Known_deterministic_diagnostic_event_id_exists_exactly_once()
    {
        await SeedAsync(_sp);
        await SeedAsync(_sp);

        // sdk-diag-and-ep00 is a fixed id the diagnostics seeder always emits for android episode 0.
        Assert.Equal(1, await CountEventsByIdAsync("sdk-diag-and-ep00"));
    }

    private async Task<long> CountEventsAsync()
    {
        using var conn = OpenConn();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM analytics_events;";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<long> CountEventsByIdAsync(string eventId)
    {
        using var conn = OpenConn();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM analytics_events WHERE event_id = @e;";
        cmd.Parameters.AddWithValue("@e", eventId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private SqliteConnection OpenConn() => new(new SqliteConnectionStringBuilder
    {
        DataSource = _store.DbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString());

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ANALYTICS_SEED_SCALE", _priorSeedScale);
        _sp.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
