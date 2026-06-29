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
        // Files go first (source of truth), then the index row. The index row carries the count +
        // byte footprint, so removal goes through RecordLifetimeAndDeleteAsync — it banks that
        // footprint into the lifetime-statistics rollup in the same transaction it deletes the row,
        // so the totals outlive the report. A swallowed index-delete is reconciled by the next
        // List() call's drift fallback.
        var deleted = _inner.Delete(platform, fileName);
        if (!deleted) return false;

        try
        {
            _index.RecordLifetimeAndDeleteAsync(platform, fileName, CancellationToken.None).GetAwaiter().GetResult();
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
            // Hard-cap the decompressed output so a gzip bomb (e.g. a 50 MiB gzip of zeros that
            // expands to GBs) can't exhaust memory on the ingestion hot path.
            using var bounded = new RSCBoundedReadStream(gz, MaxDecompressedBytes);
            using var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);

            string? firstNonBlank = null;
            for (var i = 0; i < 256; i++)
            {
                // Cap characters read per line so a crafted attachment with no newline (one
                // multi-GB "line") can't be buffered whole by ReadLine().
                var line = ReadLineBounded(reader, MaxLineChars);
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
        catch (RSCDecompressionBudgetExceededException ex)
        {
            // Exceeding the budget is treated as "no top frame" — the file is already stored, so we
            // must not fail the upload. Logged at Warning so a bomb attempt is visible.
            _logger.LogWarning(ex,
                "Decompressed attachment {Platform}/{Attachment} exceeded the {Budget}-byte budget; leaving top_frame null",
                platform, attachmentFileName, MaxDecompressedBytes);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to extract top frame from {Platform}/{Attachment}; leaving top_frame null",
                platform, attachmentFileName);
            return null;
        }
    }

    // Reads one line (terminated by \n / \r\n / \r / EOF) but never accumulates more than maxChars
    // characters. Once the cap is hit the rest of the line is drained without buffering, so a
    // newline-free payload behind the bounded stream can't materialise a giant string here either.
    private static string? ReadLineBounded(TextReader reader, int maxChars)
    {
        var sb = new StringBuilder();
        var any = false;
        int ch;
        while ((ch = reader.Read()) != -1)
        {
            any = true;
            if (ch == '\n') break;
            if (ch == '\r')
            {
                if (reader.Peek() == '\n') reader.Read();
                break;
            }
            if (sb.Length < maxChars) sb.Append((char)ch);
            // else: keep consuming until the line terminator, but stop growing the buffer.
        }
        return any ? sb.ToString() : null;
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
            // JsonDocument.Parse buffers the ENTIRE stream into memory, so it must read through the
            // decompressed-byte cap: a gzip bomb is rejected before it is fully materialised.
            using var bounded = new RSCBoundedReadStream(gz, MaxDecompressedBytes);

            using var doc = JsonDocument.Parse(bounded);
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
        catch (RSCDecompressionBudgetExceededException ex)
        {
            // Bomb / oversized payload — already stored, so don't fail the upload; just skip the
            // summary. Warning level so the attempt is visible (vs. the Debug "non-array" skip below).
            _logger.LogWarning(ex,
                "Decompressed attachment {Platform}/{File} exceeded the {Budget}-byte budget; leaving log_summary_json null",
                platform, attachmentFileName, MaxDecompressedBytes);
            return null;
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

    // Hard cap on bytes read out of the GZipStream while inspecting an attachment. The compressed
    // attachment is already capped at RSCReportServiceOptions.MaxAttachmentBytes (50 MiB default);
    // this bounds the DECOMPRESSED work so a high-ratio gzip bomb can't OOM the ingestion path.
    // Sized at ~5x the 50 MiB compressed cap; well above any legitimate crash trace / log dump.
    // Tests/docs referencing the decompression budget should use this 256 MiB value.
    private const long MaxDecompressedBytes = 256L * 1024 * 1024;

    // Max characters buffered for a single line during top-frame extraction (a stack frame line is
    // tiny; this only guards against a newline-free payload behind the bounded stream).
    private const int MaxLineChars = 8 * 1024;

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

/// <summary>
/// Thrown by <see cref="RSCBoundedReadStream"/> when a read would push the cumulative number of
/// decompressed bytes past the configured budget. Callers in the attachment-inspection path treat
/// this as "no summary / no top frame" (the file is already durably stored) rather than failing.
/// </summary>
public sealed class RSCDecompressionBudgetExceededException : Exception
{
    public RSCDecompressionBudgetExceededException(long budgetBytes)
        : base($"decompressed output exceeded the {budgetBytes}-byte budget") { }
}

/// <summary>
/// Read-only forwarding stream that throws <see cref="RSCDecompressionBudgetExceededException"/>
/// once more than <c>maxBytes</c> bytes have been read from the inner stream. Used to wrap a
/// <see cref="GZipStream"/> so a decompression bomb is rejected mid-read instead of being fully
/// materialised (by <c>JsonDocument.Parse</c> or a giant <c>ReadLine</c>).
/// </summary>
internal sealed class RSCBoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private long _read;

    public RSCBoundedReadStream(Stream inner, long maxBytes)
    {
        _inner = inner;
        _maxBytes = maxBytes;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Account(n);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        Account(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(int n)
    {
        if (n <= 0) return;
        _read += n;
        if (_read > _maxBytes) throw new RSCDecompressionBudgetExceededException(_maxBytes);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
