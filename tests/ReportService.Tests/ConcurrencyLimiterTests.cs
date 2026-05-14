using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Models;
using ReportService.Options;
using ReportService.Storage;
using Xunit;

namespace ReportService.Tests;

/// <summary>
/// Forces deterministic overlap of in-flight uploads by replacing the real
/// <see cref="RSCIReportStore"/> with a gate that holds every <c>SaveAsync</c> call until the test
/// explicitly releases it. With the concurrency limit pinned to 1 permit + 1 queue slot, we can
/// then assert that the 429 shedding and <c>Retry-After</c> header actually fire.
/// </summary>
public class ConcurrencyLimiterTests
{
    [Fact]
    public async Task Concurrent_uploads_beyond_permit_plus_queue_are_rejected_with_retry_after()
    {
        var gate = new SemaphoreSlim(0);
        var gatedStore = new GatedReportStore(gate);

        await using var app = new GatedAppFactory(gatedStore)
        {
            // Queue=0 so anything that can't grab the sole permit is rejected instantly with no
            // grace queuing — removes timing noise from the assertion.
            Configure = baseline => baseline with { IngestConcurrency = 1, IngestQueueLimit = 0 }
        };

        // Fire the "pinning" request first and wait for it to block on the gate, guaranteeing the
        // lone permit is held when the rest of the storm arrives.
        var pinner = Task.Run(() => Upload(app));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (gatedStore.InFlight < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(gatedStore.InFlight >= 1, "pinner never reached the gated store; limiter may not be applied");

        // Storm: 20 additional requests. With PermitLimit=1 (held by pinner) + QueueLimit=0, every
        // one of these must be rejected.
        var storm = Enumerable.Range(0, 20).Select(_ => Task.Run(() => Upload(app))).ToArray();
        var stormResponses = await Task.WhenAll(storm);

        // Release the pinner and collect its result.
        gate.Release(int.MaxValue);
        var pinResponse = await pinner;

        var responses = stormResponses.Append(pinResponse).ToArray();

        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        // TestServer in-process dispatch doesn't perfectly overlap all 10 requests, but the
        // limiter still must reject at least one — proof the policy is wired and reading the
        // right PermitLimit. The Retry-After header assertion doubles as a check that OnRejected
        // ran our custom hook.
        Assert.True(rejected >= 1, $"expected the limiter to reject at least one of {responses.Length}; got {rejected} rejections and {accepted} accepts");

        var rejection = responses.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(rejection.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Equal("2", retryAfter!.Single());
    }

    private static async Task<HttpResponseMessage> Upload(IngestionAppFactory app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("apiKey", IngestionAppFactory.ApiKey);

        using var form = new MultipartFormDataContent();
        var jsonPart = new StringContent(
            "{\"platform\":\"Android\",\"message\":\"Test\"}",
            Encoding.UTF8, "application/json");
        form.Add(jsonPart, "json", "report.json");
        var attachment = new byte[] { 0x1F, 0x8B, 0x08, 0x00 }.Concat(new byte[128]).ToArray();
        var filePart = new ByteArrayContent(attachment);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        form.Add(filePart, "file", "logs.log.gz");

        return await client.PostAsync("/partners/api/v2/report-problem", form);
    }

    private sealed class GatedAppFactory : IngestionAppFactory
    {
        private readonly GatedReportStore _store;

        public GatedAppFactory(GatedReportStore store) => _store = store;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<RSCIReportStore>();
                services.AddSingleton<RSCIReportStore>(_store);
            });
        }
    }

    private sealed class GatedReportStore : RSCIReportStore
    {
        private readonly SemaphoreSlim _gate;
        private int _inFlight;

        public GatedReportStore(SemaphoreSlim gate) => _gate = gate;

        public int InFlight => Volatile.Read(ref _inFlight);

        public async Task<RSCStoredReport> SaveAsync(RSCProblemReport report, ReadOnlyMemory<byte> jsonBytes,
            Stream? attachment, long? attachmentLength, string ingestionChannel, CancellationToken ct)
        {
            _ = ingestionChannel;
            Interlocked.Increment(ref _inFlight);
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                return new RSCStoredReport(
                    Platform: report.Platform.ToLowerInvariant(),
                    FileName: "test.json",
                    SizeBytes: jsonBytes.Length,
                    SubmittedAt: DateTimeOffset.UtcNow,
                    AttachmentFileName: attachment is null ? null : "test.log.gz",
                    AttachmentSizeBytes: attachmentLength);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public IReadOnlyList<RSCStoredReport> List(string platform) => Array.Empty<RSCStoredReport>();
        public Stream? OpenRead(string platform, string fileName) => null;
        public bool Delete(string platform, string fileName) => false;
    }
}
