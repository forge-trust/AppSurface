using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// A static generation engine that crawls a RazorWire application and exports its routes to CDN or hybrid static files.
/// </summary>
[ExcludeFromCodeCoverage]
public class ExportEngine
{
    private readonly ILogger<ExportEngine> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ExportReferenceProcessor _referenceProcessor;
    private readonly HtmlParser _htmlParser = new();

    /// <summary>
    /// Attribute emitted by AppSurface Docs to mark maintainer diagnostics chrome that RazorWire strips from exports.
    /// </summary>
    /// <remarks>
    /// The value is <c>data-docs-diagnostics-chrome</c>. It is a cross-component marker between the AppSurface Docs
    /// sidebar and <see cref="ExportEngine"/> only; consumers should not rely on it for production behavior or as a
    /// public styling hook.
    /// </remarks>
    private const string AppSurfaceDocsDiagnosticsChromeAttributeName = "data-docs-diagnostics-chrome";

    /// <summary>
    /// Matches marked <c>&lt;details&gt;</c> elements that contain <see cref="AppSurfaceDocsDiagnosticsChromeAttributeName"/>.
    /// </summary>
    /// <remarks>
    /// The pattern strips AppSurface Docs diagnostics disclosures whose opening <c>&lt;details&gt;</c> tag contains the
    /// marker attribute, optionally set to <c>true</c>. It uses case-insensitive, single-line matching so ordering,
    /// whitespace, and quote style variations are accepted, and the compiled static instance is safe to share across
    /// export operations.
    /// <para>
    /// This is a raw-HTML convenience regex, not an HTML parser. It assumes non-nested diagnostics
    /// <c>&lt;details&gt;</c> elements, relies on the marker being present on the opening tag, and removes through the
    /// first matching <c>&lt;/details&gt;</c>. Malformed HTML, nested details blocks, or marker-like text outside the
    /// intended diagnostics disclosure can produce incomplete stripping or false positives.
    /// </para>
    /// </remarks>
    private static readonly Regex AppSurfaceDocsDiagnosticsChromeRegex = new(
        @"<details\b(?=[^>]*\s" + AppSurfaceDocsDiagnosticsChromeAttributeName + @"(?:\s*=\s*(?:""true""|'true'|true))?(?=\s|/?>))[^>]*>.*?</details\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private const string DocsStaticPartialsMetaName = "rw-docs-static-partials";
    private const string DocsStaticPartialsMetaTag = "<meta name=\"rw-docs-static-partials\" content=\"1\" />";
    private const string AppSurfaceDocsClientConfigMarker = "window.__appSurfaceDocsConfig";
    private const int MaxArtifactRedirects = 10;
    private const string RedirectProvenanceDiagnosticCode = "RWEXPORT008";

    private static readonly Regex TurboFrameOpenTagRegex = new(
        @"<turbo-frame\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DocContentFrameIdRegex = new(
        @"\bid\s*=\s*(['""])\s*doc-content\s*\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportEngine"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public ExportEngine(ILogger<ExportEngine> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _referenceProcessor = new ExportReferenceProcessor(_logger);
    }

    /// <summary>
    /// Crawls the site starting from configured seed routes (or the root), stages the conventional reserved 404 page when available, validates CDN output when requested, and exports discovered pages, frame sources, and assets to the output path.
    /// </summary>
    /// <param name="context">Export configuration and runtime state including base URL, output path, queue, and visited set.</param>
    /// <param name="cancellationToken">Token to observe for cooperative cancellation of the crawl and export operations.</param>
    /// <remarks>
    /// If <see cref="ExportContext.SeedRoutesPath"/> is provided, the file is read and each line is validated and normalized to a root-relative route; invalid seeds are logged. If the seed file exists but yields no valid routes, the root path ("/") is enqueued. If no seed file is provided, <see cref="ExportContext.InitialSeedRoutes"/> is used when present; invalid in-memory seeds are logged and an all-invalid set also falls back to the root path. If neither source is provided, the root path is enqueued. Before crawl processing begins, the engine probes AppSurface's reserved conventional 404 route and stages <c>404.html</c> when the route returns a successful HTML response. That reserved-route probe is best-effort only: failures are logged, do not abort the crawl, and do not prevent queued seed routes from being processed. Once staged, the <c>404.html</c> body participates in the same CDN validation and reference rewriting as other HTML artifacts.
    ///
    /// Export then runs as a staged pipeline:
    ///
    /// <code>
    /// seed queue
    ///   -> crawl/fetch/discover
    ///   -> validate canonical artifacts and redirect aliases
    ///   -> materialize/rewrite text routes
    ///   -> redirect strategy
    ///        |-- html: write alias HTML fallback artifacts
    ///        `-- netlify: write root _redirects rules
    /// </code>
    ///
    /// The crawl stage records route outcomes, artifact URLs, and reference provenance. In CDN mode, HTML and CSS bodies
    /// are kept once until materialization so managed URLs can be rewritten after the artifact map is complete. In hybrid
    /// mode, text artifacts and binary assets are written directly to their final files and only their outcomes are retained
    /// in memory. Redirect alias registrations are validated before text materialization, then written according to
    /// <see cref="ExportContext.RedirectStrategy"/> after canonical artifacts exist. Netlify redirect output is an exact
    /// publish-root rule file and does not use <see cref="ExportContext.BaseUrl"/> or emitted artifact URLs.
    /// </remarks>
    /// <returns>A task that completes when the crawl and export operations have finished.</returns>
    /// <exception cref="FileNotFoundException">Thrown when <see cref="ExportContext.SeedRoutesPath"/> is specified but the file does not exist.</exception>
    public async Task RunAsync(ExportContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "RunAsync started. BaseUrl: {BaseUrl}, OutputPath: {OutputPath}",
            context.BaseUrl,
            context.OutputPath);

        ValidateDeploymentExtrasReleaseArchivePreflight(context);
        await QueueSeedRoutesAsync(context, cancellationToken);

        _logger.LogInformation(
            "Crawl starting from {BaseUrl} with {Count} seed routes.",
            context.BaseUrl,
            context.Queue.Count);

        var client = _httpClientFactory.CreateClient("ExportEngine");
        await TryStageConventionalNotFoundPageAsync(client, context, cancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (context.Queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var route = context.Queue.Dequeue();
            _logger.LogDebug("Processing route: {Route}", route);

            if (!context.Visited.Add(route))
            {
                continue;
            }

            await CrawlRouteAsync(client, route, context, cancellationToken);
        }

        ValidateExport(context);
        await MaterializeTextRoutesAsync(context, cancellationToken);
        await MaterializeRedirectsAsync(context, cancellationToken);
        await MaterializeDeploymentExtrasAsync(context, cancellationToken);
        if (context.ReleaseArchiveManifestEnabled)
        {
            context.ReleaseArchiveManifest = await ReleaseArchiveManifestWriter.WriteAsync(context.OutputPath, cancellationToken);
            _logger.LogInformation(
                """
                Release archive manifest written:
                  path: {ManifestPath}
                  schema: {ManifestSchema}
                  files: {ManifestFileCount}
                  sha256: {ManifestSha256}

                Catalog entry:
                  "releaseManifestSha256": "{ManifestSha256}"
                """,
                context.ReleaseArchiveManifest.ManifestPath,
                context.ReleaseArchiveManifest.Schema,
                context.ReleaseArchiveManifest.FileCount,
                context.ReleaseArchiveManifest.Sha256,
                context.ReleaseArchiveManifest.Sha256);
        }

        sw.Stop();
        _logger.LogInformation("Export completed in {ElapsedMilliseconds}ms", sw.ElapsedMilliseconds);
    }

    private static void ValidateDeploymentExtrasReleaseArchivePreflight(ExportContext context)
    {
        if (context.DeploymentExtras.Count == 0 || !context.ReleaseArchiveManifestEnabled)
        {
            return;
        }

        throw ExportDeploymentExtras.CreateException(
            "release-archive-incompatible",
            "Publish-root deployment extras cannot be copied into an exact AppSurface Docs release archive. Fix: export the archive to a clean directory without extras, then copy deployment files into the surrounding publish root.",
            ExportDeploymentExtras.RouteFallback);
    }

    /// <summary>
    /// Fetches the HTML or asset for the specified route, records export graph metadata, and enqueues discovered managed references.
    /// </summary>
    private async Task CrawlRouteAsync(
        HttpClient client,
        string route,
        ExportContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Exporting route: {Route}", route);

        try
        {
            using var response = await SendArtifactRequestAsync(client, context, route, route, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch {Route}: {StatusCode}", route, response.StatusCode);
                context.RouteOutcomes[route] = ExportRouteOutcome.NonSuccess(route, response.StatusCode);
                return;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var isHtml = string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase);
            var isCss = string.Equals(contentType, "text/css", StringComparison.OrdinalIgnoreCase);

            var filePath = MapRouteToFilePath(route, context.OutputPath, isHtml);
            var artifactUrl = MapFilePathToArtifactUrl(filePath, context.OutputPath, route);
            context.ArtifactUrls[route] = artifactUrl;

            if (isHtml)
            {
                var html = StripAppSurfaceDocsDiagnosticsChrome(await response.Content.ReadAsStringAsync(cancellationToken));
                html = ApplyPublicOriginRewrites(html, route, context);
                html = ApplyHybridRuntimeRewrites(html, route, context);
                var docContentFrame = ExtractDocContentFrame(html);
                context.RouteOutcomes[route] = context.Mode == ExportMode.Cdn
                    ? ExportRouteOutcome.Success(route, contentType, filePath, artifactUrl, html)
                    : ExportRouteOutcome.Success(route, contentType, filePath, artifactUrl);

                if (IsDocsExportPage(route, html, docContentFrame) && !string.IsNullOrWhiteSpace(docContentFrame))
                {
                    context.PartialArtifactUrls[route] = MapHtmlArtifactUrlToPartialUrl(artifactUrl);
                }

                AddReferencesAndQueue(ExtractReferences(html, route, htmlScope: true), context);
                if (context.Mode == ExportMode.Hybrid)
                {
                    await WriteHtmlRouteAsync(route, filePath, html, context, rewriteManagedReferences: false, cancellationToken);
                }
            }
            else if (isCss)
            {
                var css = await response.Content.ReadAsStringAsync(cancellationToken);
                context.RouteOutcomes[route] = context.Mode == ExportMode.Cdn
                    ? ExportRouteOutcome.Success(route, contentType, filePath, artifactUrl, css)
                    : ExportRouteOutcome.Success(route, contentType, filePath, artifactUrl);
                AddReferencesAndQueue(ExtractReferences(css, route, htmlScope: false), context);
                if (context.Mode == ExportMode.Hybrid)
                {
                    await WriteCssRouteAsync(route, filePath, css, context, rewriteManagedReferences: false, cancellationToken);
                }
            }
            else
            {
                EnsureDirectoryExists(filePath);
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true);
                await contentStream.CopyToAsync(fileStream, cancellationToken);
                context.RouteOutcomes[route] = ExportRouteOutcome.Success(route, contentType, filePath, artifactUrl);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExportValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting {Route}", route);
            context.RouteOutcomes[route] = ExportRouteOutcome.Failed(route, ex);
        }
    }

    private async Task QueueSeedRoutesAsync(ExportContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.SeedRoutesPath))
        {
            if (!File.Exists(context.SeedRoutesPath))
            {
                _logger.LogError("Seed routes file not found: {SeedRoutesPath}", context.SeedRoutesPath);

                throw new FileNotFoundException(
                    "The specified seed routes file does not exist.",
                    context.SeedRoutesPath);
            }

            var seeds = await File.ReadAllLinesAsync(context.SeedRoutesPath, cancellationToken);

            QueueSeedRoutes(context, seeds, cancellationToken);

            if (context.Queue.Count == 0)
            {
                _logger.LogWarning(
                    "Seed file provided but no valid routes were found. Falling back to default root path.");
                EnqueueRoute(context, "/");
            }

            QueueAdditionalSeedRoutes(context, cancellationToken);
            return;
        }

        if (context.InitialSeedRoutes.Count > 0)
        {
            QueueSeedRoutes(context, context.InitialSeedRoutes, cancellationToken);

            if (context.Queue.Count == 0)
            {
                _logger.LogWarning(
                    "Initial seed routes were provided but no valid routes were found. Falling back to default root path.");
                EnqueueRoute(context, "/");
            }

            QueueAdditionalSeedRoutes(context, cancellationToken);
            return;
        }

        EnqueueRoute(context, "/");
        QueueAdditionalSeedRoutes(context, cancellationToken);
    }

