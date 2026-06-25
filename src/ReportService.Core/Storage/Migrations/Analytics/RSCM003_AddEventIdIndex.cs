using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Analytics;

/// <summary>
/// v3 of the analytics schema. Adds a standalone index on <c>analytics_events.event_id</c>.
/// </summary>
/// <remarks>
/// The only index covering <c>event_id</c> was the composite <c>UNIQUE(platform, event_id)</c>,
/// whose leading column is <c>platform</c> — so the aggregation worker's per-event
/// <c>UPDATE analytics_events SET aggregated_at = ? WHERE event_id = ?</c> could not use it and
/// fell back to a full table scan on every row it marked. That made each aggregation tick scale
/// with the table size (O(rows × batch)), so draining a large unaggregated backlog took hours.
/// A dedicated index on <c>event_id</c> turns each mark into an O(log n) lookup.
/// </remarks>
internal sealed class RSCM003_AddEventIdIndex : RSCISchemaMigration
{
    public int Version => 3;
    public string Description => "add idx_analytics_events_event_id for fast aggregation marking";

    public void Up(SqliteConnection conn)
    {
        RSCMigrationHelpers.Execute(conn, @"
CREATE INDEX IF NOT EXISTS idx_analytics_events_event_id
  ON analytics_events(event_id);");
    }
}
