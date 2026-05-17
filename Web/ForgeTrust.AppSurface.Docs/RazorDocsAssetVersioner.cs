using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Appends content-derived version keys to RazorDocs asset URLs.
/// </summary>
/// <remarks>
/// RazorDocs often exposes package assets through friendly route-local aliases such as
/// <c>/docs/search.css</c>. ASP.NET Core's standard file version provider cannot always resolve those aliases because
/// they are endpoint redirects or web-root preview routes rather than direct static-web-asset paths. This service hashes
/// the package-embedded asset bytes instead, then applies the resulting version to the public URL that the current docs
/// surface should render. Use it for RazorDocs-owned CSS and JavaScript assets that must survive browser, CDN, or
/// service-worker caches across deployments. Do not use it for reader-authored document links or external CDN URLs.
/// </remarks>
internal sealed class RazorDocsAssetVersioner
{
    internal const string VersionParameterName = "v";

    private const string EmbeddedAssetResourcePrefix = "RazorDocsEmbeddedAssets/";

    private static readonly Assembly RazorDocsAssembly = typeof(RazorDocsWebModule).Assembly;

    private readonly ConcurrentDictionary<string, string?> _versions = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds a versioned current-surface URL for a RazorDocs docs asset.
    /// </summary>
    /// <param name="docsUrlBuilder">The URL builder for the active RazorDocs surface.</param>
    /// <param name="assetName">The docs asset file name, such as <c>search.css</c> or <c>search-client.js</c>.</param>
    /// <returns>
    /// The current-surface asset URL with a content-derived <c>v</c> query parameter, or the unversioned URL when the
    /// packaged asset cannot be found.
    /// </returns>
    internal string BuildVersionedDocsAssetUrl(DocsUrlBuilder docsUrlBuilder, string assetName)
    {
        ArgumentNullException.ThrowIfNull(docsUrlBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        return AppendVersion(docsUrlBuilder.BuildAssetUrl(assetName), $"docs/{assetName}");
    }

    /// <summary>
    /// Appends a version key to a RazorDocs-owned asset URL.
    /// </summary>
    /// <param name="url">The public URL that should be rendered.</param>
    /// <param name="embeddedAssetPath">The asset path under the RazorDocs embedded asset root.</param>
    /// <returns>
    /// <paramref name="url" /> with the content version appended, preserving existing query strings and fragments. If
    /// the URL already has a <c>v</c> parameter or the asset cannot be found, the original URL is returned.
    /// </returns>
    internal string AppendVersion(string url, string embeddedAssetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddedAssetPath);

        if (HasVersionParameter(url))
        {
            return url;
        }

        var version = GetVersion(embeddedAssetPath);
        if (string.IsNullOrEmpty(version))
        {
            return url;
        }

        var fragmentIndex = url.IndexOf('#', StringComparison.Ordinal);
        var urlWithoutFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var separator = urlWithoutFragment.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        return $"{urlWithoutFragment}{separator}{VersionParameterName}={version}{fragment}";
    }

    private string? GetVersion(string embeddedAssetPath)
    {
        var normalizedPath = NormalizeEmbeddedAssetPath(embeddedAssetPath);
        return _versions.GetOrAdd(normalizedPath, ComputeVersion);
    }

    private static string? ComputeVersion(string normalizedEmbeddedAssetPath)
    {
        var resourceName = EmbeddedAssetResourcePrefix + normalizedEmbeddedAssetPath;
        using var stream = RazorDocsAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        var hash = SHA256.HashData(stream);
        return WebEncoders.Base64UrlEncode(hash);
    }

    private static string NormalizeEmbeddedAssetPath(string embeddedAssetPath)
    {
        return embeddedAssetPath.Replace('\\', '/').TrimStart('/');
    }

    private static bool HasVersionParameter(string url)
    {
        var fragmentStart = url.IndexOf('#', StringComparison.Ordinal);
        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0 || (fragmentStart >= 0 && queryStart > fragmentStart))
        {
            return false;
        }

        var query = fragmentStart > queryStart
            ? url[(queryStart + 1)..fragmentStart]
            : url[(queryStart + 1)..];
        var parsed = QueryHelpers.ParseQuery(query);
        return parsed.ContainsKey(VersionParameterName);
    }
}
