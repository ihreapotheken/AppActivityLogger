using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using ReportService.Admin;
using ReportService.Admin.Options;
using ReportService.Admin.Services;
using ReportService.Analytics;
using ReportService.Audit;
using ReportService.Endpoints;
using ReportService.Hosting;
using ReportService.Ingestion;
using ReportService.Observability;
using ReportService.Options;
using ReportService.Security;
using ReportService.Storage;
using ReportService.Storage.Retention;
using ReportService.Validation;

var builder = WebApplication.CreateBuilder(args);

// Translate short, operator-friendly env vars (REPORT_RETENTION_MAX_MB, etc.) into the
// canonical ReportService:* / Admin:* keys. Mounted after the default providers so
// smart-unit values win over anything set on the legacy ReportService__* form.
RSCSmartUnitConfig.Apply(builder.Configuration);

var reportOptions = builder.Configuration
    .GetSection(RSCReportServiceOptions.SectionName)
    .Get<RSCReportServiceOptions>() ?? new RSCReportServiceOptions();

var adminOptions = builder.Configuration
    .GetSection(RSAAdminOptions.SectionName)
    .Get<RSAAdminOptions>() ?? new RSAAdminOptions();

var proxyHeaders = builder.Configuration
    .GetSection(RSCProxyHeadersOptions.SectionName)
    .Get<RSCProxyHeadersOptions>() ?? new RSCProxyHeadersOptions();

var analyticsOptions = builder.Configuration
    .GetSection(RSCAnalyticsOptions.SectionName)
    .Get<RSCAnalyticsOptions>() ?? new RSCAnalyticsOptions();

builder.Services.AddSingleton(reportOptions);
builder.Services.AddSingleton(adminOptions);
builder.Services.AddSingleton(proxyHeaders);
builder.Services.AddSingleton(analyticsOptions);

// Share the same storage wiring as the ingestion service so both services read the same files and
// (optionally) the same SQLite index.
builder.Services.AddSingleton<RSCReportValidator>();
builder.Services.AddSingleton<RSCComponentHealth>();
if (reportOptions.Storage == "SqliteIndex")
{
    builder.Services.AddSingleton<RSCFileSystemReportStore>();
    builder.Services.AddSingleton<RSCResilientReportIndex>(sp =>
        new RSCResilientReportIndex(
            () => new RSCSqliteReportIndex(sp.GetRequiredService<RSCReportServiceOptions>(),
                                         sp.GetRequiredService<ILogger<RSCSqliteReportIndex>>()),
            sp.GetRequiredService<RSCComponentHealth>(),
            sp.GetRequiredService<ILogger<RSCResilientReportIndex>>()));
    builder.Services.AddSingleton<RSCIReportIndex>(sp => sp.GetRequiredService<RSCResilientReportIndex>());
    builder.Services.AddSingleton<RSCIReportStore, RSCSqliteIndexingReportStore>();
}
else
{
    builder.Services.AddSingleton<RSCIReportStore, RSCFileSystemReportStore>();
}
builder.Services.AddSingleton<RSCIForcedReportStore, RSCSqliteForcedReportStore>();
builder.Services.AddSingleton<RSCServiceTelemetry>();
builder.Services.AddSingleton<RSCIAuditLog, RSCSqliteAuditLog>();
builder.Services.AddSingleton<RSCRetentionService>();
// Single merged process now owns the retention background sweep + manual purge — same internal
// semaphore prevents the two paths from racing.
builder.Services.AddHostedService<RSCRetentionBackgroundService>();

// Ingestion-side wiring (formerly hosted by the standalone ReportService process). All of these
// are no-ops at admin-only request paths; they only activate for `/partners/api/v2/...` and
// `/api/v1/...` route hits.
builder.Services.AddSingleton<RSReportIngestionService>();
builder.Services.AddSingleton<RSAcceptHeaderFilter>();

// v2 analytics pipeline. The merged process hosts both ingestion + admin views over the same
// analytics SQLite store, so wiring is identical to the standalone ingestion's.
builder.Services.AddSingleton<RSCAnalyticsIdentifierHasher>();
builder.Services.AddSingleton<RSCAnalyticsValidator>();
builder.Services.AddSingleton<RSCIAnalyticsStore, RSCSqliteAnalyticsStore>();
builder.Services.AddSingleton<RSAnalyticsIngestionService>();
if (analyticsOptions.Enabled)
{
    builder.Services.AddHostedService<RSCAnalyticsAggregationWorker>();
    builder.Services.AddHostedService<RSCAnalyticsRetentionWorker>();
    builder.Services.AddHostedService<RSCAnalyticsCohortWorker>();
    builder.Services.AddHostedService<RSCAnalyticsFunnelWorker>();
}

