using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using ReportService.Analytics;
using ReportService.Audit;
using ReportService.DeepLinks;
using ReportService.Endpoints;
using ReportService.Hosting;
using ReportService.Ingestion;
using ReportService.Observability;
using ReportService.Options;
using ReportService.Security;
using ReportService.Storage;
using ReportService.Storage.ApiKeys;
using ReportService.Storage.Catalog;
using ReportService.Storage.Retention;
using ReportService.Validation;

var builder = WebApplication.CreateBuilder(args);

// Translate short, operator-friendly env vars (REPORT_RETENTION_MAX_MB, etc.) into the
// canonical ReportService:* / Admin:* keys. Mounted after the default providers so
// smart-unit values win over anything set on the legacy ReportService__* form.
RSCSmartUnitConfig.Apply(builder.Configuration);

var options = builder.Configuration
    .GetSection(RSCReportServiceOptions.SectionName)
    .Get<RSCReportServiceOptions>() ?? new RSCReportServiceOptions();

var proxyHeaders = builder.Configuration
    .GetSection(RSCProxyHeadersOptions.SectionName)
    .Get<RSCProxyHeadersOptions>() ?? new RSCProxyHeadersOptions();

var analyticsOptions = builder.Configuration
    .GetSection(RSCAnalyticsOptions.SectionName)
    .Get<RSCAnalyticsOptions>() ?? new RSCAnalyticsOptions();

var deepLinkOptions = builder.Configuration
    .GetSection(RSCDeepLinkOptions.SectionName)
    .Get<RSCDeepLinkOptions>() ?? new RSCDeepLinkOptions();

var catalogOptions = builder.Configuration
    .GetSection(RSCCatalogOptions.SectionName)
    .Get<RSCCatalogOptions>() ?? new RSCCatalogOptions();

var fanoutOptions = builder.Configuration
    .GetSection(RSCAnalyticsFanoutOptions.SectionName)
    .Get<RSCAnalyticsFanoutOptions>() ?? new RSCAnalyticsFanoutOptions();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(proxyHeaders);
builder.Services.AddSingleton(analyticsOptions);
builder.Services.AddSingleton(deepLinkOptions);
builder.Services.AddSingleton(catalogOptions);
builder.Services.AddSingleton(fanoutOptions);

builder.WebHost.ConfigureKestrel(k => RSCHostingExtensions.ConfigureHardenedKestrel(k, maxRequestBodySize: options.MaxUploadBytes));

builder.Services.AddSingleton<RSCReportValidator>();
builder.Services.AddSingleton<RSCComponentHealth>();
if (options.Storage == "SqliteIndex")
{
    builder.Services.AddSingleton<RSCFileSystemReportStore>();
    // Lazy-construct the SQLite index inside a resilient shell so an unopenable DB (read-only
    // FS, missing disk, corrupt file) doesn't crash ingestion. Kept for any direct RSCIReportIndex
    // consumer; the active report STORE is the per-app fan-out below.
    builder.Services.AddSingleton<RSCResilientReportIndex>(sp =>
        new RSCResilientReportIndex(
            () => new RSCSqliteReportIndex(sp.GetRequiredService<RSCReportServiceOptions>(),
                                         sp.GetRequiredService<ILogger<RSCSqliteReportIndex>>()),
            sp.GetRequiredService<RSCComponentHealth>(),
            sp.GetRequiredService<ILogger<RSCResilientReportIndex>>()));
    builder.Services.AddSingleton<RSCIReportIndex>(sp => sp.GetRequiredService<RSCResilientReportIndex>());
}
// Database-per-app problem reports (both storage modes): each (client, app) owns its own report
// tree + reports.db; the fan-out store routes writes per-app and merges reads across apps.
builder.Services.AddSingleton<RSCIReportStoreFactory, RSCReportStoreFactory>();
builder.Services.AddSingleton<RSCIReportStore, RSCFanOutReportStore>();
builder.Services.AddSingleton<RSReportIngestionService>();
builder.Services.AddSingleton<RSCIForcedReportStore, RSCSqliteForcedReportStore>();

