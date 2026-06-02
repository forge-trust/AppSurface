using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// Marks an application-authored element as a RazorWire same-page navigation root.
/// </summary>
/// <remarks>
/// The helper preserves the host element and emits <c>data-rw-page-nav="true"</c>. RazorWire owns only behavior and
/// state attributes; the host application owns layout, breakpoints, colors, and spacing.
/// </remarks>
[HtmlTargetElement(Attributes = "rw-page-nav")]
public sealed class PageNavigationRootTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets a value indicating whether page navigation enhancement is enabled.
    /// </summary>
    [HtmlAttributeName("rw-page-nav")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Emits the RazorWire page-navigation root marker when enabled.
    /// </summary>
    /// <param name="context">The current tag helper context.</param>
    /// <param name="output">The tag helper output to modify.</param>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-page-nav");
        if (Enabled)
        {
            output.Attributes.SetAttribute("data-rw-page-nav", "true");
        }
    }
}

/// <summary>
/// Marks a normal same-page anchor as part of the nearest RazorWire page-navigation root.
/// </summary>
[HtmlTargetElement("a", Attributes = "rw-page-nav-link")]
public sealed class PageNavigationLinkTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets a value indicating whether the link should be managed by RazorWire page navigation.
    /// </summary>
    [HtmlAttributeName("rw-page-nav-link")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Emits the page-navigation link marker when enabled.
    /// </summary>
    /// <param name="context">The current tag helper context.</param>
    /// <param name="output">The tag helper output to modify.</param>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-page-nav-link");
        if (Enabled)
        {
            output.Attributes.SetAttribute("data-rw-page-nav-link", "true");
        }
    }
}

/// <summary>
/// Marks a button as the optional toggle for an application-owned page-navigation panel.
/// </summary>
[HtmlTargetElement("button", Attributes = "rw-page-nav-toggle")]
public sealed class PageNavigationToggleTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the optional id of the panel controlled by this toggle.
    /// </summary>
    /// <remarks>
    /// When a non-empty value other than <c>true</c> is supplied, the helper emits it as <c>aria-controls</c> unless
    /// the button already defines <c>aria-controls</c>.
    /// </remarks>
    [HtmlAttributeName("rw-page-nav-toggle")]
    public string? Controls { get; set; }

    /// <summary>
    /// Emits the page-navigation toggle marker and optional <c>aria-controls</c> relationship.
    /// </summary>
    /// <param name="context">The current tag helper context.</param>
    /// <param name="output">The tag helper output to modify.</param>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-page-nav-toggle");
        output.Attributes.SetAttribute("data-rw-page-nav-toggle", "true");

        if (!string.IsNullOrWhiteSpace(Controls)
            && !string.Equals(Controls, "true", StringComparison.OrdinalIgnoreCase)
            && !output.Attributes.ContainsName("aria-controls"))
        {
            output.Attributes.SetAttribute("aria-controls", Controls);
        }

        if (!output.Attributes.ContainsName("type"))
        {
            output.Attributes.SetAttribute("type", "button");
        }
    }
}

/// <summary>
/// Marks an application-authored element as the optional panel controlled by page-navigation toggle state.
/// </summary>
[HtmlTargetElement(Attributes = "rw-page-nav-panel")]
public sealed class PageNavigationPanelTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets a value indicating whether the panel should be managed by RazorWire page navigation.
    /// </summary>
    [HtmlAttributeName("rw-page-nav-panel")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Emits the page-navigation panel marker when enabled.
    /// </summary>
    /// <param name="context">The current tag helper context.</param>
    /// <param name="output">The tag helper output to modify.</param>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-page-nav-panel");
        if (Enabled)
        {
            output.Attributes.SetAttribute("data-rw-page-nav-panel", "true");
        }
    }
}
