using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ReportService.Admin.TagHelpers;

/// <summary>
/// Standardises admin table column headers. Activates on any <c>&lt;th&gt;</c> that carries a
/// <c>tip</c> and/or <c>sort</c> attribute; plain headers are left untouched.
///
/// <para><c>tip="..."</c> adds a hover/focus tooltip describing the column's values. It renders as a
/// native <c>title</c> attribute on the header: browser-drawn tooltips are never clipped by the
/// surrounding <c>table { overflow: hidden }</c> (a CSS popover would be), need no JavaScript, and are
/// exposed to assistive tech as the cell's description — all without violating the admin CSP
/// (<c>style-src 'self'</c>: no inline styles anywhere).</para>
///
/// <para><c>sort="text|num|bytes|date"</c> marks the column client-sortable. The header label is wrapped
/// in a real <c>&lt;button&gt;</c> (keyboard-operable) with a sort caret; <c>sortable-tables.js</c> wires
/// the click/keyboard handler and toggles <c>aria-sort</c>. <c>bytes</c> is normalised to <c>num</c> — byte
/// cells carry the raw count in <c>data-sort-value</c> so "9 B" sorts below "12 KiB".</para>
///
/// Apply only to tables whose full dataset is in the DOM. Server-paginated listings should set
/// <c>tip</c> but not <c>sort</c>, since a client sort would silently reorder one page only.
/// </summary>
[HtmlTargetElement("th", Attributes = TipAttribute)]
[HtmlTargetElement("th", Attributes = SortAttribute)]
public sealed class RSATableHeaderTagHelper : TagHelper
{
    private const string TipAttribute = "tip";
    private const string SortAttribute = "sort";

    /// <summary>Plain-text description of the column's values, shown on hover/focus.</summary>
    [HtmlAttributeName(TipAttribute)]
    public string? Tip { get; set; }

    /// <summary>Sort kind: <c>text</c>, <c>num</c>, <c>bytes</c>, or <c>date</c>. Omit for non-sortable columns.</summary>
    [HtmlAttributeName(SortAttribute)]
    public string? Sort { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        // The header label is whatever the page authored between the <th> tags (plain text in practice).
        var label = (await output.GetChildContentAsync()).GetContent();

        var sortType = NormaliseSort(Sort);
        var inner = new StringBuilder();

        if (sortType is not null)
        {
            output.Attributes.SetAttribute("data-sort", sortType);
            output.Attributes.SetAttribute("aria-sort", "none");
            AddClass(output, "th-sortable");
            inner.Append("<button type=\"button\" class=\"th-btn\">")
                 .Append(label)
                 .Append("<span class=\"th-caret\" aria-hidden=\"true\"></span></button>");
        }
        else
        {
            inner.Append("<span class=\"th-label\">").Append(label).Append("</span>");
        }

        if (!string.IsNullOrWhiteSpace(Tip))
        {
            // Expose the description via a data attribute; header-tooltips.js renders a styled tooltip
            // above the header on hover/focus. A native `title` is slow and easy to miss, and a pure-CSS
            // bubble would be clipped by the table's `overflow: hidden` — a body-level JS tooltip escapes
            // both problems. The header label sits directly below the tooltip, so no label prefix needed.
            output.Attributes.SetAttribute("data-th-tip", Tip!.Trim());
            AddClass(output, "th-help");
        }

        output.Content.SetHtmlContent(inner.ToString());

        // Strip our authoring-only attributes so they never reach the rendered DOM.
        RemoveAttribute(output, TipAttribute);
        RemoveAttribute(output, SortAttribute);
    }

    private static string? NormaliseSort(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "text" => "text",
        "num" or "number" or "int" or "bytes" or "size" => "num",
        "date" or "time" or "datetime" => "date",
        _ => null,
    };

    private static void AddClass(TagHelperOutput output, string cls)
    {
        if (output.Attributes.TryGetAttribute("class", out var existing))
        {
            var value = existing.Value?.ToString() ?? string.Empty;
            output.Attributes.SetAttribute("class", string.IsNullOrEmpty(value) ? cls : $"{value} {cls}");
        }
        else
        {
            output.Attributes.SetAttribute("class", cls);
        }
    }

    private static void RemoveAttribute(TagHelperOutput output, string name)
    {
        if (output.Attributes.TryGetAttribute(name, out var attr))
        {
            output.Attributes.Remove(attr);
        }
    }
}
