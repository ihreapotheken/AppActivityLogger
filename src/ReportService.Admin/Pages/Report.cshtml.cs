using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReportService.Audit;
using ReportService.Options;
using ReportService.Storage;
using ReportService.Validation;

namespace ReportService.Admin.Pages;

/// <summary>
/// Per-report detail page. Renders the JSON body, exposes download links for the JSON and any gzip
/// attachment, and accepts a POST to delete the report. All I/O flows through
/// <see cref="RSCIReportStore"/>, which centralizes path-traversal guarding.
/// </summary>
public sealed class RSAReportModel : PageModel
{
    private const int MaxRenderedJsonBytes = 512 * 1024;
    /// <summary>Soft cap on decompressed logcat to keep the rendered HTML page reasonable.</summary>
    private const int MaxRenderedLogBytes = 1 * 1024 * 1024;
    /// <summary>Hard upper bound on the gzip stream we'll bother decompressing inline.</summary>
    private const int MaxLogReadBytes = 8 * 1024 * 1024;

    private readonly RSCIReportStore _store;
    private readonly RSCReportServiceOptions _options;
    private readonly RSCIAuditLog _audit;
    private readonly ILogger<RSAReportModel> _logger;

    public RSAReportModel(
        RSCIReportStore store,
        RSCReportServiceOptions options,
        RSCIAuditLog audit,
        ILogger<RSAReportModel> logger)
    {
        _store = store;
        _options = options;
        _audit = audit;
        _logger = logger;
    }

    public string Platform { get; private set; } = string.Empty;

    /// <summary>The tenant (client) and app this report was submitted under, resolved from the
    /// per-app store's metadata (the fan-out store stamps each listing row with its owning
    /// <c>(client, app)</c>). Null/blank only for a legacy report that predates per-app attribution.</summary>
    public string? ClientId { get; private set; }
    public string? AppId { get; private set; }

