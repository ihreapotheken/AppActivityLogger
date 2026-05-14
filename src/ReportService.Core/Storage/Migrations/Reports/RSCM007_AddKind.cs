using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v7: index the SDK-supplied <c>kind</c> field so the admin can filter and bucket without
/// re-reading every JSON body. The four submission pages — Analytics (<c>analytics</c>),
/// Problem reports (anything-else with attachment), Error reports (<c>crash</c>), and
/// All reports (no constraint) — derive their scope from this column. Old rows that predate
/// the column stay null, which the WHERE clauses treat as "no kind" and exclude from the
/// kind-restricted views.
/// </summary>
internal sealed class RSCM007_AddKind : RSCISchemaMigration
{
    public int Version => 7;
    public string Description => "add kind column + index";

    public void Up(SqliteConnection conn)
    {
        if (!RSCMigrationHelpers.ColumnExists(conn, "problem_reports", "kind"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE problem_reports ADD COLUMN kind TEXT NULL;");
        }
        RSCMigrationHelpers.Execute(conn,
            "CREATE INDEX IF NOT EXISTS idx_problem_reports_kind ON problem_reports(kind);");
    }
}
