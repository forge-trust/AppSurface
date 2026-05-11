using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Web;
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
    private static readonly Uri ManagedUrlBase = new("http://dummy");

    // Compiled Regexes for performance
    private static readonly Regex AnchorHrefRegex = new(
        @"<a[^>]*\shref\s*=\s*(['""])(.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TurboFrameSrcRegex = new(
        @"<turbo-frame[^>]*\ssrc\s*=\s*(['""])(.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScriptSrcRegex = new(
        @"<script[^>]*\ssrc\s*=\s*(['""])(.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LinkTagRegex = new(
        "<link[^>]+>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LinkHrefRegex = new(
        @"\shref\s*=\s*(['""])(.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LinkRelRegex = new(
        @"\srel\s*=\s*(['""])(.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImgSrcRegex = new(
        @"<img[^>]*\ssrc\s*=\s*(['""])(.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SrcSetRegex = new(
        @"<[^>]*\ssrcset\s*=\s*(['""])(.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StyleBlockRegex = new(
        "<style[^>]*>(.*?)</style>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex StyleAttrRegex = new(
        @"\sstyle\s*=\s*(['""])(.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssUrlRegex = new(
        @"url\(\s*(['""]?)(.*?)\1\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string DocsStaticPartialsMetaName = "rw-docs-static-partials";
    private const string DocsStaticPartialsMetaTag = "<meta name=\"rw-docs-static-partials\" content=\"1\" />";
    private const string RazorDocsClientConfigMarker = "window.__razorDocsConfig";

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
    }

    /// <summary>
    /// Crawls the site starting from configured seed routes (or the root), stages the conventional reserved 404 page when available, validates CDN output when requested, and exports discovered pages, frame sources, and assets to the output path.
    /// </summary>
    /// <param name="context">Export configuration and runtime state including base URL, output path, queue, and visited set.</param>
    /// <param name="cancellationToken">Token to observe for cooperative cancellation of the crawl and export operations.</param>
    /// <remarks>
    /// If <see cref="ExportContext.SeedRoutesPath"/> is provided, the file is read and each line is validated and normalized to a root-relative route; invalid seeds are logged. If the seed file exists but yields no valid routes, the root path ("/") is enqueued. If no seed file is provided, the root path is enqueued. Before crawl processing begins, the engine probes AppSurface's reserved conventional 404 route and stages <c>404.html</c> when the route returns a successful HTML response. That reserved-route probe is best-effort only: failures are logged, do not abort the crawl, and do not prevent queued seed routes from being processed. Once staged, the <c>404.html</c> body participates in the same CDN validation and reference rewriting as other HTML artifacts.
    ///
    /// Export then runs as a three-stage pipeline:
    ///
    /// <code>
    /// seed queue -> crawl/fetch/discover -> CDN validation -> materialize/rewrite
    /// </code>
    ///
    /// The crawl stage records route outcomes, artifact URLs, and reference provenance. In CDN mode, HTML and CSS bodies
    /// are kept once until materialization so managed URLs can be rewritten after the artifact map is complete. In hybrid
    /// mode, text artifacts and binary assets are written directly to their final files and only their outcomes are
    /// retained in memory.
    /// </remarks>
    /// <returns>A task that completes when the crawl and export operations have finished.</returns>
    /// <exception cref="FileNotFoundException">Thrown when <see cref="ExportContext.SeedRoutesPath"/> is specified but the file does not exist.</exception>
    public async Task RunAsync(ExportContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "RunAsync started. BaseUrl: {BaseUrl}, OutputPath: {OutputPath}",
            context.BaseUrl,
            context.OutputPath);

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

        ValidateCdnExport(context);
        await MaterializeTextRoutesAsync(context, cancellationToken);

        sw.Stop();
        _logger.LogInformation("Export completed in {ElapsedMilliseconds}ms", sw.ElapsedMilliseconds);
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
            using var response = await client.GetAsync(
                $"{context.BaseUrl}{route}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

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
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting {Route}", route);
            context.RouteOutcomes[route] = ExportRouteOutcome.Failed(route, ex);
        }
    }

    private async Task QueueSeedRoutesAsync(ExportContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(context.SeedRoutesPath))
        {
            if (!File.Exists(context.SeedRoutesPath))
            {
                _logger.LogError("Seed routes file not found: {SeedRoutesPath}", context.SeedRoutesPath);

                throw new FileNotFoundException(
                    "The specified seed routes file does not exist.",
                    context.SeedRoutesPath);
            }

            var seeds = await File.ReadAllLinesAsync(context.SeedRoutesPath, cancellationToken);

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

            if (context.Queue.Count == 0)
            {
                _logger.LogWarning(
                    "Seed file provided but no valid routes were found. Falling back to default root path.");
                EnqueueRoute(context, "/");
            }

            return;
        }

        EnqueueRoute(context, "/");
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

        if (string.Equals(seedPath, basePath, StringComparison.Ordinal))
        {
            route = "/" + seedUri.Query + seedUri.Fragment;
            return true;
        }

        if (seedPath.StartsWith(basePath + "/", StringComparison.Ordinal))
        {
            route = seedPath[basePath.Length..] + seedUri.Query + seedUri.Fragment;
            return true;
        }

        return false;
    }

    private async Task WriteHtmlRouteAsync(
        string route,
        string filePath,
        string body,
        ExportContext context,
        bool rewriteManagedReferences,
        CancellationToken cancellationToken)
    {
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

    private void ValidateCdnExport(ExportContext context)
    {
        if (context.Mode != ExportMode.Cdn)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

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

        AddDiagnostic(
            context,
            seen,
            new ExportDiagnostic(
                code,
                $"The {description} '{reference.RawValue}' from '{reference.SourceRoute}' did not map to an emitted CDN artifact.",
                reference.SourceRoute,
                reference));
    }

    private static void AddDiagnostic(ExportContext context, ISet<string> seen, ExportDiagnostic diagnostic)
    {
        var key = $"{diagnostic.Code}|{diagnostic.Route}|{diagnostic.Reference?.Kind}|{diagnostic.Reference?.RawValue}";
        if (seen.Add(key))
        {
            context.Diagnostics.Add(diagnostic);
        }
    }

    private async Task TryStageConventionalNotFoundPageAsync(
        HttpClient client,
        ExportContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                $"{context.BaseUrl}{BrowserStatusPageDefaults.ReservedNotFoundRoute}",
                HttpCompletionOption.ResponseHeadersRead,
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
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
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
    /// Determines whether an exported HTML page should receive RazorDocs static partial support.
    /// </summary>
    /// <param name="route">The root-relative route being exported.</param>
    /// <param name="html">The fetched HTML document.</param>
    /// <param name="docContentFrame">The extracted <c>doc-content</c> frame, when the caller has already parsed it.</param>
    /// <returns>
    /// <c>true</c> for the legacy <c>/docs</c> route family or HTML that carries RazorDocs runtime markers; otherwise
    /// <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Custom RazorDocs hosts can mount under route families such as <c>/foo/bar</c>, so export detection cannot rely
    /// only on path prefixes. The client config marker covers search and shell pages, while the content frame covers
    /// document detail pages.
    /// </remarks>
    internal static bool IsDocsExportPage(string route, string html, string? docContentFrame = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(html);

        return IsDocsRoute(route)
               || !string.IsNullOrWhiteSpace(docContentFrame)
               || html.Contains(RazorDocsClientConfigMarker, StringComparison.Ordinal);
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

        // Detect extension on the last segment
        var fileName = Path.GetFileName(relativePath);
        var hasExtension = Path.HasExtension(fileName);

        // Only append .html if:
        // 1. It is explicitly an HTML content type
        // 2. AND it doesn't already have an extension (or ends in slash which is handled by fileName check)
        // 3. OR it's a directory-style index request (empty filename)

        if (string.IsNullOrEmpty(fileName)) // Ends in slash -> index.html
        {
            relativePath = Path.Combine(relativePath, "index.html");
        }
        else if (!hasExtension && isHtml)
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
        var references = AnchorHrefRegex.Matches(html)
            .Select(m => CreateReference(m.Groups[2].Value.Trim(), ExportReferenceKind.AnchorHref, currentRoute))
            .Where(r => r is not null)
            .Select(r => r!);

        AddReferencesAndQueue(references, context);
    }

    /// <summary>
    /// Extracts root-relative `src` values from &lt;turbo-frame&gt; elements in the provided HTML and enqueues each unvisited path for export.
    /// </summary>
    /// <param name="html">HTML content to scan.</param>
    /// <param name="context">The export context.</param>
    /// <param name="currentRoute">The route used to resolve relative frame source URLs and record source provenance.</param>
    private void ExtractFrames(string html, ExportContext context, string currentRoute = "/")
    {
        var references = TurboFrameSrcRegex.Matches(html)
            .Select(m => CreateReference(m.Groups[2].Value.Trim(), ExportReferenceKind.TurboFrameSrc, currentRoute))
            .Where(r => r is not null)
            .Select(r => r!);

        AddReferencesAndQueue(references, context);
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
    /// candidates, style blocks, and style attributes. <c>false</c> scans only CSS <c>url(...)</c> references.
    /// </param>
    /// <returns>
    /// References with managed root-relative paths only. External URLs, protocol-relative URLs, hash-only references, data URLs,
    /// JavaScript URLs, mailto links, and malformed values are filtered out before the caller enqueues or validates them.
    /// </returns>
    internal IReadOnlyList<ExportReference> ExtractReferences(string content, string currentRoute, bool htmlScope)
    {
        var references = new List<ExportReference>();

        if (htmlScope)
        {
            AddReferencesFromMatches(references, AnchorHrefRegex.Matches(content), ExportReferenceKind.AnchorHref, currentRoute);
            AddReferencesFromMatches(references, TurboFrameSrcRegex.Matches(content), ExportReferenceKind.TurboFrameSrc, currentRoute);
            AddReferencesFromMatches(references, ScriptSrcRegex.Matches(content), ExportReferenceKind.ScriptSrc, currentRoute);
            AddLinkReferences(references, content, currentRoute);
            AddReferencesFromMatches(references, ImgSrcRegex.Matches(content), ExportReferenceKind.ImgSrc, currentRoute);

            foreach (Match match in SrcSetRegex.Matches(content))
            {
                foreach (var srcSetUrl in ParseSrcSet(match.Groups[2].Value))
                {
                    AddReference(references, srcSetUrl, ExportReferenceKind.ImgSrcSet, currentRoute);
                }
            }

            var styleBlocks = StyleBlockRegex.Matches(content).Select(m => m.Groups[1].Value);
            var styleAttrs = StyleAttrRegex.Matches(content).Select(m => m.Groups[2].Value);
            foreach (var css in styleBlocks.Concat(styleAttrs))
            {
                AddCssUrlReferences(references, css, currentRoute);
            }
        }
        else
        {
            AddCssUrlReferences(references, content, currentRoute);
        }

        return references;
    }

    private void AddReferencesFromMatches(
        ICollection<ExportReference> references,
        MatchCollection matches,
        ExportReferenceKind kind,
        string currentRoute)
    {
        foreach (Match match in matches)
        {
            AddReference(references, match.Groups[2].Value.Trim(), kind, currentRoute);
        }
    }

    private void AddLinkReferences(ICollection<ExportReference> references, string html, string currentRoute)
    {
        foreach (var tag in LinkTagRegex.Matches(html).Select(m => m.Value))
        {
            var relMatch = LinkRelRegex.Match(tag);
            if (!relMatch.Success)
            {
                continue;
            }

            var rel = relMatch.Groups[2].Value;
            if (!rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase)
                && !rel.Contains("icon", StringComparison.OrdinalIgnoreCase)
                && !rel.Contains("preload", StringComparison.OrdinalIgnoreCase)
                && !rel.Contains("prefetch", StringComparison.OrdinalIgnoreCase)
                && !rel.Contains("dns-prefetch", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hrefMatch = LinkHrefRegex.Match(tag);
            if (hrefMatch.Success)
            {
                AddReference(references, hrefMatch.Groups[2].Value.Trim(), ExportReferenceKind.LinkHref, currentRoute);
            }
        }
    }

    private void AddCssUrlReferences(ICollection<ExportReference> references, string css, string currentRoute)
    {
        var urls = CssUrlRegex.Matches(css)
            .Select(m => m.Groups[2].Value.Trim())
            .Where(url => !string.IsNullOrEmpty(url)
                          && !url.StartsWith('#')
                          && !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase));

        foreach (var url in urls)
        {
            AddReference(references, url, ExportReferenceKind.CssUrl, currentRoute);
        }
    }

    private void AddReference(
        ICollection<ExportReference> references,
        string rawValue,
        ExportReferenceKind kind,
        string currentRoute)
    {
        var reference = CreateReference(rawValue, kind, currentRoute);
        if (reference is not null)
        {
            references.Add(reference);
        }
    }

    private ExportReference? CreateReference(string rawValue, ExportReferenceKind kind, string currentRoute)
    {
        if (IsHashOnlyReference(rawValue))
        {
            return null;
        }

        var resolved = ResolveRelativeUrl(currentRoute, rawValue);
        if (!TrySplitManagedUrl(resolved, out var path, out var query, out var fragment))
        {
            return null;
        }

        return new ExportReference(currentRoute, kind, rawValue, resolved, path, query, fragment);
    }

    private static bool IsHashOnlyReference(string rawValue)
    {
        return rawValue.TrimStart().StartsWith('#');
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

    private IEnumerable<string> ParseSrcSet(string srcSet)
    {
        return ParseSrcSetCandidates(srcSet).Select(candidate => candidate.Url);
    }

    private static IReadOnlyList<SrcSetCandidate> ParseSrcSetCandidates(string srcSet)
    {
        var candidates = new List<SrcSetCandidate>();
        if (string.IsNullOrWhiteSpace(srcSet))
        {
            return candidates;
        }

        var segmentStart = 0;
        while (segmentStart < srcSet.Length)
        {
            var segmentEnd = srcSet.IndexOf(',', segmentStart);
            if (segmentEnd < 0)
            {
                segmentEnd = srcSet.Length;
            }

            var urlStart = segmentStart;
            while (urlStart < segmentEnd && char.IsWhiteSpace(srcSet[urlStart]))
            {
                urlStart++;
            }

            var urlEnd = urlStart;
            while (urlEnd < segmentEnd && !char.IsWhiteSpace(srcSet[urlEnd]))
            {
                urlEnd++;
            }

            if (urlEnd > urlStart)
            {
                candidates.Add(new SrcSetCandidate(
                    srcSet[urlStart..urlEnd],
                    urlStart,
                    urlEnd - urlStart));
            }

            segmentStart = segmentEnd + 1;
        }

        return candidates;
    }

    private string RewriteManagedReferences(string content, string currentRoute, bool htmlScope, ExportContext context)
    {
        return htmlScope
            ? RewriteHtmlReferences(content, currentRoute, context)
            : RewriteCssReferences(content, currentRoute, context);
    }

    private string RewriteHtmlReferences(string html, string currentRoute, ExportContext context)
    {
        var rewritten = RewriteSimpleAttributeReferences(html, AnchorHrefRegex, ExportReferenceKind.AnchorHref, currentRoute, context);
        rewritten = RewriteSimpleAttributeReferences(rewritten, TurboFrameSrcRegex, ExportReferenceKind.TurboFrameSrc, currentRoute, context);
        rewritten = RewriteSimpleAttributeReferences(rewritten, ScriptSrcRegex, ExportReferenceKind.ScriptSrc, currentRoute, context);
        rewritten = RewriteLinkReferences(rewritten, currentRoute, context);
        rewritten = RewriteSimpleAttributeReferences(rewritten, ImgSrcRegex, ExportReferenceKind.ImgSrc, currentRoute, context);
        rewritten = RewriteSrcSetReferences(rewritten, currentRoute, context);
        rewritten = StyleBlockRegex.Replace(
            rewritten,
            match => ReplaceCapturedValue(
                match,
                1,
                RewriteCssReferences(match.Groups[1].Value, currentRoute, context)));
        rewritten = StyleAttrRegex.Replace(
            rewritten,
            match => ReplaceCapturedValue(
                match,
                2,
                RewriteCssReferences(match.Groups[2].Value, currentRoute, context)));

        return rewritten;
    }

    private string RewriteSimpleAttributeReferences(
        string html,
        Regex regex,
        ExportReferenceKind kind,
        string currentRoute,
        ExportContext context)
    {
        return regex.Replace(
            html,
            match => RewriteMatchedRawValue(match, match.Groups[2].Value.Trim(), kind, currentRoute, context));
    }

    private string RewriteLinkReferences(string html, string currentRoute, ExportContext context)
    {
        return LinkTagRegex.Replace(
            html,
            match =>
            {
                var tag = match.Value;
                var relMatch = LinkRelRegex.Match(tag);
                if (!relMatch.Success)
                {
                    return tag;
                }

                var rel = relMatch.Groups[2].Value;
                if (!rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase)
                    && !rel.Contains("icon", StringComparison.OrdinalIgnoreCase)
                    && !rel.Contains("preload", StringComparison.OrdinalIgnoreCase)
                    && !rel.Contains("prefetch", StringComparison.OrdinalIgnoreCase)
                    && !rel.Contains("dns-prefetch", StringComparison.OrdinalIgnoreCase))
                {
                    return tag;
                }

                var hrefMatch = LinkHrefRegex.Match(tag);
                if (!hrefMatch.Success)
                {
                    return tag;
                }

                var rewrittenHref = RewriteMatchedRawValue(
                    hrefMatch,
                    hrefMatch.Groups[2].Value.Trim(),
                    ExportReferenceKind.LinkHref,
                    currentRoute,
                    context);
                return tag.Replace(hrefMatch.Value, rewrittenHref, StringComparison.Ordinal);
            });
    }

    private string RewriteSrcSetReferences(string html, string currentRoute, ExportContext context)
    {
        return SrcSetRegex.Replace(
            html,
            match =>
            {
                var srcSet = match.Groups[2].Value;
                var rewrittenSrcSet = new StringBuilder(srcSet);
                foreach (var candidate in ParseSrcSetCandidates(srcSet).OrderByDescending(candidate => candidate.Start))
                {
                    var reference = CreateReference(candidate.Url, ExportReferenceKind.ImgSrcSet, currentRoute);
                    if (reference is null || !TryResolveReferenceArtifactUrl(reference, context, out var artifactUrl))
                    {
                        continue;
                    }

                    rewrittenSrcSet
                        .Remove(candidate.Start, candidate.Length)
                        .Insert(candidate.Start, artifactUrl);
                }

                return ReplaceCapturedValue(match, 2, rewrittenSrcSet.ToString());
            });
    }

    private string RewriteCssReferences(string css, string currentRoute, ExportContext context)
    {
        return CssUrlRegex.Replace(
            css,
            match => RewriteMatchedRawValue(match, match.Groups[2].Value.Trim(), ExportReferenceKind.CssUrl, currentRoute, context));
    }

    private string RewriteMatchedRawValue(
        Match match,
        string rawValue,
        ExportReferenceKind kind,
        string currentRoute,
        ExportContext context)
    {
        var reference = CreateReference(rawValue, kind, currentRoute);
        if (reference is null || !TryResolveReferenceArtifactUrl(reference, context, out var artifactUrl))
        {
            return match.Value;
        }

        return ReplaceCapturedValue(match, 2, artifactUrl);
    }

    private static string ReplaceCapturedValue(Match match, int groupNumber, string replacement)
    {
        var valueGroup = match.Groups[groupNumber];
        var valueStart = valueGroup.Index - match.Index;
        var prefix = match.Value[..valueStart];
        var suffix = match.Value[(valueStart + valueGroup.Length)..];
        return prefix + replacement + suffix;
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
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        // If it's already an absolute URL (http, https, data, mailto, javascript, or starts with /), return it or handle in normalization
        if (url.StartsWith('/') || Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }

        try
        {
            // Use generic dummy host to resolve paths
            var baseUri = new Uri(new Uri("http://dummy"), baseRoute);
            var resolvedUri = new Uri(baseUri, url);

            return resolvedUri.AbsolutePath + resolvedUri.Query + resolvedUri.Fragment;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve relative URL: {Url} against {BaseRoute}", url, baseRoute);

            return url;
        }
    }

    private static bool TrySplitManagedUrl(
        string rawRef,
        out string path,
        out string query,
        out string fragment)
    {
        path = string.Empty;
        query = string.Empty;
        fragment = string.Empty;

        if (string.IsNullOrWhiteSpace(rawRef) || !rawRef.StartsWith('/') || rawRef.StartsWith("//"))
        {
            return false;
        }

        if (!Uri.TryCreate(ManagedUrlBase, rawRef, out var normalizedUri))
        {
            return false;
        }

        rawRef = normalizedUri.PathAndQuery + normalizedUri.Fragment;

        var pathEnd = rawRef.Length;
        var queryStart = rawRef.IndexOf('?');
        var fragmentStart = rawRef.IndexOf('#');
        var hasQuery = queryStart >= 0 && (fragmentStart < 0 || queryStart < fragmentStart);

        if (hasQuery)
        {
            pathEnd = Math.Min(pathEnd, queryStart);
        }

        if (fragmentStart >= 0)
        {
            pathEnd = Math.Min(pathEnd, fragmentStart);
        }

        path = rawRef[..pathEnd];
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (hasQuery)
        {
            var queryEnd = fragmentStart >= 0 ? fragmentStart : rawRef.Length;
            query = rawRef[queryStart..queryEnd];
        }

        if (fragmentStart >= 0)
        {
            fragment = rawRef[fragmentStart..];
        }

        return true;
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

        if (TrySplitManagedUrl(rawRef, out var path, out _, out _))
        {
            normalized = path;
            return true;
        }

        return false;
    }

    private readonly record struct SrcSetCandidate(string Url, int Start, int Length);
}