// Tenancy catalog (apps + environments + clients). Its own SQLite DB so it works under a read-only
// content root regardless of Storage mode; the ingestion validator resolves it for attribution
// validation, the admin UI manages it. Self-seeds the default app/env/client + any config seeds.
builder.Services.AddSingleton<RSCICatalog, RSCSqliteCatalog>();

// v2 analytics pipeline. Enabled by default; toggled off via Analytics:Enabled=false.
builder.Services.AddSingleton<RSCAnalyticsIdentifierHasher>();
builder.Services.AddSingleton<RSCAnalyticsValidator>();
// Database-per-app: ingestion + workers resolve a specific app's store via the factory; the
// RSCIAnalyticsStore singleton is the fan-out facade the dashboards/exports read through (delegates
// to one app when scoped, merges across apps when not).
builder.Services.AddSingleton<RSCIAnalyticsStoreFactory, RSCSqliteAnalyticsStoreFactory>();
builder.Services.AddSingleton<RSCIAnalyticsStore, RSCFanOutAnalyticsStore>();
builder.Services.AddSingleton<RSAnalyticsIngestionService>();

// Deferred deep linking. Own SQLite DB under ReportsRoot; serves the click-capture + match routes.
// The retention worker purges the captured click stream on the configured schedule.
builder.Services.AddSingleton<RSCIDeferredDeepLinkStore, RSCSqliteDeferredDeepLinkStore>();
builder.Services.AddHostedService<RSCDeepLinkClickRetentionWorker>();

if (analyticsOptions.Enabled)
{
    builder.Services.AddHostedService<RSCAnalyticsAggregationWorker>();
    builder.Services.AddHostedService<RSCAnalyticsRetentionWorker>();
    builder.Services.AddHostedService<RSCAnalyticsCohortWorker>();
    builder.Services.AddHostedService<RSCAnalyticsFunnelWorker>();
}
builder.Services.AddSingleton<RSCServiceTelemetry>();
builder.Services.AddSingleton<RSAcceptHeaderFilter>();
builder.Services.AddSingleton<RSCIAuditLog, RSCSqliteAuditLog>();
builder.Services.AddSingleton<RSCIDiskSpaceProbe, RSCDriveInfoDiskSpaceProbe>();
builder.Services.AddSingleton<RSCRetentionService>();
builder.Services.AddHostedService<RSCRetentionBackgroundService>();
builder.Services.AddSingleton<RSCIAuthAbuseTracker>(sp => new RSCResilientAuthAbuseTracker(
    () => new RSCSqliteAuthAbuseTracker(sp.GetRequiredService<RSCReportServiceOptions>(),
                                      sp.GetRequiredService<ILogger<RSCSqliteAuthAbuseTracker>>()),
    sp.GetRequiredService<RSCReportServiceOptions>(),
    sp.GetRequiredService<RSCComponentHealth>(),
    sp.GetRequiredService<ILogger<RSCResilientAuthAbuseTracker>>()));

// Managed API-key store (admin/user keys, expiry, revocation). The auth handler + rate limiter
// resolve keys through it; its in-memory cache keeps the hot path off the DB.
builder.Services.AddSingleton<RSCIApiKeyStore, RSCSqliteApiKeyStore>();

builder.Services
    .AddAuthentication(RSApiKeyAuthenticationOptions.Scheme)
    .AddScheme<RSApiKeyAuthenticationOptions, RSApiKeyAuthenticationHandler>(
        RSApiKeyAuthenticationOptions.Scheme, _ => { });

builder.Services.AddAuthorization(o =>
{
    // Named policy referenced by `RSEndpointConventions.Apply(RequireAuth)`. Pinning the auth
    // scheme by name here means the same endpoint code works whether the host's default scheme
    // is ApiKey (standalone ingestion) or Cookie (merged admin process).
    o.AddPolicy(RSEndpointConventions.ApiKeyPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(RSApiKeyAuthenticationOptions.Scheme);
        policy.RequireAuthenticatedUser();
    });
    // Admin-only key management: same scheme, plus the admin role claim.
    o.AddPolicy(RSEndpointConventions.ApiKeyAdminPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(RSApiKeyAuthenticationOptions.Scheme);
        policy.RequireAuthenticatedUser();
        policy.RequireRole(RSCApiKeyRoles.Admin);
    });
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = options.MaxUploadBytes;
    // Bound a single non-file form value (the inline `json` part) at MaxJsonBytes so the framework
    // rejects an oversized JSON field during multipart parse, instead of buffering up to a generic
    // 16 MiB. The gzip attachment is a file part, governed by MultipartBodyLengthLimit above.
    o.ValueLengthLimit = (int)Math.Clamp(options.MaxJsonBytes, 1, int.MaxValue);
    o.MemoryBufferThreshold = 64 * 1024;
});

