using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ReportService.Admin.TagHelpers;

/// <summary>
/// Dashboard stat tile. <c>&lt;tile label="…"&gt;value&lt;/tile&gt;</c> renders the
/// <c>.tile / .tile-label / .tile-value</c> structure once, instead of every dashboard hand-rolling
/// the three-div block. The value is the inner content, so rich values (mono spans, formatted
/// numbers, composites) all work.
///
/// <para><c>href="…"</c> turns the tile into a link (the dashboard shortcuts on the home page).
/// <c>variant="error|warn"</c> tints it (the log-overview counts). <c>value-class="mono"</c> adds a
/// class to the value div.</para>
/// </summary>
[HtmlTargetElement("tile")]
public sealed class RSATileTagHelper : TagHelper
{
    /// <summary>Small uppercase caption shown above the value.</summary>
    public string Label { get; set; } = "";

    /// <summary>Optional destination — renders the tile as an anchor instead of a div.</summary>
    public string? Href { get; set; }

    /// <summary>Optional emphasis: <c>error</c> or <c>warn</c> tints the tile.</summary>
    public string? Variant { get; set; }

    /// <summary>Extra class(es) for the value div, e.g. <c>mono</c>.</summary>
    [HtmlAttributeName("value-class")]
    public string? ValueClass { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var value = (await output.GetChildContentAsync()).GetContent();

        output.TagName = Href is null ? "div" : "a";
        output.TagMode = TagMode.StartTagAndEndTag;

        var cls = "tile";
        if (!string.IsNullOrWhiteSpace(Variant))
        {
            cls += " tile-" + Variant.Trim().ToLowerInvariant();
        }
        output.Attributes.SetAttribute("class", cls);
        if (Href is not null)
        {
            output.Attributes.SetAttribute("href", Href);
        }

        var valueCls = string.IsNullOrWhiteSpace(ValueClass) ? "tile-value" : "tile-value " + ValueClass.Trim();
        var sb = new StringBuilder();
        sb.Append("<div class=\"tile-label\">").Append(HtmlEncoder.Default.Encode(Label)).Append("</div>");
        sb.Append("<div class=\"").Append(valueCls).Append("\">").Append(value).Append("</div>");
        output.Content.SetHtmlContent(sb.ToString());
    }
}
