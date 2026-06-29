namespace ReportService.Admin.Services;

/// <summary>One entry in a chapter's table of contents — a heading with its anchor id and depth.</summary>
public sealed record RSADocHeading(string Id, string Text, int Level);

/// <summary>
/// A single documentation chapter, ready to render as one tab on the <c>/Documentation</c> page.
/// <paramref name="Slug"/> is a stable, URL-safe id (the source filename, or "readme"); it also
/// prefixes every heading id in <paramref name="HtmlBody"/> so anchors stay unique when all chapters
/// share one DOM. <paramref name="Label"/> is the short tab caption; <paramref name="Title"/> is the
/// chapter's own first heading.
/// </summary>
public sealed record RSADocChapter(
    string Slug,
    string Label,
    string Title,
    string HtmlBody,
    IReadOnlyList<RSADocHeading> Toc);

/// <summary>The full bundled documentation set: the README landing chapter followed by guide chapters.</summary>
public sealed record RSADocSet(IReadOnlyList<RSADocChapter> Chapters);

/// <summary>
/// Renders the bundled repo docs (<c>README.md</c> + <c>guide/*.md</c>, copied into the admin output
/// by the csproj) into a navigable set of chapters for the <c>/Documentation</c> page.
/// </summary>
public interface IRSADocsService
{
    /// <summary>
    /// Loads and renders every bundled doc from the configured docs root. Returns <c>null</c> when no
    /// docs are present — callers should treat that as 404.
    /// </summary>
    RSADocSet? RenderDocs();
}
