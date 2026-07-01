using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Audit;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Storage.Catalog;

namespace ReportService.Admin.Services;

/// <summary>
/// Seeds realistic problem-report / crash data (plus a few forced-report allow-list entries and
/// audit-log rows) so the operator console has something to browse in local development.
/// </summary>
/// <remarks>
/// <para>Only runs when an <see cref="RSCIReportIndex"/> is registered — i.e. the
/// <c>Storage = "SqliteIndex"</c> deployment the docker-compose dev stack uses. The xUnit test
/// hosts run with <c>Storage = "FileSystem"</c> (no index), so this seeder is a no-op there and
/// never slows the suite or perturbs report-count assertions.</para>
/// <para><c>RSCFileSystemReportStore.SaveAsync</c> always stamps <c>SubmittedAt = UtcNow</c>, which
/// can't produce the back-dated spread a realistic dashboard needs. So this seeder writes the JSON
/// (and optional gzip attachment) to disk directly — mirroring the store's
/// <c>problem-report_&lt;ts&gt;_&lt;sha12&gt;[_&lt;attachmentSha12&gt;]</c> naming — and upserts the
/// index row itself. Idempotent via a "already seeded?" guard: if the android platform already has
/// any indexed report, the seeder returns immediately.</para>
/// </remarks>
internal static class RSAProblemReportDevDataSeeder
{
    private const int DefaultSeedScale = 0;          // OFF by default; opt in via REPORTS_SEED_SCALE>0
    private const int BaseReportsPerPlatform = 40;   // multiplied by the scale
    private const int SpreadDays = 30;               // back-date reports across this many days

    private static readonly string[] Platforms = ["android", "ios"];

    private static readonly string[] AndroidDevices =
        ["Pixel 7", "Pixel 8 Pro", "Samsung Galaxy S23", "Samsung Galaxy A54",
         "Xiaomi Redmi Note 12", "OnePlus 11", "Motorola Edge 40", "Nothing Phone (2)"];

    private static readonly string[] IosDevices =
        ["iPhone 15 Pro", "iPhone 15", "iPhone 14 Pro", "iPhone 13", "iPhone SE (3rd gen)", "iPad Air (5th gen)"];

    // Pharmacy IDs are numeric-only strings on the wire (the SDK sends the BEP pharmacy id, e.g.
    // "2163"). The trailing null seeds reports that carry no pharmacy id.
    private static readonly string[] Pharmacies =
        ["2163", "35742", "6001", "8842", "1109", "204", null!];

    private static readonly string[] Labels =
        ["SDKV2", "cardlink-client-42", "otc", "prescription", "pharmacy-search", "appointments", "beta"];

    // User-initiated "Report a Problem" descriptions (Kind = null / "analytics").
    private static readonly (string Title, string Message)[] UserReports =
    [
        ("Cart empties after scan",       "After scanning a prescription QR the cart shows 0 items even though the medication was added."),
        ("Login loops",                   "The biometric login prompt succeeds but the app immediately asks for it again."),
        ("Pharmacy search slow",          "Searching for a pharmacy by postal code takes 10+ seconds and sometimes shows no results."),
        ("CardLink stuck on consent",     "The CardLink consent screen never advances after I tap 'Accept'."),
        ("Wrong delivery address",        "Checkout pre-filled an old delivery address that I deleted weeks ago."),
        ("Appointment slots missing",     "No appointment slots show up for my pharmacy even though the website lists several."),
        ("Push notifications duplicated", "I receive every order-status notification three times."),
        ("App crashes on startup",        "The app shows the splash screen and then closes immediately. Happens every launch."),
        ("OTC prices look wrong",         "The price shown in search differs from the price on the product detail page."),
        ("Can't upload prescription",     "The 'upload from files' button does nothing when I pick a PDF."),
    ];

