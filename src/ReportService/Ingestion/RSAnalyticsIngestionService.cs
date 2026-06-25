using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;

namespace ReportService.Ingestion;

/// <summary>
/// Accepts a single JSON batch on <c>POST /api/v2/analytics/events</c>. Reads the body, parses,
/// validates, hashes identifiers, and hands the result to <see cref="RSCIAnalyticsStore"/> for
/// persistence + dead-lettering inside one transaction.
/// </summary>
public sealed class RSAnalyticsIngestionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
        // Reject unknown fields at the envelope/event/item level the same way the report
        // ingestion does — keeps schema drift loud rather than silent. Property bag values are
        // free-form (Dictionary<string,string>), so they're unaffected by this setting.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly RSCIAnalyticsStore _store;
    private readonly RSCAnalyticsValidator _validator;
    private readonly RSCAnalyticsIdentifierHasher _hasher;
    private readonly RSCReportServiceOptions _reportOptions;
    private readonly RSCAnalyticsOptions _analyticsOptions;
    private readonly ILogger<RSAnalyticsIngestionService> _logger;

    public RSAnalyticsIngestionService(
        RSCIAnalyticsStore store,
        RSCAnalyticsValidator validator,
        RSCAnalyticsIdentifierHasher hasher,
        RSCReportServiceOptions reportOptions,
        RSCAnalyticsOptions analyticsOptions,
        ILogger<RSAnalyticsIngestionService> logger)
    {
        _store = store;
        _validator = validator;
        _hasher = hasher;
        _reportOptions = reportOptions;
        _analyticsOptions = analyticsOptions;
        _logger = logger;
    }

    public async Task<RSAnalyticsIngestionResult> IngestAsync(HttpRequest request, CancellationToken ct)
    {
        if (!_analyticsOptions.Enabled)
            return RSAnalyticsIngestionResult.ServiceUnavailable("analytics ingestion is disabled");

        var contentType = request.ContentType ?? string.Empty;
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return RSAnalyticsIngestionResult.UnsupportedMediaType("application/json required");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _reportOptions.IngestTimeoutSeconds)));
        ct = timeoutCts.Token;

        // Body cap reuses MaxJsonBytes — analytics batches are JSON only, with no attachment, so
        // sharing the existing cap keeps operator-facing config a single number. The
        // MaxEventsPerBatch validator check catches "legal-size-but-too-many-events" cases.
        var maxJsonBytes = Math.Max(1, _reportOptions.MaxJsonBytes);
        if (request.ContentLength is { } cl && cl > maxJsonBytes)
            return RSAnalyticsIngestionResult.PayloadTooLarge("body exceeds MaxJsonBytes");

        byte[] body;
        try
        {
            body = await ReadBoundedAsync(request.Body, maxJsonBytes, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return RSAnalyticsIngestionResult.PayloadTooLarge("body exceeds MaxJsonBytes");
        }

        if (body.Length == 0)
            return RSAnalyticsIngestionResult.BadRequest("empty request body");

        RSCAnalyticsBatch? batch;
        try
        {
            batch = JsonSerializer.Deserialize<RSCAnalyticsBatch>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Rejected malformed analytics batch: {Reason}", ex.Message);
            return RSAnalyticsIngestionResult.BadRequest("invalid JSON payload");
        }

        if (batch is null || string.IsNullOrWhiteSpace(batch.BatchId))
            return RSAnalyticsIngestionResult.BadRequest("missing batchId");

        var receivedAt = DateTimeOffset.UtcNow;
        var verdict = _validator.Validate(batch, receivedAt);

        var anonHash = _hasher.Hash(batch.AnonymousId);
        var clientHash = _hasher.Hash(batch.ClientId);

        var receipt = await _store.WriteBatchAsync(batch, anonHash, clientHash, verdict, receivedAt, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Ingested analytics batch id={BatchId} platform={Platform} accepted={Accepted} rejected={Rejected} dup={Dup} batchRejected={BatchRejected}",
            receipt.BatchId, batch.Platform, receipt.AcceptedCount, receipt.RejectedCount,
            receipt.DuplicateCount, receipt.BatchRejected);

        return RSAnalyticsIngestionResult.Accepted(receipt);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long total = 0;
        while (true)
        {
            var n = await body.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
            if (total > maxBytes) throw new InvalidDataException("body exceeds cap");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }
}
