using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ReportService.Admin.TagHelpers;

/// <summary>
/// Standard "nothing to show here" notice. <c>&lt;empty-state&gt;…&lt;/empty-state&gt;</c> renders a
/// <c>&lt;p class="empty-state"&gt;</c> so every listing's no-rows message reads the same and is styled
/// from one place — replacing the ad-hoc <c>&lt;p class="muted"&gt;No …&lt;/p&gt;</c> repeated across the
/// listings. The inner content is rendered normally, so embedded <c>&lt;code&gt;</c>/<c>&lt;a&gt;</c>
/// markup in the message still works.
/// </summary>
[HtmlTargetElement("empty-state")]
public sealed class RSAEmptyStateTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var body = (await output.GetChildContentAsync()).GetContent();
        output.TagName = "p";
        output.Attributes.SetAttribute("class", "empty-state");
        output.Content.SetHtmlContent(body);
    }
}