    // Legacy raw analytics submissions (Kind = "analytics") — tracking events the SDK historically
    // posted as report documents. These populate the "Analytics submissions" listing at the bottom
    // of the Analytics page (distinct from the v2 analytics_events pipeline that feeds the tiles).
    private static readonly (string Title, string Message)[] AnalyticsEvents =
    [
        ("screen_view · home",             "Anonymous session viewed the home screen."),
        ("screen_view · pharmacy_search",  "Anonymous session opened pharmacy search."),
        ("action · add_to_cart",           "An OTC item was added to the cart."),
        ("action · scan_prescription",     "A prescription QR code was scanned."),
        ("ecommerce · purchase",           "An OTC order was completed."),
        ("action · cardlink_consent",      "The CardLink consent screen was reached."),
        ("screen_view · appointments",     "The appointments screen was viewed."),
        ("action · upload_prescription",   "A prescription PDF was uploaded."),
    ];

    // Crash signatures (Kind = "crash"). Each yields a stable top frame so the Errors page groups
    // many occurrences into one bucket.
    private static readonly (string Title, string TopFrame, string Message)[] AndroidCrashes =
    [
        ("java.lang.NullPointerException",      "com.ia.cardlink.cart.CartViewModel.onScanResult(CartViewModel.kt:142)",        "Attempt to invoke virtual method on a null object reference while applying a scan result."),
        ("java.lang.IllegalStateException",     "com.ia.pharmacy.PharmacySearchFragment.onBindResults(PharmacySearchFragment.kt:88)", "Fragment not attached to an activity while binding pharmacy results."),
        ("java.lang.RuntimeException",          "com.ia.rx.PrescriptionScanner.decode(PrescriptionScanner.kt:301)",             "Failure delivering result of camera decode for the prescription scanner."),
        ("kotlin.KotlinNullPointerException",   "com.ia.checkout.CheckoutRepository.placeOrder(CheckoutRepository.kt:57)",      "Null receiver while placing an order during checkout."),
        ("android.os.NetworkOnMainThreadException", "com.ia.net.ReportRemoteDataSource.flush(ReportRemoteDataSource.kt:120)",   "Network call attempted on the main thread while flushing the analytics outbox."),
    ];

    private static readonly (string Title, string TopFrame, string Message)[] IosCrashes =
    [
        ("EXC_BAD_ACCESS",          "IACardlink.BiometricAuth.authenticate(BiometricAuth.swift:210)",            "SIGSEGV dereferencing a released LAContext during biometric authentication."),
        ("NSInvalidArgumentException", "IAPharmacy.PharmacyListViewController.didSelectRow(PharmacyListViewController.swift:144)", "Unrecognized selector sent to deallocated pharmacy cell."),
        ("Swift.fatalError",        "IACheckout.CheckoutViewModel.confirm(CheckoutViewModel.swift:96)",           "Unexpectedly found nil while unwrapping the selected delivery address."),
        ("EXC_BREAKPOINT",          "IARx.PrescriptionUploader.upload(PrescriptionUploader.swift:73)",            "Precondition failure: prescription file handle was closed before upload completed."),
    ];

    // Non-fatal error signatures (Kind = "error"). Caught/handled faults the SDK reports without
    // killing the app — they carry a fault site like a crash, but the user kept going. These drive
    // the divergence between the Errors page's "Crashes" and "Errors" tiles, and give the Kind
    // filter an error-only population to narrow to.
    private static readonly (string Title, string TopFrame, string Message)[] AndroidErrors =
    [
        ("java.io.IOException",     "com.ia.net.ReportRemoteDataSource.flush(ReportRemoteDataSource.kt:131)",   "Timed out flushing the analytics outbox; upload retry scheduled."),
        ("retrofit2.HttpException", "com.ia.checkout.CheckoutRepository.placeOrder(CheckoutRepository.kt:74)",  "HTTP 503 from the order service; surfaced to the user as a soft error."),
        ("com.ia.cardlink.CardLinkException", "com.ia.cardlink.consent.ConsentViewModel.submit(ConsentViewModel.kt:58)", "CardLink consent was declined by the gateway; user prompted to retry."),
    ];

