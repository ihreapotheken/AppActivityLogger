using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Admin;
using ReportService.Admin.Options;
using ReportService.Audit;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Smoke coverage for the CMS admin pages: authentication required, rendering, maintenance actions
/// require authentication + audit trails.
/// </summary>
public class AdminCmsTests
{
    private const string AdminKey = "admin-cms-0123456789-abcdefghij";

    [Theory]
    [InlineData("/")]
    [InlineData("/ProblemReports")]
    [InlineData("/Status")]
    [InlineData("/Maintenance")]
    [InlineData("/Audit")]
    public async Task Admin_pages_require_authentication(string path)
    {
        await using var app = NewFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Login", res.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_and_status_and_audit_pages_render_after_login()
    {
        await using var app = NewFactory();
        var client = await AuthenticatedClient(app);

        var dash = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dash.StatusCode);
        var dashHtml = await dash.Content.ReadAsStringAsync();
        Assert.Contains("Dashboard", dashHtml);
        Assert.Contains("Problem reports", dashHtml);
        Assert.Contains("Health", dashHtml);

        var status = await client.GetAsync("/Status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains("Reports root", await status.Content.ReadAsStringAsync());

        var audit = await client.GetAsync("/Audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);

        var reports = await client.GetAsync("/ProblemReports");
        Assert.Equal(HttpStatusCode.OK, reports.StatusCode);
    }

    [Fact]
    public async Task ProblemReports_page_filter_platform_produces_rows_when_data_present()
    {
        var factory = NewFactory();
        await using var app = factory;

        // Seed one report through the real RSCIReportStore.
        using (var scope = app.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<RSCIReportStore>();
            await store.SaveAsync(new RSCProblemReport(
                Platform: "android", Message: "cms-list",
                Title: null, DeviceModel: null, Email: null, PhoneNumber: null, Phone: null,
                PharmacyId: null, Source: null, AppVersion: null, FunctionalityImportance: null, Labels: null),
                new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("{\"platform\":\"android\",\"message\":\"cms-list\"}")),
                null, null, default);
        }

        var client = await AuthenticatedClient(app);
        var res = await client.GetAsync("/ProblemReports?platform=android");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("problem-report_", html);
    }

    [Fact]
    public async Task Maintenance_post_handlers_require_authentication_and_antiforgery()
    {
        await using var app = NewFactory();
        var anon = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Anonymous POST → should be rejected. Cookie auth + antiforgery middleware both refuse.
        using var form = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
        var res = await anon.PostAsync("/Maintenance?handler=Rebuild", form);
        Assert.True(res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized,
            $"expected redirect/400/401 for anonymous rebuild; got {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Authenticated_maintenance_actions_are_audited()
    {
        await using var app = NewFactory();
        var client = await AuthenticatedClient(app);

        var maintenanceHtml = await (await client.GetAsync("/Maintenance")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgery(maintenanceHtml);
        Assert.False(string.IsNullOrEmpty(token));

        using var post = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token!)
        });
        var res = await client.PostAsync("/Maintenance?handler=Rebuild", post);
        Assert.True(res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK);

        using var scope = app.Services.CreateScope();
        var auditLog = scope.ServiceProvider.GetRequiredService<RSCIAuditLog>();
        var entries = await auditLog.RecentAsync(20, default);
        Assert.Contains(entries, e => e.Action == "index.rebuild");
        Assert.Contains(entries, e => e.Action == "admin.login" && e.Success);
    }

    [Fact]
    public async Task Delete_records_an_audit_entry()
    {
        await using var app = NewFactory();

        // Seed a report to delete.
        string fileName;
        using (var scope = app.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<RSCIReportStore>();
            var saved = await store.SaveAsync(new RSCProblemReport(
                Platform: "android", Message: "delete-me",
                Title: null, DeviceModel: null, Email: null, PhoneNumber: null, Phone: null,
                PharmacyId: null, Source: null, AppVersion: null, FunctionalityImportance: null, Labels: null),
                new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("{\"platform\":\"android\",\"message\":\"delete-me\"}")),
                null, null, default);
            fileName = saved.FileName;
        }

        var client = await AuthenticatedClient(app);
        var reportHtml = await (await client.GetAsync($"/Report/android/{fileName}")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgery(reportHtml);
        Assert.False(string.IsNullOrEmpty(token));

        using var post = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("platform", "android"),
            new KeyValuePair<string, string>("fileName", fileName),
            new KeyValuePair<string, string>("__RequestVerificationToken", token!)
        });
        var res = await client.PostAsync($"/Report/android/{fileName}?handler=Delete", post);
        Assert.True(res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK);