builder.Services.AddRateLimiter(rl =>
{
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Per-key sliding-window limit — partitions by the resolved API key when present (each key gets
    // its own budget), else by source IP. Sliding window removes the boundary burst of a fixed one.
    // See RSCApiKeyRateLimiter for the partition logic shared with the merged admin host.
    rl.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(RSCApiKeyRateLimiter.Partition);

    // Global concurrency cap for the write path — independent of source IP, so a distributed
    // DoS spread across many IPs still cannot spawn more than IngestConcurrency simultaneous
    // multipart parses + storage writes. Permits are tied to request lifetime: a client
    // disconnect releases the permit immediately, even mid-queue.
    //
    // Implemented via AddPolicy with a factory that resolves RSCReportServiceOptions from the
    // per-request service provider on first use. This lets test hosts swap the options singleton
    // via ConfigureTestServices and have their replacement flow through into the live limiter.
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

    // Advise well-behaved clients to back off briefly on any rejection. Keeps the rejection
    // status at 429 (consistent with the per-IP limiter) but tells the caller to try again.
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
        Description = """
            Ingests and serves Report-a-Problem submissions and analytics events from the Android/iOS
            IA SDKs and trusted backends.

            **Auth** — every ingestion route requires the `apiKey` request header. `/api/health`,
            `/api/health/ready`, and `/healthz` are anonymous. Every route (anonymous included) is
            subject to the global per-IP rate limiter.

            **Errors** — failure bodies follow RFC 7807 (`application/problem+json`) with a `traceId`;
            exception details are never echoed.
            """
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

var app = builder.Build();

// Refuse to start in Production with a missing or obviously-weak shared secret. Short secrets are
// tolerated in Development so integration tests can run with fixture values.
RSCSecretValidation.RequireInProduction(app.Environment, "ReportService:ApiKey", options.ApiKey);
// The identifier-hash pepper keys the SHA-256 of every anonymousId. Empty in Production silently
// degrades the stored anonymous_id_hash to a bare, rainbow-table-reversible SHA-256(raw id),
// defeating the "never store raw identifiers" guarantee. Fail fast so a missing pepper refuses to
// boot a Production analytics pipeline.
if (analyticsOptions.Enabled)
{
    RSCSecretValidation.RequireInProduction(app.Environment, "Analytics:IdentifierHashPepper", analyticsOptions.IdentifierHashPepper);
    // Fail fast on a mis-set analytics numeric/schema invariant (inverted schema range, non-positive
    // caps, negative retention) rather than silently dead-lettering every batch at runtime.
    var analyticsConfigErrors = analyticsOptions.Validate();
    if (analyticsConfigErrors.Count > 0)
        throw new InvalidOperationException("Invalid Analytics configuration: " + string.Join(" ", analyticsConfigErrors));
}

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
RSCCrashHandler.Install(startupLogger, app.Lifetime);

app.Lifetime.ApplicationStopping.Register(() =>
    startupLogger.LogInformation("Shutting down report-service (ingestion will stop accepting new requests)"));
app.Lifetime.ApplicationStopped.Register(() =>
    startupLogger.LogInformation("Stopped"));

app.UseStandardForwardedHeaders(proxyHeaders);

// Establish the correlation id as early as possible so every subsequent log line (including the
// exception handler below) carries it in its logger scope and the client echoes it back to us.
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
            "Expect TLS to be terminated by an upstream reverse proxy.");
    }
}

