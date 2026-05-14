using ReportService.Admin.ViewModels;
using ReportService.Options;
using ReportService.Storage;

namespace ReportService.Admin.Services;

/// <summary>
/// Walks the configured platforms once, accumulates the channel split + bytes, and shapes the
/// dashboard view-model. This was previously inlined in <c>RSAIndexModel.OnGetAsync</c> alongside
/// page-controller plumbing — extracting it keeps the page model purely about HTTP concerns.
/// </summary>
internal sealed class RSADashboardService : IRSADashboardService
{
    private const int RecentLimit = 10;
    private const int RecentPerPlatformPick = 5;

    private readonly RSCIReportStore _store;
    private readonly RSCReportServiceOptions _options;

    public RSADashboardService(RSCIReportStore store, RSCReportServiceOptions options)
    {
        _store = store;
        _options = options;
    }

    public RSADashboardVM Build()
    {
        var platforms = new List<RSAPlatformRowVM>();
        var recent = new List<RSCStoredReport>();
        long jsonBytes = 0;
        long attBytes = 0;
        var total = 0;
        var multipart = 0;
        var json = 0;

        foreach (var p in _options.AllowedPlatforms)
        {
            var list = _store.List(p);
            total += list.Count;
            var pMultipart = 0;
            var pJson = 0;
            foreach (var r in list)
            {
                jsonBytes += r.SizeBytes;
                attBytes += r.AttachmentSizeBytes ?? 0;
                var (channel, _) = RSAReportRowMapper.ResolveChannel(r.IngestionChannel);
                if (channel == RSCIngestionChannels.Json) pJson++; else pMultipart++;
            }
            multipart += pMultipart;
            json += pJson;
            platforms.Add(new RSAPlatformRowVM(
                Name: p,
                Count: list.Count,
                MultipartCount: pMultipart,
                JsonCount: pJson,
                LatestSubmittedAt: list.Count > 0 ? list[0].SubmittedAt : null));
            recent.AddRange(list.Take(RecentPerPlatformPick));
        }

        var recentRows = recent
            .OrderByDescending(r => r.SubmittedAt)
            .Take(RecentLimit)
            .Select(r => r.ToRow())
            .ToList();

        return new RSADashboardVM(
            TotalCount: total,
            MultipartCount: multipart,
            JsonCount: json,
            TotalJsonBytes: jsonBytes,
            TotalAttachmentBytes: attBytes,
            Platforms: platforms,
            Recent: recentRows);
    }
}
