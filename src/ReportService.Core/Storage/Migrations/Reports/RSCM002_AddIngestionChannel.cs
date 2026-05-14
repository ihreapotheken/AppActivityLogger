using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v2: add <c>ingestion_channel</c> so the admin can tell SDK multipart submissions apart from
/// the JSON-API path. Existing rows default to <c>'multipart'</c> via the column's <c>DEFAULT</c>.
/// Idempotent: skips the <c>ALTER TABLE</c> if the column already exists (a partially-applied
/// upgrade left it behind, or someone hand-altered the schema).
/// </summary>
internal sealed class RSCM002_AddIngestionChannel : RSCISchemaMigration
{
    public int Version => 2;
    public string Description => "add ingestion_channel column + index";

    public void Up(SqliteConnection conn)
    {
        if (!RSCMigrationHelpers.ColumnExists(conn, "problem_reports", "ingestion_channel"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE problem_reports ADD COLUMN ingestion_channel TEXT NOT NULL DEFAULT 'multipart';");
        }

        RSCMigrationHelpers.Execute(conn,
            "CREATE INDEX IF NOT EXISTS idx_problem_reports_channel ON problem_reports(ingestion_channel);");
    }
}
