using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.AppSurface.Web.TagHelpers;

/// <summary>
/// Emits AppSurface-managed PWA head metadata for MVC and Razor layouts.
/// </summary>
/// <remarks>
/// Render <c>&lt;appsurface:pwa-head /&gt;</c> inside the document <c>&lt;head&gt;</c> after enabling
/// <see cref="PwaOptions"/>. The helper emits install metadata only when <see cref="PwaOptions.Enabled"/> is enabled,
/// emits inert worker path/scope metadata for active worker capabilities, and loads the inert registration helper only
/// when push is enabled. It never registers a worker, displays an install prompt, requests notification permission, or
/// creates a push subscription; the application invokes registration deliberately.
/// </remarks>
[HtmlTargetElement("appsurface:pwa-head")]
public sealed class AppSurfacePwaHeadTagHelper : TagHelper
{
    private readonly IFileVersionProvider _fileVersionProvider;
    private readonly PwaOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfacePwaHeadTagHelper"/> class.
    /// </summary>
    /// <param name="fileVersionProvider">Version provider used for local icon URLs.</param>
    /// <param name="options">Effective AppSurface PWA options.</param>
    public AppSurfacePwaHeadTagHelper(IFileVersionProvider fileVersionProvider, PwaOptions options)
    {
        _fileVersionProvider = fileVersionProvider ?? throw new ArgumentNullException(nameof(fileVersionProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Gets or sets the view context supplied by MVC.
    /// </summary>
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;
        if (!_options.HasAnySurfaceEnabled)
        {
            output.Content.SetHtmlContent(string.Empty);
            return;
        }

        output.Content.SetHtmlContent(
            PwaHeadMetadataBuilder.Build(
                ViewContext.HttpContext.Request.PathBase,
                _options,
                _fileVersionProvider));
    }
}
