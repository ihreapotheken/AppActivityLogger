using Microsoft.Data.Sqlite;

namespace ReportService.Storage.Migrations.Analytics;

/// <summary>
/// v4 of the analytics schema. Two corrections, both keyed on the fact that an event/observation
/// is identified by <em>(platform, …)</em>, not by a platform-agnostic id:
/// <list type="number">
///   <item>
///     Replaces the single-column <c>idx_analytics_events_event_id</c> (added in v3 for the old
///     <c>WHERE event_id = ?</c> mark) with a composite <c>(platform, event_id)</c> index. The
///     aggregation mark now matches the <c>UNIQUE(platform, event_id)</c> key on both columns, so
///     the lookup stays O(log n) without a redundant standalone index on writes.
///   </item>
///   <item>
///     Rebuilds <c>analytics_funnel_steps</c> so <c>platform</c> is part of the primary key
///     (<c>funnel_key, platform, session_id, step_index</c>). Session ids are caller-supplied and
///     can collide across platforms; without platform in the key, the first platform's row won
///     INSERT-OR-IGNORE and the second platform's identical-key observation was silently dropped
///     and misattributed. The table is fully derived (the funnel worker recomputes it every tick),
///     so DROP + recreate is safe and loses nothing the worker won't re-derive.
///   </item>
/// </list>
/// </summary>
internal sealed class RSCM004_RekeyEventIdIndexAndFunnelSteps : RSCISchemaMigration
{
    public int Version => 4;
    public string Description => "composite (platform, event_id) index + rebuild analytics_funnel_steps with platform in PK";

    public void Up(SqliteConnection conn)
    {
        // (1) event_id index: drop the platform-agnostic v3 index and add a composite that the
        //     two-column mark predicate can use directly. IF NOT EXISTS / IF EXISTS keep it idempotent.
        RSCMigrationHelpers.Execute(conn, @"
DROP INDEX IF EXISTS idx_analytics_events_event_id;
CREATE INDEX IF NOT EXISTS idx_analytics_events_platform_event_id
  ON analytics_events(platform, event_id);");

        // (2) funnel_steps: rebuild with platform in the PK. Derived table — safe to drop. The new
        //     PK dedupes per (funnel_key, platform, session_id, step_index) so two platforms sharing
        //     a session_id no longer collide.
        RSCMigrationHelpers.Execute(conn, @"
DROP TABLE IF EXISTS analytics_funnel_steps;
CREATE TABLE analytics_funnel_steps (
  funnel_key         TEXT NOT NULL,
  step_index         INTEGER NOT NULL,
  platform           TEXT NOT NULL,
  session_id         TEXT NOT NULL,
  reached_at         TEXT NOT NULL,
  PRIMARY KEY (funnel_key, platform, session_id, step_index)
);
CREATE INDEX IF NOT EXISTS idx_analytics_funnel_step_reached
  ON analytics_funnel_steps(funnel_key, step_index, reached_at DESC);");
    }
}
