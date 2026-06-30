using ReportService.Models;

namespace ReportService.Analytics;

/// <summary>
/// Storage surface for the v2 analytics pipeline. The single implementation today is
/// <c>RSCSqliteAnalyticsStore</c>; the interface exists so a future ClickHouse/Postgres backend
/// can be slotted in without rewiring the ingestion endpoint, aggregation worker, or admin
/// pages.
/// </summary>
/// <remarks>
/// Method groups:
/// <list type="bullet">
///   <item><b>Ingestion</b>: <see cref="WriteBatchAsync"/> persists one batch's accepted + rejected
///         rows in a single transaction. Idempotent on <c>UNIQUE(platform, event_id)</c>.</item>
///   <item><b>Aggregation</b>: <see cref="ListUnaggregatedEventsAsync"/>,
///         <see cref="WriteAggregationTickAsync"/>, <see cref="MarkEventsAggregatedAsync"/>.</item>
///   <item><b>Dashboards</b>: <see cref="GetTotalsAsync"/>, <see cref="GetPlatformSummariesAsync"/>,
///         <see cref="GetTopScreensAsync"/>, <see cref="GetDailyRollupsAsync"/>,
///         <see cref="GetHealthSnapshotAsync"/>.</item>
///   <item><b>Maintenance</b>: <see cref="PurgeOlderThanAsync"/>.</item>
/// </list>
/// </remarks>
public interface RSCIAnalyticsStore
{
    // -------- Ingestion --------
    /// <summary>Persists one batch's accepted + rejected rows in a single transaction.</summary>
    /// <param name="batch">The normalized, attribution-stamped batch envelope to persist.</param>
    /// <param name="anonymousIdHash">Peppered hash of the batch's <c>anonymousId</c> — the per-user
    /// identity, which is never stored verbatim.</param>
    /// <param name="clientIdHash">Legacy. The client is now identified by the verbatim tenancy key
    /// <c>batch.ClientId</c> (stored as <c>client_id</c>), so production callers pass <c>null</c> and
    /// the write-only <c>client_id_hash</c> column is left unpopulated. Kept on the signature only so
    /// the column can still be set when replaying historical data.</param>
    /// <param name="verdict">Validation result splitting the batch into accepted vs dead-lettered events.</param>
    /// <param name="receivedAt">Server receive timestamp stamped on every row of this batch.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RSCAnalyticsBatchReceipt> WriteBatchAsync(
        RSCAnalyticsBatch batch,
        string? anonymousIdHash,
        string? clientIdHash,
        RSCAnalyticsValidationResult verdict,
        DateTimeOffset receivedAt,
        CancellationToken ct);

    // -------- Aggregation --------
    Task<IReadOnlyList<RSCAnalyticsStoredEvent>> ListUnaggregatedEventsAsync(
        int limit, CancellationToken ct);

    /// <summary>
    /// Atomic aggregation tick: applies every session/user-day/daily-rollup delta and marks the
    /// source events <c>aggregated_at = now</c> inside a single SQLite transaction. A crash
    /// before commit leaves everything unaggregated; a successful commit moves all events out of
    /// the unaggregated pool, so replay produces zero rollup contributions instead of doubling them.
    /// </summary>
    Task WriteAggregationTickAsync(RSCAnalyticsAggregationTick tick, CancellationToken ct);

    /// <summary>
    /// Marks the given events <c>aggregated_at = now</c>. Each ref carries platform alongside
    /// event_id because <c>analytics_events</c> is keyed by <c>UNIQUE(platform, event_id)</c> — the
    /// same id can exist on two platforms, so the mark must match both columns.
    /// </summary>
    Task MarkEventsAggregatedAsync(IReadOnlyList<RSCAggregationEventRef> events, CancellationToken ct);

    // -------- Dashboards --------
    Task<RSCAnalyticsTotals> GetTotalsAsync(RSCAnalyticsScope scope, CancellationToken ct);
    Task<IReadOnlyList<RSCAnalyticsPlatformSummary>> GetPlatformSummariesAsync(CancellationToken ct);
    Task<IReadOnlyList<RSCAnalyticsTopScreen>> GetTopScreensAsync(RSCAnalyticsScope scope, int topN, CancellationToken ct);
    Task<IReadOnlyList<RSCAnalyticsDailyRollup>> GetDailyRollupsAsync(
        DateTimeOffset from, DateTimeOffset until, RSCAnalyticsScope scope, CancellationToken ct);

    /// <summary>
    /// Sales / revenue projection for the <c>/AnalyticsSales</c> page. Scans the <c>purchase</c>
    /// events (and prescription-activity events) inside the <c>[from, until)</c> UTC-day window and
    /// folds them into totals, a daily revenue trend, shipping/payment breakdowns, and the top
    /// <paramref name="topItems"/> products by revenue. Computed on read — there is no sales rollup
    /// table — so the cost scales with the (small) number of purchase events in the window.
    /// </summary>
    Task<RSCAnalyticsSalesReport> GetSalesReportAsync(
        DateTimeOffset from, DateTimeOffset until, RSCAnalyticsScope scope, int topItems, CancellationToken ct);

    Task<RSCAnalyticsHealthSnapshot> GetHealthSnapshotAsync(int sampleSize, CancellationToken ct);

    // -------- Search / detail --------
    Task<RSCAnalyticsEventPage> SearchEventsAsync(RSCAnalyticsEventFilter filter, CancellationToken ct);
    Task<IReadOnlyList<RSCAnalyticsStoredEvent>> GetSessionTimelineAsync(
        RSCAnalyticsScope scope, string sessionId, CancellationToken ct);
    Task<IReadOnlyList<RSCAnalyticsSessionRow>> ListSessionsAsync(
        RSCAnalyticsScope scope, int limit, int offset, CancellationToken ct);

    // -------- Retention cohorts --------
    /// <summary>
    /// Recomputes retention cohorts for every (platform, install_day) where install_day falls
    /// inside <c>[windowStart, today]</c>. Upserts <c>analytics_retention_cohorts</c>. Idempotent —
    /// running twice in a row produces the same result.
    /// </summary>
    Task<int> RecomputeRetentionCohortsAsync(DateOnly windowStart, int currentHashVersion, CancellationToken ct);

    /// <summary>
    /// Cohort-weighted D1/D7/D30 retention across the last <paramref name="windowDays"/> of
    /// cohorts old enough to have observed each window. Returns 0/0/0 with zero cohorts used
    /// when there isn't enough data yet.
    /// </summary>
    Task<RSCAnalyticsRetentionSummary> GetRetentionSummaryAsync(RSCAnalyticsScope scope, int windowDays, CancellationToken ct);

    /// <summary>Per-cohort retention rows, most recent install_day first. For the admin page.</summary>
    Task<IReadOnlyList<RSCAnalyticsRetentionCohortRow>> ListRetentionCohortsAsync(
        RSCAnalyticsScope scope, int days, CancellationToken ct);

    // -------- Funnels --------
    Task<IReadOnlyList<RSCAnalyticsFunnelDefinition>> ListFunnelDefinitionsAsync(
        bool onlyEnabled, CancellationToken ct);

    Task UpsertFunnelDefinitionAsync(RSCAnalyticsFunnelDefinition definition, CancellationToken ct);

    /// <summary>
    /// Walks <c>analytics_events</c> for every session active inside <c>[windowStart, today]</c>,
    /// applies the funnel matcher in order, and records observed step reaches in
    /// <c>analytics_funnel_steps</c>. INSERT OR IGNORE makes this safe to re-run.
    /// </summary>
    Task<int> RecomputeFunnelStepsAsync(
        RSCAnalyticsFunnelDefinition definition, DateOnly windowStart, CancellationToken ct);

    /// <summary>
    /// Per-step session count for one funnel inside <c>[from, until]</c>. Returns one row per
    /// step in the funnel definition (steps with zero observed sessions appear as zero rows so
    /// the admin page renders a complete table).
    /// </summary>
    Task<IReadOnlyList<RSCAnalyticsFunnelStepStat>> GetFunnelSummaryAsync(
        string funnelKey, DateTimeOffset from, DateTimeOffset until, RSCAnalyticsScope scope, CancellationToken ct);

    // -------- Maintenance --------
    Task<int> PurgeOlderThanAsync(DateTimeOffset eventsCutoff, DateTimeOffset deadLetterCutoff, CancellationToken ct);

    /// <summary>
    /// After a pepper rotation, purges <c>analytics_user_days</c> rows whose <c>hash_version</c>
    /// is below <paramref name="minVersion"/>. The orphaned rows can't be reconciled with the new
    /// hashes (raw IDs were never stored), so they're discarded. Daily rollups already include
    /// rebuilt counts as new events flow in under the new pepper.
    /// </summary>
    Task<int> PurgeUserDaysBelowHashVersionAsync(int minVersion, CancellationToken ct);
}
