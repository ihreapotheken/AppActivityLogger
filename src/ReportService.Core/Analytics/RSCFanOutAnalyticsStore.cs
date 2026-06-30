using Microsoft.Extensions.Logging;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Analytics;

/// <summary>
/// The <see cref="RSCIAnalyticsStore"/> the admin dashboards + exports resolve from DI in the
/// database-per-app model. It owns no database of its own — it routes:
/// <list type="bullet">
///   <item><b>A scoped read</b> (a client <em>and</em> app are selected) delegates straight to that
///         one app's store — the common path, exactly as fast as a single-DB read used to be.</item>
///   <item><b>An unscoped / client-only read</b> fans out across the matching apps' databases
///         (bounded parallelism, capped at <see cref="RSCAnalyticsFanoutOptions.MaxAppsPerRead"/>,
///         one bad app DB logged + skipped) and merges the results in memory.</item>
///   <item><b><see cref="WriteBatchAsync"/></b> routes by the batch's own (client, app) — used by the
///         dev seeder + legacy importer.</item>
/// </list>
/// Per-app write/worker operations (aggregation tick, funnel/cohort recompute, purge) are NOT valid
/// on this fan-out facade — ingestion + the workers resolve a specific app via
/// <see cref="RSCIAnalyticsStoreFactory"/> — so those throw.
/// </summary>
public sealed class RSCFanOutAnalyticsStore : RSCIAnalyticsStore
{
    private readonly RSCIAnalyticsStoreFactory _factory;
    private readonly RSCICatalog _catalog;
    private readonly RSCAnalyticsFanoutOptions _fanout;
    private readonly ILogger<RSCFanOutAnalyticsStore> _logger;

    public RSCFanOutAnalyticsStore(
        RSCIAnalyticsStoreFactory factory,
        RSCICatalog catalog,
        RSCAnalyticsFanoutOptions fanout,
        ILogger<RSCFanOutAnalyticsStore> logger)
    {
        _factory = factory;
        _catalog = catalog;
        _fanout = fanout;
        _logger = logger;
    }

    // -------- Routing helpers --------

    private static bool IsSingleApp(string? clientSlug, string? appSlug) =>
        !string.IsNullOrWhiteSpace(clientSlug) && !string.IsNullOrWhiteSpace(appSlug);

    /// <summary>The set of app stores a read targets: one when an app is selected, otherwise every
    /// matching app's store (filtered to a client if one is set), capped and logged on overflow.</summary>
    private async Task<IReadOnlyList<RSCIAnalyticsStore>> ResolveStoresAsync(string? clientSlug, string? appSlug, CancellationToken ct)
    {
        if (IsSingleApp(clientSlug, appSlug))
            return new[] { _factory.Get(clientSlug, appSlug) };

        var apps = await _catalog.ListAllAppsAsync(includeArchived: false, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(clientSlug))
        {
            var c = clientSlug.Trim().ToLowerInvariant();
            apps = apps.Where(a => string.Equals(a.ClientSlug, c, StringComparison.Ordinal)).ToList();
        }
        if (_fanout.IsTruncated(apps.Count))
        {
            _logger.LogWarning("Analytics fan-out read truncated to {Cap} of {Total} apps", _fanout.MaxAppsPerRead, apps.Count);
            apps = apps.Take(_fanout.MaxAppsPerRead).ToList();
        }
        return apps.Select(a => _factory.Get(a.ClientSlug, a.Slug)).ToList();
    }

