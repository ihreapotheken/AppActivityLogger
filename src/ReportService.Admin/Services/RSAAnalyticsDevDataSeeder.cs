using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Services;

/// <summary>
/// Seeds realistic analytics data based on what the Android and iOS SDKs actually emit.
/// Idempotent — all IDs are deterministic, so re-runs produce zero duplicate rows.
///
/// Magnitudes and timings are jittered through a <see cref="StableRng"/> seeded from a stable hash
/// of the session id (NOT <see cref="object.GetHashCode"/>, which is salted per-process). The jitter
/// is therefore deterministic: the same (platform, label, idx, day) always yields the same values,
/// event count and ids, so re-runs INSERT OR IGNORE over identical rows and the dataset stays
/// idempotent — it just loses the tell-tale arithmetic regularity of a pure `seed % N` seeder.
/// </summary>
internal static class RSAAnalyticsDevDataSeeder
{
    private static readonly string[] Platforms = ["android", "ios"];

    private static readonly (string Pzn, string Name, string Cat, decimal Price)[] Products =
    [
        ("pzn-00001", "Ibuprofen 400mg",    "pain_relief",      4.49m),
        ("pzn-00002", "Aspirin 100mg",      "pain_relief",      2.99m),
        ("pzn-00003", "Paracetamol 500mg",  "pain_relief",      3.29m),
        ("pzn-00004", "Nasenspray 0.1%",    "decongestant",     5.89m),
        ("pzn-00005", "Vitamin C 1000mg",   "supplements",      6.49m),
        ("pzn-00006", "Loratadin 10mg",     "antihistamine",    4.79m),
        ("pzn-00007", "Magnesium 400mg",    "supplements",      7.99m),
        ("pzn-00008", "Omeprazol 20mg",     "gastrointestinal", 3.69m),
        ("pzn-00009", "Zinksalbe 30g",      "dermatology",      8.49m),
        ("pzn-00010", "Kamillentee 20 Btl", "phytotherapy",     2.49m),
    ];

    private static readonly string[] OtcQueries =
        ["ibuprofen", "aspirin", "husten", "erkältung", "vitamin c", "magnesium",
         "nasenspray", "loratadin", "zinksalbe", "paracetamol", "schlaftabletten", "augenspray"];

    private static readonly string[] PharmacyHashes =
        ["ph-a1b2c3", "ph-d4e5f6", "ph-g7h8i9", "ph-j0k1l2", "ph-m3n4o5",
         "ph-p6q7r8", "ph-s9t0u1", "ph-v2w3x4"];

    private static readonly string[] Insurers    = ["barmer", "tk", "aok", "dak", "kkh", "hkk", "bkk", "ikk"];
    private static readonly string[] ApptHashes  = ["appt-aa11", "appt-bb22", "appt-cc33", "appt-dd44", "appt-ee55"];
    private static readonly string[] OpenReasons = ["push", "direct", "widget", "deep_link", "notification"];
    private static readonly string[] FormIds     = ["rx_upload_form", "address_form", "contact_form"];

    // Payment method stamped on seeded purchase events. Grounded in the SDKs' OrderPayment funding
    // sources (paypal, card) plus the rails a German pharmacy checkout offers (SEPA, invoice); the
    // mobile wallet correlates with the OS so the breakdown differs by platform. The contract lists
    // payment_method under the `purchase` event (docs/analytics-contract/event-catalog.json).
    private static string PickPaymentMethod(string platform, int s) =>
        (s % 5) switch
        {
            0 => "paypal",
            1 => "credit_card",
            2 => "sepa_debit",
            3 => "invoice",
            _ => platform == "ios" ? "apple_pay" : "google_pay",
        };

    // ── Cohort generation ──────────────────────────────────────────────────
    // returnOffsets: days after install the user returns (0 = install day itself)
    private record CohortSpec(string Label, int Idx, int InstallDaysBack, int[] ReturnOffsets, int SessionsPerDay);

    // Baseline cohort populations (scale = 1). The actual seeded population is these counts
    // multiplied by the seed scale (see ResolveScale) so the same realistic mix of flows,
    // funnels, and retention curves can be inflated into a large dataset on demand.
    private const int PowerUsers   = 20;
    private const int RegularUsers = 60;
    private const int CasualUsers  = 100;
    private const int ChurnedUsers = 80;
    private const int NewUsers     = 40;

