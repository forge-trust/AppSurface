using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ForgeTrust.Runnable.Web;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorWire.Cli;

/// <summary>
/// A static generation engine that crawls a RazorWire application and exports its routes to static HTML files.
/// </summary>
[ExcludeFromCodeCoverage]
public class ExportEngine
{
    private readonly ILogger<ExportEngine> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

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

    private static readonly Regex ImgSrcSetRegex = new(
        @"<img[^>]*\ssrcset\s*=\s*(['""])(.*?)\1",
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
    /// Crawls the site starting from configured seed routes (or the root), writes the conventional reserved 404 page when available, and exports discovered pages and frame sources to the output path.
    /// </summary>
    /// <param name="context">Export configuration and runtime state including base URL, output path, queue, and visited set.</param>
    /// <param name="cancellationToken">Token to observe for cooperative cancellation of the crawl and export operations.</param>
    /// <remarks>
    /// If <see cref="ExportContext.SeedRoutesPath"/> is provided, the file is read and each line is validated and normalized to a root-relative route; invalid seeds are logged. If the seed file exists but yields no valid routes, the root path ("/") is enqueued. If no seed file is provided, the root path is enqueued. Before crawl processing begins, the engine probes Runnable's reserved conventional 404 route and writes <c>404.html</c> when the route returns a successful HTML response. That reserved-route probe and any follow-on <c>404.html</c> write are best-effort only: failures are logged, do not abort the crawl, and do not prevent queued seed routes from being processed. Discovered internal links and frame sources are queued and processed until the queue is exhausted or the operation is cancelled.
    /// </remarks>
    /// <returns>A task that completes when the crawl and export operations have finished.</returns>
    /// <exception cref="FileNotFoundException">Thrown when <see cref="ExportContext.SeedRoutesPath"/> is specified but the file does not exist.</exception>
    public async Task RunAsync(ExportContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "RunAsync started. BaseUrl: {BaseUrl}, OutputPath: {OutputPath}",
            context.BaseUrl,
            context.OutputPath);

        // 1. Seed routes
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
                // Parse as URI to strip query/fragment reliably
                if (Uri.TryCreate(seed, UriKind.RelativeOrAbsolute, out var uri))
                {
                    // If absolute, use PathAndQuery; if relative, just use the string
                    var path = uri.IsAbsoluteUri ? uri.PathAndQuery : seed;

                    // Normalize using our helper logic (strips query/fragment again to be safe and checks format)
                    if (TryGetNormalizedRoute(path, out var normalized))
                    {
                        added = true;
                        context.Queue.Enqueue(normalized);
                    }
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
                context.Queue.Enqueue("/");
            }
        }
        else
        {
            context.Queue.Enqueue("/");
        }

        _logger.LogInformation(
            "Crawl starting from {BaseUrl} with {Count} seed routes.",
            context.BaseUrl,
            context.Queue.Count);

        var client = _httpClientFactory.CreateClient("ExportEngine");
        await TryWriteConventionalNotFoundPageAsync(client, context, cancellationToken);

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

