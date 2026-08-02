using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.AppSurface.Web.TagHelpers;

/// <summary>Emits AppSurface color-scheme metadata and critical CSS inside a document head.</summary>
/// <remarks>
/// Render <c>&lt;appsurface-theme-head nonce="..." /&gt;</c> after explicitly registering the Web theme integration.
/// The optional nonce is HTML-encoded and applied only to the live inline <c>style</c>; the meta element and the
/// deterministic document snapshot never receive a nonce. This helper emits no scripts and never hides page content.
/// </remarks>
[HtmlTargetElement("appsurface-theme-head")]
public sealed class AppSurfaceThemeHeadTagHelper : TagHelper
{
    private static readonly object RenderedHttpContextItemKey = new();
    private readonly IAppSurfaceThemeDocumentProvider _documentProvider;

    /// <summary>Initializes a head helper from the registered document provider.</summary>
    /// <param name="documentProvider">Provider for the safe default theme document.</param>
    public AppSurfaceThemeHeadTagHelper(IAppSurfaceThemeDocumentProvider documentProvider)
    {
        _documentProvider = documentProvider ?? throw new ArgumentNullException(nameof(documentProvider));
    }

    /// <summary>Gets or sets the optional CSP nonce for the live inline critical stylesheet.</summary>
    [HtmlAttributeName("nonce")]
    public string? Nonce { get; set; }

    /// <summary>Gets or sets the MVC view context for request-scoped duplicate suppression.</summary>
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        output.TagName = null;
        var document = _documentProvider.GetDocument();
        if (!document.IsRenderable)
        {
            output.Content.SetHtmlContent(string.Empty);
            return;
        }

        var items = ViewContext?.HttpContext?.Items;
        if (items is not null && items.ContainsKey(RenderedHttpContextItemKey))
        {
            output.Content.SetHtmlContent(string.Empty);
            return;
        }

        items?[RenderedHttpContextItemKey] = true;
        output.Content.SetHtmlContent(
            AppSurfaceThemeDocumentSerializer.SerializeHeadContent(document, Nonce));
    }
}
