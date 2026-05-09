namespace ForgeTrust.Runnable.Web.RazorWire.Cli;

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
    public string? SeedRoutesPath { get; }

    /// <summary>
    /// Gets the base URL of the source application being exported.
    /// </summary>
    public string BaseUrl { get; }

    /// <summary>
    /// Gets the export mode that controls URL rewriting and validation behavior.
    /// </summary>
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
    /// <param name="mode">The export mode. CDN mode is the default and validates static-host-safe output.</param>
    public ExportContext(
        string outputPath,
        string? seedRoutesPath,
        string baseUrl,
        ExportMode mode = ExportMode.Cdn)
    {
        OutputPath = outputPath;
        SeedRoutesPath = seedRoutesPath;
        BaseUrl = baseUrl.TrimEnd('/');
        Mode = mode;
    }
}
