using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.ViewModels;
using ReportService.Analytics;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

public sealed class RSAAnalyticsEventsModel : PageModel
{
    private const int PageSize = 50;

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCReportServiceOptions _options;
    private readonly RSCICatalog _catalog;

    public RSAAnalyticsEventsModel(RSCIAnalyticsStore store, RSCReportServiceOptions options, RSCICatalog catalog)
    {
        _store = store;
        _options = options;
        _catalog = catalog;
    }

    [BindProperty(SupportsGet = true)] public string? Platform { get; set; }
    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }
    [BindProperty(SupportsGet = true, Name = "env")] public string? Env { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }
    [BindProperty(SupportsGet = true)] public string? Type { get; set; }
    [BindProperty(SupportsGet = true)] public string? Name { get; set; }
    [BindProperty(SupportsGet = true)] public string? Screen { get; set; }
    [BindProperty(SupportsGet = true, Name = "session")] public string? SessionId { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? Until { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public RSCAnalyticsEventPage Result { get; private set; } = default!;
    public IReadOnlyList<string> AvailablePlatforms => _options.AllowedPlatforms;
    public RSATenantScopeVM TenantScope { get; private set; } = default!;

    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)Result.Total / PageSize));

    public async Task OnGetAsync(CancellationToken ct)
    {
        TenantScope = await RSATenantScopes.BuildVmAsync(_catalog, "/AnalyticsEvents", App, Env, Client, Platform, ct).ConfigureAwait(false);
        var offset = Math.Max(0, (Math.Max(1, PageNumber) - 1) * PageSize);
        var filter = new RSCAnalyticsEventFilter(
            Platform: Platform,
            Type: Type,
            Name: Name,
            Screen: Screen,
            SessionId: SessionId,
            From: From is { } f ? new DateTimeOffset(DateTime.SpecifyKind(f, DateTimeKind.Utc)) : null,
            Until: Until is { } u ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)) : null,
            Limit: PageSize,
            Offset: offset,
            AppId: RSATenantScopes.Norm(App),
            Environment: RSATenantScopes.Norm(Env),
            ClientId: RSATenantScopes.Norm(Client));
        Result = await _store.SearchEventsAsync(filter, ct).ConfigureAwait(false);
    }

    public string BuildPageHref(int page)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) parts.Add($"{key}={Uri.EscapeDataString(value)}");
        }
        Add("app", App);
        Add("env", Env);
        Add("client", Client);
        Add("platform", Platform);
        Add("type", Type);
        Add("name", Name);
        Add("screen", Screen);
        Add("session", SessionId);
        if (From is { } f) Add("from", f.ToString("yyyy-MM-ddTHH:mm"));
        if (Until is { } u) Add("until", u.ToString("yyyy-MM-ddTHH:mm"));
        if (page > 1) Add("page", page.ToString());
        return parts.Count == 0 ? "?" : "?" + string.Join('&', parts);
    }
}
