using ForgeTrust.Runnable.Web.RazorDocs.Models;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// Builds canonical RazorDocs URLs for the live source-backed docs surface and the version archive shell.
/// </summary>
/// <remarks>
/// This builder centralizes the route contract so controllers, view components, views, and client scripts do not
/// each guess how the current docs surface is rooted. The live source-backed docs surface moves between
/// <c>/docs</c> and <c>/docs/next</c> depending on versioning settings, while historical versions always live under
/// <c>/docs/v/{version}</c> and the public archive stays at <c>/docs/versions</c>.
/// </remarks>
public sealed class DocsUrlBuilder
{
    /// <summary>
    /// Gets the stable docs entry path.
    /// </summary>
    public const string DocsEntryPath = "/docs";

    /// <summary>
    /// Gets the stable exact-version prefix.
    /// </summary>
    public const string DocsVersionPrefix = "/docs/v";

    /// <summary>
    /// Gets the stable version archive path.
    /// </summary>
    public const string DocsVersionsPath = "/docs/versions";

    private readonly string _currentDocsRootPath;

    /// <summary>
    /// Initializes a new instance of <see cref="DocsUrlBuilder"/> from typed RazorDocs options.
    /// </summary>
    /// <param name="options">Typed RazorDocs options that provide the normalized current docs root path.</param>
    public DocsUrlBuilder(RazorDocsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VersioningEnabled = options.Versioning?.Enabled == true;
        _currentDocsRootPath = NormalizeDocsRootPath(options.Routing?.DocsRootPath, VersioningEnabled);
    }

    /// <summary>
    /// Gets a value indicating whether versioning is enabled for the current host.
    /// </summary>
    public bool VersioningEnabled { get; }

    /// <summary>
    /// Gets the canonical root path for the current live source-backed docs surface.
    /// </summary>
    public string CurrentDocsRootPath => _currentDocsRootPath;

    /// <summary>
    /// Gets the docs entry path used as the stable public landing alias.
    /// </summary>
    public string DocsEntryRootPath => DocsEntryPath;

    /// <summary>
    /// Gets the stable archive path.
    /// </summary>
    public string DocsVersionsRootPath => DocsVersionsPath;

    /// <summary>
    /// Builds the current live docs home URL.
    /// </summary>
    /// <returns>The current docs home path.</returns>
    public string BuildHomeUrl()
    {
        return _currentDocsRootPath;
    }

    /// <summary>
    /// Builds the current live docs search workspace URL.
    /// </summary>
    /// <returns>The app-relative search workspace URL for the current docs surface.</returns>
    public string BuildSearchUrl()
    {
        return JoinPath(_currentDocsRootPath, "search");
    }

    /// <summary>
    /// Builds the current live docs search-index URL.
    /// </summary>
    /// <returns>The app-relative search-index URL for the current docs surface.</returns>
    public string BuildSearchIndexUrl()
    {
        return JoinPath(_currentDocsRootPath, "search-index.json");
    }

    /// <summary>
    /// Builds a current-surface public section URL.
    /// </summary>
    /// <param name="section">The section whose route should be built.</param>
    /// <returns>The canonical section URL rooted at the current docs surface.</returns>
    public string BuildSectionUrl(DocPublicSection section)
    {
        return DocPublicSectionCatalog.GetHref(section, _currentDocsRootPath);
    }

    /// <summary>
    /// Builds a current-surface canonical document URL.
    /// </summary>
    /// <param name="path">The source or canonical documentation path.</param>
    /// <returns>The canonical document URL rooted at the current docs surface.</returns>
    public string BuildDocUrl(string path)
    {
        return BuildDocUrl(_currentDocsRootPath, path);
    }

    /// <summary>
    /// Builds the current-surface search asset URL.
    /// </summary>
    /// <param name="assetName">The asset file name, such as <c>search.css</c>.</param>
    /// <returns>The canonical asset URL rooted at the current docs surface.</returns>
    public string BuildAssetUrl(string assetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        return JoinPath(_currentDocsRootPath, assetName);
    }

    /// <summary>
    /// Builds the exact-version root URL for one published docs release.
    /// </summary>
    /// <param name="version">The exact published version identifier.</param>
    /// <returns>The canonical root URL for that version.</returns>
    public string BuildVersionRootUrl(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return $"{DocsVersionPrefix}/{Uri.EscapeDataString(version.Trim())}";
    }

    /// <summary>
    /// Builds a canonical document URL rooted at a specific exact version.
    /// </summary>
    /// <param name="version">The exact published version identifier.</param>
    /// <param name="path">The source or canonical documentation path.</param>
    /// <returns>The canonical document URL rooted at the requested version.</returns>
    public string BuildVersionDocUrl(string version, string path)
    {
        return BuildDocUrl(BuildVersionRootUrl(version), path);
    }

