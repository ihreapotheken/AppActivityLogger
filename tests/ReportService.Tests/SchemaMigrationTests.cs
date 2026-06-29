using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Options;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Verifies the v1 → v2 schema migration: a v1 database (no <c>ingestion_channel</c> column) is
/// upgraded in place when the service opens it, and existing rows default to <c>"multipart"</c>.
/// Also asserts that fresh databases come up at the current schema version directly.
/// </summary>
public class SchemaMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rs-migrate-{Guid.NewGuid():N}");

    public SchemaMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Fresh_database_is_at_current_schema_version_with_channel_column()
    {
        var options = new RSCReportServiceOptions { ReportsRoot = _root, SqliteDbPath = "fresh.db" };
        _ = new RSCSqliteReportIndex(options, NullLogger<RSCSqliteReportIndex>.Instance);

        using var conn = new SqliteConnection($"Data Source={Path.Combine(_root, "fresh.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        Assert.Equal(12 /* current schema version */, Convert.ToInt32(cmd.ExecuteScalar()));

        Assert.True(ColumnExists(conn, "problem_reports", "ingestion_channel"));
    }

    [Fact]
    public async Task V1_database_is_upgraded_to_v2_and_old_rows_default_to_multipart()
    {
        var dbPath = Path.Combine(_root, "v1.db");

        // Hand-craft a v1 database — no ingestion_channel column, user_version = 1, one row.
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE problem_reports (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  platform TEXT NOT NULL,
  file_name TEXT NOT NULL,
  submitted_at TEXT NOT NULL,
  device_model TEXT,
  type TEXT,
  title TEXT,
  email_hash TEXT,
  pharmacy_id TEXT,
  app_version TEXT,
  has_attachment INTEGER NOT NULL,
  size_bytes INTEGER NOT NULL,
  attachment_bytes INTEGER,
  labels_json TEXT,
  UNIQUE(platform, file_name)
);
INSERT INTO problem_reports(platform, file_name, submitted_at, has_attachment, size_bytes)
VALUES('android', 'problem-report_20240101-000000_oldrow000000.json', '2024-01-01T00:00:00.0000000+00:00', 0, 100);
PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }

        // Open via the production class — migration should run.
        var options = new RSCReportServiceOptions { ReportsRoot = _root, SqliteDbPath = "v1.db" };
        var index = new RSCSqliteReportIndex(options, NullLogger<RSCSqliteReportIndex>.Instance);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(12 /* current schema version */, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        var rows = await index.ListAsync("android", default);
        Assert.Single(rows);
        Assert.Equal(RSCIngestionChannels.Multipart, rows[0].IngestionChannel);
    }

    [Fact]
    public async Task New_inserts_carry_the_supplied_channel()
    {
        var options = new RSCReportServiceOptions { ReportsRoot = _root, SqliteDbPath = "channel.db" };
        var index = new RSCSqliteReportIndex(options, NullLogger<RSCSqliteReportIndex>.Instance);

        await index.UpsertAsync(new RSCReportMetadata(
            Platform: "android",
            FileName: "problem-report_20260101-000000_jsonrowabcdef.json",
            SubmittedAt: DateTimeOffset.UtcNow,
            DeviceModel: null, Title: null,
            EmailHash: null, PharmacyId: null, AppVersion: null,
            HasAttachment: false, SizeBytes: 64, AttachmentSizeBytes: null, LabelsJson: null,
            IngestionChannel: RSCIngestionChannels.Json), default);

        var rows = await index.ListAsync("android", default);
        Assert.Single(rows);
        Assert.Equal(RSCIngestionChannels.Json, rows[0].IngestionChannel);
    }

    [Fact]
    public async Task Filter_by_channel_returns_only_matching_rows()
    {
        var options = new RSCReportServiceOptions { ReportsRoot = _root, SqliteDbPath = "filter.db" };
        var index = new RSCSqliteReportIndex(options, NullLogger<RSCSqliteReportIndex>.Instance);

        async Task SeedAsync(string fileName, string channel) =>
            await index.UpsertAsync(new RSCReportMetadata(
                "android", fileName, DateTimeOffset.UtcNow,
                null, null, null, null, null,
                false, 1, null, null, channel), default);

        await SeedAsync("problem-report_20260101-000000_aaaaaaaaaaaa.json", RSCIngestionChannels.Multipart);
        await SeedAsync("problem-report_20260101-000001_bbbbbbbbbbbb.json", RSCIngestionChannels.Json);
        await SeedAsync("problem-report_20260101-000002_cccccccccccc.json", RSCIngestionChannels.Json);

        RSCIReportIndexMaintenance maint = index;
        var jsonOnly = await maint.SearchAsync(new RSCReportFilter(IngestionChannel: RSCIngestionChannels.Json), default);
        Assert.Equal(2, jsonOnly.TotalMatched);
        Assert.All(jsonOnly.Items, r => Assert.Equal(RSCIngestionChannels.Json, r.IngestionChannel));
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
