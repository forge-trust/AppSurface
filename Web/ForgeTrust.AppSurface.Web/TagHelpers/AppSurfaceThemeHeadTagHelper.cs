using ForgeTrust.AppSurface.Web.Theming;
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

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        output.TagName = null;
        var document = _documentProvider.GetDocument();
        output.Content.SetHtmlContent(
            document.IsRenderable
                ? AppSurfaceThemeDocumentSerializer.SerializeHeadContent(document, Nonce)
                : string.Empty);
    }
}
