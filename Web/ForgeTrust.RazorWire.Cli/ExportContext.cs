namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Provides context and state for an export operation, including configuration and crawl progress.
/// </summary>
public class ExportContext
{
    /// <summary>
    /// Gets the path where exported files will be saved.
    /// </summary>
    public string OutputPath { get; }

    /// <summary>
    /// Gets the optional path to a seed routes file.
    /// </summary>
    /// <remarks>
    /// When this path is set, file-based seeds take precedence over <see cref="InitialSeedRoutes"/> so existing CLI
    /// callers keep their explicit seed-file behavior.
    /// </remarks>
    public string? SeedRoutesPath { get; }

    private readonly List<string> _additionalSeedRoutes = [];

    /// <summary>
    /// Gets optional in-memory seed routes used when <see cref="SeedRoutesPath"/> is not configured.
    /// </summary>
    /// <remarks>
    /// Hosts that already know their default routes can pass them directly instead of writing a temporary seed file. Values
    /// are validated and normalized by the export engine using the same rules as file-based seeds. When no valid in-memory
    /// seed remains, the engine falls back to the root route (<c>/</c>).
    /// </remarks>
    public IReadOnlyList<string> InitialSeedRoutes { get; }

    /// <summary>
    /// Gets host-registered seed routes that should be crawled in addition to configured seed-file or in-memory seeds.
    /// </summary>
    /// <remarks>
    /// Host-specific export integrations can register routes discovered from their own route graph before crawling starts.
    /// These routes are validated by the export engine with the same normalization rules as configured seeds. They do not
    /// replace <see cref="SeedRoutesPath"/> or <see cref="InitialSeedRoutes"/>; they make known public routes explicit so
    /// exports do not depend on every page being linked from the initial crawl roots.
    /// </remarks>
    public IReadOnlyList<string> AdditionalSeedRoutes => _additionalSeedRoutes;

    /// <summary>
    /// Gets the base URL of the source application being exported.
    /// </summary>
    public string BaseUrl { get; }

    /// <summary>
    /// Gets the export mode that controls URL rewriting and validation behavior.
    /// </summary>
    /// <remarks>
    /// <see cref="ExportMode.Cdn"/> is the default and rewrites exporter-managed internal URLs to emitted artifacts while validating
    /// that those managed dependencies can be served by a static host. <see cref="ExportMode.Hybrid"/> preserves application-style
    /// internal URLs for server-backed deployments that still provide routing and dynamic behavior. CDN validation and rewriting only
    /// apply to exporter-managed URLs discovered in markup and CSS; unmanaged external, JavaScript, mailto, hash-only, and data URLs
    /// are intentionally ignored rather than validated or rewritten.
    /// </remarks>
    public ExportMode Mode { get; }

    /// <summary>
    /// Gets the strategy used to materialize registered redirect aliases.
    /// </summary>
    /// <remarks>
    /// <see cref="ExportRedirectStrategy.Html"/> is the default for generic static hosts and writes tiny alias HTML
    /// files after canonical pages are materialized. <see cref="ExportRedirectStrategy.Netlify"/> writes one root
    /// <c>_redirects</c> file with exact provider rules and is intended for CDN exports published to Netlify or a
    /// compatible CDN. Netlify validation operates on the serialized provider rule paths, so duplicate exact rules are
    /// de-duplicated while self-redirects and same-source/different-target rules fail before files are written.
    /// </remarks>
    public ExportRedirectStrategy RedirectStrategy { get; }

    /// <summary>
    /// Gets split-origin hybrid export options.
    /// </summary>
    /// <remarks>
    /// The context stores normalized options. <see cref="ExportHybridOptions.LiveOrigin"/> is either
    /// <see langword="null"/> or an absolute HTTP(S) origin with no path, query, fragment, or userinfo.
    /// </remarks>
    public ExportHybridOptions Hybrid { get; }