    private static IReadOnlyList<CohortSpec> BuildCohorts(int scale)
    {
        var specs = new List<CohortSpec>();

        // Power users — installed 30-110 days ago, active for many days
        for (int i = 0; i < PowerUsers * scale; i++)
        {
            int install = Math.Min(30 + (i % PowerUsers) * 4, 110);
            // Daily for first 21 days, then every 5 days until today
            var active = Enumerable.Range(0, Math.Min(21, install + 1))
                .Concat(Enumerable.Range(0, (install - 20) / 5 + 1).Select(n => 21 + n * 5))
                .Where(d => d <= install)
                .Distinct()
                .ToArray();
            specs.Add(new("pw", i, install, active, 2));
        }

        // Regular users — D0 + D1 + D7 + D14 returns
        for (int i = 0; i < RegularUsers * scale; i++)
        {
            int install = Math.Min(8 + (i % RegularUsers) * 2, 110);
            var active  = new[] { 0, 1, 7, 14 }.Where(d => d <= install).ToArray();
            specs.Add(new("rg", i, install, active, 1));
        }

        // Casual users — D0 + D1 only
        for (int i = 0; i < CasualUsers * scale; i++)
        {
            int install = Math.Min(2 + (i % CasualUsers), 110);
            var active  = new[] { 0, 1 }.Where(d => d <= install).ToArray();
            specs.Add(new("cs", i, install, active, 1));
        }

        // Churned users — install day only (no return)
        for (int i = 0; i < ChurnedUsers * scale; i++)
        {
            int install = Math.Min(1 + (i % ChurnedUsers), 110);
            specs.Add(new("ch", i, install, [0], 1));
        }

        // New users — installed in last 21 days, active every day since
        for (int i = 0; i < NewUsers * scale; i++)
        {
            int install = (i % NewUsers) % 22; // 0..21 days ago
            var active  = Enumerable.Range(0, install + 1).ToArray();
            specs.Add(new("nw", i, install, active, 1));
        }

        return specs;
    }

    // Population multiplier. Defaults to 1 (the baseline mix) so the test host and a plain
    // Development run stay fast; the Docker dev instance opts into a large dataset by setting
    // ANALYTICS_SEED_SCALE in its .env. The seeder is deterministic per (platform, label, idx),
    // so a given scale always produces the same rows — re-runs at the same scale insert zero
    // duplicates.
    // Off by default: synthetic seeding is OPT-IN. With no ANALYTICS_SEED_SCALE (or 0) the seeder
    // writes nothing, so a fresh dev instance shows only real traffic. Set ANALYTICS_SEED_SCALE>0
    // (e.g. in .env) to populate the synthetic dataset.
    private const int DefaultSeedScale = 0;

    private static int ResolveScale(ILogger logger)
    {
        var raw = Environment.GetEnvironmentVariable("ANALYTICS_SEED_SCALE");
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultSeedScale;

        if (int.TryParse(raw, out var parsed) && parsed >= 0)
            return parsed;

        logger.LogWarning(
            "Ignoring invalid ANALYTICS_SEED_SCALE='{Raw}'; falling back to {Default}",
            raw, DefaultSeedScale);
        return DefaultSeedScale;
    }

    // Deterministic, cross-run-stable RNG keyed off an opaque string (a session id). string.GetHashCode
    // is salted per-process and would re-jitter the same session differently on every restart, breaking
    // the idempotency contract; FNV-1a is stable, so the same key always seeds the same sequence.
    private static Random StableRng(string seedKey)
    {
        uint hash = 2166136261;
        foreach (var ch in seedKey)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return new Random(unchecked((int)hash));
    }

