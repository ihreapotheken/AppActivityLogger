using System.Text.Json;
using ReportService.Admin.ViewModels;
using ReportService.Options;
using ReportService.Storage;

namespace ReportService.Admin.Services;

/// <summary>
/// Real-data error dashboard. Aggregates over <see cref="RSCIReportStore"/> by reading each
/// stored report JSON and counting per-platform crash volume + top error signatures + recent
/// occurrences. Two distinct populations drive the summary tiles: <b>crashes</b> (<c>Kind ==
/// "crash"</c>) and the broader <b>errors</b> set (<c>Kind == "crash"</c> OR <c>Kind ==
/// "error"</c>). Every crash is an error, but a non-fatal error-kind report is not a crash, so
/// the two counts can legitimately diverge. The crash-specific signature/recent/rate rollups
/// below are intentionally crash-only — they rely on the gzip stack-trace attachment that only
/// crash reports carry.
/// </summary>
public sealed class RSAReportStoreErrorDashboardService : IRSAErrorDashboardService
{
    private const int TopErrors = 5;
    private const int RecentLimit = 10;

    private readonly RSCIReportStore _store;
    private readonly RSCReportServiceOptions _options;

    public RSAReportStoreErrorDashboardService(RSCIReportStore store, RSCReportServiceOptions options)
    {
        _store = store;
        _options = options;
    }

    public RSAErrorDashboardVM Build(string? platform = null, RSAErrorRateWindow? rateWindow = null)
    {
        var now = DateTimeOffset.UtcNow;
        var dayAgo = now.AddDays(-1);
        // Default to the rolling last 7 days (daily) when the caller doesn't scope the chart.
        var rate = rateWindow ?? RSAErrorRateWindow.Resolve(RSAErrorRateRange.Last7Days, null, null, now);

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
        // Raw fault timestamps that fall inside the selected rate window; bucketed after the scan.
        var rateOccurrences = new List<DateTimeOffset>();

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
                // The "Errors" tiles count the wider fault population: crashes plus any report
                // explicitly tagged Kind=="error". General user-submitted reports (Kind null/other)
                // are not faults and are skipped entirely.
                var isError = isCrash || string.Equals(kind, "error", StringComparison.Ordinal);
                if (!isError) continue;

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
                    // Errors is the superset (every row reaching here is a fault); crashes is the
                    // strict crash subset, so the two tiles diverge whenever a non-crash error
                    // report lands in the window.
                    errors24hP++;
                    totalErrors24h++;
                    if (isCrash)
                    {
                        crashes24hP++;
                        totalCrashes24h++;
                    }
                    affectedP.Add(userKey);
                    affectedUsersAll.Add(userKey);
                }

                if (occurredAt >= rate.FromUtc && occurredAt < rate.ToUtc) rateOccurrences.Add(occurredAt);

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

        var errorRate = BucketErrorRate(rate, rateOccurrences);

