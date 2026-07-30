using Microsoft.AspNetCore.Razor.TagHelpers;
using ForgeTrust.AppSurface.Web.Theming;

namespace ForgeTrust.AppSurface.Web.TagHelpers;

/// <summary>Applies safe AppSurface theme identity and color-scheme metadata to an HTML root.</summary>
/// <remarks>
/// Use <c>&lt;html appsurface-theme-root&gt;</c> after explicitly registering
/// <see cref="AppSurfaceWebThemingServiceCollectionExtensions.AddAppSurfaceWebTheming(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// Existing attributes are preserved. An existing <c>style</c> attribute retains its declarations and receives the
/// generated color-scheme declaration only when it does not already name <c>color-scheme</c>.
/// </remarks>
[HtmlTargetElement("html", Attributes = AttributeName)]
public sealed class AppSurfaceThemeRootTagHelper : TagHelper
{
    /// <summary>Names the opt-in root marker attribute.</summary>
    public const string AttributeName = "appsurface-theme-root";

    private readonly IAppSurfaceThemeDocumentProvider _documentProvider;

    /// <summary>Initializes a root helper from the registered document provider.</summary>
    /// <param name="documentProvider">Provider for the safe default theme document.</param>
    public AppSurfaceThemeRootTagHelper(IAppSurfaceThemeDocumentProvider documentProvider)
    {
        _documentProvider = documentProvider ?? throw new ArgumentNullException(nameof(documentProvider));
    }

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        var document = _documentProvider.GetDocument();
        if (!document.IsRenderable)
        {
            return;
        }

        output.Attributes.SetAttribute("data-as-theme", GetAttributeValue(document.RootAttributes, "data-as-theme"));
        output.Attributes.SetAttribute(
            "data-as-theme-mode",
            GetAttributeValue(document.RootAttributes, "data-as-theme-mode"));

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

    private static string GetAttributeValue(string attributes, string name)
    {
        var prefix = name + "=\"";
        var start = attributes.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += prefix.Length;
        var end = attributes.IndexOf('"', start);
        return end < 0 ? string.Empty : attributes[start..end];
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
