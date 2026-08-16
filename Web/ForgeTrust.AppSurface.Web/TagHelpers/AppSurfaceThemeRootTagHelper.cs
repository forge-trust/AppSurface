using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.AppSurface.Web.TagHelpers;

/// <summary>Applies safe AppSurface theme identity and color-scheme metadata to an opted-in HTML root.</summary>
/// <remarks>
/// Use <c>&lt;html appsurface-theme-root&gt;</c> after explicitly registering
/// <see cref="AppSurfaceWebThemingServiceCollectionExtensions.AddAppSurfaceWebTheming(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>
/// or <see cref="AppSurfaceWebThemingServiceCollectionExtensions.AddAppSurfaceWebThemeSelection(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// Existing attributes are preserved. An existing <c>style</c> attribute retains its declarations and receives the
/// generated color-scheme declaration only when it does not already name <c>color-scheme</c>.
/// Set <c>appsurface-theme-root="false"</c> when the embedding layout owns a fixed color-scheme and must not emit
/// host theme metadata.
/// </remarks>
[HtmlTargetElement("html", Attributes = AttributeName)]
public sealed class AppSurfaceThemeRootTagHelper : TagHelper
{
    /// <summary>Names the opt-in root marker attribute.</summary>
    public const string AttributeName = "appsurface-theme-root";

    /// <summary>
    /// Gets or sets whether the opt-in root marker should apply the registered theme document. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Set this to <see langword="false"/> when an embedding layout owns a complete fixed color-scheme contract and
    /// must not emit potentially conflicting host-theme metadata. The marker attribute is removed without changing
    /// any other existing root attributes.
    /// </remarks>
    [HtmlAttributeName(AttributeName)]
    public bool IsEnabled { get; set; } = true;

    private readonly IAppSurfaceThemeDocumentProvider _documentProvider;

    /// <summary>Initializes a root helper from the registered document provider.</summary>
    /// <param name="documentProvider">Provider for the safe theme document for the current rendering scope.</param>
    public AppSurfaceThemeRootTagHelper(IAppSurfaceThemeDocumentProvider documentProvider)
    {
        _documentProvider = documentProvider ?? throw new ArgumentNullException(nameof(documentProvider));
    }

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        if (!IsEnabled)
        {
            output.Attributes.RemoveAll(AttributeName);
            return;
        }

        var document = _documentProvider.GetDocument();
        if (!document.IsRenderable)
        {
            return;
        }

        output.Attributes.SetAttribute("data-as-theme", document.RootThemeId);
        output.Attributes.SetAttribute("data-as-theme-mode", document.RootThemeMode);
        output.Attributes.SetAttribute("data-as-theme-schema", document.RootSchemaVersion);

        var existingStyle = output.Attributes["style"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(existingStyle))
        {
            output.Attributes.SetAttribute("style", document.RootStyle);
            return;
        }

        if (HasColorSchemeDeclaration(existingStyle))
        {
            output.Attributes.SetAttribute("data-as-theme-color-scheme-conflict", "true");
            return;
        }

        output.Attributes.SetAttribute("style", $"{existingStyle.TrimEnd().TrimEnd(';')}; {document.RootStyle}");
    }

    private static bool HasColorSchemeDeclaration(string style)
    {
        foreach (var declaration in style.Split(';'))
        {
            var separator = declaration.IndexOf(':');
            if (separator >= 0
                && declaration[..separator].Trim().Equals("color-scheme", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
