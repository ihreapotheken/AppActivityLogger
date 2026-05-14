using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Reports;

/// <summary>
/// v11: drop the <c>mappings</c> table entirely. The persisted-mapping registry was retired —
/// mappings are now applied per-report, in memory, and never written to disk. The on-disk
/// blobs under <c>&lt;ReportsRoot&gt;/mappings/</c> become orphaned by this migration; an
/// operator can <c>rm -rf</c> that directory as a one-shot cleanup (no service code references
/// it anymore).
/// </summary>
internal sealed class RSCM011_DropMappings : RSCISchemaMigration
{
    public int Version => 11;
    public string Description => "drop mappings table — per-report ephemeral deobfuscation only";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
DROP INDEX IF EXISTS idx_mappings_uploaded;
DROP INDEX IF EXISTS idx_mappings_pv_ord;
DROP TABLE IF EXISTS mappings;
");
    }
}