app.UseExceptionHandler(errApp => errApp.Run(async context =>
{
    var feature = context.Features.Get<IExceptionHandlerFeature>();
    var error = feature?.Error;

    // Client canceled (disconnect, navigation away): the socket is gone, so writing a
    // response would just fail. Log and return without touching the response.
    if (error is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
    {
        var cancelLogger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        cancelLogger.LogInformation(
            "Client canceled request (traceId={TraceId}, path={Path})",
            context.TraceIdentifier,
            context.Request.Path.Value);
        return;
    }

    var (status, title) = error switch
    {
        InvalidDataException => (StatusCodes.Status413PayloadTooLarge, "Payload too large"),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, "Bad request"),
        // Server-side cancellation (client still connected, but our IngestTimeoutSeconds fired):
        // return 503 so the client knows to retry rather than interpret a 500 as a persistent bug.
        OperationCanceledException => (StatusCodes.Status503ServiceUnavailable, "Request timed out"),
        _ => (StatusCodes.Status500InternalServerError, "Internal server error")
    };

    var exLogger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    exLogger.LogError(
        error,
        "Unhandled request exception (traceId={TraceId}, path={Path}, status={Status})",
        context.TraceIdentifier,
        context.Request.Path.Value,
        status);

    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json";

    // RFC 7807 body; never echo exception details, headers, or body to the caller.
    var problem = new
    {
        type = $"https://httpstatuses.io/{status}",
        title,
        status,
        traceId = context.TraceIdentifier,
        instance = context.Request.Path.Value ?? string.Empty
    };
    await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
}));

app.UseStandardSecurityHeaders();

// Endpoint-attached rate limit policies (like "ingest-concurrency") are resolved by reading
// endpoint metadata — which requires UseRouting to have matched the endpoint before UseRateLimiter
// runs. Explicit ordering is safer than leaving it to WebApplication's implicit insertion.
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// API docs are public metadata, not sensitive data. The OpenAPI document only describes the
// route surface and which scheme protects it; it never echoes secrets, stored payloads, or
// configuration. Gating it behind the same apiKey the operator uses for ingestion just made
// the docs unreadable for anyone trying to integrate. The actual ingestion endpoints stay
// behind `RequireAuthorization` regardless.
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Report Ingestion Service v1");
    c.DocumentTitle = "Report Ingestion Service - API";
});

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
    .WithDescription("Returns 200 OK with service uptime, start time, and informational version. Anonymous; used by orchestrators.")
    .Produces(StatusCodes.Status200OK);

app.MapGet("/api/health/ready", (RSCServiceTelemetry telemetry, RSCReportServiceOptions opts) =>
    {
        var probe = Path.Combine(opts.ReportsRoot, $".writecheck_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(opts.ReportsRoot);
            using (var fs = File.Create(probe))
            {
                fs.WriteByte(0x00);
            }
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
        catch (IOException)
        {
            return Results.Json(new
            {
                status = "not-ready",
                reason = "reports root not writable"
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Json(new
            {
                status = "not-ready",
                reason = "reports root not writable"
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* best-effort cleanup */ }
        }
    })
    .AllowAnonymous()
    .WithTags("Health")
    .WithName("GetHealthReady")
    .WithSummary("Readiness probe")
    .WithDescription("Verifies the reports root directory is writable by creating, writing, and deleting a probe file. Anonymous.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status503ServiceUnavailable);

app.MapProblemReportEndpoints();
app.MapAnalyticsEndpoints();
app.MapDeepLinkEndpoints();
app.MapApiKeyManagementEndpoints();
app.MapAppManagementEndpoints();

app.Run();

// Exposed so WebApplicationFactory<Program> in the test project can host the app in-process.
public partial class Program { }

// Distinct, namespaced marker for the test host's IngestionAppFactory. The Admin project is
// InternalsVisibleTo the test project, which exposes ITS compiler-generated global `Program`
// (both hosts use top-level statements) and makes a bare `WebApplicationFactory<Program>`
// ambiguous. Mirrors ReportService.Admin.AdminProgram so each factory targets its own assembly
// by a unique type name.
namespace ReportService
{
    public sealed class IngestionProgram { }
}
