using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using ReportService.Admin.Options;

namespace ReportService.Admin.Services;

/// <summary>
/// File-system implementation of <see cref="IRSADocsService"/>. Assembles the documentation shown at
/// <c>/Documentation</c> from <see cref="RSAAdminOptions.DocsRoot"/> (resolved against the binary's
/// base directory when relative): the landing <c>README.md</c> first, then every modular chapter under
/// <c>guide/</c> in filename order. Each source file becomes one <see cref="RSADocChapter"/> (its own
/// tab), so the page is navigable instead of one endless scroll.
/// </summary>
/// <remarks>
/// The Markdig pipeline uses <c>DisableHtml()</c> so raw HTML in source is escaped rather than passed
/// through (defence in depth behind the admin's strict same-origin CSP). <c>UseAdvancedExtensions</c>
/// gives every heading a stable auto-id; we then prefix those ids per chapter (<c>slug--id</c>) so the
/// table-of-contents anchors and the heading targets stay unique once all chapters live in one DOM.
///
/// Docs are static for the process lifetime (they ship in the image and are never edited at runtime),
/// so the assembled set is rendered once and cached in this singleton via a <see cref="Lazy{T}"/> with
/// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> — only the first request pays the parse
/// + render cost, and concurrent first requests render exactly once.
/// </remarks>
public sealed class RSADocsService : IRSADocsService
{
    private const string ReadmeFileName = "README.md";
    private const string GuideFolderName = "guide";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    // DisableHtml() guarantees these only ever match Markdig-generated markup, never escaped source.
    private static readonly Regex HeadingIdRegex = new("<(h[1-6]) id=\"", RegexOptions.Compiled);
    private static readonly Regex IntraAnchorRegex = new("href=\"#", RegexOptions.Compiled);
    private static readonly Regex OrdinalPrefixRegex = new(@"^\d+\.\s*", RegexOptions.Compiled);

    private readonly string _root;
    private readonly ILogger<RSADocsService> _log;
    private readonly Lazy<RSADocSet?> _rendered;

    public RSADocsService(RSAAdminOptions options, ILogger<RSADocsService> log)
    {
        _log = log;
        var configured = string.IsNullOrWhiteSpace(options.DocsRoot) ? "admin-docs" : options.DocsRoot;
        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
        _rendered = new Lazy<RSADocSet?>(RenderDocsUncached, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RSADocSet? RenderDocs() => _rendered.Value;

    private RSADocSet? RenderDocsUncached()
    {
        var chapters = new List<RSADocChapter>();

        var readmePath = Path.Combine(_root, ReadmeFileName);
        if (File.Exists(readmePath))
        {
            chapters.Add(BuildChapter("readme", "README", File.ReadAllText(readmePath)));
        }
        else
        {
            _log.LogWarning("README not found at '{Path}'.", readmePath);
        }

        // Modular chapters (guide/*.md) in filename order. Numeric prefixes (01-, 02-, …) drive the
        // sequence and become the slug; the prefix is stripped from the tab label.
        var guideDir = Path.Combine(_root, GuideFolderName);
        if (Directory.Exists(guideDir))
        {
            foreach (var file in Directory.EnumerateFiles(guideDir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
            {
                var slug = Path.GetFileNameWithoutExtension(file);
                chapters.Add(BuildChapter(slug, label: null, File.ReadAllText(file)));
            }
        }

        if (chapters.Count == 0)
        {
            _log.LogWarning("No documentation found under '{Root}'; /Documentation will 404.", _root);
            return null;
        }

        return new RSADocSet(chapters);
    }

    private static RSADocChapter BuildChapter(string slug, string? label, string markdown)
    {
        var doc = Markdown.Parse(markdown, Pipeline);

        var title = FirstHeadingText(doc) ?? slug;
        label ??= OrdinalPrefixRegex.Replace(title, string.Empty).Trim();

        // Table of contents: section headings (h2/h3) within this chapter, ids prefixed to match.
        var toc = new List<RSADocHeading>();
        foreach (var heading in doc.Descendants<HeadingBlock>())
        {
            if (heading.Level is < 2 or > 3) continue;
            var id = heading.GetAttributes().Id;
            if (string.IsNullOrEmpty(id)) continue;
            toc.Add(new RSADocHeading($"{slug}--{id}", ExtractText(heading.Inline), heading.Level));
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        // Namespace heading ids (and any intra-doc anchors pointing at them) to this chapter so they
        // remain unique once every chapter's HTML coexists in the same page.
        html = HeadingIdRegex.Replace(html, $"<$1 id=\"{slug}--");
        html = IntraAnchorRegex.Replace(html, $"href=\"#{slug}--");

        return new RSADocChapter(slug, label, title, html, toc);
    }

    private static string? FirstHeadingText(MarkdownDocument doc)
    {
        foreach (var heading in doc.Descendants<HeadingBlock>())
        {
            return ExtractText(heading.Inline);
        }
        return null;
    }

    private static string ExtractText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var node in inline.Descendants())
        {
            switch (node)
            {
                case LiteralInline literal: sb.Append(literal.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
            }
        }
        return sb.ToString().Trim();
    }
}
