using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.DeepLinks;

/// <summary>
/// v2 of the deferred deep-linking schema. Adds <c>query_params</c> to
/// <c>deferred_deep_link_clicks</c> — a JSON object of the attribution/campaign query parameters
/// captured with the click (from the smart link's query string or the capture body). The column is
/// nullable; existing rows simply have no captured parameters.
/// </summary>
internal sealed class RSCDM002_AddClickQueryParams : RSCISchemaMigration
{
    public int Version => 2;
    public string Description => "add query_params (JSON) to deferred_deep_link_clicks";

    public void Up(SqliteConnection conn)
    {
        // ColumnExists guard keeps the ALTER idempotent if a partially-applied migration is retried.
        if (!RSCMigrationHelpers.ColumnExists(conn, "deferred_deep_link_clicks", "query_params"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE deferred_deep_link_clicks ADD COLUMN query_params TEXT;");
        }
    }
}
