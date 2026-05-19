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
    /// Gets generated RazorDocs partial artifact URLs keyed by their source full-page route.
    /// </summary>
    internal Dictionary<string, string> PartialArtifactUrls { get; } = new(StringComparer.Ordinal);

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
        : this(outputPath, seedRoutesPath, initialSeedRoutes: null, baseUrl, mode)
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
    {
        OutputPath = outputPath;
        SeedRoutesPath = seedRoutesPath;
        InitialSeedRoutes = initialSeedRoutes?.ToArray() ?? [];
        BaseUrl = baseUrl.TrimEnd('/');
        Mode = mode;
    }
}
