using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Analytics;

/// <summary>
/// Background service that folds raw analytics events into the rollup tables. In the
/// database-per-app model each tick fans out over every registered app, pulling a bounded batch of
/// unaggregated events from that app's database, grouping them by (platform, day) and by session,
/// and writing the rollups via the app's <see cref="RSCIAnalyticsStore"/> upserts before marking the
/// source events aggregated. One app's failure is logged and skipped so it can't starve the rest.
/// </summary>
/// <remarks>
/// Exactly-once-per-tick (per app): the worker hands the precomputed deltas plus the source event IDs
/// to <see cref="RSCIAnalyticsStore.WriteAggregationTickAsync"/>, which applies the upserts and marks
/// the events <c>aggregated_at = now</c> inside a single SQLite transaction. A crash before commit
/// leaves the rows in the unaggregated pool with no rollup contribution; the next tick replays them.
/// </remarks>
public sealed class RSCAnalyticsAggregationWorker : BackgroundService
{
    private readonly RSCIAnalyticsStoreFactory _factory;
    private readonly RSCICatalog _catalog;
    private readonly RSCAnalyticsOptions _options;
    private readonly ILogger<RSCAnalyticsAggregationWorker> _logger;

    public RSCAnalyticsAggregationWorker(
        RSCIAnalyticsStoreFactory factory,
        RSCICatalog catalog,
        RSCAnalyticsOptions options,
        ILogger<RSCAnalyticsAggregationWorker> logger)
    {
        _factory = factory;
        _catalog = catalog;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Analytics disabled — aggregation worker idle.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.AggregationIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analytics aggregation tick failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Fan-out tick: aggregate every registered app's database. A per-app try/catch keeps one
    /// bad/locked app DB from starving the others. Returns the total events aggregated this tick.</summary>
    internal async Task<int> TickAsync(CancellationToken ct)
    {
        var apps = await _catalog.ListAllAppsAsync(includeArchived: false, ct).ConfigureAwait(false);
        var total = 0;
        foreach (var app in apps)
        {
            try
            {
                var store = _factory.Get(app.ClientSlug, app.Slug);
                total += await TickStoreAsync(store, _options, _logger, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analytics aggregation failed for app {Client}/{App}; skipping", app.ClientSlug, app.Slug);
            }
        }
        return total;
    }

    /// <summary>Aggregates one app's database. The per-store logic, isolated so it can be driven
    /// directly in tests and reused by the fan-out tick above.</summary>
    internal static async Task<int> TickStoreAsync(
        RSCIAnalyticsStore store, RSCAnalyticsOptions options, ILogger logger, CancellationToken ct)
    {
        var events = await store.ListUnaggregatedEventsAsync(options.AggregationBatchSize, ct)
            .ConfigureAwait(false);
        if (events.Count == 0) return 0;

        var sessionDeltas = new List<RSCAggregationSessionDelta>();
        var userDayDeltas = new List<RSCAggregationUserDayDelta>();
        var dailyRollupDeltas = new List<RSCAggregationDailyRollupDelta>();

        // Per (app, env, client, platform, day) bucket. Each bucket yields exactly one daily-rollup
        // delta, one session delta per session_id seen, and one user-day delta per distinct hashed user.
        var bucketed = events
            .GroupBy(e => (e.AppId, e.Environment, e.ClientId, e.Platform, Day: DateOnly.FromDateTime(e.OccurredAt.UtcDateTime)))
            .ToList();

        foreach (var bucket in bucketed)
        {
            var (appId, environment, clientId, platform, day) = bucket.Key;
            var list = bucket.ToList();

            int sessionsCount = 0;
            foreach (var sg in list.GroupBy(e => e.SessionId))
            {
                sessionsCount++;
                var ordered = sg.OrderBy(e => e.OccurredAt).ToList();
                var anonHash = ordered.FirstOrDefault(e => e.AnonymousIdHash is not null)?.AnonymousIdHash;
                sessionDeltas.Add(new RSCAggregationSessionDelta(
                    AppId: appId,
                    Environment: environment,
                    ClientId: clientId,
                    Platform: platform,
                    SessionId: sg.Key,
                    AnonymousIdHash: anonHash,
                    StartedAt: ordered.First().OccurredAt,
                    LastSeenAt: ordered.Last().OccurredAt,
                    EventCount: ordered.Count,
                    ScreenCount: ordered.Count(e => e.Type == "screen")));
            }

            long distinctUsers = 0;
            foreach (var ug in list.Where(e => e.AnonymousIdHash is not null).GroupBy(e => e.AnonymousIdHash!))
            {
                distinctUsers++;
                userDayDeltas.Add(new RSCAggregationUserDayDelta(
                    AppId: appId,
                    Environment: environment,
                    ClientId: clientId,
                    Platform: platform,
                    Day: day,
                    AnonymousIdHash: ug.Key,
                    HashVersion: options.IdentifierHashVersion,
                    Events: ug.Count()));
            }

            dailyRollupDeltas.Add(new RSCAggregationDailyRollupDelta(
                AppId: appId,
                Environment: environment,
                ClientId: clientId,
                Day: day,
                Platform: platform,
                Events: list.Count,
                Sessions: sessionsCount,
                DistinctUsers: distinctUsers));
        }

        // Carry the full tenant + platform alongside event_id: analytics_events is
        // UNIQUE(app_id, environment, client_id, platform, event_id), so the mark must match every
        // key column or it can stamp aggregated_at on a same-id row in another tenant/platform that
        // this tick never folded.
        var refs = events.Select(e => new RSCAggregationEventRef(e.AppId, e.Environment, e.ClientId, e.Platform, e.EventId)).ToList();
        var tick = new RSCAnalyticsAggregationTick(
            Sessions: sessionDeltas,
            UserDays: userDayDeltas,
            DailyRollups: dailyRollupDeltas,
            Events: refs);

        await store.WriteAggregationTickAsync(tick, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Analytics aggregated {Count} events across {Buckets} (app, env, client, platform, day) buckets",
            events.Count, bucketed.Count);

        return events.Count;
    }
}
