using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Options;

namespace ReportService.Analytics;

/// <summary>
/// Background service that folds raw analytics events into the rollup tables. Pulls a bounded
/// batch of unaggregated events per tick, groups them by (platform, day) and by session, and
/// writes the rollups via <see cref="RSCIAnalyticsStore"/> upserts before marking the source
/// events aggregated.
/// </summary>
/// <remarks>
/// Exactly-once-per-tick: the worker hands the precomputed deltas plus the source event IDs to
/// <see cref="RSCIAnalyticsStore.WriteAggregationTickAsync"/>, which applies the upserts and marks
/// the events <c>aggregated_at = now</c> inside a single SQLite transaction. A crash before commit
/// leaves the rows in the unaggregated pool with no rollup contribution; the next tick replays
/// them as if the first never happened.
/// </remarks>
public sealed class RSCAnalyticsAggregationWorker : BackgroundService
{
    private readonly RSCIAnalyticsStore _store;
    private readonly RSCAnalyticsOptions _options;
    private readonly ILogger<RSCAnalyticsAggregationWorker> _logger;

    public RSCAnalyticsAggregationWorker(
        RSCIAnalyticsStore store,
        RSCAnalyticsOptions options,
        ILogger<RSCAnalyticsAggregationWorker> logger)
    {
        _store = store;
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

    internal async Task<int> TickAsync(CancellationToken ct)
    {
        var events = await _store.ListUnaggregatedEventsAsync(_options.AggregationBatchSize, ct)
            .ConfigureAwait(false);
        if (events.Count == 0) return 0;

        var sessionDeltas = new List<RSCAggregationSessionDelta>();
        var userDayDeltas = new List<RSCAggregationUserDayDelta>();
        var dailyRollupDeltas = new List<RSCAggregationDailyRollupDelta>();

        // Per (platform, day) bucket. Each bucket yields exactly one daily-rollup delta, one
        // session delta per session_id seen, and one user-day delta per distinct hashed user.
        var bucketed = events
            .GroupBy(e => (e.Platform, Day: DateOnly.FromDateTime(e.OccurredAt.UtcDateTime)))
            .ToList();

        foreach (var bucket in bucketed)
        {
            var (platform, day) = bucket.Key;
            var list = bucket.ToList();

            int sessionsCount = 0;
            foreach (var sg in list.GroupBy(e => e.SessionId))
            {
                sessionsCount++;
                var ordered = sg.OrderBy(e => e.OccurredAt).ToList();
                var anonHash = ordered.FirstOrDefault(e => e.AnonymousIdHash is not null)?.AnonymousIdHash;
                sessionDeltas.Add(new RSCAggregationSessionDelta(
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
                    Platform: platform,
                    Day: day,
                    AnonymousIdHash: ug.Key,
                    HashVersion: _options.IdentifierHashVersion,
                    Events: ug.Count()));
            }

            dailyRollupDeltas.Add(new RSCAggregationDailyRollupDelta(
                Day: day,
                Platform: platform,
                Events: list.Count,
                Sessions: sessionsCount,
                DistinctUsers: distinctUsers));
        }

        var ids = events.Select(e => e.EventId).ToList();
        var tick = new RSCAnalyticsAggregationTick(
            Sessions: sessionDeltas,
            UserDays: userDayDeltas,
            DailyRollups: dailyRollupDeltas,
            EventIds: ids);

        await _store.WriteAggregationTickAsync(tick, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Analytics aggregated {Count} events across {Buckets} (platform, day) buckets",
            events.Count, bucketed.Count);

        return events.Count;
    }
}
