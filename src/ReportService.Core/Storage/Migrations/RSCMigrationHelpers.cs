using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations;

/// <summary>Small SQLite introspection helpers used by migration <c>Up</c> bodies.</summary>
public static class RSCMigrationHelpers
{
    /// <summary>True iff <paramref name="table"/> already has a column named <paramref name="column"/>.</summary>
    public static bool ColumnExists(SqliteConnection conn, string table, string column)
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

    /// <summary>Convenience for migrations that just need to fire a single statement.</summary>
    public static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
