using ForgeTrust.AppSurface.Core.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// A TagHelper that automatically applies version hashing to script and link tags
/// that reference local files, if the asp-append-version attribute is missing.
/// </summary>
[HtmlTargetElement("script", Attributes = "src")]
[HtmlTargetElement("link", Attributes = "href")]
public class AutoAssetVersioningTagHelper : TagHelper
{
    private const string AspAppendVersionAttributeName = "asp-append-version";

    private readonly IFileVersionProvider _fileVersionProvider;

    /// <inheritdoc />
    public override int Order => 1000; // Run after standard TagHelpers (Order: 0) to avoid double processing

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoAssetVersioningTagHelper"/> class.
    /// </summary>
    /// <param name="fileVersionProvider">The file version provider.</param>
    public AutoAssetVersioningTagHelper(IFileVersionProvider fileVersionProvider)
    {
        _fileVersionProvider = fileVersionProvider ?? throw new ArgumentNullException(nameof(fileVersionProvider));
    }

    /// <summary>
    /// Gets or sets the view context.
    /// </summary>
    /// <remarks>
    /// This property is automatically set by the framework when the TagHelper is created.
    /// </remarks>
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        // If 'asp-append-version' is present in the source, standard TagHelpers may handle it.
        // We check 'context.AllAttributes' because 'output.Attributes' might have it removed by then.
        if (context.AllAttributes.ContainsName(AspAppendVersionAttributeName))
        {
            return;
        }

        // Cache PathBase to avoid repeated access
        var pathBase = ViewContext.HttpContext.Request.PathBase;

        if (string.Equals(output.TagName, "script", StringComparison.OrdinalIgnoreCase))
        {
            ProcessScript(output, pathBase);
        }
        else if (string.Equals(output.TagName, "link", StringComparison.OrdinalIgnoreCase))
        {
            ProcessLink(output, pathBase);
        }
    }

    private void ProcessScript(TagHelperOutput output, PathString pathBase)
    {
        if (output.Attributes.TryGetAttribute("src", out var srcAttribute))
        {
            var src = srcAttribute.Value?.ToString();
            if (IsLocal(src))
            {
                var newSrc = _fileVersionProvider.AddFileVersionToPath(pathBase, src!);
                output.Attributes.SetAttribute("src", newSrc);
            }
        }
    }

    private void ProcessLink(TagHelperOutput output, PathString pathBase)
    {
        // Only version stylesheets
        var relAttribute = output.Attributes["rel"];
        if (relAttribute?.Value is not string relValue)
        {
            return;
        }

        var isStylesheet = relValue
            .SplitOnWhiteSpace()
            .Any(t => string.Equals(t, "stylesheet", StringComparison.OrdinalIgnoreCase));

        if (!isStylesheet)
        {
            return;
        }

        if (output.Attributes.TryGetAttribute("href", out var hrefAttribute))
        {
            var href = hrefAttribute.Value?.ToString() ?? string.Empty;
            if (IsLocal(href))
            {
                output.Attributes.SetAttribute(
                    "href",
                    _fileVersionProvider.AddFileVersionToPath(pathBase, href));
            }
        }
    }

    /// <summary>
    /// Determines if the provided path is a local path suitable for versioning.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>
    /// <c>true</c> if the path is local (starts with '/' or '~') and is not protocol-relative;
    /// otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Relative paths (e.g. "css/site.css") are excluded because they are ambiguous
    /// without knowing the current request path context, which complicates server-side resolution.
    /// Use root-relative ('/') or app-relative ('~') paths for automatic versioning.
    /// </remarks>
    private static bool IsLocal(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (path.StartsWith("//") || path.Contains("://"))
        {
            return false;
        }

        return path[0] == '/' || path[0] == '~';
    }
}