    private static readonly (string Title, string TopFrame, string Message)[] IosErrors =
    [
        ("URLError.timedOut",      "IANet.ReportRemoteDataSource.flush(ReportRemoteDataSource.swift:118)",     "Request timed out flushing the analytics outbox; upload retry scheduled."),
        ("DecodingError",          "IACheckout.OrderResponse.decode(OrderResponse.swift:42)",                  "Malformed order response decoded into a soft error banner."),
        ("CardLinkError.declined", "IACardLink.ConsentCoordinator.submit(ConsentCoordinator.swift:65)",        "CardLink consent declined by the gateway; user prompted to retry."),
    ];

    public static async Task SeedAsync(IServiceProvider sp, ILogger logger, CancellationToken ct)
    {
        // Gated on the SQLite index: present only under Storage=SqliteIndex (the docker dev stack).
        var index = sp.GetService<RSCIReportIndex>();
        if (index is null)
        {
            logger.LogDebug("Problem-report seeder skipped: no SQLite index (FileSystem storage).");
            return;
        }

        // Database-per-app: write through the fan-out store (which routes each report to its own
        // (client, app)'s tree + index by report.ClientId/AppId) and distribute the synthetic reports
        // across the catalog's registered apps, so each client's dashboard — and each client login —
        // sees its own per-app reports. Falls back to the default app if the catalog is empty.
        var store = sp.GetRequiredService<RSCIReportStore>();
        var catalog = sp.GetService<RSCICatalog>();
        var apps = catalog is null
            ? new List<(string Client, string App)>()
            : (await catalog.ListAllAppsAsync(includeArchived: false, ct).ConfigureAwait(false))
                .Select(a => (Client: a.ClientSlug, App: a.Slug)).ToList();
        if (apps.Count == 0) apps.Add((Client: "default", App: "default"));

        // Idempotency + freshness: skip only when the newest report (across all apps) is from today.
        // If the dataset is stale (newest < today — e.g. the container restarted on a later day
        // against a persistent volume), seed a fresh batch so "latest submissions" and the daily chart
        // stay current instead of trailing off days ago. Retention trims the older synthetic rows.
        var existing = store.List("android");
        if (existing.Count > 0 && existing[0].SubmittedAt.UtcDateTime.Date >= DateTime.UtcNow.Date)
        {
            logger.LogInformation("Problem-report seeder: fresh data already present, skipping.");
            return;
        }

        var options = sp.GetRequiredService<RSCReportServiceOptions>();
        var scale = ResolveScale(logger);
        if (scale <= 0)
        {
            logger.LogInformation(
                "Problem-report seeder disabled (REPORTS_SEED_SCALE unset or 0); set REPORTS_SEED_SCALE>0 to enable synthetic reports.");
            return;
        }
        var now = DateTimeOffset.UtcNow;

        int written = 0, crashes = 0, errors = 0, analytics = 0, withAttachment = 0;

        foreach (var platform in Platforms)
        {
            var devices = platform == "android" ? AndroidDevices : IosDevices;
            var crashSigs = platform == "android" ? AndroidCrashes : IosCrashes;
            var errorSigs = platform == "android" ? AndroidErrors : IosErrors;

            var total = BaseReportsPerPlatform * scale;
            for (int i = 0; i < total; i++)
            {
                // Spread across the window: a few per day, varied hour/minute, deterministic per i.
                var submittedAt = now
                    .AddDays(-(i % SpreadDays))
                    .AddHours(-(i % 11))
                    .AddMinutes(-(i * 7 % 60))
                    .AddSeconds(-(i % 47));

                // Three roughly equal buckets: fault (Errors page), analytics submission (legacy
                // raw-report store on the Analytics page), and a user-written problem report. The
                // fault bucket splits ~2:1 into fatal crashes and non-fatal Kind="error" reports so
                // the Errors page shows both populations and the Kind filter has something to narrow.
                var category = i % 3;
                var isFault = category == 0;
                var isError = isFault && i % 9 == 6;
                var isCrash = isFault && !isError;
                var isAnalytics = category == 1;
                var device = devices[i % devices.Length];
                var pharmacy = Pharmacies[i % Pharmacies.Length];
                var appVersion = $"4.{10 + i % 4}.{i % 6} (SDK 2.3.{20 + i % 12})";
                var hasEmail = i % 4 != 0;
                var email = hasEmail ? $"kunde{i:D3}@example.com" : null;
                var userId = $"{platform}-user-{1000 + i}";
                var channel = i % 5 == 0 ? RSCIngestionChannels.Json : RSCIngestionChannels.Multipart;
                var labels = new List<string> { Labels[i % Labels.Length], Labels[(i + 3) % Labels.Length] };
                // Distribute reports across the catalog's apps so each client's dashboard — and each
                // client login — sees its own per-app reports. (top_frame / log_summary / kind are
                // derived by the indexing decorator on save, exactly as production ingestion does.)
                var (clientSlug, appSlug) = apps[i % apps.Count];

                string? title, message, stackTrace, kind;
                if (isCrash)
                {
                    var (cTitle, cFrame, cMessage) = crashSigs[i % crashSigs.Length];
                    title = cTitle;
                    message = cMessage;
                    stackTrace = BuildStackTrace(platform, cTitle, cFrame, i);
                    kind = "crash";
                    crashes++;
                }
                else if (isError)
                {
                    var (eTitle, eFrame, eMessage) = errorSigs[i % errorSigs.Length];
                    title = eTitle;
                    message = eMessage;
                    stackTrace = BuildStackTrace(platform, eTitle, eFrame, i);
                    kind = "error";
                    errors++;
                }
                else if (isAnalytics)
                {
                    var (aTitle, aMessage) = AnalyticsEvents[i % AnalyticsEvents.Length];
                    title = aTitle;
                    message = aMessage;
                    stackTrace = null;
                    kind = "analytics";
                    analytics++;
                }
                else
                {
                    var (uTitle, uMessage) = UserReports[i % UserReports.Length];
                    title = uTitle;
                    message = uMessage;
                    stackTrace = null;
                    kind = null;
                }

                var report = new RSCProblemReport(
                    Platform: platform,
                    Message: message!,
                    Title: title,
                    DeviceModel: device,
                    Email: email,
                    PhoneNumber: hasEmail ? $"+49170{1000000 + i:D7}" : null,
                    Phone: null,
                    PharmacyId: pharmacy,
                    Source: "SDK",
                    AppVersion: appVersion,
                    FunctionalityImportance: platform == "android" && isCrash ? "Schränkt mich häufig ein" : null,
                    Labels: labels,
                    Kind: kind,
                    StackTrace: stackTrace,
                    EventProperties: null,
                    OccurredAt: submittedAt.ToString("O", CultureInfo.InvariantCulture),
                    UserId: userId,
                    AppId: appSlug,
                    ClientId: clientSlug);

                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);

                // Crashes and non-fatal errors always carry a gzip log bundle (both ship the
                // captured stack trace); user problem reports ~60% do. Analytics submissions are
                // lightweight tracking docs and never carry a logcat attachment.
                byte[]? attachment = (isCrash || isError || (!isAnalytics && i % 5 < 3))
                    ? GzipLog(BuildLogText(platform, report, stackTrace, submittedAt))
                    : null;
                using var attachmentStream = attachment is null ? null : new MemoryStream(attachment);

                // Route through the per-app fan-out store with the backdated submittedAt: it writes the
                // file under apps/{client}/{app}/{platform}/problem-reports and indexes the metadata
                // (top frame, log summary, kind, channel) just like production ingestion.
                await store.SaveAsync(report, jsonBytes, attachmentStream, attachment?.Length, channel, submittedAt, ct)
                    .ConfigureAwait(false);
                if (attachment is not null) withAttachment++;
                written++;
            }
        }

