using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.DeepLinks;

/// <summary>
/// v1 of the deferred deep-linking schema: the operator-managed link definitions and the recorded
/// website clicks the match endpoint correlates against. Lives in its own DB (default
/// <c>deeplinks.db</c>) so the schema evolves independently of the report index and analytics store.
/// </summary>
internal sealed class RSCDM001_CreateDeepLinkTables : RSCISchemaMigration
{
    public int Version => 1;
    public string Description => "create deferred_deep_links + deferred_deep_link_clicks tables";

    public void Up(SqliteConnection conn)
    {
        // deferred_deep_links: one row per configured link. UNIQUE(slug) lets the admin page upsert
        // by a stable operator-chosen key. page_pattern is matched as a case-insensitive substring
        // of a recorded click's page_url; redirect_url is what the app is told to open.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS deferred_deep_links (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  slug          TEXT NOT NULL UNIQUE,
  name          TEXT NOT NULL,
  page_pattern  TEXT NOT NULL,
  redirect_url  TEXT NOT NULL,
  enabled       INTEGER NOT NULL DEFAULT 1,
  created_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);");

        // deferred_deep_link_clicks: one row per recorded website visit. The matching link is
        // resolved at capture time and denormalised onto the row (link_slug + redirect_url) so the
        // match endpoint is a pure IP+time lookup that never re-evaluates patterns. matched_at is
        // stamped when an app claims the click so it isn't handed out twice.
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS deferred_deep_link_clicks (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  ip            TEXT NOT NULL,
  page_url      TEXT NOT NULL,
  user_agent    TEXT,
  link_slug     TEXT,
  redirect_url  TEXT,
  created_at    TEXT NOT NULL,
  matched_at    TEXT
);
CREATE INDEX IF NOT EXISTS idx_ddl_clicks_ip_created
  ON deferred_deep_link_clicks(ip, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_ddl_clicks_created
  ON deferred_deep_link_clicks(created_at DESC);");
    }
}
