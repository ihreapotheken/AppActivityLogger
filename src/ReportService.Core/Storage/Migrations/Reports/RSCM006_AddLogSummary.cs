using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v6: add <c>log_summary_json</c>. At ingest, the indexing store decodes the gzip attachment
/// when the SDK ships a plaintext-JSON log payload (iOS today) and persists a small structured
/// summary — entry counts per level, http-event count, time range — that the admin's report
/// detail view can render without re-decoding the gzip on every page hit. Android attachments
/// are encrypted client-side, so the column stays null for those rows.
/// </summary>
internal sealed class RSCM006_AddLogSummary : RSCISchemaMigration
{
    public int Version => 6;
    public string Description => "add log_summary_json column";

    public void Up(SqliteConnection conn)
    {
        if (!RSCMigrationHelpers.ColumnExists(conn, "problem_reports", "log_summary_json"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE problem_reports ADD COLUMN log_summary_json TEXT NULL;");
        }
    }
}
