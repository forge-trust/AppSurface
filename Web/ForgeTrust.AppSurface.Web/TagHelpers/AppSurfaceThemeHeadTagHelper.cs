using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web.TagHelpers;

/// <summary>Emits AppSurface color-scheme metadata and critical CSS inside a document head.</summary>
/// <remarks>
/// Render <c>&lt;appsurface-theme-head nonce="..." /&gt;</c> after explicitly registering the Web theme integration.
/// The optional nonce is HTML-encoded and applied to the live inline critical <c>style</c>. When the host explicitly
/// registers browser preferences through
/// <see cref="AppSurfaceWebThemingServiceCollectionExtensions.AddAppSurfaceWebThemePreferences(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{Theming.AppSurfaceThemePreferenceOptions}?)"/>,
/// the same nonce is also applied to the deterministic preference bootstrap emitted before that stylesheet. The meta
/// element never receives a nonce. The bootstrap never hides page content and binds only consumer-owned controls marked
/// with <c>data-as-theme-preference-control</c> after the document is ready.
/// </remarks>
[HtmlTargetElement("appsurface-theme-head")]
public sealed class AppSurfaceThemeHeadTagHelper : TagHelper
{
    private static readonly object RenderedHttpContextItemKey = new();
    private readonly IAppSurfaceThemeDocumentProvider _documentProvider;
    private readonly AppSurfaceThemePreferenceBootstrap? _preferenceBootstrap;

    /// <summary>Initializes a head helper from the registered document provider.</summary>
    /// <param name="documentProvider">Provider for the safe theme document for the current rendering scope.</param>
    public AppSurfaceThemeHeadTagHelper(IAppSurfaceThemeDocumentProvider documentProvider)
        : this(documentProvider, EmptyServiceProvider.Instance)
    {
    }

    /// <summary>Initializes a head helper from the registered document provider and optional preference services.</summary>
    /// <param name="documentProvider">Provider for the safe theme document for the current rendering scope.</param>
    /// <param name="serviceProvider">Provider used to resolve the optional preference bootstrap.</param>
    [ActivatorUtilitiesConstructor]
    public AppSurfaceThemeHeadTagHelper(IAppSurfaceThemeDocumentProvider documentProvider, IServiceProvider serviceProvider)
    {
        _documentProvider = documentProvider ?? throw new ArgumentNullException(nameof(documentProvider));
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _preferenceBootstrap = serviceProvider.GetService(typeof(AppSurfaceThemePreferenceBootstrap)) as AppSurfaceThemePreferenceBootstrap;
    }

    /// <summary>Gets or sets the optional CSP nonce for live inline preference and critical-style payloads.</summary>
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
        var head = AppSurfaceThemeDocumentSerializer.SerializeHeadContent(document, Nonce);
        output.Content.SetHtmlContent(_preferenceBootstrap is null ? head : _preferenceBootstrap.Render(Nonce) + head);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        internal static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