    /// <summary>
    /// Gets the optional public origin used when export rewrites same-origin metadata for the published static host.
    /// </summary>
    /// <remarks>
    /// This origin does not change the crawl source or application links. It is intended for metadata such as canonical
    /// links that must identify the public static host even though the exporter crawls a loopback or private source URL.
    /// When set, it is normalized to an absolute HTTP(S) origin with no path, query, fragment, or userinfo.
    /// </remarks>
    public string? PublicOrigin { get; }

    /// <summary>
    /// Gets the set of URLs that have already been visited during the crawl.
    /// </summary>
    public HashSet<string> Visited { get; } = new();

    /// <summary>
    /// Gets the queue of URLs pending processing.
    /// </summary>
    public Queue<string> Queue { get; } = new();

    /// <summary>
    /// Gets the normalized routes that have already been scheduled for crawl processing.
    /// </summary>
    /// <remarks>
    /// This set mirrors <see cref="Queue"/> membership over the lifetime of an export so duplicate reference discovery can perform
    /// O(1) scheduling checks without scanning the pending queue. Routes remain in this set after dequeue because <see cref="Visited"/>
    /// and <see cref="RouteOutcomes"/> record their terminal crawl state.
    /// </remarks>
    internal HashSet<string> Enqueued { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets route fetch outcomes keyed by normalized root-relative route.
    /// </summary>
    internal Dictionary<string, ExportRouteOutcome> RouteOutcomes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets every managed internal reference discovered during the crawl, including duplicate provenance.
    /// </summary>
    internal List<ExportReference> References { get; } = [];

    /// <summary>
    /// Gets CDN validation diagnostics produced for this export.
    /// </summary>
    internal List<ExportDiagnostic> Diagnostics { get; } = [];

    /// <summary>
    /// Gets static-host artifact URLs keyed by normalized route.
    /// </summary>
    internal Dictionary<string, string> ArtifactUrls { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets generated AppSurface Docs partial artifact URLs keyed by their source full-page route.
    /// </summary>
    internal Dictionary<string, string> PartialArtifactUrls { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets redirect aliases registered by host-specific exporters.
    /// </summary>
    internal List<ExportRedirectArtifact> RedirectArtifacts { get; } = [];

    /// <summary>
    /// Registers a route alias that should redirect to an already-exported canonical route.
    /// </summary>
    /// <param name="aliasRoute">Root-relative alias route that should redirect.</param>
    /// <param name="canonicalRoute">Root-relative canonical route that owns the real exported page body.</param>
    /// <remarks>
    /// This API is intended for hosts that know route aliases before crawling starts. The export engine validates registered
    /// aliases before materialization so collisions and missing canonical artifacts fail early. The selected
    /// <see cref="RedirectStrategy"/> controls whether aliases become HTML fallback artifacts or provider redirect rules, so
    /// source-shaped aliases do not become duplicate public pages.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when either route is blank, is not root-relative, is protocol-relative, contains a query string or fragment,
    /// or contains newline, carriage return, or tab characters.
    /// </exception>
    public void AddRedirectAlias(string aliasRoute, string canonicalRoute)
    {
        RedirectArtifacts.Add(
            new ExportRedirectArtifact(
                NormalizeRedirectArtifactRoute(aliasRoute, nameof(aliasRoute)),
                NormalizeRedirectArtifactRoute(canonicalRoute, nameof(canonicalRoute))));
    }

    /// <summary>
    /// Registers a route whose static output should redirect to an already-exported canonical route.
    /// </summary>
    /// <param name="aliasRoute">Root-relative alias route that should redirect.</param>
    /// <param name="canonicalRoute">Root-relative canonical route that owns the real exported page body.</param>
    /// <remarks>
    /// This compatibility wrapper preserves the original artifact-oriented API name. New host integrations should call
    /// <see cref="AddRedirectAlias"/> so code describes the route relationship instead of the selected materialization
    /// strategy.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when either route is blank, is not root-relative, is protocol-relative, contains a query string or fragment,
    /// or contains newline, carriage return, or tab characters.
    /// </exception>
    public void AddRedirectArtifact(string aliasRoute, string canonicalRoute)
    {
        AddRedirectAlias(aliasRoute, canonicalRoute);
    }

    /// <summary>
    /// Registers an additional route for the export crawler to visit before validation.
    /// </summary>
    /// <param name="seedRoute">Root-relative or same-origin route to crawl.</param>
    /// <remarks>
    /// Use this when a host already knows its public route graph. The export engine validates, normalizes, and de-duplicates
    /// registered routes during the seed queue phase. Query strings and fragments follow the same rules as other seed
    /// sources, but host-specific route manifests should generally register clean canonical paths.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="seedRoute"/> is blank.</exception>
    public void AddSeedRoute(string seedRoute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedRoute);
        _additionalSeedRoutes.Add(seedRoute);
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ExportContext"/> with the specified configuration.
    /// </summary>
    /// <param name="outputPath">The target directory for export.</param>
    /// <param name="seedRoutesPath">The path to initial seed routes, if any.</param>
    /// <param name="baseUrl">The base URL of the site to export.</param>
    /// <param name="mode">
    /// The export mode. <see cref="ExportMode.Cdn"/> is the default and validates plus rewrites exporter-managed URLs for static
    /// hosting. Use <see cref="ExportMode.Hybrid"/> when the exported output will still be served by application-aware routing.
    /// </param>
    /// <remarks>
    /// The context tracks crawl state across a staged pipeline: seed routes are scheduled first, references discovered from fetched
    /// markup and CSS can enqueue more managed routes, and CDN validation runs only after the artifact map is complete. Unmanaged URLs
    /// are outside that pipeline and are not validated or rewritten by <see cref="ExportMode.Cdn"/>.
    /// </remarks>
    public ExportContext(
        string outputPath,
        string? seedRoutesPath,
        string baseUrl,
        ExportMode mode = ExportMode.Cdn)
        : this(outputPath, seedRoutesPath, initialSeedRoutes: null, baseUrl, mode, ExportRedirectStrategy.Html)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ExportContext"/> with a redirect strategy.
    /// </summary>
    /// <param name="outputPath">The target directory for export.</param>
    /// <param name="seedRoutesPath">The path to initial seed routes, if any.</param>
    /// <param name="baseUrl">The base URL of the site to export.</param>
    /// <param name="redirectStrategy">Strategy used to materialize redirect aliases.</param>
    /// <remarks>
    /// This overload preserves the default <see cref="ExportMode.Cdn"/> mode while letting hosts opt into provider-native
    /// redirect output such as <see cref="ExportRedirectStrategy.Netlify"/>.
    /// </remarks>
    public ExportContext(
        string outputPath,
        string? seedRoutesPath,
        string baseUrl,
        ExportRedirectStrategy redirectStrategy)
        : this(outputPath, seedRoutesPath, initialSeedRoutes: null, baseUrl, ExportMode.Cdn, redirectStrategy)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ExportContext"/> with the specified export mode and redirect strategy.
    /// </summary>
    /// <param name="outputPath">The target directory for export.</param>
    /// <param name="seedRoutesPath">The path to initial seed routes, if any.</param>
    /// <param name="baseUrl">The base URL of the site to export.</param>
    /// <param name="mode">
    /// The export mode. <see cref="ExportMode.Cdn"/> is the default and validates plus rewrites exporter-managed URLs for static
    /// hosting. Use <see cref="ExportMode.Hybrid"/> when the exported output will still be served by application-aware routing.
    /// </param>
    /// <param name="redirectStrategy">
    /// Strategy used to materialize redirect aliases. <see cref="ExportRedirectStrategy.Html"/> is the generic static-host
    /// strategy; provider-native strategies can add stricter hosting constraints.
    /// </param>
    public ExportContext(
        string outputPath,
        string? seedRoutesPath,
        string baseUrl,
        ExportMode mode,
        ExportRedirectStrategy redirectStrategy)
        : this(outputPath, seedRoutesPath, initialSeedRoutes: null, baseUrl, mode, redirectStrategy)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ExportContext"/> with the specified configuration and in-memory seed
    /// routes.
    /// </summary>
    /// <param name="outputPath">The target directory for export.</param>
    /// <param name="seedRoutesPath">
    /// The path to initial seed routes, if any. When provided, this file is used instead of
    /// <paramref name="initialSeedRoutes"/>.
    /// </param>
    /// <param name="initialSeedRoutes">Optional in-memory initial seed routes used when <paramref name="seedRoutesPath"/> is null or blank.</param>
    /// <param name="baseUrl">The base URL of the site to export.</param>
    /// <param name="mode">
    /// The export mode. <see cref="ExportMode.Cdn"/> is the default and validates plus rewrites exporter-managed URLs for static
    /// hosting. Use <see cref="ExportMode.Hybrid"/> when the exported output will still be served by application-aware routing.
    /// </param>
    /// <remarks>
    /// This overload is for hosts that can derive seed routes directly from their own routing options. The engine applies
    /// the same normalization and fallback rules to in-memory seeds that it applies to seed-file lines.
    /// </remarks>
    public ExportContext(
        string outputPath,
        string? seedRoutesPath,
        IEnumerable<string>? initialSeedRoutes,
        string baseUrl,
        ExportMode mode = ExportMode.Cdn)
        : this(outputPath, seedRoutesPath, initialSeedRoutes, baseUrl, mode, ExportRedirectStrategy.Html)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ExportContext"/> with in-memory seed routes and a redirect strategy.
    /// </summary>
    /// <param name="outputPath">The target directory for export.</param>
    /// <param name="seedRoutesPath">
    /// The path to initial seed routes, if any. When provided, this file is used instead of
    /// <paramref name="initialSeedRoutes"/>.
    /// </param>
    /// <param name="initialSeedRoutes">Optional in-memory initial seed routes used when <paramref name="seedRoutesPath"/> is null or blank.</param>
    /// <param name="baseUrl">The base URL of the site to export.</param>
    /// <param name="redirectStrategy">Strategy used to materialize redirect aliases.</param>
    /// <remarks>
    /// This overload preserves the default <see cref="ExportMode.Cdn"/> mode while letting hosts choose how registered
    /// aliases become output artifacts or provider redirect rules.
    /// </remarks>
    public ExportContext(
        string outputPath,
        string? seedRoutesPath,
        IEnumerable<string>? initialSeedRoutes,
        string baseUrl,
        ExportRedirectStrategy redirectStrategy)
        : this(outputPath, seedRoutesPath, initialSeedRoutes, baseUrl, ExportMode.Cdn, redirectStrategy)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ExportContext"/> with in-memory seed routes, export mode, and redirect strategy.
    /// </summary>
    /// <param name="outputPath">The target directory for export.</param>
    /// <param name="seedRoutesPath">
    /// The path to initial seed routes, if any. When provided, this file is used instead of
    /// <paramref name="initialSeedRoutes"/>.
    /// </param>
    /// <param name="initialSeedRoutes">Optional in-memory initial seed routes used when <paramref name="seedRoutesPath"/> is null or blank.</param>
    /// <param name="baseUrl">The base URL of the site to export.</param>
    /// <param name="mode">
    /// The export mode. <see cref="ExportMode.Cdn"/> is the default and validates plus rewrites exporter-managed URLs for static
    /// hosting. Use <see cref="ExportMode.Hybrid"/> when the exported output will still be served by application-aware routing.
    /// </param>
    /// <param name="redirectStrategy">
    /// Strategy used to materialize redirect aliases. <see cref="ExportRedirectStrategy.Html"/> is the generic static-host
    /// strategy; provider-native strategies can add stricter hosting constraints.
    /// </param>
    public ExportContext(
        string outputPath,
        string? seedRoutesPath,
        IEnumerable<string>? initialSeedRoutes,
        string baseUrl,
        ExportMode mode,
        ExportRedirectStrategy redirectStrategy)
        : this(outputPath, seedRoutesPath, initialSeedRoutes, baseUrl, mode, redirectStrategy, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ExportContext"/> with in-memory seed routes, export mode, redirect strategy, and hybrid options.
    /// </summary>
    /// <param name="outputPath">The target directory for export.</param>
    /// <param name="seedRoutesPath">The path to initial seed routes, if any.</param>
    /// <param name="initialSeedRoutes">Optional in-memory initial seed routes used when <paramref name="seedRoutesPath"/> is null or blank.</param>
    /// <param name="baseUrl">The base URL of the site to export.</param>
    /// <param name="mode">The export mode.</param>
    /// <param name="redirectStrategy">Strategy used to materialize redirect aliases.</param>
    /// <param name="hybridOptions">
    /// Optional split-origin hybrid options. <see cref="ExportHybridOptions.LiveOrigin"/> must be either blank or an
    /// absolute HTTP(S) origin with no path, query, fragment, or userinfo; it is normalized before use.
    /// </param>
    /// <param name="publicOrigin">
    /// Optional public origin for same-origin metadata rewrites. It must be either blank or an absolute HTTP(S) origin
    /// with no path, query, fragment, or userinfo; it is normalized before use.
    /// </param>
    /// <remarks>
    /// The constructor validates public origin and hybrid live origin before export begins so direct API callers get the
    /// same origin contract as CLI callers. Use <paramref name="publicOrigin"/> for metadata that should point at the
    /// published static host, and use <paramref name="hybridOptions"/>.<see cref="ExportHybridOptions.LiveOrigin"/> only
    /// for RazorWire-managed live interactions in split-origin hybrid output.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when either configured origin is not an HTTP(S) origin or includes a path, query string, fragment, or
    /// userinfo.
    /// </exception>
    public ExportContext(
        string outputPath,
        string? seedRoutesPath,
        IEnumerable<string>? initialSeedRoutes,
        string baseUrl,
        ExportMode mode,
        ExportRedirectStrategy redirectStrategy,
        ExportHybridOptions? hybridOptions,
        string? publicOrigin = null)
    {
        OutputPath = outputPath;
        SeedRoutesPath = seedRoutesPath;
        InitialSeedRoutes = initialSeedRoutes?.ToArray() ?? [];
        BaseUrl = baseUrl.TrimEnd('/');
        Mode = mode;
        RedirectStrategy = redirectStrategy;
        var effectiveHybrid = hybridOptions ?? new ExportHybridOptions();
        if (!ExportHybridOptions.TryNormalizeOrigin(effectiveHybrid.LiveOrigin, out var normalizedLiveOrigin))
        {
            throw new ArgumentException(
                "Hybrid live origin must be an absolute http or https origin with no path, query, fragment, or userinfo.",
                nameof(hybridOptions));
        }

        if (!ExportHybridOptions.TryNormalizeOrigin(publicOrigin, out var normalizedPublicOrigin))
        {
            throw new ArgumentException(
                "Public origin must be an absolute http or https origin with no path, query, fragment, or userinfo.",
                nameof(publicOrigin));
        }

        Hybrid = new ExportHybridOptions
        {
            LiveOrigin = normalizedLiveOrigin,
            CredentialsMode = effectiveHybrid.CredentialsMode
        };
        PublicOrigin = normalizedPublicOrigin;
    }

    private static string NormalizeRedirectArtifactRoute(string route, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route, paramName);
        if (route.Contains('\r') || route.Contains('\n') || route.Contains('\t'))
        {
            throw new ArgumentException("Redirect alias routes must not contain newline, carriage return, or tab characters.", paramName);
        }

        var normalized = route.Trim().Replace('\\', '/');
        if (!normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Redirect alias routes must be root-relative and not protocol-relative.", paramName);
        }

        if (normalized.Contains('?') || normalized.Contains('#'))
        {
            throw new ArgumentException("Redirect alias routes must not contain query strings or fragments.", paramName);
        }

        return normalized.Length == 1 ? normalized : normalized.TrimEnd('/');
    }
}
