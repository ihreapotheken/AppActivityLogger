using System.Net;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Exercises the download endpoint with a handful of traversal-style file names. <see cref="Security.RSCSafePath"/>
/// is the sole guard between the URL segment and the filesystem call; each of these must come back as a
/// clean 404 rather than serving something outside the reports root.
/// </summary>
public class PathTraversalTests
{
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("foo/../../etc/passwd")]
    [InlineData(".ssh/id_rsa")]
    public async Task Traversal_filenames_return_404(string fileName)
    {
        await using var app = new IngestionAppFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        var res = await client.GetAsync($"/api/problem-reports/android/{fileName}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
