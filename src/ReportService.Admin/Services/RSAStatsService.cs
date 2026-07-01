using ReportService.Storage;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Services;

/// <summary>
/// Database-per-app Stats: resolves the apps in the current <c>(client, app)</c> scope from the
/// catalog, asks each app's own SQLite index for its window aggregates, and merges the results
/// (scalars summed, daily series merged by date, top-N buckets summed by key + re-ranked). Replaces
/// the old single-global-index read, which went empty once reports moved to per-app databases.
/// </summary>
internal sealed class RSAStatsService : IRSAStatsService
{
    private readonly RSCIReportStoreFactory _factory;
    private readonly RSCICatalog _catalog;

    public RSAStatsService(RSCIReportStoreFactory factory, RSCICatalog catalog)
    {
        _factory = factory;
        _catalog = catalog;
    }

    public async Task<RSCStatsReport> GetAsync(
        DateTimeOffset from, DateTimeOffset until, int topN, string? clientId, string? appId, CancellationToken ct)
    {
        var client = Norm(clientId);
        var app = Norm(appId);

        // Candidate apps: one client's (when scoped) or every client's, then narrowed to a single app
        // slug when the app axis is set. The merge then covers exactly the switcher's selection.
        var candidates = client is not null
            ? await _catalog.ListAppsAsync(client, includeArchived: true, ct).ConfigureAwait(false)
            : await _catalog.ListAllAppsAsync(includeArchived: true, ct).ConfigureAwait(false);
        var scoped = candidates
            .Where(a => app is null || string.Equals(a.Slug, app, StringComparison.OrdinalIgnoreCase))
            .Select(a => (a.ClientSlug, a.Slug))
            .ToList();

        var parts = new List<RSCStatsReport>(scoped.Count);
        foreach (var (c, a) in scoped)
        {
            try
            {
                parts.Add(await _factory.GetMaintenance(c, a).GetStatsAsync(from, until, topN, ct).ConfigureAwait(false));
            }
            catch
            {
                // One app's index being degraded shouldn't blank the whole page — skip it.
            }
        }

        return Merge(from, until, topN, parts);
    }

    private static RSCStatsReport Merge(DateTimeOffset from, DateTimeOffset until, int topN, IReadOnlyList<RSCStatsReport> parts)
    {
        int total = 0, multipart = 0, json = 0;
        long jsonBytes = 0, attachmentBytes = 0;
        var daily = new Dictionary<DateOnly, (int Multipart, int Json)>();
        var device = new Dictionary<string, int>(StringComparer.Ordinal);
        var pharmacy = new Dictionary<string, int>(StringComparer.Ordinal);
        var version = new Dictionary<string, int>(StringComparer.Ordinal);
        var platform = new Dictionary<string, int>(StringComparer.Ordinal);
        var channel = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var r in parts)
        {
            total += r.TotalReports;
            multipart += r.MultipartCount;
            json += r.JsonCount;
            jsonBytes += r.TotalJsonBytes;
            attachmentBytes += r.TotalAttachmentBytes;

            foreach (var d in r.Daily)
            {
                var cur = daily.TryGetValue(d.Date, out var v) ? v : (0, 0);
                daily[d.Date] = (cur.Item1 + d.Multipart, cur.Item2 + d.Json);
            }
            Accumulate(device, r.ByDeviceModel);
            Accumulate(pharmacy, r.ByPharmacy);
            Accumulate(version, r.ByAppVersion);
            Accumulate(platform, r.ByPlatform);
            Accumulate(channel, r.ByChannel);
        }

        var dailyMerged = daily
            .OrderBy(kv => kv.Key)
            .Select(kv => new RSCDailyVolume(kv.Key, kv.Value.Multipart, kv.Value.Json))
            .ToList();

        return new RSCStatsReport(
            From: from,
            Until: until,
            TotalReports: total,
            MultipartCount: multipart,
            JsonCount: json,
            TotalJsonBytes: jsonBytes,
            TotalAttachmentBytes: attachmentBytes,
            Daily: dailyMerged,
            ByDeviceModel: TopN(device, topN),
            ByPharmacy: TopN(pharmacy, topN),
            ByAppVersion: TopN(version, topN),
            ByPlatform: TopN(platform, topN),
            ByChannel: TopN(channel, topN));
    }

    private static void Accumulate(Dictionary<string, int> acc, IReadOnlyList<RSCStatsBucket> buckets)
    {
        foreach (var b in buckets)
            acc[b.Key] = (acc.TryGetValue(b.Key, out var n) ? n : 0) + b.Count;
    }

    // Re-rank the merged keys and keep the top N. Approximate at the tail (a key an app never reported
    // in its own top-N can't surface), but exact for the head the page actually shows.
    private static IReadOnlyList<RSCStatsBucket> TopN(Dictionary<string, int> acc, int n) =>
        acc.OrderByDescending(kv => kv.Value)
           .ThenBy(kv => kv.Key, StringComparer.Ordinal)
           .Take(n)
           .Select(kv => new RSCStatsBucket(kv.Key, kv.Value))
           .ToList();

    private static string? Norm(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToLowerInvariant();
}
