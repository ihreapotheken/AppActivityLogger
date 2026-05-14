using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Integration coverage for the public ingest + read surface. Each test spins up its own
/// <see cref="IngestionAppFactory"/> with an isolated temp directory and auth-abuse DB.
/// </summary>
public class IngestionEndpointTests
{
    private const string ValidJson =
        "{\"platform\":\"Android\",\"message\":\"Test\",\"pharmacyId\":\"DE-1\"}";

    private static MultipartFormDataContent BuildMultipart(string json, byte[]? attachment = null)
    {
        var form = new MultipartFormDataContent();
        var jsonPart = new StringContent(json, Encoding.UTF8, "application/json");
        form.Add(jsonPart, "json", "report.json");
        if (attachment is not null)
        {
            var filePart = new ByteArrayContent(attachment);
            filePart.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            form.Add(filePart, "file", "logs.log.gz");
        }
        return form;
    }

    private static byte[] GzipBytes(int payload = 32) =>
        new byte[] { 0x1F, 0x8B, 0x08, 0x00 }.Concat(new byte[payload]).ToArray();

    [Fact]
    public async Task Unauthenticated_request_gets_401()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        using var form = BuildMultipart(ValidJson, GzipBytes());
        var res = await client.PostAsync("/partners/api/v2/report-problem", form);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Authenticated_happy_path_returns_201()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        using var form = BuildMultipart(ValidJson, GzipBytes());
        var res = await client.PostAsync("/partners/api/v2/report-problem", form);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_json_field_is_rejected_as_400()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        // `extraField` is not part of RSCProblemReport — parser-differential defense must reject.
        var json = "{\"platform\":\"Android\",\"message\":\"Test\",\"extraField\":\"sneaky\"}";
        using var form = BuildMultipart(json);
        var res = await client.PostAsync("/partners/api/v2/report-problem", form);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Disallowed_platform_returns_400()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        var json = "{\"platform\":\"Windows\",\"message\":\"Test\"}";
        using var form = BuildMultipart(json);
        var res = await client.PostAsync("/partners/api/v2/report-problem", form);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Non_gzip_attachment_returns_400()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        // Valid JSON, but attachment lacks gzip magic.
        var attachment = Encoding.UTF8.GetBytes("definitely-not-gzip");
        using var form = BuildMultipart(ValidJson, attachment);
        var res = await client.PostAsync("/partners/api/v2/report-problem", form);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Non_multipart_request_returns_415()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        var body = new StringContent(ValidJson, Encoding.UTF8, "application/json");
        var res = await client.PostAsync("/partners/api/v2/report-problem", body);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_verb_on_defined_path_returns_405()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        // DELETE is not defined on the ingest path; expect 405, not 404.
        var res = await client.DeleteAsync("/partners/api/v2/report-problem");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    public async Task Incompatible_accept_header_returns_406()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        using var form = BuildMultipart(ValidJson, GzipBytes());
        var res = await client.PostAsync("/partners/api/v2/report-problem", form);

        Assert.Equal(HttpStatusCode.NotAcceptable, res.StatusCode);
    }

    [Fact]
    public async Task Wildcard_accept_is_allowed()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        using var form = BuildMultipart(ValidJson, GzipBytes());
        var res = await client.PostAsync("/partners/api/v2/report-problem", form);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_from_request_header()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        var id = "test-corr-" + Guid.NewGuid().ToString("N");
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        req.Headers.Add("X-Correlation-ID", id);
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.Equal(id, values!.Single());
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_missing()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();

        var res = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.False(string.IsNullOrWhiteSpace(values!.Single()));
    }

    [Fact]
    public async Task Identical_uploads_produce_the_same_filename_idempotency()
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        using var form1 = BuildMultipart(ValidJson);
        using var form2 = BuildMultipart(ValidJson);

        var r1 = await client.PostAsync("/partners/api/v2/report-problem", form1);
        var r2 = await client.PostAsync("/partners/api/v2/report-problem", form2);

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);

        // RSCStoredReport bodies encode the content-hash-derived file name. Identical content must
        // yield identical file names within the same wall-clock second.
        var b1 = await r1.Content.ReadAsStringAsync();
        var b2 = await r2.Content.ReadAsStringAsync();
        var name1 = ExtractFileName(b1);
        var name2 = ExtractFileName(b2);
        Assert.EndsWith(name1.Substring(name1.LastIndexOf('_')), name2);
    }

    private static string ExtractFileName(string json)
    {
        const string marker = "\"fileName\":\"";
        var start = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = json.IndexOf('"', start);
        return json.Substring(start, end - start);
    }
}
