using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.DeepLinks;

/// <summary>
/// v3 of the deferred deep-linking schema. Two scale/operability additions:
/// <list type="number">
///   <item>A key/value <c>deferred_deep_link_settings</c> table holding runtime-tunable settings —
///         currently the click-retention period an operator sets via the admin endpoint/page.</item>
///   <item>An index on <c>deferred_deep_links(updated_at DESC)</c> so the admin list's
///         <c>ORDER BY updated_at DESC LIMIT … OFFSET …</c> pagination stays cheap with thousands of
///         definitions.</item>
/// </list>
/// </summary>
internal sealed class RSCDM003_AddSettingsAndLinkIndex : RSCISchemaMigration
{
    public int Version => 3;
    public string Description => "add deferred_deep_link_settings + idx_ddl_links_updated";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS deferred_deep_link_settings (
  key    TEXT PRIMARY KEY,
  value  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_ddl_links_updated
  ON deferred_deep_links(updated_at DESC);");
    }
}
