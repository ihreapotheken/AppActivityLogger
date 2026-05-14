using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations;

/// <summary>
/// One step in a database's schema ladder. Each migration owns a unique <see cref="Version"/> and
/// is responsible for moving a database that's at <c>Version - 1</c> up to <c>Version</c>. A
/// <see cref="RSCSchemaRunner"/> picks the migrations whose version is greater than the database's
/// current <c>PRAGMA user_version</c> and runs them in ascending order.
/// </summary>
/// <remarks>
/// Implementations should be idempotent where SQLite allows it (<c>CREATE TABLE IF NOT EXISTS</c>,
/// <c>CREATE INDEX IF NOT EXISTS</c>, <c>ColumnExists</c> guards before <c>ALTER TABLE</c>) so a
/// partially-applied or rerun migration does not blow up.
/// </remarks>
public interface RSCISchemaMigration
{
    /// <summary>Strictly increasing positive integer that names this step in the ladder.</summary>
    int Version { get; }

    /// <summary>One-line description rendered in startup logs and the admin Status page.</summary>
    string Description { get; }

    /// <summary>Apply the schema change to <paramref name="conn"/>. Runs inside a transaction owned by the runner.</summary>
    void Up(SqliteConnection conn);
}
