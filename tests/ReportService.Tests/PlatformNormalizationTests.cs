using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Operators configure <c>AllowedPlatforms</c> in various cases — <c>["Android","iOS"]</c> is a
/// natural thing to write. Those values must be normalised at startup so requests that canonicalize
/// to lowercase still match.
/// </summary>
public class PlatformNormalizationTests
{
    [Fact]
    public async Task Mixed_case_AllowedPlatforms_still_accepts_lowercase_submissions()
    {
        await using var app = new IngestionAppFactory
        {
            Configure = baseline => baseline with { AllowedPlatforms = new[] { "Android", "iOS" } }
        };
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        using var form = new MultipartFormDataContent();
        var json = new StringContent(
            "{\"platform\":\"Android\",\"message\":\"Test\"}",
            Encoding.UTF8, "application/json");
        form.Add(json, "json", "report.json");

        var res = await client.PostAsync("/partners/api/v2/report-problem", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        // And the read endpoint resolves the same platform bucket.
        var list = await client.GetAsync("/api/problem-reports/android");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }
}
