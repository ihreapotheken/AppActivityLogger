using Microsoft.Data.Sqlite;
using ReportService.Storage.Migrations;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Exercises the migration framework directly: ladder application, replay-idempotency, partial-failure
/// rollback, version validation. Uses an in-memory SQLite DB so each test is hermetic.
/// </summary>
public class SchemaRunnerTests
{
    [Fact]
    public void Fresh_database_runs_full_ladder_in_order()
    {
        using var conn = OpenInMemory();

        var runner = new RSCSchemaRunner(new RSCISchemaMigration[]
        {
            new RecordingMigration(1, "create"),
            new RecordingMigration(2, "alter"),
            new RecordingMigration(3, "index"),
        });

        var ranBefore = RecordingMigration.RanVersions.Count;
        var version = runner.Run(conn);

        Assert.Equal(3, version);
        Assert.Equal(3, runner.TargetVersion);
        Assert.Equal(new[] { 1, 2, 3 }, RecordingMigration.RanVersions.Skip(ranBefore));
        Assert.Equal(3, RSCSchemaRunner.CurrentVersion(conn));
    }

    [Fact]
    public void Replay_skips_already_applied_migrations()
    {
        using var conn = OpenInMemory();

        var runner = new RSCSchemaRunner(new RSCISchemaMigration[]
        {
            new RecordingMigration(1, "first"),
            new RecordingMigration(2, "second"),
        });
        runner.Run(conn);

        var snapshot = RecordingMigration.RanVersions.Count;
        var version = runner.Run(conn);
        Assert.Equal(2, version);
        Assert.Equal(snapshot, RecordingMigration.RanVersions.Count); // nothing else ran
    }

    [Fact]
    public void Partial_database_only_applies_pending_migrations()
    {
        using var conn = OpenInMemory();

        // Manually pin user_version = 1 and create a stub problem_reports — simulating an old DB.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE problem_reports (id INTEGER PRIMARY KEY, name TEXT); PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }

        var runner = new RSCSchemaRunner(new RSCISchemaMigration[]
        {
            new ThrowingMigration(1, "must be skipped"),  // already applied
            new ColumnAddingMigration(2, "name2"),         // must run
        });

        var version = runner.Run(conn);
        Assert.Equal(2, version);
        Assert.True(RSCMigrationHelpers.ColumnExists(conn, "problem_reports", "name2"));
    }

    [Fact]
    public void Failing_migration_rolls_back_and_keeps_previous_version()
    {
        using var conn = OpenInMemory();

        var runner = new RSCSchemaRunner(new RSCISchemaMigration[]
        {
            new ColumnAddingMigration(1, "first"),
            new ThrowingMigration(2, "fails"),
            new ColumnAddingMigration(3, "never"),
        });

        Assert.Throws<InvalidOperationException>(() => runner.Run(conn));
        Assert.Equal(1, RSCSchemaRunner.CurrentVersion(conn)); // v1 committed; v2 rolled back
    }

    [Fact]
    public void Empty_migration_list_throws()
    {
        Assert.Throws<ArgumentException>(() => new RSCSchemaRunner(Array.Empty<RSCISchemaMigration>()));
    }

    [Fact]
    public void Duplicate_versions_throw()
    {
        Assert.Throws<ArgumentException>(() => new RSCSchemaRunner(new RSCISchemaMigration[]
        {
            new RecordingMigration(1, "a"),
            new RecordingMigration(1, "b"),
        }));
    }

    [Fact]
    public void Zero_or_negative_versions_throw()
    {
        Assert.Throws<ArgumentException>(() => new RSCSchemaRunner(new RSCISchemaMigration[]
        {
            new RecordingMigration(0, "zero"),
        }));
    }

    [Fact]
    public void CurrentVersion_reads_pragma_user_version()
    {
        using var conn = OpenInMemory();
        Assert.Equal(0, RSCSchemaRunner.CurrentVersion(conn));

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version = 7;";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(7, RSCSchemaRunner.CurrentVersion(conn));
    }

    private static SqliteConnection OpenInMemory()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    /// <summary>Migration that records its execution; useful for asserting "what ran in what order".</summary>
    private sealed class RecordingMigration : RSCISchemaMigration
    {
        public static readonly List<int> RanVersions = new();

        public RecordingMigration(int version, string description)
        {
            Version = version;
            Description = description;
        }

        public int Version { get; }
        public string Description { get; }

        public void Up(SqliteConnection conn) => RanVersions.Add(Version);
    }

    /// <summary>Migration that adds a column to <c>problem_reports</c>, useful for proving DDL took effect.</summary>
    private sealed class ColumnAddingMigration : RSCISchemaMigration
    {
        private readonly string _column;

        public ColumnAddingMigration(int version, string column)
        {
            Version = version;
            _column = column;
        }

        public int Version { get; }
        public string Description => $"add column {_column}";

        public void Up(SqliteConnection conn)
        {
            RSCMigrationHelpers.Execute(conn, "CREATE TABLE IF NOT EXISTS problem_reports (id INTEGER PRIMARY KEY);");
            if (!RSCMigrationHelpers.ColumnExists(conn, "problem_reports", _column))
                RSCMigrationHelpers.Execute(conn, $"ALTER TABLE problem_reports ADD COLUMN {_column} TEXT;");
        }
    }

    /// <summary>Migration that always throws — used to prove rollback semantics.</summary>
    private sealed class ThrowingMigration : RSCISchemaMigration
    {
        public ThrowingMigration(int version, string description)
        {
            Version = version;
            Description = description;
        }

        public int Version { get; }
        public string Description { get; }

        public void Up(SqliteConnection conn) => throw new InvalidOperationException("synthetic failure");
    }
}
