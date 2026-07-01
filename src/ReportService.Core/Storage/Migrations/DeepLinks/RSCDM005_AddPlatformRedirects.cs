using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.DeepLinks;

/// <summary>
/// v5 of the deferred deep-linking schema. Adds <c>redirect_url_android</c> and
/// <c>redirect_url_ios</c> to <c>deferred_deep_links</c> — optional platform-specific overrides for a
/// link's default <c>redirect_url</c>, so a single slug can route an Android visitor and an iOS
/// visitor to different addresses. Both nullable; existing links simply carry no overrides and keep
/// serving their default redirect on every platform.
/// </summary>
internal sealed class RSCDM005_AddPlatformRedirects : RSCISchemaMigration
{
    public int Version => 5;
    public string Description => "add redirect_url_android/redirect_url_ios to deferred_deep_links";

    public void Up(SqliteConnection conn)
    {
        if (!RSCMigrationHelpers.ColumnExists(conn, "deferred_deep_links", "redirect_url_android"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE deferred_deep_links ADD COLUMN redirect_url_android TEXT;");
        }

        if (!RSCMigrationHelpers.ColumnExists(conn, "deferred_deep_links", "redirect_url_ios"))
        {
            RSCMigrationHelpers.Execute(conn,
                "ALTER TABLE deferred_deep_links ADD COLUMN redirect_url_ios TEXT;");
        }
    }
}