            await ExportRouteAsync(client, route, context, cancellationToken);
        }

        sw.Stop();
        _logger.LogInformation("Export completed in {ElapsedMilliseconds}ms", sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Fetches the HTML or asset for the specified route from the base URL, writes the file to the output directory, and enqueues any discovered internal links for further export.
    /// </summary>
    private async Task ExportRouteAsync(
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

                return;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var isHtml = string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase);
            var isCss = string.Equals(contentType, "text/css", StringComparison.OrdinalIgnoreCase);

            // Save file - verify content type to determine if we should force .html extension
            // Only append .html if it's actually an HTML document and the path doesn't look like one
            var filePath = MapRouteToFilePath(route, context.OutputPath, isHtml);
            var dirPath = Path.GetDirectoryName(filePath);
            if (dirPath != null && !Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }

            if (isHtml)
            {
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                var htmlForWrite = IsDocsRoute(route)
                    ? AddDocsStaticPartialsMarker(html)
                    : html;
                await File.WriteAllTextAsync(filePath, htmlForWrite, cancellationToken);

                // Export frame fragments for docs pages so static navigation can fetch only the content island.
                await TryWriteDocsPartialAsync(route, filePath, html, cancellationToken);

                // Extract links, frames, and assets only from HTML
                ExtractLinks(html, context);
                ExtractFrames(html, context);
                ExtractAssets(html, route, context);
            }
            else if (isCss)
            {
                // For CSS, we need to read as text to parse url() refs, but write as binary/text
                var css = await response.Content.ReadAsStringAsync(cancellationToken);
                await File.WriteAllTextAsync(filePath, css, cancellationToken);

                // Extract assets from CSS
                var cssUrls = CssUrlRegex.Matches(css)
                    .Select(m => m.Groups[2].Value.Trim())
                    .Where(url => !string.IsNullOrEmpty(url)
                                  && !url.StartsWith('#')
                                  && !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase));

                var normalizedAssets = cssUrls
                    .Select(asset => ResolveRelativeUrl(route, asset))
                    .Select(resolved => TryGetNormalizedRoute(resolved, out var n) ? n : null)
                    .Where(n => n != null && !context.Visited.Contains(n))
                    .Distinct();

                foreach (var normalized in normalizedAssets)
                {
                    context.Queue.Enqueue(normalized!);
                }
            }
            else
            {
                // Binary export for assets (images, fonts, etc)
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true);
                await contentStream.CopyToAsync(fileStream, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting {Route}", route);
        }
    }

    private async Task TryWriteConventionalNotFoundPageAsync(
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

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var filePath = Path.Combine(context.OutputPath, "404.html");
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(filePath, html, cancellationToken);
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
        string route,
        string htmlFilePath,
        string html,
        CancellationToken cancellationToken)
    {
        if (!IsDocsRoute(route))
        {
            return;
        }

        var frameHtml = ExtractDocContentFrame(html);
        if (string.IsNullOrWhiteSpace(frameHtml))
        {
            return;
        }

        var partialPath = MapHtmlFilePathToPartialPath(htmlFilePath);
        await File.WriteAllTextAsync(partialPath, frameHtml, cancellationToken);
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

    /// <summary>
    /// Extracts root-relative internal link targets from the provided HTML and enqueues any unvisited routes for crawling.
    /// </summary>
    /// <param name="html">HTML source to scan.</param>
    /// <param name="context">The export context.</param>
    internal void ExtractLinks(string html, ExportContext context)
    {
        // Only match <a href="..."> not <link href="..."> which is handled by ExtractAssets
        var targets = AnchorHrefRegex.Matches(html)
            .Select(m => m.Groups[2].Value.Trim());

        foreach (var href in targets)
        {
            if (TryGetNormalizedRoute(href, out var normalized) && !context.Visited.Contains(normalized))
            {
                context.Queue.Enqueue(normalized);
            }
        }
    }

    /// <summary>
    /// Extracts root-relative `src` values from &lt;turbo-frame&gt; elements in the provided HTML and enqueues each unvisited path for export.
    /// </summary>
    /// <param name="html">HTML content to scan.</param>
    /// <param name="context">The export context.</param>
    private void ExtractFrames(string html, ExportContext context)
    {
        var targets = TurboFrameSrcRegex.Matches(html)
            .Select(m => m.Groups[2].Value.Trim());

        foreach (var src in targets)
        {
            if (TryGetNormalizedRoute(src, out var normalized) && !context.Visited.Contains(normalized))
            {
                context.Queue.Enqueue(normalized);
            }
        }
    }

    /// <summary>
    /// Extracts root-relative asset references (scripts, styles, images) from the provided HTML and enqueues each unvisited path for export.
    /// </summary>
    /// <param name="html">HTML content to scan.</param>
    /// <param name="currentRoute">The route of the page being scanned, used for resolving relative URLs.</param>
    /// <param name="context">The export context.</param>
    internal void ExtractAssets(string html, string currentRoute, ExportContext context)
    {
        // <script src="...">
        var scripts = ScriptSrcRegex.Matches(html)
            .Select(m => m.Groups[2].Value.Trim());

        // <link href="..."> (stylesheets, icons, etc)
        // <link href="..."> (stylesheets, icons, etc) - FILTERED by rel
        var links = LinkTagRegex.Matches(html)
            .Select(m => m.Value)
            .Where(tag =>
            {
                var relMatch = LinkRelRegex.Match(tag);

                if (!relMatch.Success)
                {
                    return false;
                }

                var rel = relMatch.Groups[2].Value;

                // Allow stylesheets, icons, preloads, etc.
                return rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase)
                       || rel.Contains("icon", StringComparison.OrdinalIgnoreCase)
                       || rel.Contains("preload", StringComparison.OrdinalIgnoreCase)
                       || rel.Contains("prefetch", StringComparison.OrdinalIgnoreCase)
                       || rel.Contains("dns-prefetch", StringComparison.OrdinalIgnoreCase);
            })
            .Select(tag => LinkHrefRegex.Match(tag))
            .Where(m => m.Success)
            .Select(m => m.Groups[2].Value.Trim());

        // <img src="...">
        var images = ImgSrcRegex.Matches(html)
            .Select(m => m.Groups[2].Value.Trim());

        // <img srcset="...">
        var srcsets = ImgSrcSetRegex.Matches(html)
            .SelectMany(m => ParseSrcSet(m.Groups[2].Value));

        // CSS url(...) in <style> blocks
        // 1. Extract style block content
        var styleBlocks = StyleBlockRegex.Matches(html)
            .Select(m => m.Groups[1].Value);

        // 2. Extract style="" attributes
        var styleAttrs = StyleAttrRegex.Matches(html)
            .Select(m => m.Groups[2].Value);

        // 3. Find url(...) in both
        var cssUrls = styleBlocks.Concat(styleAttrs)
            .SelectMany(css => CssUrlRegex.Matches(css))
            .Select(m => m.Groups[2].Value.Trim())
            .Where(url => !string.IsNullOrEmpty(url)
                          && !url.StartsWith('#')
                          && !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase));

        var allAssets = scripts
            .Concat(links)
            .Concat(images)
            .Concat(srcsets)
            .Concat(cssUrls);

        var normalizedAssets = allAssets
            .Select(asset => ResolveRelativeUrl(currentRoute, asset))
            .Select(resolved => TryGetNormalizedRoute(resolved, out var n) ? n : null)
            .Where(n => n != null && !context.Visited.Contains(n))
            .Distinct();

        foreach (var normalized in normalizedAssets)
        {
            context.Queue.Enqueue(normalized!);
        }
    }

    private IEnumerable<string> ParseSrcSet(string srcSet)
    {
        // srcset format: "url [descriptor], url [descriptor]"
        // Split by comma, then take the first part of the whitespace-split segment
        if (string.IsNullOrWhiteSpace(srcSet))
        {
            return Enumerable.Empty<string>();
        }

        return srcSet.Split(',')
            .Select(candidate =>
                candidate.Trim().Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 0)
            .Select(parts => parts[0]);
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

        // Strip query and fragment
        var split = rawRef.Split(['?', '#'], 2);
        normalized = split[0];

        return !string.IsNullOrEmpty(normalized);
    }
}
