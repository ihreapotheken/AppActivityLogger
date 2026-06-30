using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ReportService.Admin.Services;

/// <summary>
/// Razor Pages global filter that gates the optional "Submissions" feature areas behind the
/// build-time <see cref="RSCFeatureFlags"/>. When a page belongs to a feature that was compiled out
/// (e.g. <c>-p:FeatureAnalytics=false</c>), the request is redirected to <c>/FeatureUnavailable</c>,
/// which renders a "Not enabled — please contact your administrator" notice. Pages not owned by any
/// optional feature pass straight through.
/// </summary>
public sealed class RSAFeatureGateFilter : IAsyncPageFilter
{
    private static readonly HashSet<string> AnalyticsPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Analytics", "/AnalyticsEvents", "/AnalyticsSessions", "/AnalyticsSession",
        "/AnalyticsFunnels", "/AnalyticsRetention", "/AnalyticsHealth", "/AnalyticsSales",
    };

    private static readonly HashSet<string> ReportPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/ProblemReports", "/Report", "/Reports", "/Errors", "/ForcedReports",
    };

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var path = (context.ActionDescriptor as PageActionDescriptor)?.ViewEnginePath;
        string? feature =
            path is not null && AnalyticsPages.Contains(path) ? "Analytics" :
            path is not null && ReportPages.Contains(path) ? "ProblemReports" :
            null;

        if (feature is not null && !RSCFeatureFlags.IsEnabled(feature))
        {
            context.Result = new RedirectToPageResult("/FeatureUnavailable", new { feature });
            return;
        }

        await next().ConfigureAwait(false);
    }
}
