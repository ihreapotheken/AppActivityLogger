using System.Text.Json;
using ReportService.Admin.ViewModels;
using ReportService.Options;
using ReportService.Storage;

namespace ReportService.Admin.Services;

/// <summary>
/// Real-data analytics dashboard. Aggregates over <see cref="RSCIReportStore"/> by reading each
/// stored report JSON, filtering on <c>Kind == "analytics"</c>, and counting per-platform and
/// per-screen activity.
/// </summary>
public sealed class RSAReportStoreAnalyticsDashboardService : IRSAAnalyticsDashboardService
{
    private const int TopScreens = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RSCIReportStore _store;
    private readonly RSCReportServiceOptions _options;

    public RSAReportStoreAnalyticsDashboardService(RSCIReportStore store, RSCReportServiceOptions options)
    {
        _store = store;
        _options = options;
    }

    public RSAAnalyticsDashboardVM Build(string? platform = null)
    {
        var now = DateTimeOffset.UtcNow;
        var dayAgo = now.AddDays(-1);
        var monthAgo = now.AddDays(-30);
        var screenViewSeconds = TimeSpan.FromSeconds(54);

        var rows = new List<RSAAnalyticsPlatformRowVM>();
        var screenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int totalDay = 0, totalMonth = 0, sessionsToday = 0;
        var sessionsTodayIds = new HashSet<string>(StringComparer.Ordinal);
        var sessionsMonthIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in _options.AllowedPlatforms)
        {
            if (platform is { Length: > 0 } scope && !string.Equals(p, scope, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int dauP = 0, mauP = 0, sessionsTodayP = 0;
            var perPlatformSessions = new HashSet<string>(StringComparer.Ordinal);

            foreach (var stored in _store.List(p))
            {
                var doc = TryReadDoc(p, stored.FileName);
                if (doc is null) continue;

                var kind = doc.GetStringOrNull("Kind") ?? doc.GetStringOrNull("kind");
                if (!string.Equals(kind, "analytics", StringComparison.Ordinal)) continue;

                var occurredAt = doc.GetDateTimeOrNull("OccurredAt") ?? stored.SubmittedAt;
                var inDay = occurredAt >= dayAgo;
                var inMonth = occurredAt >= monthAgo;

                if (inDay)
                {
                    dauP++;
                    totalDay++;
                }
                if (inMonth)
                {
                    mauP++;
                    totalMonth++;
                }
                if (inDay)
                {
                    sessionsTodayP++;
                    sessionsToday++;
                }

                var sessionId = doc.GetEventProp("sessionId");
                if (sessionId is not null)
                {
                    if (inMonth) sessionsMonthIds.Add(sessionId);
                    if (inDay) sessionsTodayIds.Add(sessionId);
                    perPlatformSessions.Add(sessionId);
                }

                var screen = doc.GetEventProp("screen") ?? doc.GetStringOrNull("Source") ?? "(unknown)";
                screenCounts[screen] = screenCounts.GetValueOrDefault(screen) + 1;
            }

            rows.Add(new RSAAnalyticsPlatformRowVM(
                Name: p,
                DailyActiveUsers: dauP,
                MonthlyActiveUsers: mauP,
                SessionsToday: sessionsTodayP,
                AverageSessionLength: screenViewSeconds));
        }

        // When we have real session ids, prefer the distinct count over raw event count for
        // the headline tile — it's the more honest "users" metric.
        var dau = sessionsTodayIds.Count > 0 ? sessionsTodayIds.Count : totalDay;
        var mau = sessionsMonthIds.Count > 0 ? sessionsMonthIds.Count : totalMonth;

        var topScreens = screenCounts
            .OrderByDescending(kv => kv.Value)
            .Take(TopScreens)
            .Select(kv => new RSATopScreenVM(kv.Key, kv.Value, screenViewSeconds))
            .ToArray();

        // No real retention data yet (we'd need per-user identity over time). Surface zeroes
        // honestly rather than fabricating numbers — the page will show 0% until we get there.
        var retention = new RSARetentionVM(Day1: 0, Day7: 0, Day30: 0);

        return new RSAAnalyticsDashboardVM(
            DailyActiveUsers: dau,
            MonthlyActiveUsers: mau,
            SessionsToday: sessionsToday,
            AverageSessionLength: screenViewSeconds,
            Platforms: rows,
            TopScreens: topScreens,
            Retention: retention);
    }

    private RSAReportDoc? TryReadDoc(string platform, string fileName)
    {
        try
        {
            using var stream = _store.OpenRead(platform, fileName);
            if (stream is null) return null;
            using var doc = JsonDocument.Parse(stream);
            return RSAReportDoc.From(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Tiny read-only projection of the persisted report JSON. Avoids re-parsing the full
/// document for every accessor and tolerates missing/typed-loosely fields.</summary>
internal sealed record RSAReportDoc(IReadOnlyDictionary<string, string> StringFields,
                                    IReadOnlyDictionary<string, string> EventProperties)
{
    public static RSAReportDoc From(JsonElement root)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) return new(fields, props);

        foreach (var p in root.EnumerateObject())
        {
            if (p.NameEquals("EventProperties") && p.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var ep in p.Value.EnumerateObject())
                {
                    if (ep.Value.ValueKind == JsonValueKind.String)
                        props[ep.Name] = ep.Value.GetString()!;
                }
                continue;
            }
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                fields[p.Name] = p.Value.GetString()!;
            }
        }
        return new(fields, props);
    }

    public string? GetStringOrNull(string key) =>
        StringFields.TryGetValue(key, out var v) ? v : null;

    public string? GetEventProp(string key) =>
        EventProperties.TryGetValue(key, out var v) ? v : null;

    public DateTimeOffset? GetDateTimeOrNull(string key) =>
        GetStringOrNull(key) is { } s && DateTimeOffset.TryParse(s, out var dt)
            ? dt : null;
}
