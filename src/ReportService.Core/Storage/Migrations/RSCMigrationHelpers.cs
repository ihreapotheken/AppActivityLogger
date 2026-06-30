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

    /// <summary>
    /// Number of columns that make up <paramref name="table"/>'s primary key (0 for a rowid-only
    /// table, 1 for a single-column PK, &gt;1 for a composite). Lets a rebuild migration detect
    /// whether it already widened a PK so a re-entry after a rolled-back partial apply is a no-op.
    /// </summary>
    public static int PrimaryKeyColumnCount(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        var count = 0;
        // table_info columns: cid, name, type, notnull, dflt_value, pk (pk = position in key, 0 = not part of it).
        while (reader.Read())
        {
            if (reader.GetInt32(5) > 0) count++;
        }
        return count;
    }

    /// <summary>Convenience for migrations that just need to fire a single statement.</summary>
    public static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