    // ── Entry point ────────────────────────────────────────────────────────
    public static async Task SeedAsync(IServiceProvider sp, ILogger logger, CancellationToken ct)
    {
        var store     = sp.GetRequiredService<RSCIAnalyticsStore>();
        var validator = sp.GetRequiredService<RSCAnalyticsValidator>();
        var hasher    = sp.GetRequiredService<RSCAnalyticsIdentifierHasher>();

        // Database-per-app: distribute the synthetic cohorts across the catalog's registered apps so
        // each client's dashboard — and each client login — sees its own per-app analytics. The store
        // routes each batch to apps/{client}/{app}/analytics.db by the batch's ClientId/AppId; the
        // admin "all clients" view fans out and sums them. Falls back to the default app if empty.
        var catalog = sp.GetService<RSCICatalog>();
        var appList = catalog is null
            ? new List<(string Client, string App)>()
            : (await catalog.ListAllAppsAsync(includeArchived: false, ct).ConfigureAwait(false))
                .Select(a => (Client: a.ClientSlug, App: a.Slug)).ToList();
        if (appList.Count == 0) appList.Add((Client: "default", App: "default"));

        var now    = DateTimeOffset.UtcNow;
        var scale   = ResolveScale(logger);
        if (scale <= 0)
        {
            logger.LogInformation(
                "Dev analytics seeder disabled (ANALYTICS_SEED_SCALE unset or 0); set ANALYTICS_SEED_SCALE>0 to enable synthetic data.");
            return;
        }
        var cohorts = BuildCohorts(scale);
        int batches = 0, events = 0;

        logger.LogInformation(
            "Dev analytics seeder starting at scale {Scale}: {Users} users across {Platforms} platforms",
            scale, cohorts.Count * Platforms.Length, Platforms.Length);

        foreach (var platform in Platforms)
        {
            var sdkVer = platform == "android" ? "2.0.0" : "2.0.0-ios";
            var appVer = platform == "android" ? "4.5.0" : "4.5.0-ios";

            foreach (var spec in cohorts)
            {
                // Each cohort belongs to one catalog app (round-robin by cohort index), partitioning
                // the synthetic users across apps. Fold the app slug into the anon id so ids stay
                // unique per app.
                var (clientSlug, appSlug) = appList[spec.Idx % appList.Count];
                var anonId = $"seed-{platform[..3]}-{appSlug}-{spec.Label}-{spec.Idx:D3}";

                foreach (var offset in spec.ReturnOffsets)
                {
                    int daysBack = spec.InstallDaysBack - offset;
                    if (daysBack < 0) continue;

                    var dayStart  = now.AddDays(-daysBack);
                    // Tag IDs with the ABSOLUTE day (not days-back) so the seed is anchored to real
                    // dates. A same-day restart re-derives identical IDs (INSERT OR IGNORE = no
                    // duplicates), but a restart on a later day produces fresh IDs for the new days,
                    // topping the dataset up to "today". With a days-back tag the dates froze at the
                    // first seed, so DAU/today metrics went to 0 as wall-clock time advanced.
                    var dayTag    = dayStart.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
                    var isCold    = offset == 0;

                    for (int sIdx = 0; sIdx < spec.SessionsPerDay; sIdx++)
                    {
                        var sessionId = $"s-{platform[..3]}-{spec.Label}-{spec.Idx:D3}-{dayTag}-{sIdx}";
                        var batchId   = $"b-{platform[..3]}-{spec.Label}-{spec.Idx:D3}-{dayTag}-{sIdx}";
                        var flowKey   = (spec.Idx * 17 + daysBack * 11 + sIdx * 7) % FlowCount;

                        // One deterministic RNG per session drives every jittered value below.
                        var rng = StableRng(sessionId);

                        // Anchor sessions to morning / afternoon / evening, then jitter ±1.5h with a
                        // random minute so they don't all land on the same clock tick every day.
                        double baseHour = sIdx switch { 0 => 7.5, 1 => 14.0, _ => 20.0 };
                        var start = dayStart.AddHours(baseHour + (rng.NextDouble() * 3.0 - 1.5))
                                            .AddMinutes(rng.Next(0, 60));

                        var ctx = BuildSession(sessionId, start, platform, flowKey,
                                               isCold && sIdx == 0, spec.Idx, rng);

                        var batch = new RSCAnalyticsBatch(
                            SchemaVersion:  1,
                            BatchId:        batchId,
                            Platform:       platform,
                            SdkVersion:     sdkVer,
                            HostAppVersion: appVer,
                            AnonymousId:    anonId,
                            ClientId:       clientSlug,
                            GeneratedAt:    start.ToString("O"),
                            Events:         ctx.Events,
                            AppId:          appSlug);

                        // Receive the batch a couple of minutes after the session actually ends. Jittered
                        // gaps mean sessions no longer have a fixed length, so anchor to the real end
                        // rather than the old fixed start+12m (which could land mid-session now).
                        var receivedAt = ctx.End.AddMinutes(1 + rng.Next(0, 4));
                        var verdict    = validator.Validate(batch, receivedAt);
                        var anonHash   = hasher.Hash(anonId);

                        await store.WriteBatchAsync(batch, anonHash, null, verdict, receivedAt, ct)
                                   .ConfigureAwait(false);
                        batches++;
                        events += ctx.Events.Count;
                    }
                }
            }
        }

        await SeedSdkDiagnosticsAsync(store, validator, hasher, now, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Dev analytics seeder: {Batches} batches / {Events} events, {Users} users",
            batches, events, cohorts.Count * Platforms.Length);
    }

