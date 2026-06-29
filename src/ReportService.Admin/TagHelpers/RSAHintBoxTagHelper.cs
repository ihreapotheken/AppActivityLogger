using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ReportService.Admin.TagHelpers;

/// <summary>
/// Reusable, always-visible help callout. <c>&lt;hint-box&gt;…&lt;/hint-box&gt;</c> renders a short
/// "how to read this" note in an accent-bordered info box — centralising the <c>.hint-box</c> markup
/// so pages don't hand-roll (and drift on) the structure. The explanation is shown inline (no toggle
/// to expand); keep the copy brief enough that it earns its permanent place on the page.
///
/// <para>Usage: <c>&lt;hint-box&gt;&lt;p&gt;…explanation…&lt;/p&gt;&lt;/hint-box&gt;</c>. The inner content is
/// rendered normally (tag helpers, links, code spans all work).</para>
///
/// <para>Set <c>&lt;hint-box docs-href="/Documentation#…"&gt;</c> to append a "Full details in Docs"
/// link below the explanation. Keep each box's copy to a couple of plain-language sentences and let
/// the link carry the operator to the authoritative chapter for the deeper detail.</para>
/// </summary>
[HtmlTargetElement("hint-box")]
public sealed class RSAHintBoxTagHelper : TagHelper
{
    /// <summary>
    /// Optional deep link to the matching Docs chapter/section (e.g.
    /// <c>/Documentation#06-analytics--reading-retention</c>; note the rendered heading id drops the
    /// chapter's numeric prefix). When set, a "Full details in Docs" link is appended under the body;
    /// when null/blank the box renders its copy only.
    /// </summary>
    public string? DocsHref { get; set; }

    /// <summary>Caption for the Docs link; the "→" arrow is appended automatically.</summary>
    public string DocsText { get; set; } = "Full details in Docs";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        // Render the authored inner markup (processes nested tag helpers / Razor).
        var body = (await output.GetChildContentAsync()).GetContent();

        output.TagName = "div";
        output.Attributes.SetAttribute("class", "hint-box");

        var sb = new StringBuilder();
        sb.Append(body);
        if (!string.IsNullOrWhiteSpace(DocsHref))
        {
            sb.Append("<p class=\"hint-docs\"><a href=\"")
              .Append(HtmlEncoder.Default.Encode(DocsHref))
              .Append("\">")
              .Append(HtmlEncoder.Default.Encode(DocsText))
              .Append(" →</a></p>");
        }
        output.Content.SetHtmlContent(sb.ToString());
    }
}
