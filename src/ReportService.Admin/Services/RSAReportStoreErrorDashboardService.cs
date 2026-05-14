using System.Text.Json;
using ReportService.Admin.ViewModels;
using ReportService.Options;
using ReportService.Storage;

namespace ReportService.Admin.Services;

/// <summary>
/// Real-data error dashboard. Aggregates over <see cref="RSCIReportStore"/> by reading each
/// stored report JSON, filtering on <c>Kind == "crash"</c>, and counting per-platform crash
/// volume + top error signatures + recent occurrences.
/// </summary>
public sealed class RSAReportStoreErrorDashboardService : IRSAErrorDashboardService
{
    private const int TopErrors = 5;
    private const int RecentLimit = 10;
    private const int RateWindowDays = 7;

    private readonly RSCIReportStore _store;
    private readonly RSCReportServiceOptions _options;

    public RSAReportStoreErrorDashboardService(RSCIReportStore store, RSCReportServiceOptions options)
    {
        _store = store;
        _options = options;
    }

    public RSAErrorDashboardVM Build(string? platform = null)
    {
        var now = DateTimeOffset.UtcNow;
        var dayAgo = now.AddDays(-1);

        var rows = new List<RSAErrorPlatformRowVM>();
        // Group key: the server-extracted top stack frame (set at ingest from the gzip
        // attachment, see RSCSqliteIndexingReportStore.TryExtractTopFrame). When no frame could
        // be extracted (e.g. no attachment or attachment isn't a plain stack trace), fall back
        // to the truncated message so distinct user-submitted reports don't collapse into one
        // bucket. The legacy `Type` field is no longer part of the key — every SDK shipped a
        // static placeholder string, so it never disambiguated anything.
        // Track the multipart/json split per signature so the row can show "m: 8 / j: 2" — the
        // operator can tell at a glance whether a fault site is hitting only the SDK channel or
        // both. Recent occurrences carry the same channel tag for the same reason.
        var topErrors = new Dictionary<string, (int Occurrences, HashSet<string> Users, string Sample, int Multipart, int Json)>(StringComparer.Ordinal);
        var recent = new List<(DateTimeOffset OccurredAt, string Platform, string Sample, string Channel)>();
        var perDay = new int[RateWindowDays];

        int totalCrashes24h = 0, totalErrors24h = 0;
        var affectedUsersAll = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in _options.AllowedPlatforms)
        {
            if (platform is { Length: > 0 } scope && !string.Equals(p, scope, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int crashes24hP = 0, errors24hP = 0;
            var affectedP = new HashSet<string>(StringComparer.Ordinal);

            foreach (var stored in _store.List(p))
            {
                var doc = TryReadDoc(p, stored.FileName);
                if (doc is null) continue;

                var kind = doc.GetStringOrNull("Kind") ?? doc.GetStringOrNull("kind");
                var isCrash = string.Equals(kind, "crash", StringComparison.Ordinal);
                if (!isCrash) continue;

                var occurredAt = doc.GetDateTimeOrNull("OccurredAt") ?? stored.SubmittedAt;
                // Prefer the stable per-install UserId surfaced by the SDK (Android wires
                // GetDeviceIdUseCase; iOS will follow). It's the only field that actually
                // distinguishes one user from another — DeviceModel + PharmacyId collapses every
                // install of the same phone in the same pharmacy into a single "affected user".
                // Legacy reports (and the test fixtures shipped in reports/) predate UserId, so
                // fall back to the file name to count each unidentified crash as its own affected
                // user. That over-counts in the worst case (re-tries from one install look like
                // many users), but under-counting was the bug — the dashboard was showing 1 user
                // when 50 distinct people had crashed.
                var rawUserId = doc.GetStringOrNull("UserId");
                var userKey = !string.IsNullOrWhiteSpace(rawUserId)
                    ? "uid:" + rawUserId
                    : "anon:" + stored.FileName;

                var inDay = occurredAt >= dayAgo;
                if (inDay)
                {
                    crashes24hP++;
                    totalCrashes24h++;
                    errors24hP++;
                    totalErrors24h++;
                    affectedP.Add(userKey);
                    affectedUsersAll.Add(userKey);
                }

                var dayBucket = (int)Math.Floor((now - occurredAt).TotalDays);
                if (dayBucket >= 0 && dayBucket < RateWindowDays) perDay[dayBucket]++;

                var message = doc.GetStringOrNull("Message") ?? "";
                var rowChannel = string.Equals(stored.IngestionChannel, RSCIngestionChannels.Json, StringComparison.OrdinalIgnoreCase)
                    ? RSCIngestionChannels.Json
                    : RSCIngestionChannels.Multipart;

                // Only roll up reports whose `top_frame` column is set — those are the rows the
                // `topFrame=` URL filter on /Errors can actually find. Crashes whose stack trace
                // shape couldn't be parsed at ingest (e.g. iOS synthetic traces, non-Java
                // frames) have a NULL top_frame; surfacing a message-fallback "signature" here
                // would produce a clickable link that drills into an empty result page. Those
                // reports still show up in Recent errors + per-platform tiles below.
                if (!string.IsNullOrWhiteSpace(stored.TopFrame))
                {
                    var sample = stored.TopFrame!;
                    if (!topErrors.TryGetValue(sample, out var entry))
                    {
                        entry = (0, new HashSet<string>(StringComparer.Ordinal), sample, 0, 0);
                    }
                    entry.Occurrences++;
                    entry.Users.Add(userKey);
                    if (rowChannel == RSCIngestionChannels.Json) entry.Json++;
                    else entry.Multipart++;
                    topErrors[sample] = entry;
                }

                // Recent-errors feed: use the top frame when we have one, otherwise the message
                // first line so the operator can still see what happened. The signature link in
                // the Recent table only renders when the row has a real top frame — same
                // reasoning as above.
                var recentSample = !string.IsNullOrWhiteSpace(stored.TopFrame)
                    ? stored.TopFrame!
                    : Truncate(message, 96);
                recent.Add((occurredAt, p, recentSample, rowChannel));
            }

            rows.Add(new RSAErrorPlatformRowVM(
                Name: p,
                CrashesLast24h: crashes24hP,
                ErrorsLast24h: errors24hP,
                AffectedUsers: affectedP.Count));
        }

        var topErrorRows = topErrors
            .OrderByDescending(kv => kv.Value.Occurrences)
            .Take(TopErrors)
            .Select(kv => new RSATopErrorVM(
                Signature: kv.Value.Sample,
                Occurrences: kv.Value.Occurrences,
                AffectedUsers: kv.Value.Users.Count,
                MultipartCount: kv.Value.Multipart,
                JsonCount: kv.Value.Json))
            .ToArray();

        var recentRows = recent
            .OrderByDescending(r => r.OccurredAt)
            .Take(RecentLimit)
            .Select(r => new RSARecentErrorVM(r.OccurredAt, r.Platform, r.Sample, r.Channel))
            .ToArray();

        // Reverse perDay so index 0 = today, length-1 = oldest of the last 7 days.
        var rate7d = perDay.ToArray();

        return new RSAErrorDashboardVM(
            CrashesLast24h: totalCrashes24h,
            ErrorsLast24h: totalErrors24h,
            AffectedUsers: affectedUsersAll.Count,
            Platforms: rows,
            TopErrors: topErrorRows,
            ErrorRateLast7Days: rate7d,
            RecentErrors: recentRows);
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