    /// <summary>Runs <paramref name="read"/> over each store with bounded parallelism. A per-store
    /// failure is logged and skipped so one corrupt/locked app DB can't break an all-apps dashboard.</summary>
    private async Task<List<T>> MapAsync<T>(IReadOnlyList<RSCIAnalyticsStore> stores, Func<RSCIAnalyticsStore, Task<T>> read, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(_fanout.EffectiveParallelism);
        var tasks = stores.Select(async store =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return (ok: true, value: await read(store).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fan-out analytics read failed for an app DB; skipping it");
                return (ok: false, value: default(T)!);
            }
            finally { gate.Release(); }
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var skipped = results.Count(r => !r.ok);
        if (skipped > 0)
            _logger.LogWarning("Fan-out analytics read skipped {Skipped} of {Total} app DBs that failed; result is partial", skipped, stores.Count);
        return results.Where(r => r.ok).Select(r => r.value).ToList();
    }

    // -------- Dashboards --------

    public async Task<RSCAnalyticsTotals> GetTotalsAsync(RSCAnalyticsScope scope, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(scope.ClientId, scope.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].GetTotalsAsync(scope, ct).ConfigureAwait(false);

        var parts = await MapAsync(stores, s => s.GetTotalsAsync(scope, ct), ct).ConfigureAwait(false);
        if (parts.Count == 0) return new RSCAnalyticsTotals(0, 0, 0, 0, 0, TimeSpan.Zero, null);

        long sessions = parts.Sum(p => p.SessionsToday);
        // Average session duration: weight each app's average by its session count (an app with no
        // sessions contributes nothing). Accumulate in double-seconds, not raw ticks, so a wide
        // fan-out can't overflow Int64. DAU/WAU/MAU are summed — a user active in two apps counts
        // twice; that is the only tractable cross-file semantic for distinct counts (documented).
        var weightedSeconds = parts.Sum(p => p.AverageSessionDuration.TotalSeconds * p.SessionsToday);
        var avg = sessions > 0 ? TimeSpan.FromSeconds(weightedSeconds / sessions) : TimeSpan.Zero;
        return new RSCAnalyticsTotals(
            DailyActiveUsers: parts.Sum(p => p.DailyActiveUsers),
            WeeklyActiveUsers: parts.Sum(p => p.WeeklyActiveUsers),
            MonthlyActiveUsers: parts.Sum(p => p.MonthlyActiveUsers),
            SessionsToday: sessions,
            EventsToday: parts.Sum(p => p.EventsToday),
            AverageSessionDuration: avg,
            LastAggregatedAt: parts.Max(p => p.LastAggregatedAt));
    }

    public async Task<IReadOnlyList<RSCAnalyticsPlatformSummary>> GetPlatformSummariesAsync(CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(null, null, ct).ConfigureAwait(false);
        var parts = await MapAsync(stores, s => s.GetPlatformSummariesAsync(ct), ct).ConfigureAwait(false);
        return parts.SelectMany(p => p)
            .GroupBy(p => p.Platform, StringComparer.Ordinal)
            .Select(g => new RSCAnalyticsPlatformSummary(
                Platform: g.Key,
                AcceptedEvents: g.Sum(x => x.AcceptedEvents),
                RejectedEvents: g.Sum(x => x.RejectedEvents),
                Batches: g.Sum(x => x.Batches),
                LastReceivedAt: g.Max(x => x.LastReceivedAt)))
            .OrderBy(p => p.Platform, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<RSCAnalyticsTopScreen>> GetTopScreensAsync(RSCAnalyticsScope scope, int topN, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(scope.ClientId, scope.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].GetTopScreensAsync(scope, topN, ct).ConfigureAwait(false);

        var parts = await MapAsync(stores, s => s.GetTopScreensAsync(scope, topN, ct), ct).ConfigureAwait(false);
        return parts.SelectMany(p => p)
            .GroupBy(s => s.Screen, StringComparer.Ordinal)
            .Select(g =>
            {
                var views = g.Sum(x => x.Views);
                var weightedSeconds = g.Sum(x => x.AverageDuration.TotalSeconds * x.Views);
                return new RSCAnalyticsTopScreen(g.Key, views, views > 0 ? TimeSpan.FromSeconds(weightedSeconds / views) : TimeSpan.Zero);
            })
            .OrderByDescending(s => s.Views)
            .Take(Math.Max(1, topN))
            .ToList();
    }

    public async Task<IReadOnlyList<RSCAnalyticsDailyRollup>> GetDailyRollupsAsync(
        DateTimeOffset from, DateTimeOffset until, RSCAnalyticsScope scope, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(scope.ClientId, scope.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].GetDailyRollupsAsync(from, until, scope, ct).ConfigureAwait(false);

        var parts = await MapAsync(stores, s => s.GetDailyRollupsAsync(from, until, scope, ct), ct).ConfigureAwait(false);
        // Fold across apps by (day, platform) so the trend chart sees one series per platform/day.
        return parts.SelectMany(p => p)
            .GroupBy(r => (r.Day, r.Platform))
            .Select(g => new RSCAnalyticsDailyRollup(
                AppId: scope.AppId ?? "*",
                Environment: scope.Environment ?? "*",
                ClientId: scope.ClientId ?? "*",
                Day: g.Key.Day,
                Platform: g.Key.Platform,
                Events: g.Sum(x => x.Events),
                Sessions: g.Sum(x => x.Sessions),
                DistinctUsers: g.Sum(x => x.DistinctUsers)))
            .OrderBy(r => r.Day).ThenBy(r => r.Platform, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<RSCAnalyticsSalesReport> GetSalesReportAsync(
        DateTimeOffset from, DateTimeOffset until, RSCAnalyticsScope scope, int topItems, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(scope.ClientId, scope.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].GetSalesReportAsync(from, until, scope, topItems, ct).ConfigureAwait(false);

        var parts = await MapAsync(stores, s => s.GetSalesReportAsync(from, until, scope, topItems, ct), ct).ConfigureAwait(false);
        if (parts.Count == 0) return RSCAnalyticsSalesReport.Empty;

        static IReadOnlyList<RSCSalesDimension> FoldDim(IEnumerable<RSCSalesDimension> rows) => rows
            .GroupBy(d => d.Key, StringComparer.Ordinal)
            .Select(g => new RSCSalesDimension(g.Key, g.Sum(x => x.Revenue), g.Sum(x => x.Orders)))
            .OrderByDescending(d => d.Revenue).ToList();

        return new RSCAnalyticsSalesReport(
            TotalRevenue: parts.Sum(p => p.TotalRevenue),
            OrderCount: parts.Sum(p => p.OrderCount),
            ItemsSold: parts.Sum(p => p.ItemsSold),
            Currency: parts.Select(p => p.Currency).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "EUR",
            ByDay: parts.SelectMany(p => p.ByDay)
                .GroupBy(d => d.Day)
                .Select(g => new RSCSalesDayPoint(g.Key, g.Sum(x => x.Revenue), g.Sum(x => x.Orders)))
                .OrderBy(d => d.Day).ToList(),
            ByShippingMethod: FoldDim(parts.SelectMany(p => p.ByShippingMethod)),
            ByPaymentMethod: FoldDim(parts.SelectMany(p => p.ByPaymentMethod)),
            TopItems: parts.SelectMany(p => p.TopItems)
                .GroupBy(i => i.ItemId, StringComparer.Ordinal)
                .Select(g => new RSCSalesItemRow(
                    ItemId: g.Key,
                    Name: g.Select(x => x.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                    Category: g.Select(x => x.Category).FirstOrDefault(c => !string.IsNullOrEmpty(c)),
                    Units: g.Sum(x => x.Units),
                    Revenue: g.Sum(x => x.Revenue)))
                .OrderByDescending(i => i.Revenue).Take(Math.Max(1, topItems)).ToList(),
            Prescriptions: parts.SelectMany(p => p.Prescriptions)
                .GroupBy(a => a.Key, StringComparer.Ordinal)
                .Select(g => new RSCSalesActivity(g.Key, g.Sum(x => x.Count)))
                .OrderByDescending(a => a.Count).ToList());
    }

    public async Task<RSCAnalyticsHealthSnapshot> GetHealthSnapshotAsync(int sampleSize, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(null, null, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].GetHealthSnapshotAsync(sampleSize, ct).ConfigureAwait(false);

        var parts = await MapAsync(stores, s => s.GetHealthSnapshotAsync(sampleSize, ct), ct).ConfigureAwait(false);
        if (parts.Count == 0)
            return new RSCAnalyticsHealthSnapshot(0, new Dictionary<string, long>(), Array.Empty<RSCAnalyticsDeadLetterRow>(), new Dictionary<string, long>(), null);

        static Dictionary<string, long> MergeCounts(IEnumerable<IReadOnlyDictionary<string, long>> dicts)
        {
            var merged = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var d in dicts)
                foreach (var kv in d)
                    merged[kv.Key] = merged.TryGetValue(kv.Key, out var n) ? n + kv.Value : kv.Value;
            return merged;
        }

        return new RSCAnalyticsHealthSnapshot(
            DeadLetterTotal: parts.Sum(p => p.DeadLetterTotal),
            DeadLettersByReason: MergeCounts(parts.Select(p => p.DeadLettersByReason)),
            RecentSamples: parts.SelectMany(p => p.RecentSamples)
                .OrderByDescending(r => r.ReceivedAt).Take(Math.Max(1, sampleSize)).ToList(),
            SdkVersionsSeen: MergeCounts(parts.Select(p => p.SdkVersionsSeen)),
            LastAggregatedAt: parts.Max(p => p.LastAggregatedAt));
    }

    // -------- Search / detail --------

    public async Task<RSCAnalyticsEventPage> SearchEventsAsync(RSCAnalyticsEventFilter filter, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(filter.ClientId, filter.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].SearchEventsAsync(filter, ct).ConfigureAwait(false);

        // Pull (offset+limit) from each app with offset 0, then merge-sort + slice for a correct page.
        // NOTE: each per-app store clamps its own result to 500 rows, so cross-app deep paging is only
        // exact while Offset+Limit <= 500 (≈ first 10 pages at the default page size). Beyond that an
        // app with >500 matches contributes only its top 500 — acceptable for this internal,
        // typically-filtered list; the common scoped (single-app) path passes the real offset to SQL
        // and has no such bound. A deterministic (Platform, EventId) tie-break keeps equal-timestamp
        // rows from duplicating or vanishing across page boundaries.
        var perStore = filter with { Offset = 0, Limit = filter.Offset + filter.Limit };
        var parts = await MapAsync(stores, s => s.SearchEventsAsync(perStore, ct), ct).ConfigureAwait(false);
        var rows = parts.SelectMany(p => p.Rows)
            .OrderByDescending(e => e.OccurredAt)
            .ThenBy(e => e.Platform, StringComparer.Ordinal).ThenBy(e => e.EventId, StringComparer.Ordinal)
            .Skip(filter.Offset).Take(filter.Limit).ToList();
        return new RSCAnalyticsEventPage(rows, parts.Sum(p => p.Total), filter.Limit, filter.Offset);
    }

    public async Task<IReadOnlyList<RSCAnalyticsStoredEvent>> GetSessionTimelineAsync(
        RSCAnalyticsScope scope, string sessionId, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(scope.ClientId, scope.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].GetSessionTimelineAsync(scope, sessionId, ct).ConfigureAwait(false);

        var parts = await MapAsync(stores, s => s.GetSessionTimelineAsync(scope, sessionId, ct), ct).ConfigureAwait(false);
        // A session lives in exactly one app DB. Return the rows from the single owning app rather
        // than interleaving by Sequence across apps (which would corrupt the timeline in the rare
        // event the same session_id appeared in two app DBs).
        var owner = parts.SelectMany(e => e)
            .GroupBy(e => (e.AppId, e.Environment, e.ClientId))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        return owner is null
            ? Array.Empty<RSCAnalyticsStoredEvent>()
            : owner.OrderBy(e => e.Sequence).ThenBy(e => e.OccurredAt).ToList();
    }

    public async Task<IReadOnlyList<RSCAnalyticsSessionRow>> ListSessionsAsync(
        RSCAnalyticsScope scope, int limit, int offset, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(scope.ClientId, scope.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].ListSessionsAsync(scope, limit, offset, ct).ConfigureAwait(false);

        // Same per-app 500-row clamp bound as SearchEventsAsync (deep paging exact to ~page 10);
        // deterministic tie-break on the full session key for stable cross-app ordering.
        var parts = await MapAsync(stores, s => s.ListSessionsAsync(scope, offset + limit, 0, ct), ct).ConfigureAwait(false);
        return parts.SelectMany(p => p)
            .OrderByDescending(s => s.LastSeenAt)
            .ThenBy(s => s.ClientId, StringComparer.Ordinal).ThenBy(s => s.AppId, StringComparer.Ordinal)
            .ThenBy(s => s.Platform, StringComparer.Ordinal).ThenBy(s => s.SessionId, StringComparer.Ordinal)
            .Skip(offset).Take(limit).ToList();
    }

    // -------- Retention --------

    public async Task<RSCAnalyticsRetentionSummary> GetRetentionSummaryAsync(RSCAnalyticsScope scope, int windowDays, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(scope.ClientId, scope.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].GetRetentionSummaryAsync(scope, windowDays, ct).ConfigureAwait(false);

        // Re-pool from the raw per-cohort counts across apps (user-pool weighted) — averaging each
        // app's *rate* (weighted by cohort count) would be materially wrong when apps have very
        // different cohort sizes. A cohort counts toward DN only once it is old enough to have
        // observed day N, replicating the per-store windowing.
        var cohortLists = await MapAsync(stores, s => s.ListRetentionCohortsAsync(scope, windowDays, ct), ct).ConfigureAwait(false);
        var cohorts = cohortLists.SelectMany(c => c).ToList();
        if (cohorts.Count == 0) return new RSCAnalyticsRetentionSummary(0, 0, 0, 0, 0, 0);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        (double Rate, long Used) Window(int n, Func<RSCAnalyticsRetentionCohortRow, long> retained)
        {
            long pool = 0, ret = 0, used = 0;
            foreach (var c in cohorts)
            {
                if (today.DayNumber - c.InstallDay.DayNumber < n) continue; // too young to have observed day n
                pool += c.CohortSize;
                ret += retained(c);
                used++;
            }
            return (pool > 0 ? (double)ret / pool : 0, used);
        }
        var (d1, u1) = Window(1, c => c.Day1Retained);
        var (d7, u7) = Window(7, c => c.Day7Retained);
        var (d30, u30) = Window(30, c => c.Day30Retained);
        return new RSCAnalyticsRetentionSummary(d1, d7, d30, u1, u7, u30);
    }

    public async Task<IReadOnlyList<RSCAnalyticsRetentionCohortRow>> ListRetentionCohortsAsync(
        RSCAnalyticsScope scope, int days, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(scope.ClientId, scope.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].ListRetentionCohortsAsync(scope, days, ct).ConfigureAwait(false);

        var parts = await MapAsync(stores, s => s.ListRetentionCohortsAsync(scope, days, ct), ct).ConfigureAwait(false);
        return parts.SelectMany(p => p)
            .OrderByDescending(r => r.InstallDay)
            .ThenBy(r => r.ClientId, StringComparer.Ordinal).ThenBy(r => r.AppId, StringComparer.Ordinal)
            .ToList();
    }

    // -------- Funnels --------

    public async Task<IReadOnlyList<RSCAnalyticsFunnelDefinition>> ListFunnelDefinitionsAsync(bool onlyEnabled, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(null, null, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].ListFunnelDefinitionsAsync(onlyEnabled, ct).ConfigureAwait(false);

        // Definitions are seeded identically into every app DB; present the distinct set by key.
        var parts = await MapAsync(stores, s => s.ListFunnelDefinitionsAsync(onlyEnabled, ct), ct).ConfigureAwait(false);
        return parts.SelectMany(p => p)
            .GroupBy(d => d.FunnelKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(d => d.FunnelKey, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<RSCAnalyticsFunnelStepStat>> GetFunnelSummaryAsync(
        string funnelKey, DateTimeOffset from, DateTimeOffset until, RSCAnalyticsScope scope, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(scope.ClientId, scope.AppId, ct).ConfigureAwait(false);
        if (stores.Count == 1) return await stores[0].GetFunnelSummaryAsync(funnelKey, from, until, scope, ct).ConfigureAwait(false);

        var parts = await MapAsync(stores, s => s.GetFunnelSummaryAsync(funnelKey, from, until, scope, ct), ct).ConfigureAwait(false);
        return parts.SelectMany(p => p)
            .GroupBy(s => (s.StepIndex, s.StepName))
            .Select(g => new RSCAnalyticsFunnelStepStat(g.Key.StepIndex, g.Key.StepName, g.Sum(x => x.SessionsReached)))
            .OrderBy(s => s.StepIndex)
            .ToList();
    }

    // -------- Ingestion (routed per-app, for the seeder + legacy importer) --------

    public Task<RSCAnalyticsBatchReceipt> WriteBatchAsync(
        RSCAnalyticsBatch batch, string? anonymousIdHash, string? clientIdHash,
        RSCAnalyticsValidationResult verdict, DateTimeOffset receivedAt, CancellationToken ct)
        => _factory.Get(batch.ClientId, batch.AppId)
            .WriteBatchAsync(batch, anonymousIdHash, clientIdHash, verdict, receivedAt, ct);

    // -------- Per-app write/worker operations: not valid on the fan-out facade --------

    private static NotSupportedException PerApp(string op) => new(
        $"{op} targets a single app database; resolve it via RSCIAnalyticsStoreFactory.Get(client, app). " +
        "The fan-out store is read-only across apps.");

    public Task<IReadOnlyList<RSCAnalyticsStoredEvent>> ListUnaggregatedEventsAsync(int limit, CancellationToken ct)
        => throw PerApp(nameof(ListUnaggregatedEventsAsync));
    public Task WriteAggregationTickAsync(RSCAnalyticsAggregationTick tick, CancellationToken ct)
        => throw PerApp(nameof(WriteAggregationTickAsync));
    public Task MarkEventsAggregatedAsync(IReadOnlyList<RSCAggregationEventRef> events, CancellationToken ct)
        => throw PerApp(nameof(MarkEventsAggregatedAsync));
    public Task<int> RecomputeRetentionCohortsAsync(DateOnly windowStart, int currentHashVersion, CancellationToken ct)
        => throw PerApp(nameof(RecomputeRetentionCohortsAsync));
    public Task UpsertFunnelDefinitionAsync(RSCAnalyticsFunnelDefinition definition, CancellationToken ct)
        => throw PerApp(nameof(UpsertFunnelDefinitionAsync));
    public Task<int> RecomputeFunnelStepsAsync(RSCAnalyticsFunnelDefinition definition, DateOnly windowStart, CancellationToken ct)
        => throw PerApp(nameof(RecomputeFunnelStepsAsync));

    // Operator-wide maintenance: purge applies across EVERY app's database (sum of rows removed). A
    // per-app failure is logged + skipped (via MapAsync) so one bad DB can't block the whole purge.
    public async Task<int> PurgeOlderThanAsync(DateTimeOffset eventsCutoff, DateTimeOffset deadLetterCutoff, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(null, null, ct).ConfigureAwait(false);
        var counts = await MapAsync(stores, s => s.PurgeOlderThanAsync(eventsCutoff, deadLetterCutoff, ct), ct).ConfigureAwait(false);
        return counts.Sum();
    }

    public async Task<int> PurgeUserDaysBelowHashVersionAsync(int minVersion, CancellationToken ct)
    {
        var stores = await ResolveStoresAsync(null, null, ct).ConfigureAwait(false);
        var counts = await MapAsync(stores, s => s.PurgeUserDaysBelowHashVersionAsync(minVersion, ct), ct).ConfigureAwait(false);
        return counts.Sum();
    }
}
