using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v8: create the <c>mappings</c> table — index of uploaded R8 / ProGuard <c>mapping.txt</c>
/// files keyed by <c>(platform, app_version)</c>. The raw mapping bytes live on disk under
/// <c>mappings/&lt;platform&gt;/&lt;app_version&gt;.txt</c>; this row carries the metadata + a
/// content hash so the admin UI can show "Mapping covers v1.2.3" without touching disk.
/// </summary>
internal sealed class RSCM008_CreateMappings : RSCISchemaMigration
{
    public int Version => 8;
    public string Description => "create mappings table for R8 deobfuscation";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE TABLE IF NOT EXISTS mappings (
  platform     TEXT NOT NULL,
  app_version  TEXT NOT NULL,
  uploaded_at  TEXT NOT NULL,
  size_bytes   INTEGER NOT NULL,
  sha256       TEXT NOT NULL,
  class_count  INTEGER NOT NULL,
  -- notes column was added historically; dropped in v10. New installs run v8 + v9 + v10
  -- so the live schema never carries it. Keeping the original DDL minus the column would
  -- break upgrade paths; leaving the legacy DDL intact above and dropping in v10 keeps both
  -- fresh installs and upgrades convergent.
  notes        TEXT NULL,
  PRIMARY KEY (platform, app_version)
);
CREATE INDEX IF NOT EXISTS idx_mappings_uploaded
  ON mappings(uploaded_at DESC);
");
    }
}
