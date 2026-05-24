using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Builds canonical AppSurface Docs URLs for one AppSurface Docs route family.
/// </summary>
/// <remarks>
/// This builder centralizes the route contract so controllers, view components, views, and client scripts do not
/// each guess how the docs surface is rooted. <see cref="RouteRootPath"/> is the stable route-family root used for
/// archive and exact-version routes. <see cref="CurrentDocsRootPath"/> is the live source-backed docs root used for
/// current docs, search, and current search-index routes. Most consumers should copy <see cref="Routes"/> rather than
/// assembling route strings or calling lower-level builder methods directly.
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
    private readonly string _routeRootPath;
    private readonly string _docsVersionPrefixPath;
    private readonly string? _publicOrigin;

    /// <summary>
    /// Initializes a new instance of <see cref="DocsUrlBuilder"/> from typed AppSurface Docs options.
    /// </summary>
    /// <param name="options">Typed AppSurface Docs options that provide the normalized route root and current docs root paths.</param>
    public DocsUrlBuilder(AppSurfaceDocsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VersioningEnabled = options.Versioning?.Enabled == true;
        var normalizedDocsRootPath = NormalizeDocsRootPath(options.Routing?.DocsRootPath, VersioningEnabled);
        _routeRootPath = NormalizeRouteRootPath(options.Routing?.RouteRootPath, normalizedDocsRootPath, VersioningEnabled);
        _currentDocsRootPath = string.IsNullOrWhiteSpace(options.Routing?.DocsRootPath)
            ? ResolveDefaultDocsRootPath(_routeRootPath, VersioningEnabled)
            : normalizedDocsRootPath;
        _docsVersionPrefixPath = JoinPath(_routeRootPath, "v");
        _publicOrigin = NormalizePublicOriginOrNull(options.Routing?.PublicOrigin);
        Routes = new AppSurfaceDocsRouteReferences
        {
            Home = BuildHomeUrl(),
            Search = BuildSearchUrl(),
            SearchIndex = BuildSearchIndexUrl(),
            SearchIndexRefresh = BuildSearchIndexRefreshUrl(),
            Versions = BuildVersionsUrl(),
            Health = BuildHealthUrl(),
            HealthJson = BuildHealthJsonUrl(),
            RouteInspector = BuildRouteInspectorUrl(),
            RouteInspectorJson = BuildRouteInspectorJsonUrl()
        };
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
    /// Gets the stable route-family root for this AppSurface Docs instance.
    /// </summary>
    /// <remarks>
    /// The route root is the parent for the stable entry alias, version archive, and exact-version release trees. It is
    /// the same as <see cref="CurrentDocsRootPath"/> when versioning is disabled, and commonly the parent of the live
    /// preview root when versioning is enabled. For example, <c>RouteRootPath=/foo/bar</c> with
    /// <c>DocsRootPath=/foo/bar/next</c> keeps the archive at <c>/foo/bar/versions</c> while the live preview stays at
    /// <c>/foo/bar/next</c>.
    /// </remarks>
    public string RouteRootPath => _routeRootPath;

    /// <summary>
    /// Gets the docs entry path used as the stable public landing alias.
    /// </summary>
    public string DocsEntryRootPath => _routeRootPath;

    /// <summary>
    /// Gets the stable exact-version prefix for this route family.
    /// </summary>
    public string DocsVersionPrefixPath => _docsVersionPrefixPath;

    /// <summary>
    /// Gets the stable archive path for this route family.
    /// </summary>
    public string DocsVersionsRootPath => BuildVersionsUrl();

    /// <summary>
    /// Gets named AppSurface Docs routes that consumers should prefer over hardcoded route strings.
    /// </summary>
    public AppSurfaceDocsRouteReferences Routes { get; }

    /// <summary>
    /// Gets the configured public origin used for absolute canonical metadata, or <see langword="null"/> when unset.
    /// </summary>
    /// <remarks>
    /// This value is an origin only, for example <c>https://docs.example.com</c>. It never includes the AppSurface Docs
    /// route root because canonical route identity is still owned by <see cref="BuildDocUrl(string)"/>,
    /// <see cref="BuildVersionDocUrl(string, string)"/>, and the route manifest.
    /// </remarks>
    public string? PublicOrigin => _publicOrigin;

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
    /// Builds the current live docs search-index refresh URL.
    /// </summary>
    /// <returns>The app-relative authenticated refresh URL for the current docs search index.</returns>
    public string BuildSearchIndexRefreshUrl()
    {
        return BuildSearchIndexUrl() + "?refresh=1";
    }

    /// <summary>
    /// Builds the current live docs harvest health HTML URL.
    /// </summary>
    /// <returns>The app-relative health page URL for the current docs surface.</returns>
    public string BuildHealthUrl()
    {
        return JoinPath(_currentDocsRootPath, "_health");
    }

    /// <summary>
    /// Builds the current live docs harvest health JSON URL.
    /// </summary>
    /// <returns>The app-relative machine-readable health URL for the current docs surface.</returns>
    public string BuildHealthJsonUrl()
    {
        return JoinPath(_currentDocsRootPath, "_health.json");
    }

    /// <summary>
    /// Builds the current live docs route inspector HTML URL.
    /// </summary>
    /// <returns>The app-relative route inspector URL for the current docs surface.</returns>
    public string BuildRouteInspectorUrl()
    {
        return JoinPath(_currentDocsRootPath, "_routes");
    }

    /// <summary>
    /// Builds the current live docs route inspector JSON URL.
    /// </summary>
    /// <returns>The app-relative machine-readable route inspector URL for the current docs surface.</returns>
    public string BuildRouteInspectorJsonUrl()
    {
        return JoinPath(_currentDocsRootPath, "_routes.json");
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
        return $"{_docsVersionPrefixPath}/{Uri.EscapeDataString(version.Trim())}";
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
    /// Builds the browser-facing canonical href for an app-relative canonical route.
    /// </summary>
    /// <param name="appRelativeCanonicalUrl">The app-relative canonical route, such as <c>/docs/start</c>.</param>
    /// <returns>
    /// The app-relative canonical route when no public origin is configured, or an absolute public canonical URL when
    /// <see cref="PublicOrigin"/> is set.
    /// </returns>
    /// <remarks>
    /// AppSurface Docs keeps route identity app-relative until the final render boundary. This method is that boundary
    /// for canonical metadata: it joins the configured origin with the already-selected canonical path without changing
    /// current, preview, archive, or exact-version route semantics.
    /// </remarks>
    public string BuildCanonicalHref(string appRelativeCanonicalUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appRelativeCanonicalUrl);

        var normalizedCanonicalUrl = appRelativeCanonicalUrl.Trim();
        if (!IsAppRelativeUrl(normalizedCanonicalUrl))
        {
            throw new ArgumentException(
                "Canonical href input must be an app-relative URL such as '/docs/start'.",
                nameof(appRelativeCanonicalUrl));
        }

        return _publicOrigin is null
            ? normalizedCanonicalUrl
            : _publicOrigin + normalizedCanonicalUrl;
    }

    /// <summary>
    /// Builds the public archive URL.
    /// </summary>
    /// <returns>The stable archive URL.</returns>
    public string BuildVersionsUrl()
    {
        return JoinPath(_routeRootPath, "versions");
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
    /// Normalizes a configured public origin for absolute canonical metadata.
    /// </summary>
    /// <param name="publicOrigin">The configured origin, which may be null or already normalized.</param>
    /// <returns>The normalized origin, or <see langword="null"/> when the value is blank.</returns>
    /// <exception cref="ArgumentException">Thrown when the configured value is not an origin-only HTTP(S) URL.</exception>
    internal static string? NormalizePublicOriginOrNull(string? publicOrigin)
    {
        if (TryNormalizePublicOrigin(publicOrigin, out var normalizedPublicOrigin))
        {
            return normalizedPublicOrigin;
        }

        throw new ArgumentException(
            "Public origin must be an absolute http or https origin without a path, query string, fragment, or userinfo.",
            nameof(publicOrigin));
    }

    /// <summary>
    /// Attempts to normalize a configured public origin for absolute canonical metadata.
    /// </summary>
    /// <param name="publicOrigin">The configured origin, which may be null or already normalized.</param>
    /// <param name="normalizedPublicOrigin">The normalized origin, or <see langword="null"/> when the value is blank.</param>
    /// <returns><see langword="true"/> when the value is blank or a valid HTTP(S) origin; otherwise <see langword="false"/>.</returns>
    internal static bool TryNormalizePublicOrigin(string? publicOrigin, out string? normalizedPublicOrigin)
    {
        normalizedPublicOrigin = null;
        if (string.IsNullOrWhiteSpace(publicOrigin))
        {
            return true;
        }

        if (!Uri.TryCreate(publicOrigin.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (!string.IsNullOrEmpty(uri.AbsolutePath) && !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalizedPublicOrigin = uri.GetLeftPart(UriPartial.Authority);
        return true;
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
               || string.Equals(path, "/_health", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/_health.json", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/_routes", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/_routes.json", StringComparison.OrdinalIgnoreCase)
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
    /// Normalizes a configured docs root into the app-relative route contract AppSurface Docs uses at runtime.
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

    /// <summary>
    /// Normalizes a configured route-family root into the app-relative route contract AppSurface Docs uses at runtime.
    /// </summary>
    /// <param name="routeRootPath">The configured route root, which may be null, relative-looking, or already normalized.</param>
    /// <param name="docsRootPath">The normalized live docs root used when versioning is disabled and no route root is configured.</param>
    /// <param name="versioningEnabled">Whether versioning is enabled and the default route family should remain <c>/docs</c>.</param>
    /// <returns>The normalized app-relative route-family root path.</returns>
    internal static string NormalizeRouteRootPath(string? routeRootPath, string docsRootPath, bool versioningEnabled)
    {
        if (string.IsNullOrWhiteSpace(routeRootPath))
        {
            return versioningEnabled ? DocsEntryPath : docsRootPath;
        }

        var normalized = routeRootPath.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        normalized = normalized.TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? "/" : normalized;
    }

    internal static string ResolveDefaultDocsRootPath(string routeRootPath, bool versioningEnabled)
    {
        return versioningEnabled ? JoinPath(routeRootPath, "next") : routeRootPath;
    }

    private static bool IsAppRelativeUrl(string value)
    {
        return value.StartsWith("/", StringComparison.Ordinal)
               && !value.StartsWith("//", StringComparison.Ordinal)
               && !value.Contains("://", StringComparison.Ordinal);
    }
}

/// <summary>
/// Named AppSurface Docs routes for one configured route family.
/// </summary>
/// <remarks>
/// Consumers should prefer this record when they need well-known AppSurface Docs destinations in host code, operator guidance,
/// generated configuration, or documentation. The values are app-relative. Views and other presentation boundaries apply
/// request <c>PathBase</c> separately before sending browser-facing URLs.
/// </remarks>
public sealed record AppSurfaceDocsRouteReferences
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsRouteReferences"/> record for object-initializer use.
    /// </summary>
    public AppSurfaceDocsRouteReferences()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsRouteReferences"/> record with the original route set.
    /// </summary>
    /// <remarks>
    /// This constructor preserves source compatibility for callers that created route references before the harvest
    /// health routes were added. New code should prefer object-initializer syntax so route additions remain explicit.
    /// </remarks>
    /// <param name="home">The current live docs home route.</param>
    /// <param name="search">The current live docs search workspace route.</param>
    /// <param name="searchIndex">The current live docs search-index JSON route.</param>
    /// <param name="searchIndexRefresh">The authenticated search-index refresh route.</param>
    /// <param name="versions">The route-family archive route.</param>
    public AppSurfaceDocsRouteReferences(
        string home,
        string search,
        string searchIndex,
        string searchIndexRefresh,
        string versions)
        : this(home, search, searchIndex, searchIndexRefresh, versions, string.Empty, string.Empty)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsRouteReferences"/> record with the route set through
    /// harvest health diagnostics.
    /// </summary>
    /// <param name="home">The current live docs home route.</param>
    /// <param name="search">The current live docs search workspace route.</param>
    /// <param name="searchIndex">The current live docs search-index JSON route.</param>
    /// <param name="searchIndexRefresh">The authenticated search-index refresh route.</param>
    /// <param name="versions">The route-family archive route.</param>
    /// <param name="health">The current live docs harvest health HTML route.</param>
    /// <param name="healthJson">The current live docs harvest health JSON route.</param>
    public AppSurfaceDocsRouteReferences(
        string home,
        string search,
        string searchIndex,
        string searchIndexRefresh,
        string versions,
        string health,
        string healthJson)
        : this(home, search, searchIndex, searchIndexRefresh, versions, health, healthJson, string.Empty, string.Empty)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsRouteReferences"/> record with all known routes.
    /// </summary>
    /// <param name="home">The current live docs home route.</param>
    /// <param name="search">The current live docs search workspace route.</param>
    /// <param name="searchIndex">The current live docs search-index JSON route.</param>
    /// <param name="searchIndexRefresh">The authenticated search-index refresh route.</param>
    /// <param name="versions">The route-family archive route.</param>
    /// <param name="health">The current live docs harvest health HTML route.</param>
    /// <param name="healthJson">The current live docs harvest health JSON route.</param>
    /// <param name="routeInspector">The current live docs route inspector HTML route.</param>
    /// <param name="routeInspectorJson">The current live docs route inspector JSON route.</param>
    public AppSurfaceDocsRouteReferences(
        string home,
        string search,
        string searchIndex,
        string searchIndexRefresh,
        string versions,
        string health,
        string healthJson,
        string routeInspector,
        string routeInspectorJson)
    {
        Home = home;
        Search = search;
        SearchIndex = searchIndex;
        SearchIndexRefresh = searchIndexRefresh;
        Versions = versions;
        Health = health;
        HealthJson = healthJson;
        RouteInspector = routeInspector;
        RouteInspectorJson = routeInspectorJson;
    }

    /// <summary>
    /// Gets the current live docs home route.
    /// </summary>
    public string Home { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current live docs search workspace route.
    /// </summary>
    public string Search { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current live docs search-index JSON route.
    /// </summary>
    public string SearchIndex { get; init; } = string.Empty;

    /// <summary>
    /// Gets the authenticated search-index refresh route.
    /// </summary>
    public string SearchIndexRefresh { get; init; } = string.Empty;

    /// <summary>
    /// Gets the route-family archive route, whether or not versioning endpoints are currently enabled.
    /// </summary>
    public string Versions { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current live docs harvest health HTML route.
    /// </summary>
    public string Health { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current live docs harvest health JSON route.
    /// </summary>
    public string HealthJson { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current live docs route inspector HTML route.
    /// </summary>
    public string RouteInspector { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current live docs route inspector JSON route.
    /// </summary>
    public string RouteInspectorJson { get; init; } = string.Empty;

    /// <summary>
    /// Deconstructs the original route set for callers that used the positional record contract.
    /// </summary>
    /// <param name="home">The current live docs home route.</param>
    /// <param name="search">The current live docs search workspace route.</param>
    /// <param name="searchIndex">The current live docs search-index JSON route.</param>
    /// <param name="searchIndexRefresh">The authenticated search-index refresh route.</param>
    /// <param name="versions">The route-family archive route.</param>
    public void Deconstruct(
        out string home,
        out string search,
        out string searchIndex,
        out string searchIndexRefresh,
        out string versions)
    {
        home = Home;
        search = Search;
        searchIndex = SearchIndex;
        searchIndexRefresh = SearchIndexRefresh;
        versions = Versions;
    }

    /// <summary>
    /// Deconstructs the route set through harvest health diagnostics.
    /// </summary>
    /// <param name="home">The current live docs home route.</param>
    /// <param name="search">The current live docs search workspace route.</param>
    /// <param name="searchIndex">The current live docs search-index JSON route.</param>
    /// <param name="searchIndexRefresh">The authenticated search-index refresh route.</param>
    /// <param name="versions">The route-family archive route.</param>
    /// <param name="health">The current live docs harvest health HTML route.</param>
    /// <param name="healthJson">The current live docs harvest health JSON route.</param>
    public void Deconstruct(
        out string home,
        out string search,
        out string searchIndex,
        out string searchIndexRefresh,
        out string versions,
        out string health,
        out string healthJson)
    {
        home = Home;
        search = Search;
        searchIndex = SearchIndex;
        searchIndexRefresh = SearchIndexRefresh;
        versions = Versions;
        health = Health;
        healthJson = HealthJson;
    }

    /// <summary>
    /// Deconstructs all known routes including diagnostics routes.
    /// </summary>
    /// <param name="home">The current live docs home route.</param>
    /// <param name="search">The current live docs search workspace route.</param>
    /// <param name="searchIndex">The current live docs search-index JSON route.</param>
    /// <param name="searchIndexRefresh">The authenticated search-index refresh route.</param>
    /// <param name="versions">The route-family archive route.</param>
    /// <param name="health">The current live docs harvest health HTML route.</param>
    /// <param name="healthJson">The current live docs harvest health JSON route.</param>
    /// <param name="routeInspector">The current live docs route inspector HTML route.</param>
    /// <param name="routeInspectorJson">The current live docs route inspector JSON route.</param>
    public void Deconstruct(
        out string home,
        out string search,
        out string searchIndex,
        out string searchIndexRefresh,
        out string versions,
        out string health,
        out string healthJson,
        out string routeInspector,
        out string routeInspectorJson)
    {
        home = Home;
        search = Search;
        searchIndex = SearchIndex;
        searchIndexRefresh = SearchIndexRefresh;
        versions = Versions;
        health = Health;
        healthJson = HealthJson;
        routeInspector = RouteInspector;
        routeInspectorJson = RouteInspectorJson;
    }
}
