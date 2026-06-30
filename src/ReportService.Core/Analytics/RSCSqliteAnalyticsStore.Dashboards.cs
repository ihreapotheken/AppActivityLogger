using System.Globalization;
using System.Text.Json;
using ReportService.Models;

namespace ReportService.Analytics;

public sealed partial class RSCSqliteAnalyticsStore
{
    // -------- Dashboards --------

    public async Task<RSCAnalyticsTotals> GetTotalsAsync(RSCAnalyticsScope scope, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = today.AddDays(-6);
        var monthStart = today.AddDays(-29);

        var (clause, binder) = BuildScopeClause(scope);
        var (sessionsClause, sessionsBinder) = BuildScopeClause(scope);
        var todayStr = today.ToString("yyyy-MM-dd");

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            long dau, wau, mau;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = $@"
SELECT
  (SELECT COUNT(DISTINCT anonymous_id_hash) FROM analytics_user_days WHERE day = @today  {clause}),
  (SELECT COUNT(DISTINCT anonymous_id_hash) FROM analytics_user_days WHERE day >= @week  {clause}),
  (SELECT COUNT(DISTINCT anonymous_id_hash) FROM analytics_user_days WHERE day >= @month {clause});";
                cmd.Parameters.AddWithValue("@today", todayStr);
                cmd.Parameters.AddWithValue("@week", weekStart.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@month", monthStart.ToString("yyyy-MM-dd"));
                binder(cmd);
                using var r = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                if (await r.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    dau = r.GetInt64(0); wau = r.GetInt64(1); mau = r.GetInt64(2);
                }
                else
                {
                    dau = wau = mau = 0;
                }
            }

            long sessionsToday = 0, eventsToday = 0;
            double avgSessionSeconds = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = $@"
SELECT
  COUNT(*),
  COALESCE(SUM(event_count), 0),
  COALESCE(AVG((julianday(last_seen_at) - julianday(started_at)) * 86400.0), 0)
FROM analytics_sessions
WHERE started_at >= @start {sessionsClause};";
                cmd.Parameters.AddWithValue("@start", todayStr + "T00:00:00.0000000Z");
                sessionsBinder(cmd);
                using var r = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                if (await r.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    sessionsToday = r.GetInt64(0);
                    eventsToday = r.GetInt64(1);
                    avgSessionSeconds = r.GetDouble(2);
                }
            }

            DateTimeOffset? lastAggregated = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "SELECT MAX(aggregated_at) FROM analytics_events WHERE aggregated_at IS NOT NULL;";
                var raw = await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false);
                if (raw is string s && !string.IsNullOrEmpty(s)) lastAggregated = ParseIso(s);
            }

            return new RSCAnalyticsTotals(
                DailyActiveUsers: dau,
                WeeklyActiveUsers: wau,
                MonthlyActiveUsers: mau,
                SessionsToday: sessionsToday,
                EventsToday: eventsToday,
                AverageSessionDuration: TimeSpan.FromSeconds(Math.Max(0, avgSessionSeconds)),
                LastAggregatedAt: lastAggregated);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsPlatformSummary>> GetPlatformSummariesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT b.platform,
       COALESCE(SUM(b.accepted_count), 0),
       COALESCE(SUM(b.rejected_count), 0),
       COUNT(*),
       MAX(b.received_at)
