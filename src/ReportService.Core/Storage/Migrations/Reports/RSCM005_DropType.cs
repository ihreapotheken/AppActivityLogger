using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v5: drop the unused <c>type</c> column. Originally a free-form category supplied by the SDK,
/// but every shipping client sent the same placeholder string ("AppError" / "error-in-the-app"),
/// so it never disambiguated anything. Crash bucketing keys on <c>top_frame</c> now (extracted
/// at ingest from the gzip attachment), and there are no published consumers depending on the
/// column — safest to remove it outright rather than carry a half-deprecated stub.
/// Idempotent: skips the <c>ALTER TABLE</c> when the column is already gone.
/// </summary>
internal sealed class RSCM005_DropType : RSCISchemaMigration
{
    public int Version => 5;
    public string Description => "drop unused type column";

    public void Up(SqliteConnection conn)
    {
        if (RSCMigrationHelpers.ColumnExists(conn, "problem_reports", "type"))
        {
            // SQLite ≥ 3.35 supports ALTER TABLE … DROP COLUMN directly.
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE problem_reports DROP COLUMN type;");
        }
    }
}
