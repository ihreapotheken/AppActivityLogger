using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Admin.Options;
using ReportService.Admin.Services;
using ReportService.Analytics;
using ReportService.Audit;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Pages;

/// <summary>
/// In-dashboard API console — a Hoppscotch/Postman-style request runner backed by the bundled Postman
/// collection. The page ships the raw collection + environment JSON to the browser as data blocks;
/// <c>wwwroot/js/api-console.js</c> flattens the folders, substitutes <c>{{variables}}</c>, and runs
/// each request with <c>fetch()</c> against this same origin (so the admin session cookie + dev API key
/// carry over and there is no CORS hop). No request payload ever touches the server here — this page
/// only serves the request library; execution is entirely client-side.
///
/// <para>The one server-side exception is the <b>mock data generator</b> (dev instances only,
/// <see cref="OnPostGenerateMockAsync"/>): a POST handler that creates synthetic clients, apps,
/// analytics events and problem reports directly through the catalog + stores, so an operator can fill
/// the dashboards for a demo without hand-crafting ingestion requests.</para>
/// </summary>
public sealed class RSAApiConsoleModel : PageModel
{
    private readonly IRSAApiConsoleService _fixtures;
    private readonly RSCReportServiceOptions _reportOptions;
    private readonly RSAAdminOptions _adminOptions;
    private readonly RSCICatalog _catalog;
    private readonly RSCIAnalyticsStore _analyticsStore;
    private readonly RSCAnalyticsValidator _validator;
    private readonly RSCAnalyticsIdentifierHasher _hasher;
    private readonly RSCIReportStore _reportStore;
    private readonly RSCIAuditLog _audit;
    private readonly ILogger<RSAApiConsoleModel> _logger;

    public RSAApiConsoleModel(
        IRSAApiConsoleService fixtures, RSCReportServiceOptions reportOptions, RSAAdminOptions adminOptions,
        RSCICatalog catalog, RSCIAnalyticsStore analyticsStore, RSCAnalyticsValidator validator,
        RSCAnalyticsIdentifierHasher hasher, RSCIReportStore reportStore, RSCIAuditLog audit,
        ILogger<RSAApiConsoleModel> logger)
    {
        _fixtures = fixtures;
        _reportOptions = reportOptions;
        _adminOptions = adminOptions;
        _catalog = catalog;
        _analyticsStore = analyticsStore;
        _validator = validator;
        _hasher = hasher;
        _reportStore = reportStore;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>True on a dev instance (auto-sign-in on). Gates dev-only affordances such as the
    /// mock-data generator and injecting the real root key into the console.</summary>
    public bool IsDevInstance => _adminOptions.DevAutoSignIn;

    /// <summary>The instance's configured ingestion API key (the static root key), injected into the
    /// console so its requests authenticate without the operator pasting a key — the bundled fixture
    /// only carries a placeholder. <para>Gated to a <see cref="RSAAdminOptions.DevAutoSignIn"/> (dev)
    /// instance: the root key is unbound and highly privileged, so it is never rendered into a
    /// production admin page — there the console falls back to the fixture's <c>{{apiKey}}</c> and the
    /// operator supplies a key (e.g. a managed key minted on /Clients).</para> Null when not a dev
    /// instance or no key is configured.</summary>
    public string? DevApiKey =>
        IsDevInstance && !string.IsNullOrWhiteSpace(_reportOptions.ApiKey)
            ? _reportOptions.ApiKey
            : null;

    /// <summary>The bundled Postman v2.1 collection JSON, or <c>null</c> when none ships in this build.</summary>
    public string? CollectionJson { get; private set; }

    /// <summary>The bundled Postman environment JSON, or <c>null</c> when none ships.</summary>
    public string? EnvironmentJson { get; private set; }

    public bool HasCollection => CollectionJson is not null;

    // Global tenant scope from the top-left switcher (rsc_scope cookie → ?client/?app). Injected into
    // the console as pinned {{clientId}}/{{appId}} variables so requests default to the selected app
    // (null = "All clients" → the collection's own defaults stand). The console is a free-form runner,
    // so this sets the working scope rather than hard-enforcing it.
    [BindProperty(SupportsGet = true, Name = "client")] public string? Client { get; set; }
    [BindProperty(SupportsGet = true, Name = "app")] public string? App { get; set; }

    public string? ScopeClient => string.IsNullOrWhiteSpace(Client) ? null : Client.Trim().ToLowerInvariant();
    public string? ScopeApp => string.IsNullOrWhiteSpace(App) ? null : App.Trim().ToLowerInvariant();
    public bool HasScope => ScopeClient is not null || ScopeApp is not null;
    public string ScopeLabel => !HasScope
        ? "All clients"
        : ScopeApp is not null ? $"{ScopeClient ?? "?"} · {ScopeApp}" : $"{ScopeClient} (all apps)";

    public void OnGet()
    {
        var loaded = _fixtures.Load();
        CollectionJson = loaded?.CollectionJson;
        EnvironmentJson = loaded?.EnvironmentJson;
    }

    /// <summary>Download the bundled Postman v2.1 collection as a <c>.json</c> file so an operator can
    /// import it into desktop Postman / Hoppscotch. Serves the same raw fixture the console runs.</summary>
    public IActionResult OnGetDownloadCollection()
    {
        var json = _fixtures.Load()?.CollectionJson;
        if (string.IsNullOrEmpty(json)) return NotFound();
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "report-service.postman_collection.json");
    }

