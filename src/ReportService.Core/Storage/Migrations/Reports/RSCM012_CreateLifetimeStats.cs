using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v12: create the <c>lifetime_report_stats</c> table — a per-platform rollup of every problem
/// report ever deleted. Deletions (operator action or retention sweep) fold the row's count + byte
/// footprint in here <em>before</em> destroying it, so the operator can still see lifetime totals
/// after the underlying reports are long gone. Sharing the reports DB keeps the one-file backup
/// story intact.
/// </summary>
internal sealed class RSCM012_CreateLifetimeStats : RSCISchemaMigration
{
    public int Version => 12;
    public string Description => "create lifetime_report_stats table";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS lifetime_report_stats (
  platform                 TEXT PRIMARY KEY,
  deleted_reports          INTEGER NOT NULL DEFAULT 0,
  deleted_with_attachment  INTEGER NOT NULL DEFAULT 0,
  deleted_json_bytes       INTEGER NOT NULL DEFAULT 0,
  deleted_attachment_bytes INTEGER NOT NULL DEFAULT 0,
  first_deleted_at         TEXT NULL,
  last_deleted_at          TEXT NULL
);");
    }
}
