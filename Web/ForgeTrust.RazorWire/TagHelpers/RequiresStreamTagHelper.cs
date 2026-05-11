using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// A TagHelper that marks an element as requiring a specific Turbo Stream to be established before becoming active.
/// </summary>
[HtmlTargetElement(Attributes = "requires-stream")]
public class RequiresStreamTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the name or identifier of the stream required by this element.
    /// </summary>
    [HtmlAttributeName("requires-stream")]
    public string? RequiresStream { get; set; }

    /// <summary>
    /// Applies stream-requirement attributes and disables the element when a requires-stream value is present.
    /// </summary>
    /// <remarks>
    /// If <see cref="RequiresStream"/> is null or whitespace, the output is left unchanged. Otherwise adds the following attributes to the element:
    /// <list type="bullet">
    /// <item><description><c>data-rw-requires-stream</c> set to the <see cref="RequiresStream"/> value</description></item>
    /// <item><description><c>aria-disabled</c> to <c>"true"</c></description></item>
    /// <item><description><c>disabled</c> to <c>"disabled"</c> (only for supported form elements)</description></item>
    /// </list>
    /// The element is therefore disabled until client-side code removes or updates these attributes.
    /// </remarks>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (string.IsNullOrWhiteSpace(RequiresStream))
        {
            return;
        }

        // Add the data attribute for JS to pick up
        output.Attributes.SetAttribute("data-rw-requires-stream", RequiresStream);
        output.Attributes.SetAttribute("aria-disabled", "true");

        // Only add the standard disabled attribute for form elements where it is valid
        // For others, the aria-disabled attribute + JS handling is sufficient (and creating visual styles based on it)
        var tagName = output.TagName?.ToLowerInvariant() ?? context.TagName.ToLowerInvariant();
        if (tagName is "button" or "input" or "select" or "textarea" or "optgroup" or "option" or "fieldset")
        {
            output.Attributes.SetAttribute("disabled", "disabled");
        }

        // Remove the source attribute so it doesn't render
        output.Attributes.RemoveAll("requires-stream");
    }
}