FROM analytics_batches b
GROUP BY b.platform
ORDER BY b.platform;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsPlatformSummary>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;

            var result = new List<RSCAnalyticsPlatformSummary>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                DateTimeOffset? lastReceived = reader.IsDBNull(4) ? null : ParseIso(reader.GetString(4));
                result.Add(new RSCAnalyticsPlatformSummary(
                    Platform: reader.GetString(0),
                    AcceptedEvents: reader.GetInt64(1),
                    RejectedEvents: reader.GetInt64(2),
                    Batches: reader.GetInt64(3),
                    LastReceivedAt: lastReceived));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsTopScreen>> GetTopScreensAsync(RSCAnalyticsScope scope, int topN, CancellationToken ct)
    {
        topN = Math.Clamp(topN, 1, 100);
        var (clause, binder) = BuildScopeClause(scope);
        var sql = $@"
SELECT COALESCE(screen, '(unset)') AS k,
       COUNT(*) AS views,
       COALESCE(AVG(duration_ms), 0) AS avg_ms
FROM analytics_events
WHERE type = 'screen' {clause}
GROUP BY k
ORDER BY views DESC, k ASC
LIMIT @top;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsTopScreen>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@top", topN);
            binder(cmd);

            var result = new List<RSCAnalyticsTopScreen>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                var avgMs = reader.GetDouble(2);
                result.Add(new RSCAnalyticsTopScreen(
                    Screen: reader.GetString(0),
                    Views: reader.GetInt64(1),
                    AverageDuration: TimeSpan.FromMilliseconds(Math.Max(0, avgMs))));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RSCAnalyticsDailyRollup>> GetDailyRollupsAsync(
        DateTimeOffset from, DateTimeOffset until, RSCAnalyticsScope scope, CancellationToken ct)
    {
        var (clause, binder) = BuildScopeClause(scope);
        var sql = $@"
SELECT app_id, environment, client_id, day, platform, events, sessions, distinct_users
FROM analytics_daily_rollups
WHERE day >= @from AND day < @until {clause}
ORDER BY day ASC, platform ASC;";

        return await ExecuteWithRetryAsync<IReadOnlyList<RSCAnalyticsDailyRollup>>(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@from", DateOnly.FromDateTime(from.UtcDateTime).ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@until", DateOnly.FromDateTime(until.UtcDateTime).ToString("yyyy-MM-dd"));
            binder(cmd);

            var result = new List<RSCAnalyticsDailyRollup>();
            using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
            while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
            {
                result.Add(new RSCAnalyticsDailyRollup(
                    AppId: reader.GetString(0),
                    Environment: reader.GetString(1),
                    ClientId: reader.GetString(2),
                    Day: DateOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    Platform: reader.GetString(4),
                    Events: reader.GetInt64(5),
                    Sessions: reader.GetInt64(6),
                    DistinctUsers: reader.GetInt64(7)));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    // Items are persisted camelCase (see RSCAnalyticsValidator.SerializeItems), so read them back
    // with the same naming policy. Reused across rows to avoid re-allocating the options per parse.
    private static readonly JsonSerializerOptions SalesItemsReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Prescription-funnel events surfaced alongside OTC sales. Curated to the steps that mean a
    // prescription is heading toward fulfilment (upload/scan captured → added to cart → transferred
    // to a pharmacy) rather than every prescription event, so the breakdown stays legible.
    private static readonly IReadOnlyList<string> PrescriptionActivityNames = new[]
    {
        "prescription_upload_success",
        "prescription_scan_result",
        "prescription_cart_add",
        "prescription_transfer",
    };

    public async Task<RSCAnalyticsSalesReport> GetSalesReportAsync(
        DateTimeOffset from, DateTimeOffset until, RSCAnalyticsScope scope, int topItems, CancellationToken ct)
    {
        topItems = Math.Clamp(topItems, 1, 100);
        var (clause, binder) = BuildScopeClause(scope);
        var fromDay = DateOnly.FromDateTime(from.UtcDateTime).ToString("yyyy-MM-dd");
        var untilDay = DateOnly.FromDateTime(until.UtcDateTime).ToString("yyyy-MM-dd");

        // Window on the date prefix of occurred_at (an ISO-8601 string with a +00:00 offset). A
        // substr/string compare is offset-robust and UTC-day aligned — the same window semantics as
        // GetDailyRollupsAsync (day >= from AND day < until). type+name are covered by
        // idx_analytics_events_type_name, so the purchase scan touches only the purchase slice.
        var purchaseSql = $@"
SELECT substr(occurred_at, 1, 10) AS day, properties_json, items_json
FROM analytics_events
WHERE type = 'ecommerce' AND name = 'purchase'
  AND substr(occurred_at, 1, 10) >= @from AND substr(occurred_at, 1, 10) < @until {clause};";

        var rxSql = $@"
SELECT name, COUNT(*) AS c
FROM analytics_events
WHERE name IN ('prescription_upload_success', 'prescription_scan_result', 'prescription_cart_add', 'prescription_transfer')
  AND substr(occurred_at, 1, 10) >= @from AND substr(occurred_at, 1, 10) < @until {clause}
GROUP BY name;";

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            decimal totalRevenue = 0m;
            var orderCount = 0;
            long itemsSold = 0;
            var byDay = new Dictionary<DateOnly, (decimal Revenue, int Orders)>();
            var byShipping = new Dictionary<string, (decimal Revenue, int Orders)>(StringComparer.Ordinal);
            var byPayment = new Dictionary<string, (decimal Revenue, int Orders)>(StringComparer.Ordinal);
            var byItem = new Dictionary<string, (string? Name, string? Category, long Units, decimal Revenue)>(StringComparer.Ordinal);
            var currencyOrders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = purchaseSql;
                cmd.Parameters.AddWithValue("@from", fromDay);
                cmd.Parameters.AddWithValue("@until", untilDay);
                binder(cmd);

                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    var day = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var props = ParsePropertiesJson(reader.GetString(1));
                    var items = ParseItemsJson(reader.GetString(2));

                    // Per-item revenue (price × quantity) drives the top-products table and is the
                    // fallback order value when the SDK didn't send a `total` property.
                    decimal itemsRevenue = 0m;
                    foreach (var it in items)
                    {
                        if (string.IsNullOrEmpty(it.ItemId)) continue;
                        var qty = it.Quantity is { } q && q > 0 ? q : 1;
                        var lineRevenue = (it.Price ?? 0m) * qty;
                        itemsRevenue += lineRevenue;
                        itemsSold += qty;
                        byItem.TryGetValue(it.ItemId, out var agg);
                        byItem[it.ItemId] = (
                            agg.Name ?? it.Name,
                            agg.Category ?? it.Category,
                            agg.Units + qty,
                            agg.Revenue + lineRevenue);
                    }

                    var orderRevenue = props.TryGetValue("total", out var totalStr)
                        && decimal.TryParse(totalStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                            ? parsed
                            : itemsRevenue;

                    var currency = props.TryGetValue("currency", out var cur) && !string.IsNullOrWhiteSpace(cur)
                        ? cur.Trim().ToUpperInvariant()
                        : "EUR";
                    var shipping = NormalizeDimension(props, "shipping_method");
                    var payment = NormalizeDimension(props, "payment_method");

                    totalRevenue += orderRevenue;
                    orderCount++;

                    byDay.TryGetValue(day, out var d);
                    byDay[day] = (d.Revenue + orderRevenue, d.Orders + 1);
                    byShipping.TryGetValue(shipping, out var sh);
                    byShipping[shipping] = (sh.Revenue + orderRevenue, sh.Orders + 1);
                    byPayment.TryGetValue(payment, out var pay);
                    byPayment[payment] = (pay.Revenue + orderRevenue, pay.Orders + 1);
                    currencyOrders.TryGetValue(currency, out var cc);
                    currencyOrders[currency] = cc + 1;
                }
            }

            var prescriptions = new List<RSCSalesActivity>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = rxSql;
                cmd.Parameters.AddWithValue("@from", fromDay);
                cmd.Parameters.AddWithValue("@until", untilDay);
                binder(cmd);

                var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                    counts[reader.GetString(0)] = reader.GetInt64(1);

                // Emit in the curated order regardless of which names had rows, so the page renders a
                // stable breakdown (a step with zero observations still appears, as zero).
                foreach (var name in PrescriptionActivityNames)
                {
                    counts.TryGetValue(name, out var c);
                    prescriptions.Add(new RSCSalesActivity(name, c));
                }
            }

            // Dominant currency wins the report-level label; mixed-currency totals are summed naively
            // (the dev data is EUR-only, and a single label keeps the tiles readable).
            var dominantCurrency = currencyOrders.Count == 0
                ? "EUR"
                : currencyOrders.OrderByDescending(kv => kv.Value).First().Key;

            var byDayList = byDay
                .OrderBy(kv => kv.Key)
                .Select(kv => new RSCSalesDayPoint(kv.Key, kv.Value.Revenue, kv.Value.Orders))
                .ToArray();
            var byShippingList = byShipping
                .OrderByDescending(kv => kv.Value.Revenue).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new RSCSalesDimension(kv.Key, kv.Value.Revenue, kv.Value.Orders))
                .ToArray();
            var byPaymentList = byPayment
                .OrderByDescending(kv => kv.Value.Revenue).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new RSCSalesDimension(kv.Key, kv.Value.Revenue, kv.Value.Orders))
                .ToArray();
            var topItemsList = byItem
                .OrderByDescending(kv => kv.Value.Revenue).ThenByDescending(kv => kv.Value.Units).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(topItems)
                .Select(kv => new RSCSalesItemRow(kv.Key, kv.Value.Name, kv.Value.Category, kv.Value.Units, kv.Value.Revenue))
                .ToArray();

            return new RSCAnalyticsSalesReport(
                TotalRevenue: totalRevenue,
                OrderCount: orderCount,
                ItemsSold: itemsSold,
                Currency: dominantCurrency,
                ByDay: byDayList,
                ByShippingMethod: byShippingList,
                ByPaymentMethod: byPaymentList,
                TopItems: topItemsList,
                Prescriptions: prescriptions);
        }, ct).ConfigureAwait(false);
    }

    private static string NormalizeDimension(IReadOnlyDictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.Trim().ToLowerInvariant()
            : "(unset)";

    private static Dictionary<string, string> ParsePropertiesJson(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}") return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyList<RSCAnalyticsItem> ParseItemsJson(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return Array.Empty<RSCAnalyticsItem>();
        try
        {
            return JsonSerializer.Deserialize<List<RSCAnalyticsItem>>(json, SalesItemsReadOptions)
                ?? (IReadOnlyList<RSCAnalyticsItem>)Array.Empty<RSCAnalyticsItem>();
        }
        catch (JsonException)
        {
            return Array.Empty<RSCAnalyticsItem>();
        }
    }

    public async Task<RSCAnalyticsHealthSnapshot> GetHealthSnapshotAsync(int sampleSize, CancellationToken ct)
    {
        sampleSize = Math.Clamp(sampleSize, 1, 100);

        return await ExecuteWithRetryAsync(async innerCt =>
        {
            using var conn = await OpenConnectionAsync(innerCt).ConfigureAwait(false);

            long total = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "SELECT COUNT(*) FROM analytics_dead_letters;";
                total = Convert.ToInt64(await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false) ?? 0L);
            }

            var byReason = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "SELECT reason, COUNT(*) FROM analytics_dead_letters GROUP BY reason;";
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    byReason[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            var samples = new List<RSCAnalyticsDeadLetterRow>(sampleSize);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT id, received_at, platform, batch_id, event_id, reason, detail, raw_json
FROM analytics_dead_letters
ORDER BY id DESC
LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", sampleSize);
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    samples.Add(new RSCAnalyticsDeadLetterRow(
                        Id: reader.GetInt64(0),
                        ReceivedAt: ParseIso(reader.GetString(1)),
                        Platform: reader.GetString(2),
                        BatchId: reader.GetString(3),
                        EventId: reader.IsDBNull(4) ? null : reader.GetString(4),
                        Reason: reader.GetString(5),
                        Detail: reader.IsDBNull(6) ? null : reader.GetString(6),
                        RawJson: reader.GetString(7)));
                }
            }

            var sdkVersions = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = @"
SELECT sdk_version, COUNT(*) FROM analytics_batches
GROUP BY sdk_version
ORDER BY COUNT(*) DESC
LIMIT 20;";
                using var reader = await cmd.ExecuteReaderAsync(innerCt).ConfigureAwait(false);
                while (await reader.ReadAsync(innerCt).ConfigureAwait(false))
                {
                    sdkVersions[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            DateTimeOffset? lastAggregated = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.CommandText = "SELECT MAX(aggregated_at) FROM analytics_events WHERE aggregated_at IS NOT NULL;";
                var raw = await cmd.ExecuteScalarAsync(innerCt).ConfigureAwait(false);
                if (raw is string s && !string.IsNullOrEmpty(s)) lastAggregated = ParseIso(s);
            }

            return new RSCAnalyticsHealthSnapshot(
                DeadLetterTotal: total,
                DeadLettersByReason: byReason,
                RecentSamples: samples,
                SdkVersionsSeen: sdkVersions,
                LastAggregatedAt: lastAggregated);
        }, ct).ConfigureAwait(false);
    }
}