        return new RSAErrorDashboardVM(
            CrashesLast24h: totalCrashes24h,
            ErrorsLast24h: totalErrors24h,
            AffectedUsers: affectedUsersAll.Count,
            Platforms: rows,
            TopErrors: topErrorRows,
            ErrorRate: errorRate,
            RecentErrors: recentRows);
    }

    /// <summary>
    /// Rolls the in-window fault timestamps into a contiguous, oldest→newest series of buckets sized
    /// by <see cref="RSAErrorRateWindow.Bucket"/>. Every bucket in the window is emitted (zero-count
    /// ones included) so the line stays continuous, and each carries a pre-formatted x-axis label.
    /// </summary>
    private static IReadOnlyList<RSAErrorRatePointVM> BucketErrorRate(RSAErrorRateWindow window, List<DateTimeOffset> occurrences)
    {
        var from = window.FromUtc.UtcDateTime;
        var to = window.ToUtc.UtcDateTime;

        // Contiguous bucket start instants covering [from, to). Day/Week align to the window start's
        // UTC date; Month aligns to calendar-month boundaries.
        var starts = new List<DateTime>();
        switch (window.Bucket)
        {
            case RSAErrorRateBucket.Day:
                for (var d = from.Date; d < to; d = d.AddDays(1)) starts.Add(d);
                break;
            case RSAErrorRateBucket.Week:
                for (var d = from.Date; d < to; d = d.AddDays(7)) starts.Add(d);
                break;
            case RSAErrorRateBucket.Month:
                for (var d = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc); d < to; d = d.AddMonths(1)) starts.Add(d);
                break;
        }
        if (starts.Count == 0) starts.Add(from.Date);

        var origin = starts[0];
        var counts = new int[starts.Count];
        foreach (var occ in occurrences)
        {
            var t = occ.UtcDateTime;
            var idx = window.Bucket switch
            {
                RSAErrorRateBucket.Day => (int)(t.Date - origin).TotalDays,
                RSAErrorRateBucket.Week => (int)((t.Date - origin).TotalDays / 7),
                RSAErrorRateBucket.Month => (t.Year - origin.Year) * 12 + (t.Month - origin.Month),
                _ => 0,
            };
            if (idx >= 0 && idx < counts.Length) counts[idx]++;
        }

        var points = new RSAErrorRatePointVM[starts.Count];
        for (var i = 0; i < starts.Count; i++)
        {
            var label = window.Bucket == RSAErrorRateBucket.Month
                ? starts[i].ToString("yyyy-MM")
                : starts[i].ToString("MM-dd");
            points[i] = new RSAErrorRatePointVM(label, counts[i]);
        }
        return points;
    }

    public IReadOnlyList<RSATrendingIssueVM> BuildTrending(int recentDays = 7, int limit = 5)
    {
        if (recentDays < 1) recentDays = 1;
        var now = DateTimeOffset.UtcNow;
        var recentFrom = now.AddDays(-recentDays);
        var priorFrom = now.AddDays(-2 * recentDays);

        // signature -> recent/prior occurrence counts, recent affected users, recent per-platform tally.
        var agg = new Dictionary<string, (int Recent, int Prior, HashSet<string> Users, Dictionary<string, int> Platforms)>(StringComparer.Ordinal);

        foreach (var p in _options.AllowedPlatforms)
        {
            foreach (var stored in _store.List(p))
            {
                // Only signature-able crashes (a real top_frame) — same rule as the top-errors table,
                // so a trending row always drills into a non-empty /Errors result.
                if (string.IsNullOrWhiteSpace(stored.TopFrame)) continue;

                var doc = TryReadDoc(p, stored.FileName);
                if (doc is null) continue;

                var kind = doc.GetStringOrNull("Kind") ?? doc.GetStringOrNull("kind");
                var isError = string.Equals(kind, "crash", StringComparison.Ordinal)
                              || string.Equals(kind, "error", StringComparison.Ordinal);
                if (!isError) continue;

                var occurredAt = doc.GetDateTimeOrNull("OccurredAt") ?? stored.SubmittedAt;
                var inRecent = occurredAt >= recentFrom;
                var inPrior = occurredAt >= priorFrom && occurredAt < recentFrom;
                if (!inRecent && !inPrior) continue;

                var sig = stored.TopFrame!;
                if (!agg.TryGetValue(sig, out var e))
                {
                    e = (0, 0, new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal));
                }

                if (inRecent)
                {
                    e.Recent++;
                    var rawUserId = doc.GetStringOrNull("UserId");
                    e.Users.Add(!string.IsNullOrWhiteSpace(rawUserId) ? "uid:" + rawUserId : "anon:" + stored.FileName);
                    e.Platforms[p] = e.Platforms.GetValueOrDefault(p) + 1;
                }
                else
                {
                    e.Prior++;
                }

                agg[sig] = e;
            }
        }

        return agg
            .Where(kv => kv.Value.Recent > 0)
            // Most active this week first; ties broken by the bigger week-over-week rise.
            .OrderByDescending(kv => kv.Value.Recent)
            .ThenByDescending(kv => kv.Value.Recent - kv.Value.Prior)
            .Take(limit)
            .Select(kv => new RSATrendingIssueVM(
                Signature: kv.Key,
                Platform: kv.Value.Platforms.OrderByDescending(x => x.Value).Select(x => x.Key).FirstOrDefault() ?? "",
                RecentCount: kv.Value.Recent,
                PriorCount: kv.Value.Prior,
                AffectedUsers: kv.Value.Users.Count))
            .ToArray();
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