    public string FileName { get; private set; } = string.Empty;
    public string? AttachmentFileName { get; private set; }
    public long SizeBytes { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public string? IngestionChannel { get; private set; }
    public string JsonBody { get; private set; } = string.Empty;

    /// <summary>Structured fields parsed out of the JSON body for the summary panel and the chip
    /// header. Empty when the body could not be parsed (truncated / malformed).</summary>
    public RSAReportSummary Summary { get; private set; } = new();

    /// <summary>Stack trace text, if any field named <c>stackTrace</c>/<c>stack_trace</c>/<c>stack</c>
    /// (case-insensitive) was found at the document root.</summary>
    public string? StackTrace { get; private set; }

    /// <summary>Deobfuscated copy of <see cref="StackTrace"/> when the operator just uploaded a
    /// mapping via <see cref="OnPostDeobfuscateAsync"/>. Null on the plain GET — the page
    /// renders the obfuscated trace plus an inline upload form so the operator can deobfuscate
    /// in-place. The applied mapping is never persisted.</summary>
    public string? DeobfuscatedStackTrace { get; private set; }

    /// <summary>Number of frame lines the applier rewrote in the most recent in-memory
    /// deobfuscation pass — 0 means the uploaded mapping didn't cover any frames in this trace,
    /// which is the operator's cue that the mapping doesn't match this build.</summary>
    public int RewrittenFrameCount { get; private set; }

    /// <summary>Human-readable summary of the mapping(s) the operator just uploaded — e.g.
    /// "host (56,814 classes) + ia-sdk (1 class)". Empty when the page is rendering its plain
    /// GET response.</summary>
    public string? AppliedMappingSummary { get; private set; }

    /// <summary>Server-extracted summary of the gzip log attachment (iOS plaintext JSON shape).
    /// Null when the attachment is encrypted (Android), missing, or didn't parse as the expected
    /// LogEntry array shape at ingest.</summary>
    public RSCAttachmentLogSummary? LogSummary { get; private set; }

    /// <summary>Decompressed logcat lines (when an attachment exists and looks like text).</summary>
    public IReadOnlyList<RSALogcatLine> LogcatLines { get; private set; } = Array.Empty<RSALogcatLine>();
    public string? LogcatNotice { get; private set; }
    public bool LogcatAvailable => LogcatLines.Count > 0;

    /// <summary>Whether a mapping.txt would have anything to rewrite on this report — an obfuscated
    /// stack trace, or a decompressed logcat from the attachment. When false (e.g. a plain problem
    /// report with no crash log attached) the "Deobfuscate this report" section is hidden, since the
    /// deobfuscation pass acts only on the stack trace and the logcat lines.</summary>
    public bool CanDeobfuscate => !string.IsNullOrEmpty(StackTrace) || LogcatAvailable;

    /// <summary>Summary of the attachment log: per-level counts + first error excerpt. Lets the
    /// operator triage a large log without scrolling the full dump below.</summary>
    public RSALogcatOverview? LogcatOverview { get; private set; }

    /// <summary>Server-side log filter, bound from the query string (a GET filter form like the rest
    /// of the app). The level + search are applied to the full <see cref="LogcatLines"/> set and the
    /// matching lines paginated server-side, so only the visible page is sent to the browser instead
    /// of shipping the whole log and filtering it in JavaScript.</summary>
    [BindProperty(SupportsGet = true, Name = "logLevel")] public string? LogLevel { get; set; }
    [BindProperty(SupportsGet = true, Name = "logSearch")] public string? LogSearch { get; set; }
    [BindProperty(SupportsGet = true, Name = "logPage")] public int LogPage { get; set; } = 1;

    /// <summary>Lines per page in the attachment-log viewer.</summary>
    public const int LogPageSize = 500;

    /// <summary>The filtered + paginated slice of <see cref="LogcatLines"/> the view actually renders.</summary>
    public IReadOnlyList<RSALogcatLine> LogcatPage { get; private set; } = Array.Empty<RSALogcatLine>();
    /// <summary>Total parsed lines in the log (before filtering).</summary>
    public int LogTotalLines { get; private set; }
    /// <summary>Lines matching the current level + search filter (across all pages).</summary>
    public int LogMatchCount { get; private set; }
    public int LogPageNumber { get; private set; } = 1;
    public int LogTotalPages { get; private set; } = 1;
    public bool HasLogFilter => !string.IsNullOrEmpty(LogLevel) || !string.IsNullOrWhiteSpace(LogSearch);

#pragma warning disable CS1998 // OnGetAsync has no async work after the in-memory deobfuscation
                                // path moved to OnPostDeobfuscateAsync — kept as Task-returning
                                // because that handler awaits it via the shared model setup.
    public async Task<IActionResult> OnGetAsync(string platform, string fileName, CancellationToken ct)
#pragma warning restore CS1998
    {
        if (RSCPlatforms.TryCanonicalize(platform, _options) is not { } p) return NotFound();

        var meta = _store.List(p).FirstOrDefault(r => string.Equals(r.FileName, fileName, StringComparison.Ordinal));
        if (meta is null) return NotFound();

        // A client login may only open reports belonging to its own client (database-per-app means
        // a guessed filename could otherwise resolve to another tenant's report via the fan-out).
        // Operators (no client claim) are unrestricted. 404 (not 403) so a client can't probe which
        // filenames exist under other clients.
        if (DeniedForClientLogin(meta)) return NotFound();

        using var stream = _store.OpenRead(p, fileName);
        if (stream is null) return NotFound();

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0 && ms.Length < MaxRenderedJsonBytes)
        {
            ms.Write(buffer, 0, read);
        }
        var raw = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var truncated = meta.SizeBytes > MaxRenderedJsonBytes;

        // Pretty-print only when the document is complete — a truncated body is invalid JSON and
        // would just throw. Server CPU is already bounded by MaxRenderedJsonBytes (512 KiB), so
        // formatting the in-memory node tree is cheap; for >cap files we skip and stay raw.
        JsonNode? parsed = truncated ? null : TryParse(raw);
        var body = parsed is null ? raw : parsed.ToJsonString(PrettyOptions);

        if (truncated)
        {
            body += $"\n\n… truncated to first {MaxRenderedJsonBytes} bytes (file is {meta.SizeBytes} bytes; use the download button for the full document).";
        }

        if (parsed is JsonObject obj)
        {
            Summary = ExtractSummary(obj);
            StackTrace = ExtractStackTrace(obj);
        }

        Platform = p;
        ClientId = string.IsNullOrWhiteSpace(meta.ClientId) ? null : meta.ClientId;
        AppId = string.IsNullOrWhiteSpace(meta.AppId) ? null : meta.AppId;
        FileName = meta.FileName;
        AttachmentFileName = meta.AttachmentFileName;
        SizeBytes = meta.SizeBytes;
        SubmittedAt = meta.SubmittedAt;
        IngestionChannel = meta.IngestionChannel;
        JsonBody = body;

        // Server-extracted log summary (iOS plaintext JSON attachments) — null for crashes,
        // analytics, encrypted Android logcat, or any attachment that didn't parse as a JSON
        // array at ingest. The detail view renders the panel only when this is populated.
        if (!string.IsNullOrEmpty(meta.LogSummaryJson))
        {
            try
            {
                LogSummary = System.Text.Json.JsonSerializer.Deserialize<RSCAttachmentLogSummary>(
                    meta.LogSummaryJson,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            }
            catch { /* tolerate malformed blobs */ }
        }

        if (meta.AttachmentFileName is { } att)
        {
            (LogcatLines, LogcatNotice) = TryReadLogcat(p, att);
            if (LogcatLines.Count > 0)
            {
                LogcatOverview = BuildOverview(LogcatLines);
            }
            ApplyLogFilter();
        }

        // Initial GET renders the obfuscated trace as-is. To deobfuscate, the operator uploads
        // a mapping.txt via the inline form on this page (POST → OnPostDeobfuscateAsync). The
        // mapping is applied in memory and never written to disk; refreshing this URL returns
        // to the obfuscated view.
        return Page();
    }

    /// <summary>
    /// Decompresses the gzip attachment, splits into lines, and tags each line with a log level
    /// derived from the leading <c>E/W/I/D/V</c> column (Android logcat) or <c>error/warn/…</c>
    /// keywords (anything else). Returns an empty list + a notice when the attachment isn't a
    /// well-formed gzip text stream — the operator can still download the raw file.
    /// </summary>
    private (IReadOnlyList<RSALogcatLine> Lines, string? Notice) TryReadLogcat(string platform, string attachmentName)
    {
        Stream? raw = null;
        try
        {
            raw = _store.OpenRead(platform, attachmentName);
            if (raw is null) return (Array.Empty<RSALogcatLine>(), "Attachment not found on disk.");

            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            using var capped = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = gz.Read(buffer, 0, buffer.Length)) > 0 && capped.Length < MaxLogReadBytes)
            {
                capped.Write(buffer, 0, read);
            }
            var decoded = Encoding.UTF8.GetString(capped.ToArray());
            var truncated = capped.Length >= MaxLogReadBytes;

            var rendered = decoded.Length > MaxRenderedLogBytes
                ? decoded[..MaxRenderedLogBytes]
                : decoded;
            var lines = new List<RSALogcatLine>();
            var n = 0;
            foreach (var raw_line in rendered.Split('\n'))
            {
                n++;
                var trimmed = raw_line.TrimEnd('\r');
                if (trimmed.Length == 0 && rendered.Length > 0 && n == rendered.Split('\n').Length) continue;
                lines.Add(new RSALogcatLine(n, ClassifyLevel(trimmed), trimmed));
            }

            string? notice = null;
            if (truncated)
            {
                notice = $"Logcat truncated to first {MaxLogReadBytes / 1024} KiB of the gzip stream — download for full content.";
            }
            else if (decoded.Length > MaxRenderedLogBytes)
            {
                notice = $"Showing first {MaxRenderedLogBytes / 1024} KiB of {decoded.Length / 1024} KiB — download for full content.";
            }
            return (lines, notice);
        }
        catch (InvalidDataException)
        {
            return (Array.Empty<RSALogcatLine>(), "Attachment is not a valid gzip stream — download to inspect manually.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decompress attachment {Platform}/{File}", platform, attachmentName);
            return (Array.Empty<RSALogcatLine>(), "Could not decompress attachment — see service logs.");
        }
        finally
        {
            raw?.Dispose();
        }
    }

    /// <summary>Applies the level + search filter and pagination over the full <see cref="LogcatLines"/>
    /// set, populating <see cref="LogcatPage"/> (the slice the view renders) and the paging metadata.
    /// Doing this on the server keeps a large log off the wire — only the matching page is sent.</summary>
    private void ApplyLogFilter()
    {
        LogTotalLines = LogcatLines.Count;
        if (LogcatLines.Count == 0)
        {
            LogcatPage = Array.Empty<RSALogcatLine>();
            LogMatchCount = 0;
            LogPageNumber = 1;
            LogTotalPages = 1;
            return;
        }

        var level = string.IsNullOrEmpty(LogLevel) ? null : LogLevel;
        var matcher = CompileLogMatcher(LogSearch);
        var matched = new List<RSALogcatLine>();
        foreach (var line in LogcatLines)
        {
            if (level is not null && !string.Equals(line.Level, level, StringComparison.Ordinal)) continue;
            if (!matcher(line.Text)) continue;
            matched.Add(line);
        }

        LogMatchCount = matched.Count;
        LogTotalPages = Math.Max(1, (int)Math.Ceiling(matched.Count / (double)LogPageSize));
        LogPageNumber = Math.Min(Math.Max(1, LogPage), LogTotalPages);
        LogcatPage = matched
            .Skip((LogPageNumber - 1) * LogPageSize)
            .Take(LogPageSize)
            .ToArray();
    }

    /// <summary>Builds a line matcher: case-insensitive substring by default, or <c>/regex/</c> when
    /// wrapped in slashes (mirrors the retired client-side filter). The regex runs with a short
    /// timeout so a pathological pattern from the query string can't hang the request (ReDoS guard);
    /// an invalid pattern falls back to a substring match.</summary>
    private static Func<string, bool> CompileLogMatcher(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return static _ => true;
        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '/' && trimmed[^1] == '/')
        {
            try
            {
                var re = new Regex(trimmed[1..^1],
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
                return text =>
                {
                    try { return re.IsMatch(text); }
                    catch (RegexMatchTimeoutException) { return false; }
                };
            }
            catch (ArgumentException) { /* invalid pattern → fall through to substring */ }
        }
        return text => text.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Query string for a log pager / filter link on this report, preserving the level +
    /// search and jumping back to the log section via the <c>#log</c> fragment.</summary>
    public string BuildLogHref(int page)
    {
        var qs = new List<string>(3);
        if (!string.IsNullOrEmpty(LogLevel)) qs.Add("logLevel=" + Uri.EscapeDataString(LogLevel));
        if (!string.IsNullOrWhiteSpace(LogSearch)) qs.Add("logSearch=" + Uri.EscapeDataString(LogSearch));
        if (page > 1) qs.Add("logPage=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var path = Request.Path.ToString();
        return (qs.Count == 0 ? path : path + "?" + string.Join('&', qs)) + "#log";
    }

    private static string ClassifyLevel(string line)
    {
        // Android logcat: timestamps then a single level letter, e.g. "01-29 12:34:56.000  E AndroidRuntime: …"
        var idx = 0;
        while (idx < line.Length && line[idx] is ' ' or '\t' or '0'
               or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9'
               or '-' or ':' or '.' or '/' or 'T' or 'Z')
        {
            idx++;
        }
        if (idx < line.Length)
        {
            var ch = line[idx];
            if (ch is 'E' or 'W' or 'I' or 'D' or 'V' or 'F')
            {
                // Confirm it's a stand-alone level column (next char is whitespace).
                if (idx + 1 < line.Length && line[idx + 1] is ' ' or '\t')
                {
                    return ch switch
                    {
                        'E' or 'F' => "error",
                        'W' => "warn",
                        'I' => "info",
                        'D' => "debug",
                        'V' => "verbose",
                        _ => "other",
                    };
                }
            }
        }

        // Fallbacks for iOS / non-logcat content.
        if (line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Exception", StringComparison.Ordinal) ||
            line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("SIGABRT", StringComparison.Ordinal))
            return "error";
        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ANR", StringComparison.Ordinal))
            return "warn";
        if (line.Contains("info", StringComparison.OrdinalIgnoreCase))
            return "info";
        return "other";
    }

    public IActionResult OnGetDownloadJson(string platform, string fileName)
    {
        if (DeniedForClientLogin(platform, fileName)) return NotFound();
        return Download(platform, fileName, expectJson: true);
    }

    public IActionResult OnGetDownloadAttachment(string platform, string fileName)
    {
        // `fileName` is the JSON document; verify the client login owns it before deriving/serving the
        // sibling attachment (a client must not pull another tenant's logcat by filename).
        if (DeniedForClientLogin(platform, fileName)) return NotFound();
        // The `fileName` for a detail page is always the JSON document. Derive the sibling gzip name
        // rather than trusting a client-supplied attachment name: that keeps all path checks and the
        // allow-list entirely server-side.
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(baseName)) return NotFound();
        return Download(platform, baseName + ".log.gz", expectJson: false);
    }

    private IActionResult Download(string platform, string fileName, bool expectJson)
    {
        if (RSCPlatforms.TryCanonicalize(platform, _options) is not { } p) return NotFound();
        var stream = _store.OpenRead(p, fileName);
        if (stream is null) return NotFound();

        var contentType = expectJson ? "application/json" : "application/gzip";
        return File(stream, contentType, fileName);
    }

    /// <summary>The client slug this request's cookie is bound to (a <c>/ClientLogin</c> session), or
    /// null for an operator/admin session. Drives the per-report tenant guard below.</summary>
    private string? LoginClient =>
        User?.FindFirst(ReportService.Security.RSCTenantClaims.ClientId)?.Value is { Length: > 0 } c ? c : null;

    /// <summary>True when a client login is trying to reach a report that isn't its own client's.
    /// Operators (no client claim) are never denied.</summary>
    private bool DeniedForClientLogin(RSCStoredReport meta) =>
        LoginClient is { } client && !string.Equals(meta.ClientId, client, StringComparison.OrdinalIgnoreCase);

    /// <summary>Overload for the download handlers, which don't already hold the report's metadata:
    /// resolves it by its JSON file name and applies the same tenant guard. Unknown platform/file →
    /// denied (the caller 404s either way).</summary>
    private bool DeniedForClientLogin(string platform, string jsonFileName)
    {
        if (LoginClient is null) return false;
        if (RSCPlatforms.TryCanonicalize(platform, _options) is not { } p) return true;
        var meta = _store.List(p).FirstOrDefault(r => string.Equals(r.FileName, jsonFileName, StringComparison.Ordinal));
        return meta is null || DeniedForClientLogin(meta);
    }

    /// <summary>One decompressed logcat line with its inferred level. Filtered + paginated
    /// server-side (see <see cref="ApplyLogFilter"/>) before only the visible page reaches the view.</summary>
    public sealed record RSALogcatLine(int Number, string Level, string Text);

    /// <summary>At-a-glance summary of an attachment log: counts per level and the first error/fatal
    /// lines so operators can triage a large log without scanning the full dump.</summary>
    public sealed record RSALogcatOverview(
        int TotalLines,
        int ErrorCount,
        int WarnCount,
        int InfoCount,
        int DebugCount,
        int VerboseCount,
        int OtherCount,
        IReadOnlyList<RSALogcatLine> FirstErrors,
        string? FirstTimestamp,
        string? LastTimestamp);

    private const int FirstErrorsLimit = 10;

    private static RSALogcatOverview BuildOverview(IReadOnlyList<RSALogcatLine> lines)
    {
        int err = 0, warn = 0, info = 0, debug = 0, verbose = 0, other = 0;
        var firstErrors = new List<RSALogcatLine>();
        foreach (var line in lines)
        {
            switch (line.Level)
            {
                case "error":
                    err++;
                    if (firstErrors.Count < FirstErrorsLimit) firstErrors.Add(line);
                    break;
                case "warn": warn++; break;
                case "info": info++; break;
                case "debug": debug++; break;
                case "verbose": verbose++; break;
                default: other++; break;
            }
        }

        var firstTs = ExtractTimestamp(lines.Count > 0 ? lines[0].Text : null);
        var lastTs = ExtractTimestamp(lines.Count > 0 ? lines[^1].Text : null);

        return new RSALogcatOverview(
            TotalLines: lines.Count,
            ErrorCount: err,
            WarnCount: warn,
            InfoCount: info,
            DebugCount: debug,
            VerboseCount: verbose,
            OtherCount: other,
            FirstErrors: firstErrors,
            FirstTimestamp: firstTs,
            LastTimestamp: lastTs);
    }

    /// <summary>Best-effort timestamp lift from the start of a log line. Recognises Android logcat
    /// "MM-DD HH:MM:SS" and ISO-8601 "yyyy-MM-ddTHH:mm:ss" prefixes; returns null otherwise.</summary>
    private static string? ExtractTimestamp(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        // ISO 8601 "2026-01-01T12:34:56" — 19 leading characters.
        if (line.Length >= 19
            && line[4] == '-' && line[7] == '-' && line[10] == 'T'
            && line[13] == ':' && line[16] == ':')
        {
            return line[..19];
        }
        // Android logcat "01-29 12:34:56.000" — 18 leading characters.
        if (line.Length >= 18
            && line[2] == '-' && line[5] == ' '
            && line[8] == ':' && line[11] == ':' && line[14] == '.')
        {
            return line[..18];
        }
        return null;
    }

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private static JsonNode? TryParse(string raw)
    {
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RSAReportSummary ExtractSummary(JsonObject obj)
    {
        // Build a case-insensitive view once, so we accept both PascalCase legacy SDK payloads and
        // the camelCase envelope newer SDKs produce. Without this, the summary panel renders blank
        // for camelCase docs and the page degenerates to a raw JSON dump.
        var ci = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in obj) ci[kv.Key] = kv.Value;

        string? Get(string name) =>
            ci.TryGetValue(name, out var node) && node is not null
                ? node.GetValueKind() switch
                {
                    JsonValueKind.String => node.GetValue<string>(),
                    JsonValueKind.Null => null,
                    _ => node.ToJsonString(),
                }
                : null;

        var labels = (ci.TryGetValue("Labels", out var l) ? l : null) is JsonArray arr
            ? arr.Where(n => n is not null).Select(n => n!.ToString()).ToArray()
            : Array.Empty<string>();

        return new RSAReportSummary(
            Title: Get("Title"),
            Message: Get("Message"),
            DeviceModel: Get("DeviceModel"),
            Email: Get("Email"),
            PhoneNumber: Get("PhoneNumber") ?? Get("Phone"),
            PharmacyId: Get("PharmacyId"),
            Source: Get("Source"),
            AppVersion: Get("AppVersion"),
            FunctionalityImportance: Get("FunctionalityImportance"),
            Labels: labels,
            Kind: Get("Kind"),
            OccurredAt: Get("OccurredAt"));
    }

    private RSAReportFields? TryReadDoc(string platform, string fileName)
    {
        try
        {
            using var stream = _store.OpenRead(platform, fileName);
            if (stream is null) return null;
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    fields[p.Name] = p.Value.GetString()!;
                }
            }
            return new RSAReportFields(fields);
        }
        catch
        {
            return null;
        }
    }

    private sealed record RSAReportFields(IReadOnlyDictionary<string, string> Fields)
    {
        public string? GetField(string key) => Fields.TryGetValue(key, out var v) ? v : null;
        public DateTimeOffset? GetDateTime(string key) =>
            GetField(key) is { } s && DateTimeOffset.TryParse(s, out var dt) ? dt : null;
    }

    private static string? ExtractStackTrace(JsonObject obj)
    {
        // Look at the document root for any field whose name (case-insensitive) is one of the
        // common stack-trace spellings. The first non-empty string wins. We don't recurse — a
        // multi-line stack at the root is the convention, and walking arbitrary nesting would
        // surface unrelated stacks (e.g. a "stack" property on a nested log line).
        foreach (var kv in obj)
        {
            if (kv.Value is null) continue;
            var name = kv.Key.ToLowerInvariant();
            if (name is "stacktrace" or "stack_trace" or "stack")
            {
                if (kv.Value is JsonArray arr)
                {
                    return string.Join('\n', arr.Where(n => n is not null).Select(n => n!.ToString()));
                }
                if (kv.Value.GetValueKind() == JsonValueKind.String)
                {
                    var s = kv.Value.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
        }
        return null;
    }

    /// <summary>Structured fields lifted out of the report JSON for the summary panel.</summary>
    public sealed record RSAReportSummary(
        string? Title = null,
        string? Message = null,
        string? DeviceModel = null,
        string? Email = null,
        string? PhoneNumber = null,
        string? PharmacyId = null,
        string? Source = null,
        string? AppVersion = null,
        string? FunctionalityImportance = null,
        IReadOnlyList<string>? Labels = null,
        string? Kind = null,
        string? OccurredAt = null);

    public async Task<IActionResult> OnPostDeleteAsync(string platform, string fileName)
    {
        if (RSCPlatforms.TryCanonicalize(platform, _options) is not { } p) return NotFound();

        var removed = _store.Delete(p, fileName);
        await _audit.RecordAsync(HttpContext, "report.delete", success: removed, target: $"{p}/{fileName}");
        if (removed)
        {
            _logger.LogInformation(
                "Operator {Operator} deleted {Platform}/{File} from {Remote}",
                User?.Identity?.Name, p, fileName, HttpContext.Connection.RemoteIpAddress);
            TempData["Flash"] = $"Deleted {fileName}.";
        }
        else
        {
            _logger.LogWarning(
                "Operator {Operator} attempted to delete missing {Platform}/{File}",
                User?.Identity?.Name, p, fileName);
        }

        // "All reports" was retired; land back on the general report listing scoped to the platform.
        return RedirectToPage("/ProblemReports", new { platform = p });
    }

    /// <summary>
    /// Inline mapping upload from the report-detail page. Convenience handler so an operator
    /// looking at an obfuscated trace can drop in the matching <c>mapping.txt</c> without
    /// retyping the (platform, appVersion) on /Mappings. The handler delegates to the same
    /// store as the dedicated upload page; on success it redirects back to this same report
    /// so the page re-renders with the trace deobfuscated.
    /// </summary>
    /// <summary>
    /// Ephemeral, no-state deobfuscation. The operator drops a <c>mapping.txt</c> (and
    /// optionally an SDK consumer mapping for chained retrace) on the form, the page parses
    /// them in memory, applies them to the stack trace + logcat lines, and re-renders the
    /// page with the deobfuscated content. Nothing is written to disk; refreshing the URL
    /// returns to the obfuscated view. Same handler covers the single-mapping and chained
    /// cases — the SDK input is optional.
    /// </summary>
    public async Task<IActionResult> OnPostDeobfuscateAsync(
        string platform,
        string fileName,
        IFormFile? hostMapping,
        IFormFile? consumerMapping,
        CancellationToken ct)
    {
        // Run the standard GET flow first so the page model has the report metadata + logcat
        // populated; we then layer the in-memory mapping pass on top.
        var get = await OnGetAsync(platform, fileName, ct).ConfigureAwait(false);
        if (get is not PageResult) return get;

        if (hostMapping is null || hostMapping.Length == 0)
        {
            TempData["Flash"] = "select a host mapping.txt to deobfuscate this report";
            return Page();
        }

        var chain = new List<Mappings.RSCMappingFile>(capacity: 2);
        var summary = new List<string>(capacity: 2);
        try
        {
            var parser = new Mappings.RSCMappingParser();

            await using (var hostStream = hostMapping.OpenReadStream())
            {
                var parsed = parser.Parse(hostStream);
                if (parsed.ClassesByObfuscated.Count == 0)
                {
                    TempData["Flash"] = "host mapping parsed to zero classes — wrong file?";
                    return Page();
                }
                chain.Add(parsed);
                summary.Add($"host ({parsed.ClassesByObfuscated.Count:N0} classes)");
            }

            if (consumerMapping is { Length: > 0 })
            {
                await using var sdkStream = consumerMapping.OpenReadStream();
                var parsed = parser.Parse(sdkStream);
                if (parsed.ClassesByObfuscated.Count == 0)
                {
                    TempData["Flash"] = "SDK consumer mapping parsed to zero classes — wrong file?";
                    return Page();
                }
                chain.Add(parsed);
                summary.Add($"sdk consumer ({parsed.ClassesByObfuscated.Count:N0} classes)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse uploaded mapping for {Platform}/{File}", platform, fileName);
            TempData["Flash"] = "could not parse the uploaded mapping file";
            return Page();
        }

        var applier = new Mappings.RSCMappingChainApplier(chain);
        if (!string.IsNullOrEmpty(StackTrace))
        {
            DeobfuscatedStackTrace = applier.Apply(StackTrace);
        }
        var totalRewritten = applier.RewrittenFrames;
        if (LogcatLines.Count > 0)
        {
            // RSCMappingChainApplier.RewrittenFrames is per-Apply (it resets each call), so we
            // sum after each line rather than relying on a single accumulated property.
            var rewritten = new List<RSALogcatLine>(LogcatLines.Count);
            foreach (var line in LogcatLines)
            {
                var perLine = new Mappings.RSCMappingChainApplier(chain);
                var newText = perLine.Apply(line.Text);
                totalRewritten += perLine.RewrittenFrames;
                rewritten.Add(newText == line.Text ? line : new RSALogcatLine(line.Number, line.Level, newText));
            }
            LogcatLines = rewritten;
            // Re-run the filter so the rendered page reflects the deobfuscated text (and any
            // logLevel/logSearch carried through the POST), not the obfuscated lines from OnGet.
            ApplyLogFilter();
        }
        RewrittenFrameCount = totalRewritten;

        AppliedMappingSummary = string.Join(" + ", summary);
        await _audit.RecordAsync(HttpContext, "mapping.deobfuscate", success: true,
            target: $"{Platform}/{FileName} [{RewrittenFrameCount} frame(s)]");
        return Page();
    }
}
