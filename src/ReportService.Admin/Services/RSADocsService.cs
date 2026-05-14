using Markdig;
using ReportService.Admin.Options;

namespace ReportService.Admin.Services;

/// <summary>
/// File-system implementation of <see cref="IRSADocsService"/>. Reads <c>README.md</c> from
/// <see cref="RSAAdminOptions.DocsRoot"/> (resolved against the binary's base directory when
/// relative) and renders it with Markdig.
/// </summary>
/// <remarks>
/// The Markdig pipeline is built with <c>DisableHtml()</c> so raw HTML in source is escaped
/// rather than passed through. The admin's strict same-origin CSP would also block any injected
/// <c>&lt;script&gt;</c>, but disabling pass-through is defense in depth.
/// </remarks>
public sealed class RSADocsService : IRSADocsService
{
    private const string ReadmeFileName = "README.md";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private readonly string _root;
    private readonly ILogger<RSADocsService> _log;

    public RSADocsService(RSAAdminOptions options, ILogger<RSADocsService> log)
    {
        _log = log;
        var configured = string.IsNullOrWhiteSpace(options.DocsRoot) ? "admin-docs" : options.DocsRoot;
        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
    }

    public RSADocView? RenderReadme()
    {
        var path = Path.Combine(_root, ReadmeFileName);
        if (!File.Exists(path))
        {
            _log.LogWarning("README not found at '{Path}'; /Documentation will 404.", path);
            return null;
        }

        var source = File.ReadAllText(path);
        var html = Markdown.ToHtml(source, Pipeline);
        return new RSADocView(ReadTitle(path), html);
    }

    private static string ReadTitle(string path)
    {
        // First ATX heading wins; skip the optional YAML frontmatter block.
        try
        {
            using var reader = new StreamReader(path);
            string? line;
            var inFrontmatter = false;
            var lineNo = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                if (lineNo == 1 && line.Trim() == "---")
                {
                    inFrontmatter = true;
                    continue;
                }
                if (inFrontmatter)
                {
                    if (line.Trim() == "---") inFrontmatter = false;
                    continue;
                }
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                    return trimmed[2..].Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                    break;
            }
        }
        catch (IOException)
        {
            // Falls through to the filename fallback below.
        }

        return "README";
    }
}
