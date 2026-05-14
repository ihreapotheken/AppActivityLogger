using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Services;
using ReportService.Storage;

namespace ReportService.Admin.Pages;

/// <summary>
/// Aggregate statistics over a date window: tiles, per-day volume chart, top-N breakdowns.
/// Requires the SQLite index — without it the page renders a friendly notice instead of 5xx.
/// </summary>
public sealed class RSAStatsModel : PageModel
{
    private const int TopN = 50;
    private const int DefaultDays = 30;
    public const int BucketPageSize = 5;

    private readonly IRSAStatsService _stats;

    public RSAStatsModel(IRSAStatsService stats) => _stats = stats;

    [BindProperty(SupportsGet = true, Name = "from")] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true, Name = "until")] public DateTime? Until { get; set; }
    [BindProperty(SupportsGet = true, Name = "preset")] public string? Preset { get; set; }

    [BindProperty(SupportsGet = true, Name = "devicePage")] public int DevicePage { get; set; } = 1;
    [BindProperty(SupportsGet = true, Name = "pharmacyPage")] public int PharmacyPage { get; set; } = 1;
    [BindProperty(SupportsGet = true, Name = "versionPage")] public int VersionPage { get; set; } = 1;
    [BindProperty(SupportsGet = true, Name = "platformPage")] public int PlatformPage { get; set; } = 1;
    [BindProperty(SupportsGet = true, Name = "channelPage")] public int ChannelPage { get; set; } = 1;

    public RSCStatsReport? Stats { get; private set; }
    public DateTimeOffset RangeFrom { get; private set; }
    public DateTimeOffset RangeUntil { get; private set; }

    /// <summary>Builds a query string for a bucket pager link, preserving the current date window
    /// and every other bucket's page so flipping one bucket doesn't reset the others.</summary>
    public string BuildBucketQuery(string pageParam, int page)
    {
        var pairs = new List<(string Key, string Value)>();
        if (!string.IsNullOrEmpty(Preset)) pairs.Add(("preset", Preset!));
        if (From is { } f) pairs.Add(("from", f.ToString("yyyy-MM-ddTHH:mm")));
        if (Until is { } u) pairs.Add(("until", u.ToString("yyyy-MM-ddTHH:mm")));

        AddPage("devicePage", DevicePage);
        AddPage("pharmacyPage", PharmacyPage);
        AddPage("versionPage", VersionPage);
        AddPage("platformPage", PlatformPage);
        AddPage("channelPage", ChannelPage);

        var idx = pairs.FindIndex(p => p.Key == pageParam);
        if (idx >= 0) pairs[idx] = (pageParam, page.ToString());
        else if (page > 1) pairs.Add((pageParam, page.ToString()));

        return pairs.Count == 0 ? string.Empty :
            "?" + string.Join('&', pairs.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        void AddPage(string key, int value)
        {
            if (value > 1) pairs.Add((key, value.ToString()));
        }
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var (from, until) = ResolveWindow();
        RangeFrom = from;
        RangeUntil = until;
        Stats = await _stats.GetAsync(from, until, TopN, ct).ConfigureAwait(false);
    }

    private (DateTimeOffset From, DateTimeOffset Until) ResolveWindow()
    {
        // Presets win when supplied (operator clicked a quick-link); otherwise honour explicit
        // From/Until query params; otherwise default to "last 30 days".
        var todayUtc = DateTime.UtcNow.Date;
        if (!string.IsNullOrEmpty(Preset))
        {
            var until = todayUtc.AddDays(1);
            var fromDays = Preset switch
            {
                "7d" => 7,
                "14d" => 14,
                "30d" => 30,
                "60d" => 60,
                "90d" => 90,
                _ => DefaultDays,
            };
            return (Utc(until.AddDays(-fromDays)), Utc(until));
        }

        if (From is { } f && Until is { } u && u > f)
        {
            return (Utc(DateTime.SpecifyKind(f, DateTimeKind.Utc)),
                    Utc(DateTime.SpecifyKind(u, DateTimeKind.Utc)));
        }

        return (Utc(todayUtc.AddDays(-DefaultDays)), Utc(todayUtc.AddDays(1)));
    }

    private static DateTimeOffset Utc(DateTime dt) =>
        new(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
}
