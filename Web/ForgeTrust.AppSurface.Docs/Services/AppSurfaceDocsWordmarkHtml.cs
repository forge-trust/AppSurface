using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Renders configured AppSurface Docs wordmarks for package-owned Razor views.
/// </summary>
/// <remarks>
/// This helper keeps the docs chrome's encoded wordmark markup in one place so layout and landing views share the same
/// highlight splitting, CSS variable placement, and plain-text fallback. It only consumes resolved identity values; the
/// options post-configuration and validator remain the source of truth for trimming, matching, and color safety.
/// </remarks>
internal static class AppSurfaceDocsWordmarkHtml
{
    /// <summary>
    /// Renders the resolved identity display name with the optional configured wordmark highlight.
    /// </summary>
    /// <param name="identity">Resolved identity for the current docs host.</param>
    /// <param name="cssClass">CSS classes for the outer wordmark element.</param>
    /// <param name="elementName">The package-owned element to render. Supported values are <c>span</c> and <c>h1</c>.</param>
    /// <returns>HTML-safe wordmark markup with all display text encoded.</returns>
    public static IHtmlContent Render(AppSurfaceDocsResolvedIdentity identity, string cssClass, string elementName = "span")
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (elementName is not "span" and not "h1")
        {
            throw new ArgumentOutOfRangeException(nameof(elementName), "AppSurface Docs wordmarks support only span and h1 elements.");
        }

        var builder = new StringBuilder();
        builder
            .Append('<')
            .Append(elementName)
            .Append(" class=\"")
            .Append(HtmlEncoder.Default.Encode(cssClass))
            .Append('"');

        if (identity.WordmarkHighlightColor is not null)
        {
            builder
                .Append(" style=\"--docs-brand-wordmark-highlight-color:")
                .Append(HtmlEncoder.Default.Encode(identity.WordmarkHighlightColor))
                .Append('"');
        }

        builder.Append('>');
        AppendEncodedWordmarkText(builder, identity);
        builder
            .Append("</")
            .Append(elementName)
            .Append('>');

        return new HtmlString(builder.ToString());
    }

    private static void AppendEncodedWordmarkText(StringBuilder builder, AppSurfaceDocsResolvedIdentity identity)
    {
        if (identity.WordmarkHighlightText is null)
        {
            builder.Append(HtmlEncoder.Default.Encode(identity.DisplayName));
            return;
        }

        var highlightStart = identity.DisplayName.IndexOf(identity.WordmarkHighlightText, StringComparison.Ordinal);
        if (highlightStart < 0)
        {
            builder.Append(HtmlEncoder.Default.Encode(identity.DisplayName));
            return;
        }

        builder.Append(HtmlEncoder.Default.Encode(identity.DisplayName[..highlightStart]));
        builder
            .Append("<span class=\"docs-wordmark-highlight\">")
            .Append(HtmlEncoder.Default.Encode(identity.WordmarkHighlightText))
            .Append("</span>");
        builder.Append(HtmlEncoder.Default.Encode(identity.DisplayName[(highlightStart + identity.WordmarkHighlightText.Length)..]));
    }
}