    /// <summary>
    /// Builds the public archive URL.
    /// </summary>
    /// <returns>The stable archive URL.</returns>
    public string BuildVersionsUrl()
    {
        return DocsVersionsPath;
    }

    /// <summary>
    /// Determines whether the supplied request path is inside the current live docs surface.
    /// </summary>
    /// <param name="path">The request path to check.</param>
    /// <returns><c>true</c> when the path belongs to the current live docs surface; otherwise <c>false</c>.</returns>
    public bool IsCurrentDocsPath(string? path)
    {
        return IsUnderRoot(path, _currentDocsRootPath);
    }

    /// <summary>
    /// Builds a canonical document URL rooted at an explicit docs surface root.
    /// </summary>
    /// <param name="docsRootPath">The app-relative docs root path.</param>
    /// <param name="path">The source or canonical documentation path.</param>
    /// <returns>The canonical document URL.</returns>
    internal static string BuildDocUrl(string docsRootPath, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docsRootPath);

        if (string.IsNullOrWhiteSpace(path))
        {
            return docsRootPath;
        }

        var fragmentSeparator = path.IndexOf('#');
        var pathPart = fragmentSeparator >= 0 ? path[..fragmentSeparator] : path;
        var fragmentPart = fragmentSeparator >= 0 ? path[(fragmentSeparator + 1)..] : string.Empty;

        var encodedPath = string.Join(
            "/",
            pathPart
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        var url = string.IsNullOrEmpty(encodedPath) ? docsRootPath : JoinPath(docsRootPath, encodedPath);
        if (!string.IsNullOrWhiteSpace(fragmentPart))
        {
            url += $"#{Uri.EscapeDataString(fragmentPart)}";
        }

        return url;
    }

    /// <summary>
    /// Determines whether a request path belongs to the supplied docs root.
    /// </summary>
    /// <param name="path">The incoming request path to evaluate.</param>
    /// <param name="docsRootPath">The normalized docs root path configured for the live docs surface.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="path"/> resolves to the docs root itself or one of its child
    /// routes; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Blank paths always return <see langword="false"/>. Root-mounted docs (<c>/</c>) use
    /// <see cref="IsLikelyRootMountedDocsPath(string)"/> so only known docs-like routes are treated as current docs
    /// traffic. Non-root mounts use case-insensitive exact and prefix matching against <c>{docsRootPath}/...</c>.
    /// </remarks>
    internal static bool IsUnderRoot(string? path, string docsRootPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (string.Equals(docsRootPath, "/", StringComparison.Ordinal))
        {
            return IsLikelyRootMountedDocsPath(path);
        }

        return string.Equals(path, docsRootPath, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(docsRootPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Joins a normalized docs root with a relative docs route segment.
    /// </summary>
    /// <param name="docsRootPath">The normalized app-relative docs root path.</param>
    /// <param name="relativePath">The relative docs route to append.</param>
    /// <returns>The combined app-relative route path.</returns>
    /// <remarks>
    /// Leading slashes on <paramref name="relativePath"/> are ignored. <see langword="null"/>, empty, and whitespace-only
    /// relative paths return the docs root unchanged. When the docs root is <c>/</c>, the result stays root-mounted
    /// instead of producing a doubled slash. Callers are expected to pass already-normalized root paths and docs-relative
    /// segments rather than arbitrary URLs.
    /// </remarks>
    internal static string JoinPath(string docsRootPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docsRootPath);

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return docsRootPath;
        }

        var trimmedRelativePath = relativePath.TrimStart('/');
        if (trimmedRelativePath.Length == 0)
        {
            return docsRootPath;
        }

        return string.Equals(docsRootPath, "/", StringComparison.Ordinal)
            ? "/" + trimmedRelativePath
            : $"{docsRootPath}/{trimmedRelativePath}";
    }

    private static bool IsLikelyRootMountedDocsPath(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(path, "/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/search", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/search-index.json", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/search.css", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/search-client.js", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/outline-client.js", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/minisearch.min.js", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/Namespaces.html", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/sections/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/Namespaces/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".md.html", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".partial.html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes a configured docs root into the app-relative route contract RazorDocs uses at runtime.
    /// </summary>
    /// <param name="docsRootPath">The configured docs root, which may be null, relative-looking, or already normalized.</param>
    /// <param name="versioningEnabled">Whether versioning is enabled and the default should therefore become <c>/docs/next</c>.</param>
    /// <returns>The normalized app-relative docs root path.</returns>
    internal static string NormalizeDocsRootPath(string? docsRootPath, bool versioningEnabled)
    {
        if (string.IsNullOrWhiteSpace(docsRootPath))
        {
            return versioningEnabled ? "/docs/next" : "/docs";
        }

        var normalized = docsRootPath.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        normalized = normalized.TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? "/" : normalized;
    }
}
