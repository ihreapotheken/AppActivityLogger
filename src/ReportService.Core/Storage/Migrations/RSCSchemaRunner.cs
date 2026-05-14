using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReportService.Storage.Migrations;

/// <summary>
/// Drives a SQLite database from its current <c>PRAGMA user_version</c> up to the highest version
/// declared in its migration list. Each migration runs inside its own transaction; if a migration
/// fails the transaction is rolled back and the database stays at the previous version, so the
/// next bootstrap can retry cleanly.
/// </summary>
/// <remarks>
/// Versions must be strictly positive and unique. The constructor sorts the input by version and
/// validates both invariants up front — a misconfigured migration list fails fast at composition
/// time rather than after the first migration silently overwrites another.
/// </remarks>
public sealed class RSCSchemaRunner
{
    private readonly IReadOnlyList<RSCISchemaMigration> _migrations;
    private readonly ILogger _logger;

    public RSCSchemaRunner(IEnumerable<RSCISchemaMigration> migrations, ILogger? logger = null)
    {
        var list = migrations?.ToList() ?? throw new ArgumentNullException(nameof(migrations));
        if (list.Count == 0)
            throw new ArgumentException("at least one migration is required", nameof(migrations));

        list.Sort((a, b) => a.Version.CompareTo(b.Version));

        if (list[0].Version < 1)
            throw new ArgumentException($"migration version must be ≥ 1; got {list[0].Version}", nameof(migrations));

        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Version == list[i - 1].Version)
                throw new ArgumentException($"duplicate migration version {list[i].Version}", nameof(migrations));
        }

        _migrations = list;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Highest version declared in the migration list — what a fresh DB ends up at.</summary>
    public int TargetVersion => _migrations[^1].Version;

    /// <summary>Reads the DB's current <c>user_version</c> without applying anything.</summary>
    public static int CurrentVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Applies every migration whose version is strictly greater than the database's current
    /// <c>user_version</c>, in ascending version order. Returns the new version.
    /// </summary>
    public int Run(SqliteConnection conn)
    {
        var current = CurrentVersion(conn);

        foreach (var migration in _migrations)
        {
            if (migration.Version <= current) continue;

            _logger.LogInformation(
                "Applying schema migration v{Version}: {Description}",
                migration.Version, migration.Description);

            using var tx = conn.BeginTransaction();
            try
            {
                migration.Up(conn);

                using (var bump = conn.CreateCommand())
                {
                    bump.Transaction = tx;
                    bump.CommandText = $"PRAGMA user_version = {migration.Version};";
                    bump.ExecuteNonQuery();
                }

                tx.Commit();
                current = migration.Version;
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogError(ex,
                    "Schema migration v{Version} failed; rolled back, database remains at v{Current}",
                    migration.Version, current);
                throw;
            }
        }

        return current;
    }
}
