using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.DeepLinks;

/// <summary>
/// v4 of the deferred deep-linking schema. Adds <c>signals</c> to <c>deferred_deep_link_clicks</c> —
/// a JSON object of device-identification signals captured alongside the IP (screen dimensions,
/// browser, timezone, device time, language, …) from custom <c>X-DeepLink-*</c> request headers, a
/// curated set of standard fingerprint headers, and/or the capture body. Nullable; existing rows
/// simply carry no signals.
/// </summary>
internal sealed class RSCDM004_AddClickSignals : RSCISchemaMigration
{
    public int Version => 4;
    public string Description => "add signals (JSON) to deferred_deep_link_clicks";

    public void Up(SqliteConnection conn)
    {
        if (!RSCMigrationHelpers.ColumnExists(conn, "deferred_deep_link_clicks", "signals"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE deferred_deep_link_clicks ADD COLUMN signals TEXT;");
        }
    }
}
