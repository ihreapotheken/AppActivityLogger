namespace ReportService.Admin.Services;

/// <summary>The rendered README, ready to drop into a Razor view as raw HTML.</summary>
public sealed record RSADocView(string Title, string HtmlBody);

/// <summary>
/// Renders the bundled repo <c>README.md</c> (copied into the admin output by the csproj) into
/// safe HTML for the <c>/Documentation</c> page.
/// </summary>
public interface IRSADocsService
{
    /// <summary>
    /// Loads <c>README.md</c> from the configured docs root and renders it. Returns <c>null</c>
    /// when the file is missing — callers should treat that as 404.
    /// </summary>
    RSADocView? RenderReadme();
}
