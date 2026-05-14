using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReportService.Models;

namespace ReportService.Storage;

/// <summary>
/// <see cref="RSCIReportStore"/> decorator that composes the canonical <see cref="RSCFileSystemReportStore"/>
/// with an <see cref="RSCIReportIndex"/>. Files remain the source of truth; the index accelerates listing.
/// </summary>
/// <remarks>
/// Write ordering: the file is persisted first. Only after a successful file write is the index row
/// upserted. If the index call fails, the failure is logged at Warning level and swallowed — the
/// caller's upload has already been durably stored, and it would be confusing to surface a 5xx after
/// a successful persist. The index can be rebuilt from disk if it ever drifts.
/// </remarks>
public sealed class RSCSqliteIndexingReportStore : RSCIReportStore
{
    private readonly RSCFileSystemReportStore _inner;
    private readonly RSCIReportIndex _index;
    private readonly ILogger<RSCSqliteIndexingReportStore> _logger;

    public RSCSqliteIndexingReportStore(RSCFileSystemReportStore inner, RSCIReportIndex index, ILogger<RSCSqliteIndexingReportStore> logger)
    {
        _inner = inner;
        _index = index;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RSCStoredReport> SaveAsync(
        RSCProblemReport report,
        ReadOnlyMemory<byte> jsonBytes,
        Stream? attachment,
        long? attachmentLength,
        string ingestionChannel,
        CancellationToken ct)
    {
        var stored = await _inner.SaveAsync(report, jsonBytes, attachment, attachmentLength, ingestionChannel, ct).ConfigureAwait(false);

        string? topFrame = null;
        string? logSummaryJson = null;
        if (stored.AttachmentFileName is not null)
        {
            // Crash uploads ship a plain stack-trace gzip (top_frame); user-RaP from iOS ships a
            // plaintext JSON array of LogEntry records (log_summary_json). Both are best-effort —
            // extraction failures leave the column null and the detail view degrades to "raw
            // attachment, N bytes". The two paths are mutually exclusive in practice (crash kind
            // → trace gzip; non-crash multipart → log-entries JSON) but we attempt both so a
            // future SDK that ships a different mix doesn't silently lose its summary.
            if (IsCrash(report))
            {
                topFrame = TryExtractTopFrame(stored.Platform, stored.AttachmentFileName);
            }
            else
            {
                logSummaryJson = TryExtractLogSummary(stored.Platform, stored.AttachmentFileName);
            }
        }

        var metadata = new RSCReportMetadata(
            Platform: stored.Platform,
            FileName: stored.FileName,
            SubmittedAt: stored.SubmittedAt,
            DeviceModel: report.DeviceModel,
            Title: report.Title,
            EmailHash: HashEmail(report.Email),
            PharmacyId: report.PharmacyId,
            UserId: report.UserId,
            Phone: report.Phone,
            AppVersion: report.AppVersion,
            HasAttachment: stored.AttachmentFileName is not null,
            SizeBytes: stored.SizeBytes,
            AttachmentSizeBytes: stored.AttachmentSizeBytes,
            LabelsJson: SerializeLabels(report.Labels),
            IngestionChannel: ingestionChannel,
            TopFrame: topFrame,
            LogSummaryJson: logSummaryJson,
            Kind: report.Kind);

        await TryIndexAsync(metadata, ct).ConfigureAwait(false);
        return stored;
    }

    /// <inheritdoc />
    public IReadOnlyList<RSCStoredReport> List(string platform)
    {
        // Union of index rows and on-disk files — any file whose index upsert was swallowed stays
        // discoverable. Index wins on conflict because it has the authoritative SubmittedAt.
        IReadOnlyList<RSCStoredReport> indexed;
        try
        {
            indexed = _index.ListAsync(platform, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite index list failed for platform {Platform}; falling back to file system", platform);
            return _inner.List(platform);
        }

        var onDisk = _inner.List(platform);
        if (onDisk.Count == 0) return indexed;

        var known = new HashSet<string>(indexed.Count, StringComparer.Ordinal);
        foreach (var r in indexed) known.Add(r.FileName);

        List<RSCStoredReport>? merged = null;
        foreach (var r in onDisk)
        {
            if (known.Contains(r.FileName)) continue;
            merged ??= new List<RSCStoredReport>(indexed);
            merged.Add(r);
            _logger.LogWarning(
                "Disk file {Platform}/{FileName} missing from index; surfacing via fallback",
                r.Platform, r.FileName);
        }

        if (merged is null) return indexed;

        merged.Sort(static (a, b) => b.SubmittedAt.CompareTo(a.SubmittedAt));
        return merged;
    }

    /// <inheritdoc />
    public Stream? OpenRead(string platform, string fileName) => _inner.OpenRead(platform, fileName);

    /// <inheritdoc />
    public bool Delete(string platform, string fileName)
    {
        // Files go first (source of truth), then the index row. A swallowed index-delete is
        // reconciled by the next List() call's drift fallback.
        var deleted = _inner.Delete(platform, fileName);
        if (!deleted) return false;

        try
        {
            _index.DeleteAsync(platform, fileName, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Index delete failed for {Platform}/{FileName}; files are gone, index row remains",
                platform, fileName);
        }
        return true;
    }

    // Swallow index-upsert failures so a durable file write never turns into a 5xx.
    private async Task TryIndexAsync(RSCReportMetadata metadata, CancellationToken ct)
    {
        try
        {
            await _index.UpsertAsync(metadata, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Index upsert failed for {Platform}/{FileName}; file is persisted, index row missing",
                metadata.Platform,
                metadata.FileName);
        }
    }

    /// <summary>
    /// True for SDK-supplied crash reports — tagged via <c>Kind = "crash"</c> by both platform
    /// uploaders. User-submitted "Report a Problem" payloads leave <c>Kind</c> null and never get
    /// their attachments parsed.
    /// </summary>
    private static bool IsCrash(RSCProblemReport report) =>
        string.Equals(report.Kind, "crash", StringComparison.OrdinalIgnoreCase);

    // First line that looks like "at de.ihreapotheken.sdk....(File.kt:42)" or
    // "  at SomeFile.swift line 42" / "0   AppName  ... [Class method]".
    private static readonly Regex JvmFrameRegex = new(@"^\s*at\s+(?<frame>\S.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private string? TryExtractTopFrame(string platform, string attachmentFileName)
    {
        try
        {
            using var raw = _inner.OpenRead(platform, attachmentFileName);
            if (raw is null) return null;
            using var gz = new GZipStream(raw, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new StreamReader(gz, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);

            string? firstNonBlank = null;
            for (var i = 0; i < 256; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                firstNonBlank ??= line.Trim();

                var match = JvmFrameRegex.Match(line);
                if (match.Success)
                {
                    var frame = match.Groups["frame"].Value.Trim();
                    return Truncate(frame, 256);
                }
            }
            return firstNonBlank is null ? null : Truncate(firstNonBlank, 256);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to extract top frame from {Platform}/{Attachment}; leaving top_frame null",
                platform, attachmentFileName);
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// Decodes the gzip attachment when it looks like a plaintext JSON array of iOS-style
    /// <c>LogEntry</c> records (the iOS SDK's <c>LogEntryRepository.readLogs()</c> dump). Counts
    /// entries by <c>level</c>, totals http-event details, and captures the time range. Returns
    /// JSON for direct persistence in <c>log_summary_json</c>; encrypted Android logcat payloads
    /// fail the array-shape check and yield null.
    /// </summary>
    private string? TryExtractLogSummary(string platform, string attachmentFileName)
    {
        try
        {
            using var raw = _inner.OpenRead(platform, attachmentFileName);
            if (raw is null) return null;
            using var gz = new GZipStream(raw, CompressionMode.Decompress, leaveOpen: false);

            using var doc = JsonDocument.Parse(gz);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var byLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0, httpEvents = 0;
            DateTimeOffset? earliest = null, latest = null;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                total++;

                var levelLabel = ReadLevelLabel(entry);
                if (!string.IsNullOrEmpty(levelLabel))
                {
                    byLevel.TryGetValue(levelLabel, out var n);
                    byLevel[levelLabel] = n + 1;
                }

                if (entry.TryGetProperty("details", out var details)
                    && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("httpEvent", out var http)
                    && http.ValueKind == JsonValueKind.Object)
                {
                    httpEvents++;
                }

                if (entry.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    if (earliest is null || parsed < earliest) earliest = parsed;
                    if (latest is null || parsed > latest) latest = parsed;
                }

                // Cap the work per attachment — log files can be tens of thousands of entries
                // and we only want a summary, not a full re-walk on every render.
                if (total >= MaxLogEntriesScanned) break;
            }

            // Empty arrays still produce a valid (zero-everything) summary so the UI can show
            // "iOS log file, 0 entries" instead of "raw attachment, decode unavailable".
            var summary = new RSCAttachmentLogSummary(
                TotalEntries: total,
                ByLevel: byLevel,
                HttpEventCount: httpEvents,
                Earliest: earliest,
                Latest: latest);

            return JsonSerializer.Serialize(summary, SummaryJson);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryExtractLogSummary: skipping {Platform}/{File} (probably encrypted or non-array shape)", platform, attachmentFileName);
            return null;
        }
    }

    /// <summary>
    /// iOS's <c>LogEntryLevel</c> enum encodes either as a bare string ("debug", "info", …) for
    /// the named cases or as a single-property object (<c>{"other": {...}}</c>) for the
    /// <c>.other(title:…)</c> case. Read both shapes; unknown / mis-shaped values land in the
    /// "(unknown)" bucket so the rollup still reflects entry counts even when the level is
    /// uninterpretable.
    /// </summary>
    private static string ReadLevelLabel(JsonElement entry)
    {
        if (!entry.TryGetProperty("level", out var level)) return "(unknown)";
        return level.ValueKind switch
        {
            JsonValueKind.String => level.GetString() ?? "(unknown)",
            JsonValueKind.Object => level.EnumerateObject().FirstOrDefault().Name ?? "(unknown)",
            _ => "(unknown)"
        };
    }

    private const int MaxLogEntriesScanned = 50_000;
    private static readonly JsonSerializerOptions SummaryJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private static string? HashEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return null;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(email), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? SerializeLabels(IReadOnlyList<string>? labels)
    {
        if (labels is null || labels.Count == 0) return null;
        return JsonSerializer.Serialize(labels);
    }
}