        await SeedForcedReportsAsync(sp, now, ct).ConfigureAwait(false);
        await SeedAuditLogAsync(sp, now, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Problem-report seeder (scale {Scale}): wrote {Written} reports ({Crashes} crashes, {Errors} errors, {Analytics} analytics, {Attachments} with attachments) across {Platforms} platforms",
            scale, written, crashes, errors, analytics, withAttachment, Platforms.Length);
    }

    private static async Task SeedForcedReportsAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var store = sp.GetService<RSCIForcedReportStore>();
        if (store is null) return;

        var entries = new (string Id, string Note)[]
        {
            ("android-user-1003", "Investigating cart-empties-after-scan ticket #4214"),
            ("android-user-1012", "Repeated NPE in CartViewModel — need a fresh capture"),
            ("ios-user-1007",     "Biometric login loop reported via support, no crash logged"),
            ("ios-user-1019",     "CardLink consent hang — collecting logs"),
            ("android-user-1042", "Beta tester opted in to forced captures"),
        };

        foreach (var (id, note) in entries)
            await store.AddAsync(id, note, ct).ConfigureAwait(false);
    }

    private static async Task SeedAuditLogAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var audit = sp.GetService<RSCIAuditLog>();
        if (audit is null) return;
        if (await audit.CountAsync(ct).ConfigureAwait(false) > 0) return;

        var entries = new (int DaysBack, string Action, string? Target, string? Details, bool Success)[]
        {
            (12, "login",            null,                                   "cookie issued",          true),
            (12, "login",            null,                                   "invalid admin key",      false),
            (10, "delete",           "android/problem-report_old_aaaa.json", "operator-initiated",     true),
            (8,  "export",           "analytics/events.ndjson",              "12480 rows streamed",    true),
            (6,  "vacuum",           "analytics.db",                         "reclaimed 4.2 MiB",      true),
            (5,  "integrity_check",  "reports.db",                           "ok",                     true),
            (3,  "rebuild",          "report index",                         "rebuilt from disk",      true),
            (1,  "backup",           "reports.db",                           "snapshot written",       true),
        };

        foreach (var (daysBack, action, target, details, success) in entries)
        {
            await audit.RecordAsync(new RSCAuditEntry(
                At: now.AddDays(-daysBack),
                Actor: success ? "operator" : "anonymous",
                RemoteAddress: "127.0.0.1",
                Action: action,
                Target: target,
                Details: details,
                Success: success), ct).ConfigureAwait(false);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static int ResolveScale(ILogger logger)
    {
        var raw = Environment.GetEnvironmentVariable("REPORTS_SEED_SCALE");
        if (string.IsNullOrWhiteSpace(raw)) return DefaultSeedScale;
        if (int.TryParse(raw, out var parsed) && parsed >= 0) return parsed;
        logger.LogWarning("Ignoring invalid REPORTS_SEED_SCALE='{Raw}'; using {Default}", raw, DefaultSeedScale);
        return DefaultSeedScale;
    }

    private static string BuildStackTrace(string platform, string title, string topFrame, int seed) =>
        platform == "android"
            ? string.Join('\n',
                $"{title}: see message",
                $"\tat {topFrame}",
                "\tat com.ia.sdk.IaSdk$initialize$1.invokeSuspend(IaSdk.kt:64)",
                "\tat kotlinx.coroutines.DispatchedTask.run(DispatchedTask.kt:106)",
                $"\tat android.os.Handler.dispatchMessage(Handler.java:{100 + seed % 50})")
            : string.Join('\n',
                $"{title}",
                $"0   IACardlink   0x000000010 {topFrame}",
                "1   IACardlink   0x000000020 closure #1 in AppDelegate.application(_:didFinishLaunching:)",
                "2   UIKitCore    0x000000030 -[UIApplication _run]",
                $"3   IACardlink   0x0000000{40 + seed % 50} main");

    private static string BuildLogText(string platform, RSCProblemReport r, string? stackTrace, DateTimeOffset at)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== IA SDK log bundle ({platform}) ==");
        sb.AppendLine($"capturedAt={at:O}");
        sb.AppendLine($"appVersion={r.AppVersion}");
        sb.AppendLine($"device={r.DeviceModel}");
        sb.AppendLine($"kind={r.Kind ?? "user_report"}");
        sb.AppendLine("--- recent log lines ---");
        for (int n = 0; n < 12; n++)
            sb.AppendLine($"{at.AddSeconds(-n):HH:mm:ss} D/IaSdk: heartbeat seq={n} feature=cardlink");
        if (stackTrace is not null)
        {
            sb.AppendLine("--- stack trace ---");
            sb.AppendLine(stackTrace);
        }
        return sb.ToString();
    }

    private static byte[] GzipLog(string text)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gz.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }
}