    /// <summary>Download the bundled Postman environment as a <c>.json</c> file. The environment's
    /// <c>apiKey</c> is the fixture placeholder (not this instance's real key — that is only injected
    /// into the in-page console), so the operator sets their own key after importing.</summary>
    public IActionResult OnGetDownloadEnvironment()
    {
        var json = _fixtures.Load()?.EnvironmentJson;
        if (string.IsNullOrEmpty(json)) return NotFound();
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "report-service.postman_environment.json");
    }

    // ─────────────────────────────  MOCK DATA GENERATOR  ─────────────────────────────

    /// <summary>Dev-only: create synthetic clients, apps, analytics events and problem reports so the
    /// dashboards have something to show for a demo. Runs server-side (not through the client-side
    /// console) because tenant creation + analytics/report writes go through the same services the
    /// dev seeders use. Not exposed on a production instance (404 when auto-sign-in is off).</summary>
    public async Task<IActionResult> OnPostGenerateMockAsync(
        int clients, int appsPerClient, int eventsPerApp, int reportsPerApp, CancellationToken ct)
    {
        if (!IsDevInstance) return NotFound();

        // Clamp to sane bounds so a stray value can't spawn thousands of tenants / DB files.
        clients = Math.Clamp(clients, 1, 20);
        appsPerClient = Math.Clamp(appsPerClient, 1, 10);
        eventsPerApp = Math.Clamp(eventsPerApp, 0, 500);
        reportsPerApp = Math.Clamp(reportsPerApp, 0, 50);

        var rng = new Random();
        var now = DateTimeOffset.UtcNow;
        int createdClients = 0, createdApps = 0, writtenEvents = 0, writtenReports = 0;

        try
        {
            for (int ci = 0; ci < clients; ci++)
            {
                // Unique-ish slug: a random hex suffix keeps re-runs from colliding on the catalog's
                // UNIQUE(slug). If a slug still collides, CreateClientAsync throws — skip that client.
                var clientSlug = $"mock-{rng.Next(0x100000, 0xFFFFFF):x6}";
                try
                {
                    await _catalog.CreateClientAsync(clientSlug, $"Mock Client {clientSlug[^6..]}", ct).ConfigureAwait(false);
                }
                catch (RSCCatalogException)
                {
                    continue; // slug clash / invalid — move on
                }
                createdClients++;

                for (int ai = 0; ai < appsPerClient; ai++)
                {
                    var appSlug = $"app-{ai + 1}";
                    try
                    {
                        await _catalog.CreateAppAsync(clientSlug, appSlug, $"Mock App {ai + 1}", ct).ConfigureAwait(false);
                    }
                    catch (RSCCatalogException)
                    {
                        continue;
                    }
                    createdApps++;

                    writtenEvents += await GenerateAnalyticsAsync(clientSlug, appSlug, eventsPerApp, now, rng, ct)
                        .ConfigureAwait(false);
                    writtenReports += await GenerateReportsAsync(clientSlug, appSlug, reportsPerApp, now, rng, ct)
                        .ConfigureAwait(false);
                }
            }

            await _audit.RecordAsync(HttpContext, "mock.generate", success: true,
                details: $"clients={createdClients} apps={createdApps} events={writtenEvents} reports={writtenReports}")
                .ConfigureAwait(false);

            TempData["Flash"] =
                $"Generated {createdClients} clients, {createdApps} apps, {writtenEvents} analytics events, {writtenReports} reports. " +
                "Give the aggregation workers a few seconds, then explore the dashboards.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mock data generation failed");
            await _audit.RecordAsync(HttpContext, "mock.generate", success: false, details: ex.Message).ConfigureAwait(false);
            TempData["Flash"] =
                $"Mock data generation failed after {createdClients} clients / {createdApps} apps: {ex.Message}";
        }

        return RedirectToPage();
    }

    // ── analytics ────────────────────────────────────────────────────────────

    // A small, funnel-aware taxonomy — the event names/types here are the same the SDKs emit and the
    // dev analytics seeder uses, so they pass RSCAnalyticsValidator and (via otc_purchase /
    // cardlink_activation step names) populate the seeded funnels. Property keys are all PII-clean
    // (no email/phone/token/…), matching the validator's forbidden-key denylist.
    private static readonly string[] MockPlatforms = ["android", "ios"];

    private static readonly (string Pzn, string Name, string Cat, decimal Price)[] MockProducts =
    [
        ("pzn-00001", "Ibuprofen 400mg",   "pain_relief",   4.49m),
        ("pzn-00002", "Aspirin 100mg",     "pain_relief",   2.99m),
        ("pzn-00005", "Vitamin C 1000mg",  "supplements",   6.49m),
        ("pzn-00007", "Magnesium 400mg",   "supplements",   7.99m),
    ];

    private static readonly string[] MockInsurers = ["barmer", "tk", "aok", "dak"];

    /// <summary>Generate up to <paramref name="targetEvents"/> analytics events for one (client, app),
    /// spread across ~3-6 sessions and a handful of distinct anonymous ids, backdated over the last
    /// ~7 days. Batches are validated, hashed and written exactly like the dev seeder does. Returns
    /// the number of events actually written.</summary>
    private async Task<int> GenerateAnalyticsAsync(
        string clientSlug, string appSlug, int targetEvents, DateTimeOffset now, Random rng, CancellationToken ct)
    {
        if (targetEvents <= 0) return 0;

        // A handful of distinct installs (anonymousIds) so distinct-user / DAU tiles aren't all 1.
        int userCount = Math.Clamp(targetEvents / 5, 2, 8);
        int sessionCount = Math.Clamp(targetEvents / 6, 3, 6);

        int written = 0, sessionIdx = 0;
        while (written < targetEvents && sessionIdx < sessionCount * 4) // guard against infinite loop
        {
            var platform = MockPlatforms[sessionIdx % MockPlatforms.Length];
            var sdkVer = platform == "android" ? "2.0.0" : "2.0.0-ios";
            var appVer = platform == "android" ? "4.5.0" : "4.5.0-ios";
            var userNo = sessionIdx % userCount;
            var anonId = $"mock-{clientSlug}-{appSlug}-{platform[..3]}-u{userNo:D2}";

            // Backdate the session over the last 7 days, at a random hour/minute.
            var daysBack = rng.Next(0, 7);
            var start = now.AddDays(-daysBack)
                           .AddHours(-(now.Hour) + rng.Next(7, 21))
                           .AddMinutes(rng.Next(0, 60));

            var sessionId = $"ms-{clientSlug}-{appSlug}-{platform[..3]}-{sessionIdx:D3}";
            var events = BuildMockSession(sessionId, start, rng);

            // Trim so we don't overshoot the requested total (keep the first N events — a session
            // prefix is still valid: required fields per-event are independent of ordering).
            if (written + events.Count > targetEvents)
                events = events.Take(Math.Max(1, targetEvents - written)).ToList();

            // Split to respect the validator's MaxEventsPerBatch (default 250). Sessions here are far
            // smaller, but the split keeps us correct if a caller widens the session builder later.
            foreach (var chunk in Chunk(events, 200))
            {
                var batch = new RSCAnalyticsBatch(
                    SchemaVersion: 1,
                    BatchId: $"mb-{sessionId}-{rng.Next(0, 999999):D6}",
                    Platform: platform,
                    SdkVersion: sdkVer,
                    HostAppVersion: appVer,
                    AnonymousId: anonId,
                    ClientId: clientSlug,
                    GeneratedAt: start.ToString("O"),
                    Events: chunk,
                    AppId: appSlug);

                var receivedAt = start.AddMinutes(chunk.Count + 1);
                var verdict = _validator.Validate(batch, receivedAt, allowServerPlatforms: false);
                var anonHash = _hasher.Hash(anonId);
                await _analyticsStore.WriteBatchAsync(batch, anonHash, clientIdHash: null, verdict, receivedAt, ct)
                    .ConfigureAwait(false);
                written += chunk.Count;
            }

            sessionIdx++;
        }

        return written;
    }

    // Builds one realistic session: lifecycle bookends + one of a few funnel-aware flows. Uses the
    // exact event names the seeded funnels (otc_purchase / cardlink_activation) match, so funnels
    // populate. Deterministic-enough via the passed RNG; ids are unique per (session, sequence).
    private static List<RSCAnalyticsEvent> BuildMockSession(string sessionId, DateTimeOffset start, Random rng)
    {
        var events = new List<RSCAnalyticsEvent>();
        var ts = start;
        int seq = 0;

        void E(string type, string name, string? screen = null, string? feature = null,
            long? durationMs = null, Dictionary<string, string>? props = null, List<RSCAnalyticsItem>? items = null)
        {
            events.Add(new RSCAnalyticsEvent(
                EventId: $"{sessionId}-{seq:D3}",
                SessionId: sessionId,
                Sequence: seq,
                OccurredAt: ts.ToString("O"),
                Type: type, Name: name,
                Screen: screen, Feature: feature,
                DurationMs: durationMs,
                Properties: props,
                Items: items?.AsReadOnly()));
            seq++;
            ts = ts.AddSeconds(5 + rng.Next(0, 20));
        }

        E("lifecycle", "session_start", props: new() { ["cold_start"] = "true", ["host_app_state"] = "foreground" });
        E("screen", "screen_view", screen: "home", durationMs: 1200);
        E("engagement", "scroll", screen: "home", props: new() { ["depth_pct"] = $"{rng.Next(15, 95)}" });

        switch (rng.Next(0, 3))
        {
            case 0: // OTC purchase — drives the otc_purchase funnel end to end.
            {
                var p = MockProducts[rng.Next(0, MockProducts.Length)];
                var qty = rng.Next(1, 3);
                var resultCount = rng.Next(6, 27);
                var total = p.Price * qty;
                E("screen", "screen_view", screen: "otc", feature: "otc", durationMs: 700);
                E("action", "otc_search_submitted", screen: "otc_search", feature: "otc",
                    props: new() { ["query_length"] = "8", ["result_count"] = $"{resultCount}" });
                E("screen", "otc_result_view", screen: "otc_search_results", feature: "otc",
                    props: new() { ["result_count"] = $"{resultCount}" }, durationMs: 1800);
                E("screen", "otc_product_view", screen: "otc_product_detail", feature: "otc",
                    props: new() { ["item_id"] = p.Pzn, ["category"] = p.Cat }, durationMs: 3200);
                E("ecommerce", "add_to_cart", feature: "otc",
                    props: new() { ["item_id"] = p.Pzn },
                    items: [new(p.Pzn, p.Name, p.Cat, p.Price, qty, "EUR")]);
                E("screen", "cart_view", screen: "cart", feature: "otc",
                    props: new() { ["items_count"] = "1", ["total"] = total.ToString("F2", CultureInfo.InvariantCulture) }, durationMs: 1100);
                E("action", "checkout_step", screen: "checkout", feature: "otc",
                    props: new() { ["step_name"] = "payment", ["step_index"] = "1" });
                E("ecommerce", "purchase", feature: "otc",
                    props: new()
                    {
                        ["order_id"] = $"ord-{sessionId[^6..]}",
                        ["items_count"] = "1",
                        ["total"] = total.ToString("F2", CultureInfo.InvariantCulture),
                        ["currency"] = "EUR",
                        ["shipping_method"] = rng.Next(0, 2) == 0 ? "standard" : "express",
                        ["payment_method"] = rng.Next(0, 2) == 0 ? "paypal" : "sepa_debit",
                    },
                    items: [new(p.Pzn, p.Name, p.Cat, p.Price, qty, "EUR")]);
                break;
            }
            case 1: // CardLink activation — drives the cardlink_activation funnel.
            {
                var insurer = MockInsurers[rng.Next(0, MockInsurers.Length)];
                E("screen", "screen_view", screen: "cardlink_intro", feature: "cardlink", durationMs: 1600);
                E("action", "cardlink_start", feature: "cardlink", props: new() { ["entry_point"] = "home_banner" });
                E("screen", "cardlink_consent_shown", screen: "cardlink_consent", feature: "cardlink", durationMs: 4800);
                E("action", "cardlink_consent_given", feature: "cardlink");
                E("action", "cardlink_auth_started", feature: "cardlink", props: new() { ["auth_method"] = "scan" });
                E("action", "cardlink_scan_step", feature: "cardlink", props: new() { ["step_name"] = "scanning" });
                if (rng.Next(0, 5) == 0) // occasional failure so the Errors page has content
                {
                    E("error", "cardlink_failure", feature: "cardlink", props: new() { ["failure_reason"] = "nfc_error" });
                    E("error", "error_shown", props: new() { ["error_class"] = "CardLinkAuthFailure", ["retry_offered"] = "true", ["status_code"] = "0" });
                }
                else
                {
                    E("action", "cardlink_success", feature: "cardlink", props: new() { ["insurer"] = insurer });
                }
                break;
            }
            default: // Pharmacy browse.
            {
                E("screen", "screen_view", screen: "pharmacy_search", feature: "pharmacy", durationMs: 900);
                E("action", "pharmacy_search_submitted", screen: "pharmacy_search", feature: "pharmacy",
                    props: new() { ["query_length"] = "8", ["result_count"] = $"{4 + rng.Next(0, 22)}", ["filter_open"] = "false" });
                E("screen", "screen_view", screen: "pharmacy_list", feature: "pharmacy", durationMs: 2400);
                E("action", "pharmacy_selected", feature: "pharmacy",
                    props: new() { ["pharmacy_id_hash"] = $"ph-{rng.Next(0, 999):D3}", ["list_position"] = $"{rng.Next(0, 6)}" });
                E("screen", "screen_view", screen: "pharmacy_detail", feature: "pharmacy", durationMs: 3200);
                break;
            }
        }

        E("screen", "screen_exit", screen: "home", durationMs: 600);
        var elapsedMs = (long)(ts - start).TotalMilliseconds;
        E("lifecycle", "session_end", durationMs: elapsedMs,
            props: new() { ["exit_reason"] = "background", ["duration_ms"] = elapsedMs.ToString(CultureInfo.InvariantCulture) });

        return events;
    }

    private static IEnumerable<List<RSCAnalyticsEvent>> Chunk(List<RSCAnalyticsEvent> events, int size)
    {
        for (int i = 0; i < events.Count; i += size)
            yield return events.GetRange(i, Math.Min(size, events.Count - i));
    }

    // ── problem reports ────────────────────────────────────────────────────────

    private static readonly string[] MockDevices =
        ["Pixel 8 Pro", "Samsung Galaxy S23", "iPhone 15 Pro", "iPhone 14", "OnePlus 11"];

    private static readonly (string Title, string TopFrame, string Message)[] MockCrashes =
    [
        ("java.lang.NullPointerException", "com.ia.cardlink.cart.CartViewModel.onScanResult(CartViewModel.kt:142)", "Attempt to invoke virtual method on a null object reference while applying a scan result."),
        ("EXC_BAD_ACCESS", "IACardlink.BiometricAuth.authenticate(BiometricAuth.swift:210)", "SIGSEGV dereferencing a released LAContext during biometric authentication."),
        ("kotlin.KotlinNullPointerException", "com.ia.checkout.CheckoutRepository.placeOrder(CheckoutRepository.kt:57)", "Null receiver while placing an order during checkout."),
    ];

    private static readonly (string Title, string TopFrame, string Message)[] MockErrors =
    [
        ("java.io.IOException", "com.ia.net.ReportRemoteDataSource.flush(ReportRemoteDataSource.kt:131)", "Timed out flushing the analytics outbox; upload retry scheduled."),
        ("URLError.timedOut", "IANet.ReportRemoteDataSource.flush(ReportRemoteDataSource.swift:118)", "Request timed out flushing the analytics outbox; upload retry scheduled."),
    ];

    private static readonly (string Title, string Message)[] MockUserReports =
    [
        ("Cart empties after scan", "After scanning a prescription QR the cart shows 0 items even though the medication was added."),
        ("Pharmacy search slow", "Searching for a pharmacy by postal code takes 10+ seconds and sometimes shows no results."),
        ("CardLink stuck on consent", "The CardLink consent screen never advances after I tap 'Accept'."),
    ];

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Generate <paramref name="target"/> problem reports for one (client, app), mixing kinds
    /// (crash / error / user report), backdated over the last ~7 days, written via the fan-out store's
    /// backdated <c>SaveAsync</c> overload so the historical spread survives. Returns the count written.
    /// A no-op when the store has no SQLite index (FileSystem test host) is fine — SaveAsync still
    /// persists the JSON, but the /ProblemReports listing needs the index to surface it.</summary>
    private async Task<int> GenerateReportsAsync(
        string clientSlug, string appSlug, int target, DateTimeOffset now, Random rng, CancellationToken ct)
    {
        if (target <= 0) return 0;

        int written = 0;
        for (int i = 0; i < target; i++)
        {
            var platform = MockPlatforms[i % MockPlatforms.Length];
            var device = MockDevices[i % MockDevices.Length];
            var submittedAt = now.AddDays(-rng.Next(0, 7)).AddHours(-rng.Next(0, 12)).AddMinutes(-rng.Next(0, 60));
            var appVersion = $"4.{10 + i % 4}.{i % 6} (SDK 2.3.{20 + i % 12})";

            // Roughly: half faults (2:1 crash:error), half user reports — so the Errors page and the
            // problem-report listing both have content.
            string? title, message, stackTrace, kind;
            var bucket = i % 3;
            if (bucket == 0)
            {
                var (t, frame, m) = MockCrashes[i % MockCrashes.Length];
                title = t; message = m; kind = "crash";
                stackTrace = $"{t}: see message\n\tat {frame}\n\tat com.ia.sdk.IaSdk.run(IaSdk.kt:64)";
            }
            else if (bucket == 1)
            {
                var (t, frame, m) = MockErrors[i % MockErrors.Length];
                title = t; message = m; kind = "error";
                stackTrace = $"{t}: see message\n\tat {frame}";
            }
            else
            {
                var (t, m) = MockUserReports[i % MockUserReports.Length];
                title = t; message = m; kind = null; stackTrace = null;
            }

            var report = new RSCProblemReport(
                Platform: platform,
                Message: message,
                Title: title,
                DeviceModel: device,
                Email: null,
                PhoneNumber: null,
                Phone: null,
                PharmacyId: $"{2000 + i}",
                Source: "SDK",
                AppVersion: appVersion,
                FunctionalityImportance: null,
                Labels: new List<string> { "mock", kind ?? "user" },
                Kind: kind,
                StackTrace: stackTrace,
                EventProperties: null,
                OccurredAt: submittedAt.ToString("O", CultureInfo.InvariantCulture),
                UserId: $"mock-{platform}-user-{i}",
                AppId: appSlug,
                ClientId: clientSlug);

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(report, ReportJsonOptions);
            await _reportStore.SaveAsync(report, jsonBytes, attachment: null, attachmentLength: null,
                ingestionChannel: RSCIngestionChannels.Json, submittedAt, ct).ConfigureAwait(false);
            written++;
        }

        return written;
    }
}
