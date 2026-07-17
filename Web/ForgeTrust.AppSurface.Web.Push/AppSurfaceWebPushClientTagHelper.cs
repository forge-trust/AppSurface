using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>Renders the CSP-compatible versioned AppSurface Web Push browser client script.</summary>
[HtmlTargetElement("appsurface:web-push-client")]
public sealed class AppSurfaceWebPushClientTagHelper : TagHelper
{
    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        output.TagName = "script";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute(
            "src",
            $"{ViewContext.HttpContext.Request.PathBase}{AppSurfaceWebPushClientAsset.Path}?v={AppSurfaceWebPushClientAsset.Version}");
    }

    /// <summary>Gets or sets the current Razor view context.</summary>
    [Microsoft.AspNetCore.Mvc.ViewFeatures.ViewContext]
    [HtmlAttributeNotBound]
    public Microsoft.AspNetCore.Mvc.Rendering.ViewContext ViewContext { get; set; } = null!;
}
