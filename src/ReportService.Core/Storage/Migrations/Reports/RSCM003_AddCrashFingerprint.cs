using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v3: add <c>user_id</c>, <c>phone</c>, and <c>top_frame</c> so the admin can filter on the new
/// SDK-supplied identifiers and group crashes by their first stack frame instead of the
/// ProblemType enum (which collapses every <c>AppError</c> into one bucket).
/// </summary>
internal sealed class RSCM003_AddCrashFingerprint : RSCISchemaMigration
{
    public int Version => 3;
    public string Description => "add user_id, phone, top_frame columns";

    public void Up(SqliteConnection conn)
    {
        if (!RSCMigrationHelpers.ColumnExists(conn, "problem_reports", "user_id"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE problem_reports ADD COLUMN user_id TEXT NULL;");
        }

        if (!RSCMigrationHelpers.ColumnExists(conn, "problem_reports", "phone"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE problem_reports ADD COLUMN phone TEXT NULL;");
        }

        if (!RSCMigrationHelpers.ColumnExists(conn, "problem_reports", "top_frame"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE problem_reports ADD COLUMN top_frame TEXT NULL;");
        }

        RSCMigrationHelpers.Execute(conn,
            "CREATE INDEX IF NOT EXISTS idx_problem_reports_user_id ON problem_reports(user_id);");
        RSCMigrationHelpers.Execute(conn,
            "CREATE INDEX IF NOT EXISTS idx_problem_reports_top_frame ON problem_reports(top_frame);");
    }
}