// Admin presentation services. The index accessor is registered unconditionally — when
// Storage != "SqliteIndex" it returns null/false and pages handle the absence uniformly. The
// previous pattern (services.GetService(typeof(...)) inside each page constructor) is gone.
builder.Services.AddSingleton<IRSAReportIndexAccessor>(sp =>
    new RSAReportIndexAccessor(sp.GetService<RSCResilientReportIndex>()));
builder.Services.AddSingleton<IRSADashboardService, RSADashboardService>();
// One-shot importer that converts kind = "analytics" problem reports into v2 events. Registered
// unconditionally so the Maintenance page can offer the action even when analytics is disabled —
// the handler itself checks the runtime state.
builder.Services.AddSingleton<RSAAnalyticsLegacyImporter>();

// Pick the analytics dashboard backend based on whether the v2 pipeline is enabled. With the
// pipeline on, the dashboard reads rollup tables (cheap, scales with activity). Without it, the
// legacy report-store implementation continues to scan stored JSON for the kind = "analytics"
// rows. Both render the same RSAAnalyticsDashboardVM, so the page itself is unchanged.
if (analyticsOptions.Enabled)
{
    builder.Services.AddSingleton<IRSAAnalyticsDashboardService, RSAAnalyticsStoreDashboardService>();
}
else
{
    builder.Services.AddSingleton<IRSAAnalyticsDashboardService, RSAReportStoreAnalyticsDashboardService>();
}
builder.Services.AddSingleton<IRSAErrorDashboardService, RSAReportStoreErrorDashboardService>();
builder.Services.AddSingleton<IRSAReportListingService, RSAReportListingService>();
builder.Services.AddSingleton<IRSAStatsService, RSAStatsService>();
builder.Services.AddSingleton<IRSADocsService, RSADocsService>();
builder.Services.AddSingleton<RSCIAuthAbuseTracker>(sp => new RSCResilientAuthAbuseTracker(
    () => new RSCSqliteAuthAbuseTracker(sp.GetRequiredService<RSCReportServiceOptions>(),
                                      sp.GetRequiredService<ILogger<RSCSqliteAuthAbuseTracker>>()),
    sp.GetRequiredService<RSCReportServiceOptions>(),
    sp.GetRequiredService<RSCComponentHealth>(),
    sp.GetRequiredService<ILogger<RSCResilientAuthAbuseTracker>>()));

// Multi-scheme auth. Cookie is the *default* so unauthenticated browser hits to Razor pages
// redirect to /Login; ApiKey is registered as an additional scheme that ingestion endpoints
// opt into via the named policy below. Razor pages never see the ApiKey scheme; ingestion
// endpoints never see the Cookie scheme.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.Cookie.Name = "report-admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(Math.Max(5, adminOptions.SessionMinutes));
        options.SlidingExpiration = true;
    })
    .AddScheme<RSApiKeyAuthenticationOptions, RSApiKeyAuthenticationHandler>(
        RSApiKeyAuthenticationOptions.Scheme, _ => { });

// PostConfigure binds ExpectedKey from the (possibly test-overridden) RSCReportServiceOptions
// singleton, mirroring the standalone ingestion's DI shape.
builder.Services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<RSApiKeyAuthenticationOptions>>(
    sp => new RSPostConfigureApiKey(sp.GetRequiredService<RSCReportServiceOptions>()));

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;

    // Named policy for ingestion endpoints. Resolves to the ApiKey scheme regardless of which
    // host process is wiring this up — keeps the route maps in `RSReportEndpoints` portable
    // between the standalone ingestion and this merged admin.
    options.AddPolicy(RSEndpointConventions.ApiKeyPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(RSApiKeyAuthenticationOptions.Scheme);
        policy.RequireAuthenticatedUser();
    });
});