        using var scope2 = app.Services.CreateScope();
        var auditLog = scope2.ServiceProvider.GetRequiredService<RSCIAuditLog>();
        var entries = await auditLog.RecentAsync(20, default);
        Assert.Contains(entries, e => e.Action == "report.delete" && e.Target!.EndsWith(fileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Toggling_a_deep_link_redirects_and_does_not_500()
    {
        // Regression: the DeepLinks redirect helpers passed `page` (the pager number) as a route
        // value, but `page` is a RESERVED Razor Pages route key (the page-path slot). RedirectToPage
        // then threw "No page named '' matches the supplied values" (HTTP 500) on every
        // save/toggle/delete. The fix builds a plain local-URL redirect instead.
        await using var app = NewFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var loginToken = ExtractAntiforgery(await client.GetStringAsync("/Login"));
        using (var login = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Key", AdminKey),
            new KeyValuePair<string, string>("__RequestVerificationToken", loginToken!),
        }))
        {
            var lr = await client.PostAsync("/Login", login);
            Assert.Equal(HttpStatusCode.Redirect, lr.StatusCode);
        }

        var token = ExtractAntiforgery(await client.GetStringAsync("/DeepLinks"));
        Assert.False(string.IsNullOrEmpty(token));

        // The slug need not exist — toggling still runs the shared redirect helper.
        using var toggle = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("slug", "regression-probe"),
            new KeyValuePair<string, string>("enabled", "true"),
            new KeyValuePair<string, string>("__RequestVerificationToken", token!),
        });
        var res = await client.PostAsync("/DeepLinks?handler=Toggle", toggle);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode); // 302, not 500
        Assert.Contains("/DeepLinks", res.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // -------- plumbing ---------------------------------------------------------------------

    private static async Task<HttpClient> AuthenticatedClient(AdminCmsFactory app)
    {
        var client = app.CreateClient();
        var loginPage = await client.GetStringAsync("/Login");
        var token = ExtractAntiforgery(loginPage);
        Assert.False(string.IsNullOrEmpty(token));
        using var post = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Key", AdminKey),
            new KeyValuePair<string, string>("__RequestVerificationToken", token!)
        });
        var res = await client.PostAsync("/Login", post);
        Assert.Contains(res.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Redirect });
        return client;
    }

    private static string? ExtractAntiforgery(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\"";
        var idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var valueMarker = "value=\"";
        var start = html.IndexOf(valueMarker, idx, StringComparison.Ordinal) + valueMarker.Length;
        var end = html.IndexOf('"', start);
        return html.Substring(start, end - start);
    }

    private static AdminCmsFactory NewFactory() => new();

    private sealed class AdminCmsFactory : WebApplicationFactory<AdminProgram>
    {
        public string ReportsRoot { get; } = Path.Combine(Path.GetTempPath(), $"rs-cms-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);

            // Feed the real Storage=SqliteIndex configuration path *before* Program.cs reads it —
            // that way the SQLite wiring (RSCFileSystemReportStore + RSCResilientReportIndex +
            // RSCSqliteIndexingReportStore) gets registered, exactly as it would in production.
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReportService:Storage"] = "SqliteIndex",
                    ["ReportService:ReportsRoot"] = ReportsRoot,
                    ["ReportService:SqliteDbPath"] = "reports.db",
                    ["ReportService:AuthAbuseDbPath"] = "auth-abuse.db",
                    ["ReportService:AuditDbPath"] = "audit.db",
                    ["ReportService:BackupRoot"] = "backups",
                    ["Admin:AdminKey"] = AdminKey,
                    ["Admin:SessionMinutes"] = "60"
                });
            });

            // Also swap the RSCReportServiceOptions singleton for anything that resolves it lazily.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<RSCReportServiceOptions>();
                services.AddSingleton(new RSCReportServiceOptions
                {
                    ApiKey = "ignored-in-admin-tests-but-long-enough-for-ctor",
                    ReportsRoot = ReportsRoot,
                    AuthAbuseDbPath = "auth-abuse.db",
                    SqliteDbPath = "reports.db",
                    AuditDbPath = "audit.db",
                    BackupRoot = "backups",
                    Storage = "SqliteIndex"
                });
                services.RemoveAll<RSAAdminOptions>();
                services.AddSingleton(new RSAAdminOptions { AdminKey = AdminKey, SessionMinutes = 60 });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            try { Directory.Delete(ReportsRoot, recursive: true); } catch { }
        }
    }
}
