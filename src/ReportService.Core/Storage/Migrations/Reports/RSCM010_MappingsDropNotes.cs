using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v10: drop the <c>notes</c> column from <c>mappings</c>. The field was vestigial — operators
/// never had a real workflow that required free-form text alongside an upload (the
/// <c>(platform, app_version, label)</c> triple already identifies the build, the audit log
/// records the operator + timestamp), and surveying the column post-rollout showed every entry
/// was either null or duplicated information already on the row. Removing it keeps the upload
/// form to the minimum-required fields.
/// </summary>
internal sealed class RSCM010_MappingsDropNotes : RSCISchemaMigration
{
    public int Version => 10;
    public string Description => "drop notes column from mappings";

    public void Up(SqliteConnection conn)
    {
        // SQLite supports `ALTER TABLE ... DROP COLUMN` since 3.35 (Microsoft.Data.Sqlite ships
        // a current SQLite). If the column is already gone (someone rolled forward + back) the
        // statement throws — wrap so the migration stays idempotent across replays.
        try
        {
            RSCMigrationHelpers.Execute(conn, "ALTER TABLE mappings DROP COLUMN notes;");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 /* SQLITE_ERROR — no such column */)
        {
            // already dropped, nothing to do
        }
    }
}
