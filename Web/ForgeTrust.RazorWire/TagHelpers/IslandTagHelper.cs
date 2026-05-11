using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// A TagHelper that renders a Turbo Frame as a "RazorWire Island", enabling partial page updates and client-side module mounting.
/// </summary>
[HtmlTargetElement("rw:island")]
public class IslandTagHelper : TagHelper
{
    /// <summary>
    /// The unique identifier for the island, which becomes the id of the rendered turbo-frame.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The source URL for the turbo-frame content.
    /// </summary>
    public string? Src { get; set; }

    /// <summary>
    /// The loading strategy for the turbo-frame (e.g., "eager", "lazy").
    /// </summary>
    public string? Loading { get; set; }

    /// <summary>
    /// If true, the rendered element will have the 'data-turbo-permanent' attribute.
    /// </summary>
    public bool Permanent { get; set; }

    /// <summary>
    /// If true, enables Stale-While-Revalidate behavior via 'data-rw-swr'.
    /// </summary>
    public bool Swr { get; set; }

    /// <summary>
    /// The name for the CSS View Transition, applied as 'view-transition-name' in the style attribute.
    /// </summary>
    public string? TransitionName { get; set; }

    /// <summary>
    /// The name of the client-side module to export/expose.
    /// </summary>
    public string? Export { get; set; }

    /// <summary>
    /// The path to the client-side module to mount on this island.
    /// </summary>
    [HtmlAttributeName("client-module")]
    public string? ClientModule { get; set; }

    /// <summary>
    /// The mounting strategy for the client module (e.g., "load", "visible", "idle").
    /// </summary>
    [HtmlAttributeName("client-strategy")]
    public string? ClientStrategy { get; set; }

    /// <summary>
    /// Initial properties (JSON) to pass to the client module's mount function.
    /// </summary>
    [HtmlAttributeName("client-props")]
    public string? ClientProps { get; set; }

    /// <summary>
    /// Renders a <c>&lt;turbo-frame&gt;</c> element whose attributes are populated from the tag helper's properties.
    /// </summary>
    /// <remarks>
    /// Validates that <see cref="Id"/> is not null, empty, or whitespace and sets attributes such as id, src, loading, data-turbo-permanent, data-rw-swr, style (view-transition-name), data-rw-export, and client-related data attributes when the corresponding properties are provided.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <see cref="Id"/> is null, empty, or consists only of white-space characters.</exception>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "turbo-frame";
        output.TagMode = TagMode.StartTagAndEndTag;

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("The 'id' attribute is required for rw:island.", nameof(Id));
        }

        output.Attributes.SetAttribute("id", Id);

        if (!string.IsNullOrEmpty(Src))
        {
            output.Attributes.SetAttribute("src", Src);
        }

        if (!string.IsNullOrEmpty(Loading))
        {
            output.Attributes.SetAttribute("loading", Loading);
        }

        if (Permanent || Swr)
        {
            output.Attributes.SetAttribute("data-turbo-permanent", "");
        }

        if (Swr)
        {
            output.Attributes.SetAttribute("data-rw-swr", "true");
        }

        if (!string.IsNullOrWhiteSpace(TransitionName))
        {
            var style = output.Attributes["style"]?.Value.ToString() ?? "";
            if (!style.TrimEnd().EndsWith(";"))
            {
                style += ";";
            }

            // Sanitize transition name using centralized logic
            var safeTransitionName = StringUtils.ToSafeId(TransitionName);

            // Encoding is still a good practice even if ToSafeId ensures safe chars
            var encoded = HtmlEncoder.Default.Encode(safeTransitionName);
            output.Attributes.SetAttribute("style", $"{style} view-transition-name: {encoded};");
        }

        if (!string.IsNullOrEmpty(Export))
        {
            output.Attributes.SetAttribute("data-rw-export", Export);
        }

        if (!string.IsNullOrEmpty(ClientModule))
        {
            output.Attributes.SetAttribute("data-rw-module", ClientModule);

            if (!string.IsNullOrEmpty(ClientStrategy))
            {
                output.Attributes.SetAttribute("data-rw-strategy", ClientStrategy);
            }

            if (!string.IsNullOrEmpty(ClientProps))
            {
                output.Attributes.SetAttribute("data-rw-props", ClientProps);
            }
        }
    }
}
