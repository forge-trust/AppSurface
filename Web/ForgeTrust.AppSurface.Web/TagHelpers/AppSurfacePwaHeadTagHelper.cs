using System.Text;
using System.Text.Encodings.Web;
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
/// <see cref="PwaOptions"/>. The helper emits the manifest link, theme-color metadata, mobile app-capable hints, icon
/// links, and the service-worker path as inert metadata when an explicit offline strategy is configured. It does not
/// register a service worker or display an install prompt; host JavaScript or a future RazorWire affordance can opt into
/// that UI deliberately.
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
        if (!_options.Enabled)
        {
            output.Content.SetHtmlContent(string.Empty);
            return;
        }

        var pathBase = ViewContext.HttpContext.Request.PathBase;
        var manifestPath = pathBase.Add(new PathString(_options.ManifestPath)).Value ?? _options.ManifestPath;
        var builder = new StringBuilder();
        builder.Append("<link rel=\"manifest\" href=\"");
        builder.Append(HtmlEncoder.Default.Encode(manifestPath));
        builder.AppendLine("\" />");
        builder.Append("<meta name=\"theme-color\" content=\"");
        builder.Append(HtmlEncoder.Default.Encode(_options.ThemeColor));
        builder.AppendLine("\" />");
        builder.Append("<meta name=\"application-name\" content=\"");
        builder.Append(HtmlEncoder.Default.Encode(_options.Name));
        builder.AppendLine("\" />");
        builder.Append("<meta name=\"apple-mobile-web-app-capable\" content=\"yes\" />");
        builder.Append("<meta name=\"apple-mobile-web-app-title\" content=\"");
        builder.Append(HtmlEncoder.Default.Encode(_options.ShortName));
        builder.AppendLine("\" />");

        foreach (var icon in _options.Icons)
        {
            var href = icon.Source;
            if (PwaOptionsValidator.IsSafeLocalPath(href))
            {
                href = _fileVersionProvider.AddFileVersionToPath(pathBase, href);
            }

            builder.Append("<link rel=\"icon\" href=\"");
            builder.Append(HtmlEncoder.Default.Encode(href));
            builder.Append("\" sizes=\"");
            builder.Append(HtmlEncoder.Default.Encode(icon.Sizes));
            builder.Append("\" type=\"");
            builder.Append(HtmlEncoder.Default.Encode(icon.Type));
            builder.AppendLine("\" />");
        }

        if (_options.Offline.Enabled)
        {
            var serviceWorkerPath = pathBase.Add(new PathString(_options.Offline.ServiceWorkerPath)).Value
                ?? _options.Offline.ServiceWorkerPath;
            builder.Append("<meta name=\"appsurface:pwa-service-worker\" content=\"");
            builder.Append(HtmlEncoder.Default.Encode(serviceWorkerPath));
            builder.AppendLine("\" />");
        }

        output.Content.SetHtmlContent(builder.ToString());
    }
}
