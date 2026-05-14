using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>v1: initial <c>problem_reports</c> table + the platform/submitted-at index.</summary>
internal sealed class RSCM001_CreateProblemReports : RSCISchemaMigration
{
    public int Version => 1;
    public string Description => "create problem_reports table + platform/submitted_at index";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS problem_reports (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  platform TEXT NOT NULL,
  file_name TEXT NOT NULL,
  submitted_at TEXT NOT NULL,
  device_model TEXT,
  type TEXT,
  title TEXT,
  email_hash TEXT,
  pharmacy_id TEXT,
  app_version TEXT,
  has_attachment INTEGER NOT NULL,
  size_bytes INTEGER NOT NULL,
  attachment_bytes INTEGER,
  labels_json TEXT,
  UNIQUE(platform, file_name)
);
CREATE INDEX IF NOT EXISTS idx_problem_reports_platform_submitted
  ON problem_reports(platform, submitted_at DESC);");
    }
}
