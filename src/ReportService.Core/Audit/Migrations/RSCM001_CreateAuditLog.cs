using Microsoft.Data.Sqlite;
using ReportService.Storage.Migrations;

namespace ReportService.Audit.Migrations;

/// <summary>v1: initial <c>audit_log</c> table + at-index for time-ordered reads.</summary>
internal sealed class RSCM001_CreateAuditLog : RSCISchemaMigration
{
    public int Version => 1;
    public string Description => "create audit_log table + at index";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS audit_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  at TEXT NOT NULL,
  actor TEXT NOT NULL,
  remote TEXT NOT NULL,
  action TEXT NOT NULL,
  target TEXT,
  details TEXT,
  success INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_audit_at ON audit_log(at DESC);");
    }
}