// Multipart limits on the merged process need to allow the ingestion's gzip attachments
// (`MaxUploadBytes`, default 500 MiB). Razor page POSTs are tiny by comparison; raising the
// global cap doesn't expand the admin's surface meaningfully.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = reportOptions.MaxUploadBytes;
    o.ValueLengthLimit = 16 * 1024 * 1024;
    o.MemoryBufferThreshold = 64 * 1024;
});

builder.Services.AddRateLimiter(rl =>
{
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Per-IP fixed-window limiter. Applies to every endpoint, including admin pages, but the
    // permit budget is sized for ingestion traffic (~120/min by default) so an operator clicking
    // around the dashboard doesn't trip it.
    rl.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = reportOptions.RateLimitPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    // Per-endpoint policy that the ingestion route maps opt into. Caps simultaneous multipart
    // parses + storage writes regardless of source IP.
    rl.AddPolicy("ingest-concurrency", context =>
    {
        var cfg = context.RequestServices.GetRequiredService<RSCReportServiceOptions>();
        return RateLimitPartition.GetConcurrencyLimiter("ingest", _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Max(1, cfg.IngestConcurrency),
            QueueLimit = Math.Max(0, cfg.IngestQueueLimit),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });

    rl.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "2";
        return ValueTask.CompletedTask;
    };
});

builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Report Ingestion Service",
        Version = "v1",
        Description = "Ingests and serves Report-a-Problem submissions from mobile SDKs. Protected by the apiKey header."
    });

    var apiKeyScheme = new OpenApiSecurityScheme
    {
        Name = "apiKey",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "Shared-secret API key sent in the apiKey request header."
    };
    c.AddSecurityDefinition("ApiKey", apiKeyScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
        }] = Array.Empty<string>()
    });

    var xmlPath = Path.Combine(AppContext.BaseDirectory, "ReportService.xml");
    if (File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AuthorizeFolder("/");
});

builder.Services.AddAntiforgery();

// Hardened Kestrel: same shape as the standalone ingestion. MaxRequestBodySize must accommodate
// the ingestion's gzip attachments (default 500 MiB).
builder.WebHost.ConfigureKestrel(k =>
    RSCHostingExtensions.ConfigureHardenedKestrel(k, maxRequestBodySize: reportOptions.MaxUploadBytes));

var app = builder.Build();

// Admin must have a strong operator secret in Production; otherwise anyone reaching /Login gets in
// (or is rejected with "AdminKey not configured", which is equally useless).
RSCSecretValidation.RequireInProduction(app.Environment, "Admin:AdminKey", adminOptions.AdminKey);
// Ingestion's apiKey is now also enforced here since the merged process owns those routes.
RSCSecretValidation.RequireInProduction(app.Environment, "ReportService:ApiKey", reportOptions.ApiKey);

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
RSCCrashHandler.Install(startupLogger, app.Lifetime);

app.Lifetime.ApplicationStopping.Register(() =>
    startupLogger.LogInformation("Shutting down report-admin"));
app.Lifetime.ApplicationStopped.Register(() =>
    startupLogger.LogInformation("Stopped"));

app.UseStandardForwardedHeaders(proxyHeaders);
app.UseCorrelationId();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    if (RSCHostingExtensions.HasHttpsEndpoint(builder.Configuration))
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }
    else
    {
        startupLogger.LogInformation(
            "No HTTPS endpoint detected; HSTS and HTTPS redirection are disabled. " +
            "Expect TLS to be terminated by an upstream reverse proxy, or keep the admin bound to loopback.");
    }
    app.UseExceptionHandler("/Error");
}

app.UseStandardSecurityHeaders();
app.Use(async (ctx, next) =>
{
    // Razor admin pages get the strict same-origin CSP. Swagger UI ships its own bundled inline
    // <script> + <style> blocks, so /docs/* and /swagger/* need 'unsafe-inline' or the page is
    // a blank screen. Ingestion JSON / health endpoints serve no HTML; the header is harmless
    // there. Path-prefix branching keeps the strict admin CSP unchanged.
    var path = ctx.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/docs", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        // Swagger UI ships inline <script>/<style>; the /ApiDocs Razor page frames /docs/ inside
        // an iframe, so frame-ancestors must allow same-origin (otherwise the browser refuses to
        // render the framed content).
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
            "script-src 'self' 'unsafe-inline'; form-action 'self'; frame-ancestors 'self'; base-uri 'none'";
    }
    else
    {
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
            "form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    }
    await next().ConfigureAwait(false);
});

