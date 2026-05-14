using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v4: create the <c>forced_reports</c> table — the allow-list of identifiers that mobile clients
/// query at startup to learn whether they should automatically POST a Report-a-Problem entry on
/// the next backend fetch. Operators add IDs through the admin UI; the public check endpoint
/// reads from this table. Sharing the reports DB rather than spawning a third SQLite file keeps
/// the operator's backup story (one file) simple.
/// </summary>
internal sealed class RSCM004_CreateForcedReports : RSCISchemaMigration
{
    public int Version => 4;
    public string Description => "create forced_reports table";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS forced_reports (
  id        TEXT PRIMARY KEY,
  added_at  TEXT NOT NULL,
  note      TEXT NULL
);");
    }
}