    private static async Task SeedSdkDiagnosticsAsync(
        RSCIAnalyticsStore store, RSCAnalyticsValidator validator,
        RSCAnalyticsIdentifierHasher hasher, DateTimeOffset now, CancellationToken ct)
    {
        // Each (platform, daysBack, error) combination is its own batch so that receivedAt can be
        // set close to the event's occurredAt — the validator rejects any event where
        // |occurredAt − receivedAt| > MaxClockSkewSeconds (24 h).
        var episodes = new (int DaysBack, string Name, Dictionary<string, string> Props)[]
        {
            (7, "sdk_batch_http_error",    new() { ["status_code"] = "429", ["events_count"] = "32", ["retryable"] = "true"  }),
            (6, "sdk_batch_http_error",    new() { ["status_code"] = "503", ["events_count"] = "18", ["retryable"] = "true"  }),
            (5, "sdk_batch_http_error",    new() { ["status_code"] = "400", ["events_count"] = "12", ["retryable"] = "false" }),
            (4, "sdk_batch_network_error", new() { ["error_class"] = "IOException",            ["events_count"] = "28"       }),
            (3, "sdk_batch_network_error", new() { ["error_class"] = "SocketTimeoutException", ["events_count"] = "15"       }),
            (2, "sdk_api_http_error",      new() { ["status_code"] = "401", ["error_class"] = "HttpError"                   }),
            (2, "sdk_api_http_error",      new() { ["status_code"] = "500", ["error_class"] = "HttpError"                   }),
            (1, "sdk_outbox_parse_error",  new() { ["lines_skipped"] = "3"                                                  }),
            (0, "sdk_batch_http_error",    new() { ["status_code"] = "429", ["events_count"] = "40", ["retryable"] = "true"  }),
        };

        foreach (var platform in Platforms)
        {
            var sdkVer = platform == "android" ? "2.0.0" : "2.0.0-ios";
            var appVer = platform == "android" ? "4.5.0" : "4.5.0-ios";
            var anonId = $"seed-{platform[..3]}-sdk-diag";
            var diagSessionId = $"sdk-diag-{platform[..3]}-seed";

            for (int ep = 0; ep < episodes.Length; ep++)
            {
                var (daysBack, name, props) = episodes[ep];
                var occurredAt = now.AddDays(-daysBack).AddHours(9).AddMinutes(ep * 7);
                var receivedAt = occurredAt.AddMinutes(2);

                var ev = new RSCAnalyticsEvent(
                    EventId:    $"sdk-diag-{platform[..3]}-ep{ep:D2}",
                    SessionId:  diagSessionId,
                    Sequence:   ep,
                    OccurredAt: occurredAt.ToString("O"),
                    Type: "error", Name: name,
                    Screen: null, Feature: "sdk", DurationMs: null,
                    Properties: props, Items: null);

                var batch = new RSCAnalyticsBatch(
                    SchemaVersion:  1,
                    BatchId:        $"b-sdk-diag-{platform[..3]}-ep{ep:D2}",
                    Platform:       platform,
                    SdkVersion:     sdkVer,
                    HostAppVersion: appVer,
                    AnonymousId:    anonId,
                    ClientId:       null,
                    GeneratedAt:    occurredAt.ToString("O"),
                    Events:         [ev]);

                var verdict  = validator.Validate(batch, receivedAt);
                var anonHash = hasher.Hash(anonId);
                await store.WriteBatchAsync(batch, anonHash, null, verdict, receivedAt, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Session builder ────────────────────────────────────────────────────
    private const int FlowCount = 18;

    private static Ctx BuildSession(
        string sessionId, DateTimeOffset start, string platform, int flowKey, bool coldStart, int seed, Random rng)
    {
        var ctx = new Ctx(sessionId, start, platform, rng);

        if (coldStart)
            ctx.E("lifecycle", "sdk_initialized", props: new()
            {
                ["sdk_version"]      = platform == "android" ? "2.0.0" : "2.0.0-ios",
                ["host_app_version"] = platform == "android" ? "4.5.0" : "4.5.0-ios",
                ["cold_start"]       = "true",
            });

        ctx.E("lifecycle", "session_start", props: new()
        {
            ["cold_start"]     = coldStart ? "true" : "false",
            ["host_app_state"] = "foreground",
        });
        ctx.E("screen", "screen_view", screen: "home", durationMs: 1200);
        ctx.E("engagement", "scroll",  screen: "home",
            props: new() { ["depth_pct"] = $"{ctx.RandInt(15, 95)}" });

        switch (flowKey)
        {
            case 0:  FlowOtcComplete(ctx, seed, multiItem: false);    break;
            case 1:  FlowOtcComplete(ctx, seed + 1, multiItem: true); break;
            case 2:  FlowOtcAbandonCart(ctx, seed);                   break;
            case 3:  FlowOtcAbandonSearch(ctx, seed);                 break;
            case 4:  FlowOtcReorder(ctx, seed);                       break;
            case 5:  FlowCardLinkComplete(ctx, seed, authMethod: "scan");  break;
            case 6:  FlowCardLinkComplete(ctx, seed, authMethod: "entry"); break;
            case 7:  FlowCardLinkCancelled(ctx, seed);                break;
            case 8:  FlowCardLinkFailed(ctx, seed);                   break;
            case 9:  FlowPrescriptionScan(ctx, seed);                 break;
            case 10: FlowPrescriptionUpload(ctx, seed);               break;
            case 11: FlowPrescriptionScanFailure(ctx, seed);          break;
            case 12: FlowPharmacySearchAndSelect(ctx, seed);          break;
            case 13: FlowPharmacyEmergency(ctx, seed);                break;
            case 14: FlowAppointmentSuccess(ctx, seed);               break;
            case 15: FlowAppointmentFailure(ctx, seed);               break;
            case 16: FlowFormWithError(ctx, seed);                    break;
            default: FlowBrowseOnly(ctx, seed);                       break;
        }

        ctx.E("screen", "screen_exit", screen: "home", durationMs: 600);
        ctx.E("lifecycle", "session_end", durationMs: ctx.ElapsedMs,
            props: new() { ["exit_reason"] = "background", ["duration_ms"] = ctx.ElapsedMs.ToString() });

        return ctx;
    }

    // ── Flows ──────────────────────────────────────────────────────────────

    private static void FlowOtcComplete(Ctx c, int s, bool multiItem)
    {
        var p1  = Products[s % Products.Length];
        var p2  = Products[(s + 3) % Products.Length];
        var q   = OtcQueries[s % OtcQueries.Length];
        var qty = c.RandInt(1, 3);
        var resultCount = c.RandInt(6, 27);

        c.E("screen", "screen_view",         screen: "otc",                feature: "otc", durationMs: 700);
        c.E("action", "otc_search_submitted", screen: "otc_search",         feature: "otc",
            props: new() { ["query_length"] = q.Length.ToString(), ["result_count"] = $"{resultCount}" });
        c.E("screen", "otc_result_view",      screen: "otc_search_results", feature: "otc",
            props: new() { ["result_count"] = $"{resultCount}" }, durationMs: 1800);
        c.E("action", "otc_product_select",   screen: "otc_search_results", feature: "otc",
            props: new() { ["item_id"] = p1.Pzn, ["list_position"] = $"{s % 8}" });
        c.E("screen", "otc_product_view",     screen: "otc_product_detail", feature: "otc",
            props: new() { ["item_id"] = p1.Pzn, ["category"] = p1.Cat }, durationMs: 3800 + s % 3000);
        c.E("ecommerce", "add_to_cart", feature: "otc",
            props: new() { ["item_id"] = p1.Pzn },
            items: [new(p1.Pzn, p1.Name, p1.Cat, p1.Price, qty, "EUR")]);

        if (multiItem)
        {
            c.E("screen", "otc_product_view", screen: "otc_product_detail", feature: "otc",
                props: new() { ["item_id"] = p2.Pzn, ["category"] = p2.Cat }, durationMs: 2200);
            c.E("ecommerce", "add_to_cart", feature: "otc",
                props: new() { ["item_id"] = p2.Pzn },
                items: [new(p2.Pzn, p2.Name, p2.Cat, p2.Price, 1, "EUR")]);
        }

        var total = multiItem ? p1.Price * qty + p2.Price : p1.Price * qty;
        c.E("screen", "cart_view", screen: "cart", feature: "otc",
            props: new() { ["items_count"] = multiItem ? "2" : "1", ["total"] = total.ToString("F2") },
            durationMs: 1100);
        c.E("action", "checkout_step", screen: "checkout", feature: "otc",
            props: new() { ["step_name"] = "address", ["step_index"] = "0" });
        c.E("action", "checkout_step", screen: "checkout", feature: "otc",
            props: new() { ["step_name"] = "payment", ["step_index"] = "1" });
        c.E("ecommerce", "purchase", feature: "otc",
            props: new()
            {
                ["order_id"]        = $"ord-{c.Id[^6..]}",
                ["items_count"]     = multiItem ? "2" : "1",
                ["total"]           = total.ToString("F2"),
                ["currency"]        = "EUR",
                ["shipping_method"] = s % 2 == 0 ? "standard" : "express",
                ["payment_method"]  = PickPaymentMethod(c.Platform, s),
            },
            items: multiItem
                ? [new(p1.Pzn, p1.Name, p1.Cat, p1.Price, qty, "EUR"), new(p2.Pzn, p2.Name, p2.Cat, p2.Price, 1, "EUR")]
                : [new(p1.Pzn, p1.Name, p1.Cat, p1.Price, qty, "EUR")]);
    }

    private static void FlowOtcAbandonCart(Ctx c, int s)
    {
        var p = Products[(s + 2) % Products.Length];
        var q = OtcQueries[(s + 1) % OtcQueries.Length];
        var resultCount = c.RandInt(4, 21);

        c.E("screen", "screen_view",         screen: "otc",                feature: "otc", durationMs: 500);
        c.E("action", "otc_search_submitted", screen: "otc_search",         feature: "otc",
            props: new() { ["query_length"] = q.Length.ToString(), ["result_count"] = $"{resultCount}" });
        c.E("screen", "otc_result_view",      screen: "otc_search_results", feature: "otc",
            props: new() { ["result_count"] = $"{resultCount}" }, durationMs: 1500);
        c.E("screen", "otc_product_view",     screen: "otc_product_detail", feature: "otc",
            props: new() { ["item_id"] = p.Pzn, ["category"] = p.Cat }, durationMs: 2800);
        c.E("ecommerce", "add_to_cart", feature: "otc",
            props: new() { ["item_id"] = p.Pzn },
            items: [new(p.Pzn, p.Name, p.Cat, p.Price, 1, "EUR")]);
        c.E("screen", "cart_view", screen: "cart", feature: "otc",
            props: new() { ["items_count"] = "1", ["total"] = p.Price.ToString("F2") }, durationMs: 900);
        c.E("ecommerce", "remove_from_cart", feature: "otc", props: new() { ["item_id"] = p.Pzn });
    }

    private static void FlowOtcAbandonSearch(Ctx c, int s)
    {
        var q = OtcQueries[(s + 4) % OtcQueries.Length];
        c.E("screen", "screen_view",          screen: "otc",                feature: "otc", durationMs: 400);
        c.E("action", "otc_search_submitted",  screen: "otc_search",         feature: "otc",
            props: new() { ["query_length"] = q.Length.ToString(), ["result_count"] = "0" });
        c.E("screen", "otc_result_view",       screen: "otc_search_results", feature: "otc",
            props: new() { ["result_count"] = "0" }, durationMs: 800);
    }

    private static void FlowOtcReorder(Ctx c, int s)
    {
        var p = Products[(s + 5) % Products.Length];
        c.E("screen", "screen_view", screen: "orders", feature: "otc", durationMs: 1200);
        c.E("action", "reorder", screen: "orders", feature: "otc",
            props: new() { ["order_id"] = $"ord-prev-{s % 999:D3}" });
        c.E("screen", "cart_view", screen: "cart", feature: "otc",
            props: new() { ["items_count"] = "1", ["total"] = p.Price.ToString("F2") }, durationMs: 800);
        c.E("action", "checkout_step", screen: "checkout", feature: "otc",
            props: new() { ["step_name"] = "confirm", ["step_index"] = "0" });
        c.E("ecommerce", "purchase", feature: "otc",
            props: new()
            {
                ["order_id"]        = $"ord-{c.Id[^6..]}",
                ["items_count"]     = "1",
                ["total"]           = p.Price.ToString("F2"),
                ["currency"]        = "EUR",
                ["shipping_method"] = "standard",
                ["payment_method"]  = PickPaymentMethod(c.Platform, s),
            },
            items: [new(p.Pzn, p.Name, p.Cat, p.Price, 1, "EUR")]);
    }

    private static void FlowCardLinkComplete(Ctx c, int s, string authMethod)
    {
        var insurer = Insurers[s % Insurers.Length];
        c.E("screen", "screen_view",           screen: "cardlink_intro",   feature: "cardlink", durationMs: 1600);
        c.E("action", "cardlink_start",         feature: "cardlink",
            props: new() { ["entry_point"] = s % 3 == 0 ? "home_banner" : s % 3 == 1 ? "profile_menu" : "onboarding" });
        c.E("screen", "cardlink_consent_shown", screen: "cardlink_consent", feature: "cardlink", durationMs: 4800);
        c.E("action", "cardlink_consent_given", feature: "cardlink");
        c.E("action", "cardlink_auth_started",  feature: "cardlink",
            props: new() { ["auth_method"] = authMethod });

        if (authMethod == "scan")
        {
            foreach (var step in new[] { "position_card", "scanning", "reading_chip", "confirm" })
                c.E("action", "cardlink_scan_step", feature: "cardlink",
                    props: new() { ["step_name"] = step });
        }
        else
        {
            foreach (var step in new[] { "enter_can", "enter_pin", "confirm" })
                c.E("action", "cardlink_entry_step", feature: "cardlink",
                    props: new() { ["step_name"] = step });
        }
        c.E("action", "cardlink_success", feature: "cardlink",
            props: new() { ["insurer"] = insurer });
    }

    private static void FlowCardLinkCancelled(Ctx c, int s)
    {
        c.E("screen", "screen_view",           screen: "cardlink_intro",   feature: "cardlink", durationMs: 1100);
        c.E("action", "cardlink_start",         feature: "cardlink",
            props: new() { ["entry_point"] = "home_banner" });
        c.E("screen", "cardlink_consent_shown", screen: "cardlink_consent", feature: "cardlink", durationMs: 6200);
        c.E("action", "cardlink_consent_given", feature: "cardlink");
        c.E("action", "cardlink_auth_started",  feature: "cardlink",
            props: new() { ["auth_method"] = "scan" });
        c.E("action", "cardlink_scan_step", feature: "cardlink",
            props: new() { ["step_name"] = "position_card" });
        c.E("action", "cardlink_cancelled", feature: "cardlink",
            props: new() { ["cancel_reason"] = s % 3 == 0 ? "user_abort" : s % 3 == 1 ? "timeout" : "back_navigation" });
    }

    private static void FlowCardLinkFailed(Ctx c, int s)
    {
        c.E("screen", "screen_view",           screen: "cardlink_intro",   feature: "cardlink", durationMs: 900);
        c.E("action", "cardlink_start",         feature: "cardlink",
            props: new() { ["entry_point"] = "profile_menu" });
        c.E("screen", "cardlink_consent_shown", screen: "cardlink_consent", feature: "cardlink", durationMs: 3100);
        c.E("action", "cardlink_consent_given", feature: "cardlink");
        c.E("action", "cardlink_auth_started",  feature: "cardlink",
            props: new() { ["auth_method"] = "entry" });
        c.E("action", "cardlink_entry_step", feature: "cardlink",
            props: new() { ["step_name"] = "enter_can" });
        c.E("error",  "cardlink_failure",   feature: "cardlink",
            props: new() { ["failure_reason"] = s % 4 == 0 ? "nfc_error" : s % 4 == 1 ? "pin_locked" : s % 4 == 2 ? "network_error" : "card_expired" });
        c.E("error",  "error_shown",
            props: new() { ["error_class"] = "CardLinkAuthFailure", ["retry_offered"] = "true", ["status_code"] = "0" });
    }

    private static void FlowPrescriptionScan(Ctx c, int s)
    {
        var rxHash = $"rx-{s % 999:D3}";
        var pharma = PharmacyHashes[s % PharmacyHashes.Length];
        c.E("screen", "screen_view",              screen: "prescription",          feature: "rx", durationMs: 1000);
        c.E("action", "prescription_scan_start",   screen: "prescription_scanner",  feature: "rx",
            props: new() { ["scan_source"] = "camera" });
        c.E("action", "prescription_scan_result",  feature: "rx",
            props: new() { ["scan_source"] = "camera", ["result_kind"] = "success" });
        c.E("action", "prescription_cart_add",     feature: "rx",
            props: new() { ["prescription_id_hash"] = rxHash });
        c.E("action", "prescription_transfer",     feature: "rx",
            props: new() { ["to_pharmacy_id_hash"] = pharma });
    }

    private static void FlowPrescriptionUpload(Ctx c, int s)
    {
        var rxHash = $"rx-{(s + 100) % 999:D3}";
        var pharma = PharmacyHashes[(s + 2) % PharmacyHashes.Length];
        c.E("screen", "screen_view",                  screen: "prescription",         feature: "rx", durationMs: 1100);
        c.E("action", "prescription_upload_start",    screen: "prescription_upload",   feature: "rx",
            props: new() { ["upload_source"] = s % 2 == 0 ? "photo_library" : "files_app" });
        c.E("action", "prescription_upload_success",  feature: "rx",
            props: new() { ["upload_source"] = s % 2 == 0 ? "photo_library" : "files_app" });
        c.E("action", "prescription_cart_add",        feature: "rx",
            props: new() { ["prescription_id_hash"] = rxHash });
        c.E("action", "prescription_transfer",        feature: "rx",
            props: new() { ["to_pharmacy_id_hash"] = pharma });
    }

    private static void FlowPrescriptionScanFailure(Ctx c, int s)
    {
        c.E("screen", "screen_view",              screen: "prescription",         feature: "rx", durationMs: 800);
        c.E("action", "prescription_scan_start",   screen: "prescription_scanner", feature: "rx",
            props: new() { ["scan_source"] = "camera" });
        c.E("error",  "prescription_scan_failure", feature: "rx",
            props: new() { ["error_class"] = s % 2 == 0 ? "QRCodeNotFound" : "ImageTooBlurry" });
        c.E("error",  "error_shown",
            props: new() { ["error_class"] = "PrescriptionScanError", ["retry_offered"] = "true", ["status_code"] = "0" });
        // retry succeeds
        c.E("action", "prescription_scan_start",  screen: "prescription_scanner", feature: "rx",
            props: new() { ["scan_source"] = "camera" });
        c.E("action", "prescription_scan_result", feature: "rx",
            props: new() { ["scan_source"] = "camera", ["result_kind"] = "success" });
        c.E("action", "prescription_cart_add", feature: "rx",
            props: new() { ["prescription_id_hash"] = $"rx-{(s + 50) % 999:D3}" });
    }

    private static void FlowPharmacySearchAndSelect(Ctx c, int s)
    {
        var pharma  = PharmacyHashes[s % PharmacyHashes.Length];
        var pharmaB = PharmacyHashes[(s + 1) % PharmacyHashes.Length];
        c.E("screen", "screen_view",               screen: "pharmacy_search", feature: "pharmacy", durationMs: 900);
        c.E("action", "pharmacy_search_submitted",  screen: "pharmacy_search", feature: "pharmacy",
            props: new() { ["query_length"] = "8", ["result_count"] = $"{4 + s % 22}", ["filter_open"] = "false" });
        c.E("action", "pharmacy_filter_applied",    screen: "pharmacy_search", feature: "pharmacy",
            props: new() { ["filter_kind"] = s % 2 == 0 ? "open_now" : "delivery", ["filter_value"] = "true" });
        c.E("screen", "screen_view",                screen: "pharmacy_list",   feature: "pharmacy", durationMs: 2400);
        c.E("action", "pharmacy_selected",          feature: "pharmacy",
            props: new() { ["pharmacy_id_hash"] = pharma, ["list_position"] = $"{s % 6}" });
        c.E("screen", "screen_view",                screen: "pharmacy_detail", feature: "pharmacy", durationMs: 3200);

        switch (s % 4)
        {
            case 0: c.E("action", "pharmacy_call",       feature: "pharmacy",
                        props: new() { ["pharmacy_id_hash"] = pharma }); break;
            case 1: c.E("action", "pharmacy_directions", feature: "pharmacy",
                        props: new() { ["pharmacy_id_hash"] = pharma }); break;
            case 2: c.E("action", "pharmacy_share",      feature: "pharmacy",
                        props: new() { ["pharmacy_id_hash"] = pharma, ["share_target"] = "messages" }); break;
            default: c.E("action", "pharmacy_switch",    feature: "pharmacy",
                        props: new() { ["from_pharmacy_id_hash"] = pharma, ["to_pharmacy_id_hash"] = pharmaB }); break;
        }
    }

    private static void FlowPharmacyEmergency(Ctx c, int s)
    {
        var pharma = PharmacyHashes[s % PharmacyHashes.Length];
        c.E("screen", "screen_view",               screen: "pharmacy_search", feature: "pharmacy", durationMs: 600);
        c.E("action", "pharmacy_search_submitted",  screen: "pharmacy_search", feature: "pharmacy",
            props: new() { ["query_length"] = "10", ["result_count"] = $"{2 + s % 8}", ["filter_open"] = "true" });
        c.E("action", "pharmacy_filter_applied",    screen: "pharmacy_search", feature: "pharmacy",
            props: new() { ["filter_kind"] = "emergency_service", ["filter_value"] = "true" });
        c.E("screen", "screen_view",                screen: "pharmacy_list",   feature: "pharmacy", durationMs: 1800);
        c.E("action", "pharmacy_selected",          feature: "pharmacy",
            props: new() { ["pharmacy_id_hash"] = pharma, ["list_position"] = "0" });
        c.E("action", "pharmacy_emergency_service", feature: "pharmacy",
            props: new() { ["service_kind"] = s % 2 == 0 ? "night_service" : "weekend_service" });
    }

    private static void FlowAppointmentSuccess(Ctx c, int s)
    {
        var pharma = PharmacyHashes[s % PharmacyHashes.Length];
        var appt   = ApptHashes[s % ApptHashes.Length];
        c.E("screen", "appointment_list_view",   screen: "appointments",       feature: "appointments",
            durationMs: 2100, props: new() { ["pharmacy_id_hash"] = pharma });
        c.E("screen", "appointment_detail_view", screen: "appointment_detail",  feature: "appointments",
            durationMs: 3400, props: new() { ["event_id_hash"] = appt });
        c.E("action", "appointment_slot_select", feature: "appointments",
            props: new() { ["event_id_hash"] = appt });
        c.E("action", "appointment_book_submit", feature: "appointments",
            props: new() { ["event_id_hash"] = appt });
        c.E("action", "appointment_book_success", feature: "appointments",
            props: new() { ["event_id_hash"] = appt });
    }

    private static void FlowAppointmentFailure(Ctx c, int s)
    {
        var pharma = PharmacyHashes[(s + 1) % PharmacyHashes.Length];
        var appt   = ApptHashes[(s + 2) % ApptHashes.Length];
        c.E("screen", "appointment_list_view",   screen: "appointments",      feature: "appointments",
            durationMs: 1900, props: new() { ["pharmacy_id_hash"] = pharma });
        c.E("screen", "appointment_detail_view", screen: "appointment_detail", feature: "appointments",
            durationMs: 2700, props: new() { ["event_id_hash"] = appt });
        c.E("action", "appointment_slot_select", feature: "appointments",
            props: new() { ["event_id_hash"] = appt });
        c.E("action", "appointment_book_submit", feature: "appointments",
            props: new() { ["event_id_hash"] = appt });
        c.E("error",  "appointment_book_failure", feature: "appointments",
            props: new() { ["event_id_hash"] = appt, ["failure_reason"] = s % 2 == 0 ? "slot_taken" : "service_unavailable" });
        c.E("error",  "error_shown",
            props: new() { ["error_class"] = "AppointmentConflict", ["retry_offered"] = "true", ["status_code"] = "409" });
    }

    private static void FlowFormWithError(Ctx c, int s)
    {
        var formId = FormIds[s % FormIds.Length];
        c.E("action", "form_start", screen: "form", props: new() { ["form_id"] = formId });
        c.E("engagement", "scroll", screen: "form", props: new() { ["depth_pct"] = "30" });
        c.E("error", "form_error", screen: "form",
            props: new() { ["form_id"] = formId, ["field_id"] = "insurance_number", ["error_class"] = "ValidationError" });
        c.E("action", "element_interaction", screen: "form",
            props: new() { ["element_id"] = "insurance_number_field", ["interaction_kind"] = "tap" });
        c.E("action", "form_submit", screen: "form", props: new() { ["form_id"] = formId });
    }

    private static void FlowBrowseOnly(Ctx c, int s)
    {
        c.E("action",     "element_interaction", screen: "home",
            props: new() { ["element_id"] = "banner_otc", ["interaction_kind"] = "tap" });
        c.E("screen",     "screen_view",          screen: "otc",              durationMs: 1400);
        c.E("engagement", "scroll",               screen: "otc",
            props: new() { ["depth_pct"] = $"{c.RandInt(30, 90)}" });
        c.E("screen",     "screen_exit",          screen: "otc",              durationMs: 1400);
        c.E("screen",     "screen_view",          screen: "pharmacy_search",  durationMs: 1900);
        c.E("screen",     "screen_exit",          screen: "pharmacy_search",  durationMs: 1900);
    }

    // ── Event context ──────────────────────────────────────────────────────
    private sealed class Ctx(string sessionId, DateTimeOffset start, string platform, Random rng)
    {
        private readonly DateTimeOffset _start = start;
        private readonly Random         _rng   = rng;
        private DateTimeOffset _ts  = start;
        private int            _seq;

        public string             Id       => sessionId;
        public string             Platform => platform;
        public List<RSCAnalyticsEvent> Events { get; } = [];
        public long ElapsedMs => (long)(_ts - _start).TotalMilliseconds;

        /// <summary>Timestamp just past the last emitted event — i.e. when the session ended.</summary>
        public DateTimeOffset End => _ts;

        /// <summary>Random integer in [minInclusive, maxInclusive] from the session's deterministic RNG.</summary>
        public int RandInt(int minInclusive, int maxInclusive) => _rng.Next(minInclusive, maxInclusive + 1);

        public void E(string type, string name, string? screen = null, string? feature = null,
            long? durationMs = null, Dictionary<string, string>? props = null,
            List<RSCAnalyticsItem>? items = null)
        {
            // Treat a caller-supplied screen/engagement duration as a baseline and scatter it ±~30%
            // so dwell times stop falling into tight arithmetic bands. Lifecycle durations carry the
            // real elapsed time (session_end) and must stay exact so they match the duration_ms prop.
            if (durationMs is { } d && type != "lifecycle")
                durationMs = (long)Math.Round(d * (0.75 + _rng.NextDouble() * 0.6));

            Events.Add(new RSCAnalyticsEvent(
                EventId:    $"{sessionId}-{_seq:D3}",
                SessionId:  sessionId,
                Sequence:   _seq,
                OccurredAt: _ts.ToString("O"),
                Type: type, Name: name,
                Screen: screen, Feature: feature,
                DurationMs: durationMs,
                Properties: props,
                Items: items?.AsReadOnly()));
            _seq++;
            // Natural, non-monotonic spacing (~5-22s) instead of the old fixed 12 + seq*2 ramp.
            _ts = _ts.AddSeconds(5 + _rng.Next(0, 13) + _rng.NextDouble() * 4.0);
        }
    }
}
