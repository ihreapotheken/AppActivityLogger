using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReportService.Analytics;
using ReportService.Models;
using ReportService.Options;
using ReportService.Security;

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

    // Optional request-header overrides for tenancy attribution (canonical names centralised in
    // RSCTenantHeaders so analytics + reports can't drift). Header wins over the body.
    private const string AppHeader = RSCTenantHeaders.AnalyticsApp;
    private const string EnvHeader = RSCTenantHeaders.AnalyticsEnvironment;
    private const string ClientHeader = RSCTenantHeaders.AnalyticsClient;

    private readonly RSCIAnalyticsStoreFactory _storeFactory;
    private readonly RSCAnalyticsValidator _validator;
    private readonly RSCAnalyticsIdentifierHasher _hasher;
    private readonly RSCReportServiceOptions _reportOptions;
    private readonly RSCAnalyticsOptions _analyticsOptions;
    private readonly RSCCatalogOptions _catalogOptions;
    private readonly ILogger<RSAnalyticsIngestionService> _logger;

    public RSAnalyticsIngestionService(
        RSCIAnalyticsStoreFactory storeFactory,
        RSCAnalyticsValidator validator,
        RSCAnalyticsIdentifierHasher hasher,
        RSCReportServiceOptions reportOptions,
        RSCAnalyticsOptions analyticsOptions,
        RSCCatalogOptions catalogOptions,
        ILogger<RSAnalyticsIngestionService> logger)
    {
        _storeFactory = storeFactory;
        _validator = validator;
        _hasher = hasher;
        _reportOptions = reportOptions;
        _analyticsOptions = analyticsOptions;
        _catalogOptions = catalogOptions;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the (app, environment, client) attribution for a batch, each trimmed-lowercased.
    /// <para><b>Client</b> is the top-level tenant and is taken from the <em>authenticated API key</em>
    /// (the <see cref="RSCTenantClaims.ClientId"/> claim) when the key is bound to a client — the key
    /// IS the client's identity, so a body/header value can't override it. Only an unbound key (the
    /// static root key, or a legacy managed key) falls back to header → body → default, preserving the
    /// older body-declared flow.</para>
    /// <para><b>App + environment</b> resolve header → body → default (the header path lets a gateway
    /// force them); they are later validated against the resolved client's registered apps.</para>
    /// </summary>
    private (string AppId, string Environment, string ClientId) ResolveAttribution(
        HttpRequest request, string? bodyAppId, string? bodyEnvironment, string? bodyClientId)
    {
        string Pick(string headerName, string? bodyValue, string fallback)
        {
            var header = request.Headers[headerName].ToString();
            var chosen = !string.IsNullOrWhiteSpace(header) ? header
                : !string.IsNullOrWhiteSpace(bodyValue) ? bodyValue
                : fallback;
            return chosen.Trim().ToLowerInvariant();
        }

        // Key-bound client wins outright (the access key identifies the client). Absent only for the
        // root/unbound keys, which keep the header → body → default resolution.
        var keyClient = request.HttpContext.User?.FindFirst(RSCTenantClaims.ClientId)?.Value;
        var client = !string.IsNullOrWhiteSpace(keyClient)
            ? keyClient.Trim().ToLowerInvariant()
            : Pick(ClientHeader, bodyClientId, _catalogOptions.DefaultClientSlug);

        return (
            Pick(AppHeader, bodyAppId, _catalogOptions.DefaultAppSlug),
            Pick(EnvHeader, bodyEnvironment, _catalogOptions.DefaultEnvironment),
            client);
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

        // Stamp the resolved tenancy onto the envelope so validation + storage see concrete,
        // normalized values regardless of whether the SDK supplied them in the body or a gateway
        // set them via headers.
        var (appId, environment, clientId) = ResolveAttribution(request, batch.AppId, batch.Environment, batch.ClientId);
        batch = batch with { AppId = appId, Environment = environment, ClientId = clientId };

        // SDK path: only the problem-report platforms (android/ios) are accepted — never the
        // analytics-only ServerPlatforms — so an SDK client cannot inject "backend" events.
        return await FinishBatchAsync(batch, "sdk", allowServerPlatforms: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts first-party events reported by a trusted backend on
    /// <c>POST /api/v2/analytics/server-events</c>. The lighter <see cref="RSCServerAnalyticsRequest"/>
    /// is mapped into a full <see cref="RSCAnalyticsBatch"/> (synthesizing the SDK-centric envelope
    /// fields) and then runs through the identical validate → hash → store path as SDK batches, so
    /// server-reported events land in the same tables, rollups, and funnels. Idempotent on a
    /// caller-supplied stable <c>eventId</c> via the existing <c>UNIQUE(platform, event_id)</c>.
    /// </summary>
    public async Task<RSAnalyticsIngestionResult> IngestServerAsync(HttpRequest request, CancellationToken ct)
    {
        if (!_analyticsOptions.Enabled)
            return RSAnalyticsIngestionResult.ServiceUnavailable("analytics ingestion is disabled");

        var contentType = request.ContentType ?? string.Empty;
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return RSAnalyticsIngestionResult.UnsupportedMediaType("application/json required");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _reportOptions.IngestTimeoutSeconds)));
        ct = timeoutCts.Token;

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

        RSCServerAnalyticsRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<RSCServerAnalyticsRequest>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Rejected malformed server analytics request: {Reason}", ex.Message);
            return RSAnalyticsIngestionResult.BadRequest("invalid JSON payload");
        }

        // A request carrying zero events is a structural client bug — a hard 400 here, up front,
        // before the store is even touched (so there's no receipt to inspect). This is consistent
        // with the standardised outcome rule (see RSAnalyticsIngestionResult.FromReceipt): a batch
        // from which nothing usable lands is a 400, never a 202. Batches that DO reach the store and
        // are then fully rejected (unknown platform, every event dead-lettered) also 400 — but carry
        // the receipt so the caller can read the per-event detail.
        if (req is null || req.Events is null || req.Events.Count == 0)
            return RSAnalyticsIngestionResult.BadRequest("at least one event is required");

        // Name is the one field RSCServerAnalyticsEvent documents as required. Validate it up front
        // and 400 with a specific message so a caller that omits it gets a clear, actionable error
        // rather than the generic full-rejection 400 with the event dead-lettered as
        // missing_required_field downstream.
        if (req.Events.Any(e => string.IsNullOrWhiteSpace(e.Name)))
            return RSAnalyticsIngestionResult.BadRequest("each event requires a name");

        // eventId/sessionId are stored VERBATIM (never hashed) and exported, while subjectId/clientId
        // are hashed precisely because they are raw account/user keys. A naive caller that sets
        // eventId = subjectId (or sessionId = subjectId/clientId) would permanently route a raw PII
        // identifier into an un-hashed, exported column — the exact leak the hashing exists to
        // prevent. Reject such requests outright rather than silently storing the leak.
        if (RoutesIdentifierIntoEnvelope(req, out var leakDetail))
            return RSAnalyticsIngestionResult.BadRequest(leakDetail);

        var (appId, environment, clientId) = ResolveAttribution(request, req.AppId, req.Environment, req.ClientId);
        var batch = MapToBatch(req) with { AppId = appId, Environment = environment, ClientId = clientId };
        // Server path: ServerPlatforms (e.g. "backend") are additionally accepted on top of
        // android/ios.
        return await FinishBatchAsync(batch, "server", allowServerPlatforms: true, ct).ConfigureAwait(false);
    }

    /// <summary>Validate → hash identifiers → persist (+ dead-letter) → log → receipt. Shared by the
    /// SDK and server ingestion paths so both honour the exact same rules and idempotency. The SDK
    /// path passes <paramref name="allowServerPlatforms"/>=false so only android/ios are accepted;
    /// the server path passes true so the analytics-only ServerPlatforms (e.g. "backend") are also
    /// accepted — the two endpoints must NOT share a single widened platform allow-list.</summary>
    private async Task<RSAnalyticsIngestionResult> FinishBatchAsync(
        RSCAnalyticsBatch batch, string origin, bool allowServerPlatforms, CancellationToken ct)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var verdict = _validator.Validate(batch, receivedAt, allowServerPlatforms);

        var anonHash = _hasher.Hash(batch.AnonymousId);
        // clientId is no longer hashed: it's a verbatim, queryable tenancy key (a pharmacy/business
        // id, not user PII — see RSCAnalyticsBatch.ClientId / the tenancy docs). The legacy
        // client_id_hash column is left unpopulated (write-only; nothing reads it). The per-user
        // identity (anonymousId) stays hashed.
        // Database-per-app: route to the store for this batch's resolved (client, app); the factory
        // provisions + migrates that app's analytics.db on first use. (app_id/client_id columns are
        // still stamped inside the row — harmless — but the FILE is the real isolation boundary.)
        var store = _storeFactory.Get(batch.ClientId, batch.AppId);
        var receipt = await store.WriteBatchAsync(batch, anonHash, clientIdHash: null, verdict, receivedAt, ct)
            .ConfigureAwait(false);

        // A wholesale rejection (bad platform/schema/oversize, or every event dead-lettered) is a
        // discarded batch: it now maps to a 400 (via FromReceipt) rather than a 202 that would mask
        // the total failure. Log it at Warning so an integration that's having everything dropped is
        // still alertable, and so the operator signal survives regardless of the status code.
        var fullyRejected = RSAnalyticsIngestionResult.IsFullyRejected(receipt);
        var level = fullyRejected ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(level,
            "Ingested analytics batch id={BatchId} origin={Origin} platform={Platform} accepted={Accepted} rejected={Rejected} dup={Dup} batchRejected={BatchRejected}",
            receipt.BatchId, origin, batch.Platform, receipt.AcceptedCount, receipt.RejectedCount,
            receipt.DuplicateCount, receipt.BatchRejected);

        return RSAnalyticsIngestionResult.FromReceipt(receipt);
    }

    /// <summary>
    /// Maps a backend <see cref="RSCServerAnalyticsRequest"/> into a full <see cref="RSCAnalyticsBatch"/>.
    /// Every SDK-centric field the backend doesn't have is synthesized: platform defaults to
    /// <see cref="RSCAnalyticsPlatforms.Backend"/>, sequence to the event's position,
    /// eventId/batchId to generated ids, occurredAt to now, and type to <c>action</c>.
    /// SubjectId/ClientId become the (hashed-downstream) anonymousId/clientId.
    /// <para>
    /// SessionId synthesis: when a caller omits sessionId we derive a per-event id (<c>srv-{eventId}</c>)
    /// rather than a single per-batch id, so a batch of K unrelated server events (e.g. K independent
    /// purchases) is NOT collapsed into one synthetic session — which would inflate session counts and
    /// session-duration metrics. The id is derived from the (non-secret) eventId, never from the
    /// SubjectId, so no raw identifier leaks into the verbatim-stored session_id column. A caller that
    /// genuinely has a session concept should supply an explicit opaque sessionId.
    /// </para>
    /// </summary>
    private RSCAnalyticsBatch MapToBatch(RSCServerAnalyticsRequest req)
    {
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var platform = string.IsNullOrWhiteSpace(req.Platform)
            ? RSCAnalyticsPlatforms.Backend
            : req.Platform.Trim().ToLowerInvariant();
        var batchId = string.IsNullOrWhiteSpace(req.BatchId) ? $"srv-{Guid.NewGuid():N}" : req.BatchId.Trim();
        var sdkVersion = string.IsNullOrWhiteSpace(req.Source) ? "server" : $"server:{req.Source.Trim()}";

        var src = req.Events ?? Array.Empty<RSCServerAnalyticsEvent>();
        var events = new List<RSCAnalyticsEvent>(src.Count);
        for (var i = 0; i < src.Count; i++)
        {
            var e = src[i];
            var eventId = string.IsNullOrWhiteSpace(e.EventId) ? $"srv-{Guid.NewGuid():N}" : e.EventId.Trim();
            events.Add(new RSCAnalyticsEvent(
                EventId: eventId,
                // Default to a per-event session derived from the eventId, not a per-batch id, so
                // unrelated server events don't merge into one synthetic session.
                SessionId: string.IsNullOrWhiteSpace(e.SessionId) ? $"srv-{eventId}" : e.SessionId.Trim(),
                Sequence: e.Sequence ?? i,
                OccurredAt: string.IsNullOrWhiteSpace(e.OccurredAt) ? nowIso : e.OccurredAt.Trim(),
                Type: string.IsNullOrWhiteSpace(e.Type) ? RSCAnalyticsEventKinds.Action : e.Type.Trim(),
                Name: e.Name ?? string.Empty,
                Screen: e.Screen,
                Feature: e.Feature,
                DurationMs: e.DurationMs,
                Properties: e.Properties,
                Items: e.Items));
        }

        return new RSCAnalyticsBatch(
            SchemaVersion: _analyticsOptions.MinAcceptedSchemaVersion,
            BatchId: batchId,
            Platform: platform,
            SdkVersion: sdkVersion,
            HostAppVersion: null,
            AnonymousId: req.SubjectId,
            ClientId: req.ClientId,
            GeneratedAt: nowIso,
            Events: events);
    }

    /// <summary>
    /// Guards against a caller routing a raw PII identifier (the hashed-only SubjectId/ClientId)
    /// into the verbatim-stored, exported eventId/sessionId envelope columns. Returns true (with a
    /// caller-facing detail) when any event's supplied eventId or sessionId equals the request's
    /// SubjectId or ClientId after trimming.
    /// </summary>
    private static bool RoutesIdentifierIntoEnvelope(RSCServerAnalyticsRequest req, out string detail)
    {
        detail = string.Empty;
        var subject = req.SubjectId?.Trim();
        var client = req.ClientId?.Trim();
        if (string.IsNullOrEmpty(subject) && string.IsNullOrEmpty(client))
            return false;

        foreach (var e in req.Events ?? Array.Empty<RSCServerAnalyticsEvent>())
        {
            var eventId = e.EventId?.Trim();
            var sessionId = e.SessionId?.Trim();
            if (Matches(eventId) || Matches(sessionId))
            {
                detail = "eventId/sessionId must be opaque non-PII keys and must not equal subjectId/clientId";
                return true;
            }
        }
        return false;

        bool Matches(string? value) =>
            !string.IsNullOrEmpty(value) &&
            ((!string.IsNullOrEmpty(subject) && string.Equals(value, subject, StringComparison.Ordinal)) ||
             (!string.IsNullOrEmpty(client) && string.Equals(value, client, StringComparison.Ordinal)));
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