    private void QueueAdditionalSeedRoutes(ExportContext context, CancellationToken cancellationToken)
    {
        if (context.AdditionalSeedRoutes.Count == 0)
        {
            return;
        }

        QueueSeedRoutes(context, context.AdditionalSeedRoutes, cancellationToken);
    }

    /// <summary>
    /// Validates, normalizes, and enqueues seed routes from a file or in-memory route collection.
    /// </summary>
    /// <param name="context">Export context whose queue receives valid normalized routes.</param>
    /// <param name="seeds">Raw seed route values to validate.</param>
    /// <param name="cancellationToken">Token observed between seed values.</param>
    private void QueueSeedRoutes(
        ExportContext context,
        IEnumerable<string> seeds,
        CancellationToken cancellationToken)
    {
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var added = false;
            if (TryGetNormalizedSeedRoute(seed, context, out var normalized))
            {
                added = true;
                EnqueueRoute(context, normalized);
            }

            if (!added)
            {
                _logger.LogWarning("Invalid seed route: {SeedRoute}", seed);
            }
        }
    }

    private async Task MaterializeTextRoutesAsync(ExportContext context, CancellationToken cancellationToken)
    {
        foreach (var outcome in context.RouteOutcomes.Values.Where(o => o.Succeeded && o.TextBody is not null).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (outcome.ArtifactPath is null || outcome.ArtifactUrl is null)
            {
                continue;
            }

            EnsureDirectoryExists(outcome.ArtifactPath);
            var body = outcome.TextBody!;
            if (outcome.IsHtml)
            {
                await WriteHtmlRouteAsync(outcome.Route, outcome.ArtifactPath, body, context, rewriteManagedReferences: context.Mode == ExportMode.Cdn, cancellationToken);
            }
            else if (outcome.IsCss)
            {
                await WriteCssRouteAsync(outcome.Route, outcome.ArtifactPath, body, context, rewriteManagedReferences: context.Mode == ExportMode.Cdn, cancellationToken);
            }

            context.RouteOutcomes[outcome.Route] = ExportRouteOutcome.Success(
                outcome.Route,
                outcome.ContentType,
                outcome.ArtifactPath,
                outcome.ArtifactUrl);
        }
    }

    private async Task MaterializeRedirectsAsync(ExportContext context, CancellationToken cancellationToken)
    {
        switch (context.RedirectStrategy)
        {
            case ExportRedirectStrategy.Html:
                await MaterializeHtmlRedirectArtifactsAsync(context, cancellationToken);
                break;
            case ExportRedirectStrategy.Netlify:
                await MaterializeNetlifyRedirectsAsync(context, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unsupported redirect strategy '{context.RedirectStrategy}'.");
        }
    }

    private async Task MaterializeDeploymentExtrasAsync(ExportContext context, CancellationToken cancellationToken)
    {
        if (context.DeploymentExtras.Count == 0)
        {
            return;
        }

        var inventory = BuildOwnedPublishPathInventory(context);
        var diagnostics = new List<ExportDiagnostic>();
        foreach (var extra in context.DeploymentExtras)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = ExportDeploymentExtras.MapPublishPathToFilePath(context.OutputPath, extra.PublishPath);
            try
            {
                ExportDeploymentExtras.ValidateTargetParentPath(context.OutputPath, targetPath, extra.PublishPath);
            }
            catch (ExportValidationException ex)
            {
                diagnostics.AddRange(ex.Diagnostics);
                continue;
            }

            if (inventory.TryGetOwner(targetPath, out var owner))
            {
                diagnostics.Add(
                    ExportDeploymentExtras.CreateDiagnostic(
                        "target-generated-collision",
                        $"Publish-root deployment extra target '{extra.PublishPath}' would overwrite exporter-owned output '{owner}'. Fix: choose a different publishPath or remove the generated route/provider output.",
                        extra.PublishPath));
                continue;
            }

            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                diagnostics.Add(
                    ExportDeploymentExtras.CreateDiagnostic(
                        "target-exists",
                        $"Publish-root deployment extra target '{extra.PublishPath}' already exists at '{targetPath}'. Fix: export to a clean output directory or choose a different publishPath; deployment extras never overwrite existing files.",
                        extra.PublishPath));
            }
        }

        if (diagnostics.Count > 0)
        {
            throw new ExportValidationException(diagnostics);
        }

        foreach (var extra in context.DeploymentExtras)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CopyDeploymentExtraAsync(context.OutputPath, extra, cancellationToken);
        }
    }

    private static async Task CopyDeploymentExtraAsync(
        string outputPath,
        ExportDeploymentExtra extra,
        CancellationToken cancellationToken)
    {
        var targetPath = ExportDeploymentExtras.MapPublishPathToFilePath(outputPath, extra.PublishPath);
        // Keep the copy helper defensive if future call paths bypass the batch preflight.
        ExportDeploymentExtras.ValidateTargetParentPath(outputPath, targetPath, extra.PublishPath);
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var tempPath = Path.Join(
            targetDirectory ?? Path.GetFullPath(outputPath),
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(
                             extra.SourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 128 * 1024,
                             useAsync: true))
            await using (var destination = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            File.Move(tempPath, targetPath, overwrite: false);
        }
        catch (OperationCanceledException)
        {
            DeleteTempFile(tempPath);
            throw;
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            DeleteTempFile(tempPath);
            throw ExportDeploymentExtras.CreateException(
                "copy-failed",
                $"Publish-root deployment extra '{extra.PublishPath}' could not be copied from '{extra.SourcePath}' to '{targetPath}': {ex.Message}. Fix: verify file permissions and export to a clean output directory.",
                extra.PublishPath);
        }
    }

    private static void DeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            // Best-effort cleanup; the original copy/cancellation failure is more useful to callers.
        }
    }

    private ExportOwnedPublishPathInventory BuildOwnedPublishPathInventory(ExportContext context)
    {
        var inventory = new ExportOwnedPublishPathInventory();
        foreach (var outcome in context.RouteOutcomes.Values.Where(outcome => outcome.Succeeded && outcome.ArtifactPath is not null))
        {
            inventory.Add(outcome.ArtifactPath!, $"route '{outcome.Route}'");
            if (outcome.IsHtml && context.PartialArtifactUrls.ContainsKey(outcome.Route))
            {
                inventory.Add(MapHtmlFilePathToPartialPath(outcome.ArtifactPath!), $"docs partial for route '{outcome.Route}'");
            }
        }

        inventory.Add(Path.Join(context.OutputPath, "404.html"), "conventional 404 artifact");
        inventory.Add(Path.Join(context.OutputPath, "_redirects"), "provider redirect artifact");
        inventory.Add(Path.Join(context.OutputPath, "_headers"), "provider headers artifact");
        inventory.Add(Path.Join(context.OutputPath, ".appsurface-docs-route-manifest.json"), "AppSurface Docs route manifest");
        inventory.Add(Path.Join(context.OutputPath, ReleaseArchiveManifestWriter.FileName), "AppSurface Docs release archive manifest");

        foreach (var artifact in context.RedirectArtifacts)
        {
            if (context.RedirectStrategy == ExportRedirectStrategy.Html)
            {
                inventory.Add(
                    MapRouteToFilePath(artifact.AliasRoute, context.OutputPath, isHtml: true),
                    $"redirect alias '{artifact.AliasRoute}'");
            }
        }

        return inventory;
    }

    private static bool IsNonFatal(Exception ex)
    {
        return ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not ThreadAbortException;
    }

    private sealed class ExportOwnedPublishPathInventory
    {
        private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);

        internal void Add(string path, string owner)
        {
            _owners.TryAdd(NormalizeArtifactPath(path), owner);
        }

        internal bool TryGetOwner(string path, [NotNullWhen(true)] out string? owner)
        {
            return _owners.TryGetValue(NormalizeArtifactPath(path), out owner);
        }
    }

    private string ApplyPublicOriginRewrites(string html, string route, ExportContext context)
    {
        if (string.IsNullOrWhiteSpace(context.PublicOrigin))
        {
            return html;
        }

        var document = _htmlParser.ParseDocument(html);
        var changed = false;

        foreach (var link in document.QuerySelectorAll("link[rel~=\"canonical\"][href]"))
        {
            changed |= TrySetPublicOriginUrl(link, "href", route, context);
        }

        foreach (var meta in document.QuerySelectorAll("meta[content]"))
        {
            var key = meta.GetAttribute("property") ?? meta.GetAttribute("name") ?? string.Empty;
            if (string.Equals(key, "og:url", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "twitter:url", StringComparison.OrdinalIgnoreCase))
            {
                changed |= TrySetPublicOriginUrl(meta, "content", route, context);
            }
        }

        return changed ? SerializeDocument(document, html) : html;
    }

    private bool TrySetPublicOriginUrl(IElement element, string attributeName, string route, ExportContext context)
    {
        if (!TryResolveOriginUrl(element.GetAttribute(attributeName), route, context, context.PublicOrigin!, out var publicUrl, out _))
        {
            return false;
        }

        element.SetAttribute(attributeName, publicUrl);
        return true;
    }

    private string ApplyHybridRuntimeRewrites(string html, string route, ExportContext context)
    {
        if (context.Mode != ExportMode.Hybrid && !MayContainAntiforgerySurface(html))
        {
            return html;
        }

        var document = _htmlParser.ParseDocument(html);
        var changed = false;

        if (context.Mode == ExportMode.Hybrid && context.Hybrid.HasLiveOrigin)
        {
            foreach (var script in document.QuerySelectorAll("script[src]")
                         .Where(script => (script.GetAttribute("src") ?? string.Empty)
                             .Contains("/razorwire/razorwire.js", StringComparison.OrdinalIgnoreCase)))
            {
                script.SetAttribute("data-rw-live-origin", context.Hybrid.LiveOrigin!);
                script.SetAttribute("data-rw-hybrid-credentials", ResolveHybridCredentialsAttribute(context));
                var antiforgeryEndpoint = script.GetAttribute("data-rw-antiforgery-endpoint");
                if (string.IsNullOrWhiteSpace(antiforgeryEndpoint))
                {
                    script.SetAttribute("data-rw-antiforgery-endpoint", "/_rw/antiforgery/token");
                }
                else if (TryResolveManagedUrl(antiforgeryEndpoint, route, context, out var managedEndpoint, out _))
                {
                    script.SetAttribute("data-rw-antiforgery-endpoint", managedEndpoint);
                }

                changed = true;
            }

            foreach (var streamResolution in document.QuerySelectorAll("rw-stream-source[src]")
                         .Select(streamSource => (
                             Element: streamSource,
                             LiveUrl: TryResolveOriginUrl(streamSource.GetAttribute("src"), route, context, context.Hybrid.LiveOrigin!, out var liveUrl, out _)
                                 ? liveUrl
                                 : null))
                         .Where(streamResolution => streamResolution.LiveUrl is not null))
            {
                streamResolution.Element.SetAttribute("src", streamResolution.LiveUrl!);
                changed = true;
            }

            foreach (var islandResolution in document.QuerySelectorAll("turbo-frame[data-rw-island=\"true\"][src]")
                         .Select(island => (
                             Element: island,
                             LiveUrl: TryResolveOriginUrl(island.GetAttribute("src"), route, context, context.Hybrid.LiveOrigin!, out var liveUrl, out _)
                                 ? liveUrl
                                 : null))
                         .Where(islandResolution => islandResolution.LiveUrl is not null))
            {
                islandResolution.Element.SetAttribute("src", islandResolution.LiveUrl!);
                changed = true;
            }
        }

        foreach (var form in document.QuerySelectorAll("form[data-rw-form=\"true\"]"))
        {
            changed |= TryRewriteHybridForm(form, route, context, document);
        }

        foreach (var form in document.QuerySelectorAll("form"))
        {
            if (string.Equals(form.GetAttribute("data-rw-form"), "true", StringComparison.OrdinalIgnoreCase)
                || !HasExportAntiforgerySurface(form, document))
            {
                continue;
            }

            AddExportAntiforgeryDiagnostic(
                context,
                route,
                "Static anti-forgery token cannot be exported because the form is not managed by RazorWire. Problem: the form contains a request-specific __RequestVerificationToken minted for the export crawler. Cause: static hosts cannot mint a token for the real user, and RazorWire can only auto-refresh forms it owns. Fix: add rw-active to let RazorWire manage the form, load the form from a live endpoint, or remove the static token before export.");
        }

        AddUnownedAntiforgeryTokenDiagnostics(document, context, route);

        return changed ? SerializeDocument(document, html) : html;
    }

    private bool TryRewriteHybridForm(IElement form, string route, ExportContext context, IDocument document)
    {
        var tokenInputs = FindAntiforgeryTokenInputs(form, document);
        var antiforgeryMode = form.GetAttribute("data-rw-antiforgery") ?? string.Empty;
        var hasBakedToken = tokenInputs.Count > 0;
        var wantsLazy = string.Equals(antiforgeryMode, "lazy", StringComparison.OrdinalIgnoreCase);
        var optsOut = string.Equals(antiforgeryMode, "off", StringComparison.OrdinalIgnoreCase);

        if (!hasBakedToken && !wantsLazy)
        {
            return false;
        }

        if (context.Mode == ExportMode.Cdn)
        {
            AddExportAntiforgeryDiagnostic(
                context,
                route,
                "Static RazorWire form anti-forgery token cannot be exported in CDN mode. Problem: the form contains a request-specific __RequestVerificationToken minted for the export crawler. Cause: a plain static host cannot mint or refresh a token for the real user. Fix: use --mode hybrid with backend passthrough for RazorWire endpoints, use --mode hybrid --live-origin for split-origin live calls, or load the form from a live endpoint instead of static HTML.");
            return false;
        }

        if (optsOut)
        {
            AddExportAntiforgeryDiagnostic(
                context,
                route,
                "Static RazorWire form anti-forgery token cannot be exported because the form opts out of lazy refresh. Problem: the form contains a request-specific __RequestVerificationToken minted for the export crawler. Cause: static hosts cannot mint a token for the real user. Fix: remove data-rw-antiforgery=\"off\", allow export to auto-convert the form, or load the form from a live RazorWire frame.");
            return false;
        }

        if (context.Hybrid.HasLiveOrigin && !context.Hybrid.IncludesCredentials)
        {
            AddExportAntiforgeryDiagnostic(
                context,
                route,
                "Static RazorWire form anti-forgery token cannot be exported because hybrid credentials are explicitly omitted. Problem: lazy anti-forgery needs the token cookie to round-trip to the live origin. Cause: --hybrid-credentials omit disables credentialed managed live calls. Fix: use the default hybrid credentials behavior or remove --hybrid-credentials omit for forms that use anti-forgery.");
            return false;
        }

        var action = form.GetAttribute("action");
        if (context.Hybrid.HasLiveOrigin)
        {
            if (!TryResolveOriginUrl(action, route, context, context.Hybrid.LiveOrigin!, out var liveAction, out var error))
            {
                AddExportAntiforgeryDiagnostic(
                    context,
                    route,
                    $"Static RazorWire form anti-forgery token cannot be exported because the form action cannot be rewritten to the live origin. Problem: the form contains a request-specific __RequestVerificationToken minted for the export crawler. Cause: {error} Fix: use a root-relative, relative, empty, or same-origin form action, mark the form for live-frame rendering, or remove RazorWire form enhancement.");
                return false;
            }

            form.SetAttribute("action", liveAction);
        }
        else if (!TryResolveManagedUrl(action, route, context, out var managedAction, out var error))
        {
            AddExportAntiforgeryDiagnostic(
                context,
                route,
                $"Static RazorWire form anti-forgery token cannot be exported because the form action cannot be preserved as an app-owned hybrid route. Problem: the form contains a request-specific __RequestVerificationToken minted for the export crawler. Cause: {error} Fix: use a root-relative, relative, empty, or same-origin form action, mark the form for live-frame rendering, or remove RazorWire form enhancement.");
            return false;
        }
        else
        {
            form.SetAttribute("action", managedAction);
        }

        form.SetAttribute("data-rw-antiforgery", "lazy");
        foreach (var tokenInput in tokenInputs)
        {
            tokenInput.Remove();
        }

        _logger.LogInformation(
            "RWEXPORT006 Static RazorWire anti-forgery token on {Route} was converted to lazy runtime refresh.",
            route);
        return true;
    }

    private static bool MayContainAntiforgerySurface(string html)
    {
        return html.Contains("data-rw-antiforgery", StringComparison.OrdinalIgnoreCase)
               || html.Contains("__RequestVerificationToken", StringComparison.Ordinal)
               || html.Contains("VerificationToken", StringComparison.OrdinalIgnoreCase)
               || html.Contains("antiforgery", StringComparison.OrdinalIgnoreCase)
               || html.Contains("csrf", StringComparison.OrdinalIgnoreCase)
               || html.Contains("xsrf", StringComparison.OrdinalIgnoreCase);
    }

    private void AddUnownedAntiforgeryTokenDiagnostics(IDocument document, ExportContext context, string route)
    {
        foreach (var input in document.QuerySelectorAll("input"))
        {
            if (!IsAntiforgeryTokenInput(input)
                || FindOwningForm(input, document) is not null)
            {
                continue;
            }

            AddExportAntiforgeryDiagnostic(
                context,
                route,
                "Static anti-forgery token cannot be exported because the token input is not owned by any form in the document. Problem: the input contains a request-specific __RequestVerificationToken minted for the export crawler. Cause: static hosts cannot mint a token for the real user, and RazorWire can only auto-refresh tokens that submit with a RazorWire-managed form. Fix: attach the token to a RazorWire form, load the form from a live endpoint, or remove the static token before export.");
        }
    }

    private static IReadOnlyList<IElement> FindAntiforgeryTokenInputs(IElement form, IDocument document)
    {
        var tokens = form.QuerySelectorAll("input")
            .Where(input => IsAntiforgeryTokenInput(input)
                            && FindOwningForm(input, document) == form)
            .ToList();

        var formId = form.GetAttribute("id");
        if (string.IsNullOrWhiteSpace(formId))
        {
            return tokens;
        }

        foreach (var token in document.QuerySelectorAll("input[form]")
                     .Where(input => IsAntiforgeryTokenInput(input)
                                     && string.Equals(input.GetAttribute("form"), formId, StringComparison.Ordinal))
                     .Where(token => !tokens.Contains(token)))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    private static bool HasExportAntiforgerySurface(IElement form, IDocument document)
    {
        var antiforgeryMode = form.GetAttribute("data-rw-antiforgery") ?? string.Empty;
        return FindAntiforgeryTokenInputs(form, document).Count > 0
               || string.Equals(antiforgeryMode, "lazy", StringComparison.OrdinalIgnoreCase);
    }

    private static IElement? FindOwningForm(IElement input, IDocument document)
    {
        if (input.HasAttribute("form"))
        {
            var formId = input.GetAttribute("form");
            if (string.IsNullOrEmpty(formId))
            {
                return null;
            }

            var explicitOwner = document.GetElementById(formId);
            return string.Equals(explicitOwner?.LocalName, "form", StringComparison.OrdinalIgnoreCase)
                ? explicitOwner
                : null;
        }

        for (var ancestor = input.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            if (string.Equals(ancestor.LocalName, "form", StringComparison.OrdinalIgnoreCase))
            {
                return ancestor;
            }
        }

        return null;
    }

    private static bool IsAntiforgeryTokenInput(IElement input)
    {
        var name = input.GetAttribute("name") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return string.Equals(name, "__RequestVerificationToken", StringComparison.Ordinal)
               || name.Contains("RequestVerificationToken", StringComparison.OrdinalIgnoreCase)
               || name.Contains("VerificationToken", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase)
               || name.Contains("AntiForgery", StringComparison.OrdinalIgnoreCase)
               || name.Contains("csrf", StringComparison.OrdinalIgnoreCase)
               || name.Contains("xsrf", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolveOriginUrl(
        string? rawValue,
        string route,
        ExportContext context,
        string targetOrigin,
        out string resolvedUrl,
        out string error)
    {
        resolvedUrl = string.Empty;
        error = string.Empty;

        if (!TryResolveManagedUrl(rawValue, route, context, out var managedUrl, out error))
        {
            return false;
        }

        resolvedUrl = targetOrigin.TrimEnd('/') + managedUrl;
        return true;
    }

    private bool TryResolveManagedUrl(
        string? rawValue,
        string route,
        ExportContext context,
        out string managedUrl,
        out string error)
    {
        managedUrl = string.Empty;
        error = string.Empty;

        var value = rawValue?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            value = route;
        }

        if (value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{value}' is not an app-owned HTTP route.";
            return false;
        }

        if (!Uri.TryCreate(EnsureTrailingSlash(context.BaseUrl), UriKind.Absolute, out var baseUri))
        {
            error = "The export base URL is not an absolute HTTP route.";
            return false;
        }

        string resolved;
        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(baseUri.GetLeftPart(UriPartial.Authority) + value, UriKind.Absolute, out var rootRelativeUri)
                || !TryGetAppRelativeRoute(rootRelativeUri, baseUri, out resolved))
            {
                error = $"'{value}' points outside the exported application origin.";
                return false;
            }
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.Scheme != Uri.UriSchemeHttp && absoluteUri.Scheme != Uri.UriSchemeHttps)
            {
                error = $"'{value}' is not an app-owned HTTP route.";
                return false;
            }

            if (!HasSameOrigin(absoluteUri, baseUri)
                || !TryGetAppRelativeRoute(absoluteUri, baseUri, out resolved))
            {
                error = $"'{value}' points outside the exported application origin.";
                return false;
            }
        }
        else
        {
            resolved = _referenceProcessor.ResolveRelativeUrl(route, value);
        }

        if (!ExportReferenceProcessor.TrySplitManagedUrl(resolved, out var path, out var query, out var fragment))
        {
            error = $"'{value}' is not a root-relative managed route after normalization.";
            return false;
        }

        managedUrl = path + query + fragment;
        return true;
    }

    private static string ResolveHybridCredentialsAttribute(ExportContext context)
    {
        return context.Hybrid.IncludesCredentials ? "include" : "omit";
    }

    private static string SerializeDocument(IDocument document, string originalHtml)
    {
        var html = document.DocumentElement.OuterHtml;
        var trimmedOriginal = originalHtml.TrimStart();
        if (trimmedOriginal.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase))
        {
            return "<!doctype html>" + Environment.NewLine + html;
        }

        if (trimmedOriginal.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        return document.Body?.InnerHtml ?? html;
    }

    private static void AddExportAntiforgeryDiagnostic(
        ExportContext context,
        string route,
        string message)
    {
        if (context.Diagnostics.Any(diagnostic => diagnostic.Code == "RWEXPORT006"
                                                  && diagnostic.Route == route
                                                  && diagnostic.Message == message))
        {
            return;
        }

        context.Diagnostics.Add(new ExportDiagnostic("RWEXPORT006", message, route));
    }

    private async Task MaterializeHtmlRedirectArtifactsAsync(ExportContext context, CancellationToken cancellationToken)
    {
        var writtenArtifactPaths = new HashSet<string>(CreateArtifactPathComparer());
        foreach (var artifact in context.RedirectArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!context.ArtifactUrls.TryGetValue(artifact.CanonicalRoute, out var canonicalArtifactUrl))
            {
                continue;
            }

            var artifactPath = MapRouteToFilePath(artifact.AliasRoute, context.OutputPath, isHtml: true);
            var artifactUrl = MapFilePathToArtifactUrl(artifactPath, context.OutputPath, artifact.AliasRoute);
            if (writtenArtifactPaths.Add(artifactPath))
            {
                EnsureDirectoryExists(artifactPath);
                await File.WriteAllTextAsync(
                    artifactPath,
                    BuildRedirectArtifactBody(canonicalArtifactUrl),
                    cancellationToken);
            }

            context.ArtifactUrls[artifact.AliasRoute] = artifactUrl;
            context.RouteOutcomes[artifact.AliasRoute] = ExportRouteOutcome.RedirectAliasArtifact(
                artifact.AliasRoute,
                artifactPath,
                artifactUrl);
        }
    }

    private async Task MaterializeNetlifyRedirectsAsync(ExportContext context, CancellationToken cancellationToken)
    {
        if (context.RedirectArtifacts.Count == 0)
        {
            return;
        }

        var rules = context.RedirectArtifacts
            .Select(artifact => new NetlifyRedirectRule(
                SerializeNetlifyRedirectPath(artifact.AliasRoute),
                SerializeNetlifyRedirectPath(artifact.CanonicalRoute)))
            .Distinct()
            .OrderBy(rule => rule.From, StringComparer.Ordinal)
            .ThenBy(rule => rule.To, StringComparer.Ordinal)
            .ToArray();

        if (rules.Length == 0)
        {
            return;
        }

        var redirectsPath = Path.Join(context.OutputPath, "_redirects");
        EnsureDirectoryExists(redirectsPath);
        var body = string.Join(
            Environment.NewLine,
            rules.Select(rule => $"{rule.From} {rule.To} 301!"));
        await File.WriteAllTextAsync(redirectsPath, body + Environment.NewLine, cancellationToken);
    }

    private bool TryGetNormalizedSeedRoute(string seed, ExportContext context, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(seed, UriKind.RelativeOrAbsolute, out var uri))
        {
            return false;
        }

        var route = seed;
        if (uri.IsAbsoluteUri)
        {
            if (!Uri.TryCreate(EnsureTrailingSlash(context.BaseUrl), UriKind.Absolute, out var baseUri)
                || !HasSameOrigin(uri, baseUri)
                || !TryGetAppRelativeRoute(uri, baseUri, out route))
            {
                return false;
            }
        }

        return TryGetNormalizedRoute(route, out normalized);
    }

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith('/') ? value : value + "/";
    }

    private static bool HasSameOrigin(Uri left, Uri right)
    {
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;
    }

    private static bool TryGetAppRelativeRoute(Uri seedUri, Uri baseUri, out string route)
    {
        route = string.Empty;
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var seedPath = seedUri.AbsolutePath;

        if (string.IsNullOrEmpty(basePath))
        {
            route = seedPath + seedUri.Query + seedUri.Fragment;
            return true;
        }

        if (string.Equals(seedPath, basePath, StringComparison.OrdinalIgnoreCase))
        {
            route = "/" + seedUri.Query + seedUri.Fragment;
            return true;
        }

        if (seedPath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            route = seedPath[basePath.Length..] + seedUri.Query + seedUri.Fragment;
            return true;
        }

        return false;
    }

    private async Task<HttpResponseMessage> SendArtifactRequestAsync(
        HttpClient client,
        ExportContext context,
        string artifactRoute,
        string fetchRoute,
        CancellationToken cancellationToken)
    {
        var baseUri = CreateExportBaseUri(context);
        var currentUri = CreateArtifactRequestUri(context, fetchRoute);

        for (var redirectCount = 0; redirectCount <= MaxArtifactRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var finalUri = response.RequestMessage?.RequestUri ?? currentUri;
            if (!IsWithinExportBoundary(finalUri, baseUri))
            {
                response.Dispose();
                ThrowRedirectProvenanceFailure(
                    context,
                    artifactRoute,
                    $"Export route '{artifactRoute}' received a response from '{SanitizeUriForDiagnostic(finalUri)}', which is outside the configured export origin and app path. Fix: keep export redirects on the same scheme, host, port, and base path, or make the reference external instead of exporter-managed.");
            }

            if (!IsRedirectStatusCode(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                ThrowRedirectProvenanceFailure(
                    context,
                    artifactRoute,
                    $"Export route '{artifactRoute}' returned redirect status {(int)response.StatusCode} without a Location header. Fix: return a final artifact response or a redirect target inside the configured export origin and app path.");
            }

            var redirectUri = ResolveRedirectUri(finalUri, location);
            if (!IsWithinExportBoundary(redirectUri, baseUri))
            {
                response.Dispose();
                ThrowRedirectProvenanceFailure(
                    context,
                    artifactRoute,
                    $"Export route '{artifactRoute}' redirected to '{SanitizeUriForDiagnostic(redirectUri)}', which is outside the configured export origin and app path. Fix: keep export redirects on the same scheme, host, port, and base path, or make the reference external instead of exporter-managed.");
            }

            response.Dispose();
            currentUri = redirectUri;
        }

        ThrowRedirectProvenanceFailure(
            context,
            artifactRoute,
            $"Export route '{artifactRoute}' exceeded the artifact redirect limit of {MaxArtifactRedirects}. Fix: remove the redirect loop or return a final response inside the configured export origin and app path.");

        throw new InvalidOperationException("Artifact redirect validation reached an unreachable state.");
    }

    private static Uri CreateExportBaseUri(ExportContext context)
    {
        return new Uri(EnsureTrailingSlash(context.BaseUrl), UriKind.Absolute);
    }

    private static Uri CreateArtifactRequestUri(ExportContext context, string route)
    {
        return new Uri(context.BaseUrl.TrimEnd('/') + route, UriKind.Absolute);
    }

    private static bool IsWithinExportBoundary(Uri uri, Uri baseUri)
    {
        return uri.IsAbsoluteUri
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && HasSameOrigin(uri, baseUri)
            && HasSafeEscapedPathSegments(uri)
            && HasSafeEscapedPathSegments(baseUri)
            && TryGetAppRelativeRoute(uri, baseUri, out _);
    }

    private static bool HasSafeEscapedPathSegments(Uri uri)
    {
        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            string unescapedSegment;
            try
            {
                unescapedSegment = Uri.UnescapeDataString(segment);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (unescapedSegment is "." or ".."
                || unescapedSegment.Contains('/', StringComparison.Ordinal)
                || unescapedSegment.Contains('\\', StringComparison.Ordinal)
                || unescapedSegment.Any(char.IsControl))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static Uri ResolveRedirectUri(Uri currentUri, Uri location)
    {
        return location.IsAbsoluteUri
            ? location
            : new Uri(currentUri, location);
    }

    [DoesNotReturn]
    private static void ThrowRedirectProvenanceFailure(
        ExportContext context,
        string route,
        string message)
    {
        AddRedirectProvenanceDiagnostic(context, route, message);
        throw new ExportValidationException(context.Diagnostics);
    }

    private static void AddRedirectProvenanceDiagnostic(
        ExportContext context,
        string route,
        string message)
    {
        if (context.Diagnostics.Any(diagnostic => diagnostic.Code == RedirectProvenanceDiagnosticCode
                                                  && diagnostic.Route == route
                                                  && diagnostic.Message == message))
        {
            return;
        }

        context.Diagnostics.Add(new ExportDiagnostic(RedirectProvenanceDiagnosticCode, message, route));
    }

    private static string SanitizeUriForDiagnostic(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return uri.OriginalString;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.ToString();
    }

    private async Task WriteHtmlRouteAsync(
        string route,
        string filePath,
        string body,
        ExportContext context,
        bool rewriteManagedReferences,
        CancellationToken cancellationToken)
    {
        body = StripAppSurfaceDocsDiagnosticsChrome(body);
        var docContentFrame = ExtractDocContentFrame(body);
        var isDocsPage = IsDocsExportPage(route, body, docContentFrame);
        var htmlForWrite = isDocsPage
            ? AddDocsStaticPartialsMarker(body)
            : body;
        htmlForWrite = rewriteManagedReferences
            ? RewriteManagedReferences(htmlForWrite, route, htmlScope: true, context)
            : htmlForWrite;
        EnsureDirectoryExists(filePath);
        await File.WriteAllTextAsync(filePath, htmlForWrite, cancellationToken);
        await TryWriteDocsPartialAsync(
            filePath,
            ExtractDocContentFrame(htmlForWrite),
            cancellationToken);
    }

    /// <summary>
    /// Removes AppSurface Docs maintainer diagnostics chrome from export HTML before link discovery or artifact writes.
    /// </summary>
    /// <remarks>
    /// AppSurface Docs diagnostics links are useful in live maintainer hosts but must not become reader-facing static
    /// artifacts. The marker is owned by the AppSurface Docs sidebar view and allows the generic RazorWire exporter to
    /// suppress the whole diagnostics disclosure even when exporting from a URL source whose host has diagnostics chrome
    /// enabled.
    /// <para>
    /// <see cref="StripAppSurfaceDocsDiagnosticsChrome"/> returns null or empty input unchanged, then performs an
    /// ordinal case-insensitive marker precheck against <see cref="AppSurfaceDocsDiagnosticsChromeAttributeName"/> before
    /// applying a global <see cref="Regex.Replace(string, string)"/> with
    /// <see cref="AppSurfaceDocsDiagnosticsChromeRegex"/>. All matching diagnostics disclosures are removed.
    /// </para>
    /// <para>
    /// The method operates on raw HTML rather than a parsed DOM, so marker-like text in unexpected locations can be
    /// removed. The static compiled regex is thread-safe for concurrent exports, but it intentionally trades parser-level
    /// correctness for lightweight export-time cleanup and inherits the regex constraints documented on
    /// <see cref="AppSurfaceDocsDiagnosticsChromeRegex"/>.
    /// </para>
    /// </remarks>
    /// <param name="html">The fetched or staged HTML document.</param>
    /// <returns>The HTML with marked AppSurface Docs diagnostics chrome removed.</returns>
    internal static string StripAppSurfaceDocsDiagnosticsChrome(string html)
    {
        if (string.IsNullOrEmpty(html)
            || !html.Contains(AppSurfaceDocsDiagnosticsChromeAttributeName, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        return AppSurfaceDocsDiagnosticsChromeRegex.Replace(html, string.Empty);
    }

    private async Task WriteCssRouteAsync(
        string route,
        string filePath,
        string body,
        ExportContext context,
        bool rewriteManagedReferences,
        CancellationToken cancellationToken)
    {
        var cssForWrite = rewriteManagedReferences
            ? RewriteManagedReferences(body, route, htmlScope: false, context)
            : body;
        EnsureDirectoryExists(filePath);
        await File.WriteAllTextAsync(filePath, cssForWrite, cancellationToken);
    }

    private void ValidateExport(ExportContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ValidateRedirectArtifacts(context, seen);
        if (context.Mode == ExportMode.Hybrid)
        {
            ValidateRequiredStaticAssets(context, seen);
        }

        if (context.Mode != ExportMode.Cdn)
        {
            if (context.Diagnostics.Count > 0)
            {
                throw new ExportValidationException(context.Diagnostics);
            }

            return;
        }

        foreach (var outcome in context.RouteOutcomes.Values.Where(o => !o.Succeeded))
        {
            var references = context.References.Where(r => string.Equals(r.Path, outcome.Route, StringComparison.Ordinal)).ToList();
            if (references.Count == 0)
            {
                AddDiagnostic(
                    context,
                    seen,
                    new ExportDiagnostic(
                        "RWEXPORT004",
                        $"Route '{outcome.Route}' could not be materialized for CDN output.",
                        outcome.Route));
                continue;
            }

            foreach (var reference in references)
            {
                AddMissingReferenceDiagnostic(context, seen, reference);
            }
        }

        foreach (var reference in context.References)
        {
            if (reference.Kind == ExportReferenceKind.TurboFrameSrc && !string.IsNullOrEmpty(reference.Query))
            {
                AddDiagnostic(
                    context,
                    seen,
                    new ExportDiagnostic(
                        "RWEXPORT002",
                        $"Turbo Frame source '{reference.RawValue}' from '{reference.SourceRoute}' includes a query string and cannot be represented as one static CDN artifact.",
                        reference.SourceRoute,
                        reference));
            }

            if (!TryResolveReferenceArtifactUrl(reference, context, out _))
            {
                AddMissingReferenceDiagnostic(context, seen, reference);
            }
        }

        if (context.Diagnostics.Count > 0)
        {
            throw new ExportValidationException(context.Diagnostics);
        }
    }

    private void ValidateRequiredStaticAssets(ExportContext context, ISet<string> seen)
    {
        foreach (var reference in context.References.Where(reference => reference.RequiresStaticMaterialization(context.Mode)))
        {
            if (!TryResolveReferenceArtifactUrl(reference, context, out _))
            {
                AddMissingReferenceDiagnostic(context, seen, reference);
            }
        }
    }

    private void ValidateRedirectArtifacts(ExportContext context, ISet<string> seen)
    {
        var aliasArtifactPathByCanonicalRoute = new Dictionary<string, string>(CreateArtifactPathComparer());
        var canonicalRouteByAliasRoute = new Dictionary<string, string>(StringComparer.Ordinal);
        var routeByArtifactPath = BuildProtectedArtifactPathMap(context);

        if (context.RedirectStrategy == ExportRedirectStrategy.Netlify)
        {
            ValidateNetlifyRedirectStrategy(context, routeByArtifactPath, seen);
        }

        foreach (var artifact in context.RedirectArtifacts)
        {
            if (canonicalRouteByAliasRoute.TryGetValue(artifact.AliasRoute, out var existingCanonicalRoute)
                && !string.Equals(existingCanonicalRoute, artifact.CanonicalRoute, StringComparison.Ordinal))
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        $"AppSurface Docs redirect alias '{artifact.AliasRoute}' targets both '{existingCanonicalRoute}' and '{artifact.CanonicalRoute}'."));
            }
            else
            {
                canonicalRouteByAliasRoute[artifact.AliasRoute] = artifact.CanonicalRoute;
            }

            if (!context.ArtifactUrls.TryGetValue(artifact.CanonicalRoute, out _)
                || !context.RouteOutcomes.TryGetValue(artifact.CanonicalRoute, out var canonicalOutcome)
                || !canonicalOutcome.Succeeded
                || canonicalOutcome.ArtifactPath is null)
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        $"AppSurface Docs redirect alias '{artifact.AliasRoute}' could not resolve canonical artifact '{artifact.CanonicalRoute}'."));
                continue;
            }

            if (context.RedirectStrategy != ExportRedirectStrategy.Html)
            {
                if (context.RouteOutcomes.TryGetValue(artifact.AliasRoute, out var nonHtmlStrategyAliasOutcome)
                    && nonHtmlStrategyAliasOutcome.Succeeded
                    && !nonHtmlStrategyAliasOutcome.IsRedirectAliasArtifact)
                {
                    AddDiagnostic(
                        context,
                        seen,
                        CreateRedirectArtifactDiagnostic(
                            artifact.AliasRoute,
                            $"AppSurface Docs redirect alias '{artifact.AliasRoute}' conflicts with an exported route at the same published path."));
                }

                continue;
            }

            var aliasArtifactPath = MapRouteToFilePath(artifact.AliasRoute, context.OutputPath, isHtml: true);
            if (routeByArtifactPath.TryGetValue(aliasArtifactPath, out var existingRoute)
                && !string.Equals(existingRoute, artifact.AliasRoute, StringComparison.Ordinal)
                && !string.Equals(existingRoute, artifact.CanonicalRoute, StringComparison.Ordinal))
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        $"AppSurface Docs redirect alias '{artifact.AliasRoute}' would overwrite the artifact for exported route '{existingRoute}'."));
            }

            if (ArtifactPathsEqual(aliasArtifactPath, canonicalOutcome.ArtifactPath))
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        $"AppSurface Docs redirect alias '{artifact.AliasRoute}' maps to the same artifact as canonical route '{artifact.CanonicalRoute}'."));
            }

            if (aliasArtifactPathByCanonicalRoute.TryGetValue(aliasArtifactPath, out var existingArtifactCanonicalRoute)
                && !string.Equals(existingArtifactCanonicalRoute, artifact.CanonicalRoute, StringComparison.Ordinal))
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        $"AppSurface Docs redirect alias '{artifact.AliasRoute}' maps to the same artifact as alias for '{existingArtifactCanonicalRoute}'."));
            }
            else
            {
                aliasArtifactPathByCanonicalRoute[aliasArtifactPath] = artifact.CanonicalRoute;
            }

            if (context.RouteOutcomes.TryGetValue(artifact.AliasRoute, out var aliasOutcome)
                && aliasOutcome.Succeeded
                && aliasOutcome.IsHtml
                && !aliasOutcome.IsRedirectAliasArtifact)
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        $"AppSurface Docs redirect alias '{artifact.AliasRoute}' was crawled as a normal HTML page body."));
            }
        }
    }

    private static void ValidateNetlifyRedirectStrategy(
        ExportContext context,
        IReadOnlyDictionary<string, string> routeByArtifactPath,
        ISet<string> seen)
    {
        if (context.Mode != ExportMode.Cdn)
        {
            AddDiagnostic(
                context,
                seen,
                CreateRedirectArtifactDiagnostic(
                    "/_redirects",
                    "Netlify redirect rules require CDN export mode because they point at publish-root static routes."));
        }

        var redirectsPath = Path.Join(context.OutputPath, "_redirects");
        if (routeByArtifactPath.TryGetValue(redirectsPath, out var existingRoute))
        {
            AddDiagnostic(
                context,
                seen,
                CreateRedirectArtifactDiagnostic(
                    "/_redirects",
                    $"Netlify redirect output would overwrite the artifact for exported route '{existingRoute}'."));
        }

        foreach (var artifact in context.RedirectArtifacts.Where(artifact => string.Equals(artifact.AliasRoute, "/_redirects", StringComparison.Ordinal)))
        {
            AddDiagnostic(
                context,
                seen,
                CreateRedirectArtifactDiagnostic(
                    artifact.AliasRoute,
                    "Netlify redirect output reserves the root '_redirects' file and cannot use it as an alias route."));
        }

        var targetBySerializedAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in context.RedirectArtifacts)
        {
            var aliasSerialized = TrySerializeNetlifyRedirectPath(artifact.AliasRoute, out var serializedAlias, out var aliasSerializationError);
            if (!aliasSerialized)
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        aliasSerializationError ?? $"Netlify redirect route '{artifact.AliasRoute}' cannot be represented in _redirects output."));
            }

            var canonicalSerialized = TrySerializeNetlifyRedirectPath(artifact.CanonicalRoute, out var serializedCanonical, out var canonicalSerializationError);
            if (!canonicalSerialized)
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        $"Netlify redirect canonical route '{artifact.CanonicalRoute}' cannot be represented in _redirects output. {canonicalSerializationError}"));
            }

            if (!aliasSerialized || !canonicalSerialized)
            {
                continue;
            }

            var serializedAliasValue = serializedAlias!;
            var serializedCanonicalValue = serializedCanonical!;

            if (string.Equals(serializedAliasValue, serializedCanonicalValue, StringComparison.Ordinal))
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        $"Netlify redirect alias '{artifact.AliasRoute}' serializes to the same path as canonical route '{artifact.CanonicalRoute}'."));
            }

            if (targetBySerializedAlias.TryGetValue(serializedAliasValue, out var existingSerializedCanonical)
                && !string.Equals(existingSerializedCanonical, serializedCanonicalValue, StringComparison.Ordinal))
            {
                AddDiagnostic(
                    context,
                    seen,
                    CreateRedirectArtifactDiagnostic(
                        artifact.AliasRoute,
                        $"Netlify redirect alias '{artifact.AliasRoute}' serializes to '{serializedAliasValue}', which already targets '{existingSerializedCanonical}'."));
            }
            else
            {
                targetBySerializedAlias[serializedAliasValue] = serializedCanonicalValue;
            }
        }
    }

    private static Dictionary<string, string> BuildProtectedArtifactPathMap(ExportContext context)
    {
        var routeByArtifactPath = new Dictionary<string, string>(CreateArtifactPathComparer());
        foreach (var outcome in context.RouteOutcomes.Values.Where(outcome => outcome.Succeeded && outcome.ArtifactPath is not null))
        {
            routeByArtifactPath.TryAdd(outcome.ArtifactPath!, outcome.Route);

            if (outcome.IsHtml && context.PartialArtifactUrls.ContainsKey(outcome.Route))
            {
                routeByArtifactPath.TryAdd(
                    MapHtmlFilePathToPartialPath(outcome.ArtifactPath!),
                    $"{outcome.Route} partial");
            }
        }

        return routeByArtifactPath;
    }

    private static void AddMissingReferenceDiagnostic(
        ExportContext context,
        ISet<string> seen,
        ExportReference reference)
    {
        var code = reference.Kind == ExportReferenceKind.TurboFrameSrc
            ? "RWEXPORT001"
            : reference.IsAsset
                ? "RWEXPORT003"
                : "RWEXPORT004";
        var description = code switch
        {
            "RWEXPORT001" => "server-fetched frame route",
            "RWEXPORT003" => "required internal asset",
            _ => "managed internal URL"
        };
        var provenance = reference.Provenance;
        var sourceDescription = provenance is null
            ? $"from '{reference.SourceRoute}'"
            : $"from {provenance.DisplaySource} on '{reference.SourceRoute}'";
        var linkDescription = reference.LinkMetadata is null
            ? string.Empty
            : $" ({reference.LinkMetadata.Display})";
        var positionDescription = provenance?.Line is null
            ? string.Empty
            : $" near line {provenance.Line.Value}";
        var validationTarget = context.Mode == ExportMode.Hybrid
            ? "during hybrid export"
            : "to an emitted static export artifact";
        var fix = context.Mode == ExportMode.Hybrid && code == "RWEXPORT003"
            ? "Hybrid export can leave page routes, frames, forms, streams, and islands to live infrastructure, but browser-delivered assets requested by HTML or CSS must be emitted by the export or made external/data/hash-only. Add or copy the asset, correct path casing, make the URL external/data/hash-only, or remove the browser dependency."
            : "Add the route or asset to the export, make the reference external/data/hash-only, or use hybrid mode when a live server owns it.";

        AddDiagnostic(
            context,
            seen,
            new ExportDiagnostic(
                code,
                $"The {description} '{reference.RawValue}' {sourceDescription}{linkDescription}{positionDescription} did not resolve {validationTarget}. Normalized path: '{reference.Path}'. {fix}",
                reference.SourceRoute,
            reference));
    }

    private static ExportDiagnostic CreateRedirectArtifactDiagnostic(string route, string problem)
    {
        return new ExportDiagnostic(
            "RWEXPORT005",
            problem + " Review redirect artifact registration so aliases map to exported canonical routes without colliding with existing artifacts.",
            route);
    }

    private static void AddDiagnostic(ExportContext context, ISet<string> seen, ExportDiagnostic diagnostic)
    {
        var key = $"{diagnostic.Code}|{diagnostic.Route}|{diagnostic.Message}|{diagnostic.Reference?.Kind}|{diagnostic.Reference?.RawValue}";
        if (seen.Add(key))
        {
            context.Diagnostics.Add(diagnostic);
        }
    }

    private static string BuildRedirectArtifactBody(string canonicalArtifactUrl)
    {
        var encodedUrl = WebUtility.HtmlEncode(canonicalArtifactUrl);
        return $"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="appsurface-docs-redirect-alias" content="1">
              <meta name="appsurface-docs-canonical-artifact" content="{encodedUrl}">
              <link rel="canonical" href="{encodedUrl}">
              <meta http-equiv="refresh" content="0; url={encodedUrl}">
              <title>Redirecting...</title>
            </head>
            <body>
              <a href="{encodedUrl}">Continue to the canonical documentation page.</a>
            </body>
            </html>
            """;
    }

    private static string SerializeNetlifyRedirectPath(string route)
    {
        if (!TrySerializeNetlifyRedirectPath(route, out var serialized, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return serialized;
    }

    private static bool TrySerializeNetlifyRedirectPath(
        string route,
        [NotNullWhen(true)] out string? serialized,
        [NotNullWhen(false)] out string? error)
    {
        serialized = null;
        error = null;

        if (!route.StartsWith("/", StringComparison.Ordinal)
            || route.StartsWith("//", StringComparison.Ordinal)
            || route.Contains('?')
            || route.Contains('#')
            || route.Contains('\r')
            || route.Contains('\n')
            || route.Contains('\t'))
        {
            error = $"Netlify redirect route '{route}' cannot be represented in _redirects output.";
            return false;
        }

        var segments = route.Split('/');
        var builder = new StringBuilder(route.Length);
        try
        {
            for (var i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append('/');
                }

                if (segments[i].Length == 0)
                {
                    continue;
                }

                builder.Append(Uri.EscapeDataString(Uri.UnescapeDataString(segments[i])));
            }
        }
        catch (UriFormatException ex)
        {
            error = $"Netlify redirect route '{route}' cannot be represented in _redirects output: {ex.Message}";
            return false;
        }

        serialized = builder.ToString();
        return true;
    }

    private sealed record NetlifyRedirectRule(string From, string To);

    private async Task TryStageConventionalNotFoundPageAsync(
        HttpClient client,
        ExportContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendArtifactRequestAsync(
                client,
                context,
                "/404.html",
                BrowserStatusPageDefaults.ReservedNotFoundRoute,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Skipping 404.html export because {Route} returned {StatusCode}.",
                    BrowserStatusPageDefaults.ReservedNotFoundRoute,
                    response.StatusCode);
                return;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Skipping 404.html export because {Route} returned non-HTML content type {ContentType}.",
                    BrowserStatusPageDefaults.ReservedNotFoundRoute,
                    contentType ?? "(none)");
                return;
            }

            const string route = "/404.html";
            var html = StripAppSurfaceDocsDiagnosticsChrome(await response.Content.ReadAsStringAsync(cancellationToken));
            html = ApplyPublicOriginRewrites(html, route, context);
            html = ApplyHybridRuntimeRewrites(html, route, context);
            var filePath = MapRouteToFilePath(route, context.OutputPath, isHtml: true);
            var artifactUrl = MapFilePathToArtifactUrl(filePath, context.OutputPath, route);
            context.ArtifactUrls[route] = artifactUrl;
            context.RouteOutcomes[route] = context.Mode == ExportMode.Cdn
                ? ExportRouteOutcome.Success(route, contentType, filePath, artifactUrl, html)
                : ExportRouteOutcome.Success(route, contentType, filePath, artifactUrl);
            context.Visited.Add(route);
            AddReferencesAndQueue(ExtractReferences(html, route, htmlScope: true), context);
            if (context.Mode == ExportMode.Hybrid)
            {
                await WriteHtmlRouteAsync(route, filePath, html, context, rewriteManagedReferences: false, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExportValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to export conventional 404 page from {Route}.",
                BrowserStatusPageDefaults.ReservedNotFoundRoute);
        }
    }

    internal static bool IsDocsRoute(string route)
    {
        return route.Equals("/docs", StringComparison.OrdinalIgnoreCase)
               || route.StartsWith("/docs/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether an exported HTML page should receive AppSurfaceDocs static partial support.
    /// </summary>
    /// <param name="route">The root-relative route being exported.</param>
    /// <param name="html">The fetched HTML document.</param>
    /// <param name="docContentFrame">The extracted <c>doc-content</c> frame, when the caller has already parsed it.</param>
    /// <returns>
    /// <c>true</c> for the legacy <c>/docs</c> route family or HTML that carries AppSurfaceDocs runtime markers; otherwise
    /// <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Custom AppSurfaceDocs hosts can mount under route families such as <c>/foo/bar</c>, so export detection cannot rely
    /// only on path prefixes. The client config marker covers search and shell pages, while the content frame covers
    /// document detail pages.
    /// </remarks>
    internal static bool IsDocsExportPage(string route, string html, string? docContentFrame = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(html);

        return IsDocsRoute(route)
               || !string.IsNullOrWhiteSpace(docContentFrame)
               || html.Contains(AppSurfaceDocsClientConfigMarker, StringComparison.Ordinal);
    }

    internal static string MapHtmlFilePathToPartialPath(string htmlFilePath)
    {
        if (htmlFilePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            return htmlFilePath[..^5] + ".partial.html";
        }

        return htmlFilePath + ".partial.html";
    }

    internal static string? ExtractDocContentFrame(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        foreach (Match openTag in TurboFrameOpenTagRegex.Matches(html))
        {
            if (!DocContentFrameIdRegex.IsMatch(openTag.Value))
            {
                continue;
            }

            var frameEnd = FindMatchingTurboFrameEnd(html, openTag.Index + openTag.Length);
            if (frameEnd < 0)
            {
                return null;
            }

            return html[openTag.Index..frameEnd];
        }

        return null;
    }

    internal static string AddDocsStaticPartialsMarker(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        if (html.Contains($"name=\"{DocsStaticPartialsMetaName}\"", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var markerWithLeadingNewline = Environment.NewLine + DocsStaticPartialsMetaTag;
        var headCloseIndex = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headCloseIndex >= 0)
        {
            return html.Insert(headCloseIndex, markerWithLeadingNewline);
        }

        return markerWithLeadingNewline + html;
    }

    private static int FindMatchingTurboFrameEnd(string html, int scanStart)
    {
        var depth = 1;
        var cursor = scanStart;

        while (cursor < html.Length)
        {
            var nextOpen = html.IndexOf("<turbo-frame", cursor, StringComparison.OrdinalIgnoreCase);
            var nextClose = html.IndexOf("</turbo-frame", cursor, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0)
            {
                return -1;
            }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                // Raw string scanning assumes no quoted attribute contains a standalone '>' or '/>' token.
                // If turbo-frame markup evolves to include those cases, switch this matcher to an HTML parser.
                var openTagEnd = html.IndexOf('>', nextOpen);
                if (openTagEnd < 0)
                {
                    return -1;
                }

                var tagCursor = openTagEnd - 1;
                while (tagCursor > nextOpen && char.IsWhiteSpace(html[tagCursor]))
                {
                    tagCursor--;
                }

                var isSelfClosing = html[tagCursor] == '/';
                if (!isSelfClosing)
                {
                    depth++;
                }

                cursor = openTagEnd + 1;
                continue;
            }

            var closeTagEnd = html.IndexOf('>', nextClose);
            if (closeTagEnd < 0)
            {
                return -1;
            }

            depth--;
            cursor = closeTagEnd + 1;

            if (depth == 0)
            {
                return cursor;
            }
        }

        return -1;
    }

    private async Task TryWriteDocsPartialAsync(
        string htmlFilePath,
        string? docContentFrame,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(docContentFrame))
        {
            return;
        }

        var partialPath = MapHtmlFilePathToPartialPath(htmlFilePath);
        await File.WriteAllTextAsync(partialPath, docContentFrame, cancellationToken);
    }

    /// <summary>
    /// Maps a root-relative route to an absolute file path inside the configured output directory.
    /// </summary>
    private string MapRouteToFilePath(string route, string outputPath, bool isHtml)
    {
        var normalized = route;

        // Check if ends with /index or /index.html (case-insensitive)
        if (normalized.EndsWith("/index", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)
            || normalized == "/")
        {
            // Make sure it goes to /index.html logic below
            if (normalized == "/")
            {
                normalized = "/index";
            }
            else if (normalized.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^5]; // strip .html for consistent handling
            }
        }

        var relativePath = normalized.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        var fileName = Path.GetFileName(relativePath);
        var isExplicitHtmlFile = relativePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(fileName)) // Ends in slash -> index.html
        {
            relativePath = Path.Combine(relativePath, "index.html");
        }
        else if (isHtml && !isExplicitHtmlFile)
        {
            relativePath += ".html";
        }

        var fullPath = Path.GetFullPath(Path.Combine(outputPath, relativePath));
        var fullOutputPath = Path.GetFullPath(outputPath);

        // Normalize both paths to ensure consistent comparison
        var normalizedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedOutput = fullOutputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Filesystem case-sensitivity varies by OS. Linux/macOS are typically case-sensitive (Ordinal).
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Ensure the resolved file path is strictly within the output directory
        if (!normalizedFull.Equals(normalizedOutput, comparison)
            && !normalizedFull.StartsWith(
                normalizedOutput + Path.DirectorySeparatorChar,
                comparison))
        {
            throw new InvalidOperationException($"Invalid route path traversal detected: {route}");
        }

        return fullPath;
    }

    private static bool ArtifactPathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizeArtifactPath(left),
            NormalizeArtifactPath(right),
            GetArtifactPathComparison());
    }

    private static IEqualityComparer<string> CreateArtifactPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static StringComparison GetArtifactPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static string NormalizeArtifactPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string MapFilePathToArtifactUrl(string filePath, string outputPath, string route)
    {
        if (route == "/")
        {
            return "/";
        }

        var relativePath = Path.GetRelativePath(Path.GetFullPath(outputPath), filePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return "/" + relativePath;
    }

    private static string MapHtmlArtifactUrlToPartialUrl(string artifactUrl)
    {
        return artifactUrl.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? artifactUrl[..^5] + ".partial.html"
            : artifactUrl + ".partial.html";
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        var dirPath = Path.GetDirectoryName(filePath);
        if (dirPath != null && !Directory.Exists(dirPath))
        {
            Directory.CreateDirectory(dirPath);
        }
    }

    /// <summary>
    /// Extracts root-relative internal link targets from the provided HTML and enqueues any unvisited routes for crawling.
    /// </summary>
    /// <param name="html">HTML source to scan.</param>
    /// <param name="context">The export context.</param>
    /// <param name="currentRoute">The route used to resolve relative anchor URLs and record source provenance.</param>
    internal void ExtractLinks(string html, ExportContext context, string currentRoute = "/")
    {
        AddReferencesAndQueue(
            ExtractReferences(html, currentRoute, htmlScope: true)
                .Where(reference => reference.Kind == ExportReferenceKind.AnchorHref),
            context);
    }

    /// <summary>
    /// Extracts root-relative `src` values from &lt;turbo-frame&gt; elements in the provided HTML and enqueues each unvisited path for export.
    /// </summary>
    /// <param name="html">HTML content to scan.</param>
    /// <param name="context">The export context.</param>
    /// <param name="currentRoute">The route used to resolve relative frame source URLs and record source provenance.</param>
    private void ExtractFrames(string html, ExportContext context, string currentRoute = "/")
    {
        AddReferencesAndQueue(
            ExtractReferences(html, currentRoute, htmlScope: true)
                .Where(reference => reference.Kind == ExportReferenceKind.TurboFrameSrc),
            context);
    }

    /// <summary>
    /// Extracts root-relative asset references (scripts, styles, images) from the provided HTML and enqueues each unvisited path for export.
    /// </summary>
    /// <param name="html">HTML content to scan.</param>
    /// <param name="currentRoute">The route of the page being scanned, used for resolving relative URLs.</param>
    /// <param name="context">The export context.</param>
    internal void ExtractAssets(string html, string currentRoute, ExportContext context)
    {
        AddReferencesAndQueue(
            ExtractReferences(html, currentRoute, htmlScope: true)
                .Where(r => r.IsAsset),
            context);
    }

    /// <summary>
    /// Extracts exporter-managed internal references from HTML or CSS content.
    /// </summary>
    /// <param name="content">The HTML document, style block, style attribute, or stylesheet body to scan.</param>
    /// <param name="currentRoute">The normalized route that owns <paramref name="content"/>, used to resolve relative URLs and record provenance.</param>
    /// <param name="htmlScope">
    /// <c>true</c> scans HTML surfaces including anchors, Turbo Frames, scripts, supported link tags, image sources, <c>srcset</c>
    /// candidates, style blocks, and style attributes. Anchors marked with <c>data-rw-export-ignore</c>, and relative anchors
    /// pointing at common source or project file extensions, are skipped so authoring-only source-navigation links can remain
    /// clickable without becoming CDN dependencies. <c>false</c> scans only CSS <c>url(...)</c> references.
    /// </param>
    /// <returns>
    /// References with managed root-relative paths only. External URLs, protocol-relative URLs, hash-only references, data URLs,
    /// JavaScript URLs, mailto links, and malformed values are filtered out before the caller enqueues or validates them.
    /// </returns>
    internal IReadOnlyList<ExportReference> ExtractReferences(string content, string currentRoute, bool htmlScope)
    {
        return _referenceProcessor.ExtractReferences(content, currentRoute, htmlScope);
    }

    private static void AddReferencesAndQueue(IEnumerable<ExportReference> references, ExportContext context)
    {
        foreach (var reference in references)
        {
            context.References.Add(reference);
            if (!context.Visited.Contains(reference.Path)
                && !context.RouteOutcomes.ContainsKey(reference.Path))
            {
                EnqueueRoute(context, reference.Path);
            }
        }
    }

    private static void EnqueueRoute(ExportContext context, string route)
    {
        if (context.Enqueued.Add(route))
        {
            context.Queue.Enqueue(route);
        }
    }

    private string RewriteManagedReferences(string content, string currentRoute, bool htmlScope, ExportContext context)
    {
        return _referenceProcessor.RewriteManagedReferences(
            content,
            currentRoute,
            htmlScope,
            reference => TryResolveReferenceArtifactUrl(reference, context, out var artifactUrl) ? artifactUrl : null);
    }

    private static bool TryResolveReferenceArtifactUrl(
        ExportReference reference,
        ExportContext context,
        out string artifactUrl)
    {
        artifactUrl = string.Empty;

        if (reference.Kind == ExportReferenceKind.TurboFrameSrc
            && context.PartialArtifactUrls.TryGetValue(reference.Path, out var partialArtifactUrl))
        {
            artifactUrl = AppendQueryAndFragment(partialArtifactUrl, reference.Query, reference.Fragment);
            return true;
        }

        if (!context.ArtifactUrls.TryGetValue(reference.Path, out var routeArtifactUrl))
        {
            return false;
        }

        var outcomeIsUsable = context.RouteOutcomes.TryGetValue(reference.Path, out var outcome) && outcome.Succeeded;
        if (!outcomeIsUsable)
        {
            return false;
        }

        artifactUrl = AppendQueryAndFragment(routeArtifactUrl, reference.Query, reference.Fragment);
        return true;
    }

    private static string AppendQueryAndFragment(string artifactUrl, string query, string fragment)
    {
        return artifactUrl + query + fragment;
    }

    /// <summary>
    /// Resolves a potentially relative URL against a base route.
    /// </summary>
    internal string ResolveRelativeUrl(string baseRoute, string url)
    {
        return _referenceProcessor.ResolveRelativeUrl(baseRoute, url);
    }

    internal bool TryGetNormalizedRoute(string rawRef, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(rawRef))
        {
            return false;
        }

        // Must start with /, not //
        if (!rawRef.StartsWith('/') || rawRef.StartsWith("//"))
        {
            // Log external/relative skips at debug level per user request
            if (!rawRef.StartsWith('#')
                && !rawRef.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                && !rawRef.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping external/relative URL: {Url}", rawRef);
            }

            return false;
        }

        if (ExportReferenceProcessor.TrySplitManagedUrl(rawRef, out var path, out _, out _))
        {
            normalized = path;
            return true;
        }

        return false;
    }
}
