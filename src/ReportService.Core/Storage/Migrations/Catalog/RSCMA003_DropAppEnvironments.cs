using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Catalog;

/// <summary>
/// v3 of the <c>catalog.db</c> schema. Drops the <c>app_environments</c> table: environment is no
/// longer a separate tenancy axis — it is folded into the app slug (a client creates a separate app
/// entry per environment, e.g. <c>app-a-qa</c> / <c>app-a-prod</c>). An app is now identified by
/// <c>(client_id, slug)</c> alone; the <c>apps</c> table is unchanged.
/// </summary>
internal sealed class RSCMA003_DropAppEnvironments : RSCISchemaMigration
{
    public int Version => 3;
    public string Description => "drop app_environments (environment folded into the app slug)";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, "DROP TABLE IF EXISTS app_environments;");
    }
}
