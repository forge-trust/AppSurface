using Microsoft.AspNetCore.Mvc;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Provides Razor view helpers for emitting app-relative RazorDocs links that honor the active request path base.
/// </summary>
internal static class RazorDocsUrlHelperExtensions
{
    /// <summary>
    /// Rewrites rooted app-relative href values through <see cref="IUrlHelper.Content(string)" /> so mounted hosts keep
    /// their current <c>PathBase</c>, while leaving non-rooted URLs unchanged.
    /// </summary>
    /// <param name="url">The active URL helper for the rendered view.</param>
    /// <param name="href">The href to normalize for rendering.</param>
    /// <returns>
    /// A path-base-aware href for rooted docs links, the original non-rooted href for relative/external links, or an
    /// empty string when <paramref name="href" /> is blank. Protocol-relative URLs such as
    /// <c>//cdn.example.com/app.css</c> are preserved as-is so they do not get mistaken for app-relative docs links.
    /// </returns>
    internal static string PathBaseAware(this IUrlHelper url, string? href)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (string.IsNullOrWhiteSpace(href))
        {
            return string.Empty;
        }

        return href.StartsWith("/", StringComparison.Ordinal)
               && !href.StartsWith("//", StringComparison.Ordinal)
            ? url.Content("~" + href)
            : href;
    }
}
