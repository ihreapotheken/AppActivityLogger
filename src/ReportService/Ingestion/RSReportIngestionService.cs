using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReportService.Models;
using ReportService.Options;
using ReportService.Security;
using ReportService.Storage;
using ReportService.Storage.Catalog;
using ReportService.Validation;

namespace ReportService.Ingestion;

/// <summary>Accept → validate → persist for inbound multipart submissions. Size limits are enforced by Kestrel + <c>FormOptions</c> in Program.cs.</summary>
public sealed class RSReportIngestionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
        // Reject payloads that carry fields we do not know about. Closes parser-differential attacks
        // where an attacker sneaks extra fields past validation by exploiting lenient deserializer
        // behavior; also surfaces accidental SDK drift early rather than silently dropping data.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly RSCIReportStore _store;
    private readonly RSCReportValidator _validator;
    private readonly RSCReportServiceOptions _options;
    private readonly RSCICatalog _catalog;
    private readonly RSCCatalogOptions _catalogOptions;
    private readonly ILogger<RSReportIngestionService> _logger;

    public RSReportIngestionService(
        RSCIReportStore store,
        RSCReportValidator validator,
        RSCReportServiceOptions options,
        RSCICatalog catalog,
        RSCCatalogOptions catalogOptions,
        ILogger<RSReportIngestionService> logger)
    {
        _store = store;
        _validator = validator;
        _options = options;
        _catalog = catalog;
        _catalogOptions = catalogOptions;
        _logger = logger;
    }

    /// <summary>
    /// Resolves + validates the report's tenancy (database-per-app). The <b>client</b> comes from the
    /// authenticated API key's <c>rsc:client_id</c> claim (the key IS the client); an unbound/root key
    /// falls back to the <c>X-Report-Client</c> header → body → default. <b>app</b>/<b>environment</b>
    /// resolve header (<c>X-Report-App</c> / <c>X-Report-Environment</c>) → body → default. The resolved
    /// triple is validated against the catalog (app must belong to the client) when
    /// <c>Catalog:Enabled</c>; an unknown value rejects the whole submission. Returns the
    /// attribution-stamped report, or a rejection result.
    /// </summary>
    private (RSCProblemReport Report, RSIngestionResult? Rejection) ResolveAttribution(HttpRequest request, RSCProblemReport report)
    {
        string Pick(string headerName, string? bodyValue, string fallback)
        {
            var header = request.Headers[headerName].ToString();
            var chosen = !string.IsNullOrWhiteSpace(header) ? header
                : !string.IsNullOrWhiteSpace(bodyValue) ? bodyValue
                : fallback;
            return chosen.Trim().ToLowerInvariant();
        }

        var keyClient = request.HttpContext.User?.FindFirst(RSCTenantClaims.ClientId)?.Value;
        var client = !string.IsNullOrWhiteSpace(keyClient)
            ? keyClient.Trim().ToLowerInvariant()
            : Pick("X-Report-Client", report.ClientId, _catalogOptions.DefaultClientSlug);
        var app = Pick("X-Report-App", report.AppId, _catalogOptions.DefaultAppSlug);
        var env = Pick("X-Report-Environment", report.Environment, _catalogOptions.DefaultEnvironment);

        if (_catalogOptions.Enabled)
        {
            if (!_catalog.IsValidClient(client))
                return (report, RSIngestionResult.BadRequest($"client '{client}' is not registered"));
            if (!_catalog.IsValidApp(client, app))
                return (report, RSIngestionResult.BadRequest($"app '{app}' is not registered for client '{client}'"));
            if (!_catalog.IsValidEnvironment(client, app, env))
                return (report, RSIngestionResult.BadRequest($"environment '{env}' is not declared for app '{app}' (client '{client}')"));
        }

        return (report with { ClientId = client, AppId = app, Environment = env }, null);
    }

    /// <summary>Parses + validates the multipart submission, then persists via <see cref="RSCIReportStore"/>. Returns the matching <see cref="RSIngestionResult"/> status code.</summary>
    public async Task<RSIngestionResult> IngestAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
            return RSIngestionResult.UnsupportedMediaType("multipart/form-data required");

        // Hard per-request wall-clock limit. Linked with RequestAborted so a client disconnect or
        // the framework abort still cancels immediately, but a stuck disk or slow upload cannot
        // hold an IngestConcurrency permit past this deadline. The catch block downstream maps
        // OperationCanceledException from our timeout into a 503-style response via the central
        // exception handler (client-disconnect was already handled there).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.IngestTimeoutSeconds)));
        ct = timeoutCts.Token;

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning("Rejected oversized multipart upload: {Reason}", ex.Message);
            return RSIngestionResult.PayloadTooLarge("request body exceeds configured maximum");
        }
        catch (BadHttpRequestException ex)
        {
            _logger.LogWarning("Rejected malformed multipart upload: {Reason}", ex.Message);
            return RSIngestionResult.BadRequest("malformed multipart body");
        }

        // The IA SDKs (and the documented curl example) send `json` as a multipart FILE part with
        // Content-Type: application/json, not a text form field — so check Files first. Fall back
        // to a plain form field to accommodate clients that post the body that way.
        //
        // Size check uses MaxJsonBytes, not MaxUploadBytes: the envelope cap can be raised to
        // accept a large attachment without implicitly widening what we will buffer into a string.
        var maxJsonBytes = Math.Max(1, _options.MaxJsonBytes);
        string jsonField;
        var jsonFile = form.Files["json"];
        if (jsonFile is not null && jsonFile.Length > 0)
        {
            if (jsonFile.Length > maxJsonBytes)
                return RSIngestionResult.PayloadTooLarge("json part exceeds MaxJsonBytes");
            using var reader = new StreamReader(jsonFile.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            jsonField = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        else
        {
            jsonField = form["json"].ToString();
            // Form-field path: ReadFormAsync has already buffered the value, bounded only by
            // FormOptions.ValueLengthLimit (16 MiB) — which is far larger than MaxJsonBytes
            // (1 MiB default), so we must re-assert the json-part cap here rather than rely on the
            // framework's parse-time limit. Reject on the cheap char count first: UTF-8 encodes
            // every character as >= 1 byte, so Length (chars) > maxJsonBytes already guarantees the
            // byte count exceeds the cap and we can skip the full GetByteCount scan. Only when the
            // char count is within budget do we pay for the exact UTF-8 byte measurement (a single
            // multi-byte run can still push a sub-cap char count over the byte cap).
            if (jsonField.Length > maxJsonBytes || Encoding.UTF8.GetByteCount(jsonField) > maxJsonBytes)
                return RSIngestionResult.PayloadTooLarge("json part exceeds MaxJsonBytes");
        }

        if (string.IsNullOrWhiteSpace(jsonField))
            return RSIngestionResult.BadRequest("missing 'json' part");

        RSCProblemReport? report;
        try
        {
            report = JsonSerializer.Deserialize<RSCProblemReport>(jsonField, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Rejected malformed JSON part: {Reason}", ex.Message);
            return RSIngestionResult.BadRequest("invalid JSON payload");
        }

        var vr = _validator.ValidateReport(report);
        if (!vr.IsValid) return RSIngestionResult.BadRequest(vr.Error ?? "validation failed");

        var fileField = form.Files["file"];
        Stream? attachmentStream = null;
        long? attachmentLength = null;

        if (fileField is not null && fileField.Length > 0)
        {
            if (fileField.Length > _options.MaxAttachmentBytes)
                return RSIngestionResult.PayloadTooLarge("attachment exceeds MaxAttachmentBytes");

            // IFormFile.OpenReadStream() yields a fresh seekable view over the buffered part
            // on every call, so probing for the gzip magic and then streaming the full body
            // to storage from a second call is safe.
            byte[] firstTwo = new byte[2];
            await using (var probe = fileField.OpenReadStream())
            {
                var read = 0;
                while (read < 2)
                {
                    var n = await probe.ReadAsync(firstTwo.AsMemory(read, 2 - read), ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }

                if (read < 2)
                    return RSIngestionResult.BadRequest("attachment is not a gzip stream");
            }

            var av = _validator.ValidateAttachment(fileField.Length, _options.MaxAttachmentBytes, firstTwo);
            if (!av.IsValid)
            {
                return av.Error == "attachment exceeds MaxAttachmentBytes"
                    ? RSIngestionResult.PayloadTooLarge(av.Error)
                    : RSIngestionResult.BadRequest(av.Error ?? "attachment validation failed");
            }

            attachmentStream = fileField.OpenReadStream();
            attachmentLength = fileField.Length;
        }

        try
        {
            var jsonBytes = Encoding.UTF8.GetBytes(jsonField);
            var jsonMemory = new ReadOnlyMemory<byte>(jsonBytes);

            // Database-per-app: attribute (client from key, app from X-Report-App/body) + validate,
            // then the store routes to that app's own report tree.
            var (attributed, rejection) = ResolveAttribution(request, report!);
            if (rejection is not null) return rejection;

            var stored = await _store.SaveAsync(attributed, jsonMemory, attachmentStream, attachmentLength,
                RSCIngestionChannels.Multipart, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Ingested problem report channel=multipart platform={Platform} file={File} bytes={Bytes} hasAttachment={HasAttachment}",
                stored.Platform,
                stored.FileName,
                stored.SizeBytes,
                stored.AttachmentFileName is not null);

            return RSIngestionResult.Created(stored);
        }
        finally
        {
            if (attachmentStream is not null)
            {
                await attachmentStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Single-JSON ingest path. Accepts the request body as a <see cref="RSCProblemReport"/> document
    /// directly — no multipart, no attachment. Reuses the multipart pipeline's validation, size
    /// caps, and storage. The persisted row is tagged with channel = <see cref="RSCIngestionChannels.Json"/>.
    /// </summary>
    public async Task<RSIngestionResult> IngestJsonAsync(HttpRequest request, CancellationToken ct)
    {
        // Be permissive about charset/profile parameters: any application/json is fine.
        var contentType = request.ContentType ?? string.Empty;
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return RSIngestionResult.UnsupportedMediaType("application/json required");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.IngestTimeoutSeconds)));
        ct = timeoutCts.Token;

        var maxJsonBytes = Math.Max(1, _options.MaxJsonBytes);

        // Reject up front when the client sent a Content-Length that already exceeds the cap;
        // otherwise read into a length-bounded buffer to prevent runaway memory under a hostile
        // chunked body.
        if (request.ContentLength is { } cl && cl > maxJsonBytes)
            return RSIngestionResult.PayloadTooLarge("body exceeds MaxJsonBytes");

        byte[] jsonBytes;
        try
        {
            jsonBytes = await ReadBoundedAsync(request.Body, maxJsonBytes, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return RSIngestionResult.PayloadTooLarge("body exceeds MaxJsonBytes");
        }

        if (jsonBytes.Length == 0)
            return RSIngestionResult.BadRequest("empty request body");

        RSCProblemReport? report;
        try
        {
            report = JsonSerializer.Deserialize<RSCProblemReport>(jsonBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Rejected malformed JSON body: {Reason}", ex.Message);
            return RSIngestionResult.BadRequest("invalid JSON payload");
        }

        var vr = _validator.ValidateReport(report);
        if (!vr.IsValid) return RSIngestionResult.BadRequest(vr.Error ?? "validation failed");

        var (attributed, rejection) = ResolveAttribution(request, report!);
        if (rejection is not null) return rejection;

        var stored = await _store.SaveAsync(attributed, jsonBytes, attachment: null, attachmentLength: null,
            RSCIngestionChannels.Json, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Ingested problem report channel=json platform={Platform} file={File} bytes={Bytes}",
            stored.Platform, stored.FileName, stored.SizeBytes);

        return RSIngestionResult.Created(stored);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        // We can't blindly trust ContentLength — chunked transfer encodings + buggy clients exist.
        // Copy with a hard byte budget; if the source overshoots, throw so the caller can map to 413.
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