app.UseStaticFiles();
app.UseRouting();
// Endpoint-attached rate-limit policies (e.g. "ingest-concurrency") need UseRouting to have
// matched the endpoint before UseRateLimiter runs.
app.UseRateLimiter();
app.UseAuthentication();

// Dev auto-sign-in: when explicitly enabled, every unauthenticated request is signed in as a
// synthetic "dev-operator" so the admin UI is reachable without typing the admin key. Gated on
// an explicit config flag (not just IsDevelopment) so test hosts that run with Development for
// the exception page keep seeing the real auth flow. The local docker-compose stack sets this
// flag, and pairs it with a `127.0.0.1:` host-port binding so the bypass is bounded by network
// isolation, not application auth.
if (adminOptions.DevAutoSignIn)
{
    startupLogger.LogWarning("Admin DevAutoSignIn is ENABLED. Every request is treated as 'dev-operator'.");
    app.Use(async (ctx, next) =>
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "dev-operator") },
                CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal).ConfigureAwait(false);
            ctx.User = principal;
        }
        await next().ConfigureAwait(false);
    });
}

app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// OpenAPI document + interactive UI, both anonymous (the spec is metadata, not data). The merged
// process generates the spec locally from its route table — the previous proxy from a separate
// ingestion container is gone with the second port.
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "docs";
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Report Ingestion Service v1");
    c.DocumentTitle = "Report Ingestion Service · API";
});

// Public health probes (anonymous). The merged process serves both ingestion clients and
// admin operators, so `/api/health` reports the same payload either side would expect.
app.MapGet("/api/health", (RSCServiceTelemetry telemetry, RSCReportServiceOptions opts) => Results.Ok(new
{
    status = "ok",
    environment = opts.Environment,
    startedAt = telemetry.StartedAt.UtcDateTime.ToString("O"),
    uptimeSeconds = telemetry.UptimeSeconds,
    version = telemetry.Version
}))
    .AllowAnonymous()
    .WithTags("Health")
    .WithName("GetHealth")
    .WithSummary("Liveness probe")
    .Produces(StatusCodes.Status200OK);

app.MapGet("/api/health/ready", (RSCServiceTelemetry telemetry, RSCReportServiceOptions opts) =>
{
    var probe = Path.Combine(opts.ReportsRoot, $".writecheck_{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(opts.ReportsRoot);
        using (var fs = File.Create(probe)) { fs.WriteByte(0x00); }
        File.Delete(probe);
        return Results.Ok(new
        {
            status = "ready",
            environment = opts.Environment,
            startedAt = telemetry.StartedAt.UtcDateTime.ToString("O"),
            uptimeSeconds = telemetry.UptimeSeconds,
            version = telemetry.Version
        });
    }
    catch (IOException) { return Results.Json(new { status = "not-ready", reason = "reports root not writable" }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (UnauthorizedAccessException) { return Results.Json(new { status = "not-ready", reason = "reports root not writable" }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { /* best-effort cleanup */ } }
})
    .AllowAnonymous()
    .WithTags("Health")
    .WithName("GetHealthReady")
    .WithSummary("Readiness probe")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status503ServiceUnavailable);

// SDK-facing ingestion routes. Mounted last so URL collisions with Razor pages would surface
// before this point — there are none today (Razor pages live at `/`, `/Reports`, etc.; ingestion
// uses `/partners/api/v2/...`, `/api/v1/...`, and `/api/v2/analytics/...`).
app.MapProblemReportEndpoints();
app.MapAnalyticsEndpoints();

// Admin-only NDJSON exports. The global authorization fallback policy gates these the same as
// the Razor pages — anonymous callers are bounced to /Login.
app.MapAdminAnalyticsExport();

app.MapRazorPages();

if (app.Environment.IsDevelopment())
{
    await ReportService.Admin.Services.RSAAnalyticsDevDataSeeder.SeedAsync(
        app.Services, startupLogger, CancellationToken.None);
}

app.Run();

// Marker type in a dedicated namespace so WebApplicationFactory<AdminProgram> can pick this
// assembly without clashing with the ingestion project's own `public partial class Program`.
namespace ReportService.Admin
{
    public class AdminProgram { }
}
