using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Verifies the JSON-part size cap bounds the per-request memory footprint regardless of
/// <c>MaxUploadBytes</c>. Oversized JSON — sent either as a file part or as a form field — must
/// be rejected with <c>413</c>.
/// </summary>
public class MaxJsonBytesTests
{
    [Fact]
    public async Task File_part_json_over_cap_returns_413()
    {
        await using var app = new IngestionAppFactory
        {
            Configure = baseline => baseline with { MaxJsonBytes = 512 }
        };
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        // Oversized JSON carried as a file part. Well-formed shape, just too big.
        var padding = new string('x', 1024);
        var oversized = $"{{\"platform\":\"Android\",\"message\":\"{padding}\"}}";

        using var form = new MultipartFormDataContent();
        var json = new StringContent(oversized, Encoding.UTF8, "application/json");
        form.Add(json, "json", "report.json");

        var res = await client.PostAsync("/partners/api/v2/report-problem", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task Form_field_json_over_cap_returns_413()
    {
        await using var app = new IngestionAppFactory
        {
            Configure = baseline => baseline with { MaxJsonBytes = 256 }
        };
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        var padding = new string('y', 512);
        var oversized = $"{{\"platform\":\"Android\",\"message\":\"{padding}\"}}";

        // Text form field carries the JSON this time — tests the fallback path.
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(oversized), "json");

        var res = await client.PostAsync("/partners/api/v2/report-problem", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task Json_under_cap_still_succeeds()
    {
        await using var app = new IngestionAppFactory
        {
            Configure = baseline => baseline with { MaxJsonBytes = 8192 }
        };
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        using var form = new MultipartFormDataContent();
        var json = new StringContent(
            "{\"platform\":\"Android\",\"message\":\"small\"}",
            Encoding.UTF8, "application/json");
        form.Add(json, "json", "report.json");

        var res = await client.PostAsync("/partners/api/v2/report-problem", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }
}
