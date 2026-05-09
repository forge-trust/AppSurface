using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// Serves one or more published RazorDocs trees from static export artifacts.
/// </summary>
/// <remarks>
/// Published trees are usually exported from the stable <c>/docs</c> surface and then mounted later under the configured
/// route-family root or <c>{RouteRootPath}/v/{version}</c>. This handler resolves extensionless requests back to the
/// exporter’s <c>.html</c> files and rewrites stable-root HTML or search-index payloads so the mounted tree stays
/// version-local.
/// </remarks>
internal sealed class RazorDocsPublishedTreeHandler
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private readonly IReadOnlyList<RazorDocsPublishedTreeMount> _mounts;
    private readonly string _previewRootPath;
    private readonly string _routeRootPath;

    /// <summary>
    /// Initializes a new instance of <see cref="RazorDocsPublishedTreeHandler"/>.
    /// </summary>
    /// <param name="mounts">Published trees to expose, ordered arbitrarily.</param>
    /// <param name="previewRootPath">The live preview docs root that should bypass published-tree handling.</param>
    /// <param name="routeRootPath">The route-family root that owns archive and exact-version routes.</param>
    internal RazorDocsPublishedTreeHandler(
        IEnumerable<RazorDocsPublishedTreeMount> mounts,
        string previewRootPath,
        string routeRootPath = DocsUrlBuilder.DocsEntryPath)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeRootPath);

        _mounts = mounts
            .OrderByDescending(mount => mount.MountRootPath.Length)
            .ToList();
        _previewRootPath = previewRootPath;
        _routeRootPath = routeRootPath;
    }

    /// <summary>
    /// Attempts to serve the current request from one of the configured published trees.
    /// </summary>
    /// <param name="httpContext">The current HTTP request context.</param>
    /// <returns><c>true</c> when a published tree handled the request; otherwise <c>false</c>.</returns>
    internal async Task<bool> TryHandleAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!HttpMethods.IsGet(httpContext.Request.Method)
            && !HttpMethods.IsHead(httpContext.Request.Method))
        {
            return false;
        }

        var requestPath = httpContext.Request.Path.Value ?? string.Empty;
        foreach (var mount in _mounts)
        {
            if (!IsRequestForMount(requestPath, mount.MountRootPath))
            {
                continue;
            }

            if (ShouldBypassStableAlias(requestPath, mount.MountRootPath))
            {
                return false;
            }

            if (!TryResolveFile(mount, requestPath, out var fileInfo, out var relativeFilePath))
            {
                return false;
            }

            await WriteResponseAsync(httpContext, mount, _previewRootPath, _routeRootPath, relativeFilePath, fileInfo);
            return true;
        }

        return false;
    }

    private bool ShouldBypassStableAlias(string requestPath, string mountRootPath)
    {
        if (!string.Equals(mountRootPath, _routeRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DocsUrlBuilder.IsUnderRoot(requestPath, _previewRootPath)
               || DocsUrlBuilder.IsUnderRoot(requestPath, DocsUrlBuilder.JoinPath(_routeRootPath, "versions"))
               || DocsUrlBuilder.IsUnderRoot(requestPath, DocsUrlBuilder.JoinPath(_routeRootPath, "v"));
    }

    private static bool IsRequestForMount(string requestPath, string mountRootPath)
    {
        if (string.Equals(mountRootPath, "/", StringComparison.Ordinal))
        {
            return requestPath.StartsWith("/", StringComparison.Ordinal);
        }

        return string.Equals(requestPath, mountRootPath, StringComparison.OrdinalIgnoreCase)
               || requestPath.StartsWith(mountRootPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveFile(
        RazorDocsPublishedTreeMount mount,
        string requestPath,
        out IFileInfo fileInfo,
        out string relativeFilePath)
    {
        fileInfo = new NotFoundFileInfo(string.Empty);
        relativeFilePath = string.Empty;

        var relativeRequestPath = requestPath.Length == mount.MountRootPath.Length
            ? string.Empty
            : requestPath[mount.MountRootPath.Length..];
        var trimmed = relativeRequestPath.TrimStart('/');
        if (HasHiddenPathSegment(trimmed))
        {
            return false;
        }

        if (IsAllowedExactFilePath(trimmed)
            && !relativeRequestPath.EndsWith("/", StringComparison.Ordinal))
        {
            var exactFile = mount.FileProvider.GetFileInfo(trimmed);
            if (exactFile.Exists)
            {
                fileInfo = exactFile;
                relativeFilePath = trimmed;
                return true;
            }
        }

        foreach (var candidate in BuildCandidatePaths(relativeRequestPath))
        {
            var candidateFile = mount.FileProvider.GetFileInfo(candidate);
            if (!candidateFile.Exists)
            {
                continue;
            }

            fileInfo = candidateFile;
            relativeFilePath = candidate;
            return true;
        }

        return false;
    }

    private static bool IsAllowedExactFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || HasHiddenPathSegment(path))
        {
            return false;
        }

        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(path, "search.css", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "search-client.js", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "outline-client.js", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "minisearch.min.js", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "search-index.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasAllowedEmbeddedAssetExtension(path);
    }

    private static IEnumerable<string> BuildCandidatePaths(string relativeRequestPath)
    {
        var trimmed = relativeRequestPath.TrimStart('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            yield return "index.html";
            yield break;
        }

        if (relativeRequestPath.EndsWith("/", StringComparison.Ordinal))
        {
            yield return trimmed + "index.html";
            yield break;
        }

        if (IsAllowedExactFilePath(trimmed))
        {
            yield break;
        }

        yield return trimmed + ".html";
        yield return trimmed + "/index.html";
    }

    private static bool HasAllowedEmbeddedAssetExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".woff", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".eot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasHiddenPathSegment(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
               && path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                   .Any(segment => segment.StartsWith(".", StringComparison.Ordinal));
    }

    private static async Task WriteResponseAsync(
        HttpContext httpContext,
        RazorDocsPublishedTreeMount mount,
        string previewRootPath,
        string routeRootPath,
        string relativeFilePath,
        IFileInfo fileInfo)
    {
        var contentType = ResolveContentType(relativeFilePath);
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = contentType;
        httpContext.Response.ContentLength = null;
        httpContext.Response.Headers.LastModified = fileInfo.LastModified.ToUniversalTime().ToString(
            "R",
            CultureInfo.InvariantCulture);

        if (ShouldRewriteHtml(relativeFilePath))
        {
            var html = await ReadUtf8TextAsync(fileInfo, httpContext.RequestAborted);
            var rewrittenHtml = RazorDocsPublishedTreeContentRewriter.RewriteHtml(
                html,
                mount.MountRootPath,
                previewRootPath,
                routeRootPath,
                httpContext.Request.PathBase.Value);
            await WriteUtf8TextAsync(httpContext, rewrittenHtml, contentType);
            return;
        }

        if (ShouldRewriteSearchIndex(relativeFilePath))
        {
            var json = await ReadUtf8TextAsync(fileInfo, httpContext.RequestAborted);
            var rewrittenJson = RazorDocsPublishedTreeContentRewriter.RewriteSearchIndexJson(
                json,
                mount.MountRootPath,
                previewRootPath,
                routeRootPath,
                httpContext.Request.PathBase.Value);
            await WriteUtf8TextAsync(httpContext, rewrittenJson, "application/json; charset=utf-8");
            return;
        }

        httpContext.Response.ContentLength = fileInfo.Length;
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return;
        }

        await httpContext.Response.SendFileAsync(fileInfo, httpContext.RequestAborted);
    }

    private static bool ShouldRewriteHtml(string relativeFilePath)
    {
        return relativeFilePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRewriteSearchIndex(string relativeFilePath)
    {
        return string.Equals(relativeFilePath, "search-index.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveContentType(string relativeFilePath)
    {
        return ContentTypeProvider.TryGetContentType(relativeFilePath, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    private static async Task<string> ReadUtf8TextAsync(IFileInfo fileInfo, CancellationToken cancellationToken)
    {
        await using var stream = fileInfo.CreateReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task WriteUtf8TextAsync(HttpContext httpContext, string content, string contentType)
    {
        var payload = Encoding.UTF8.GetBytes(content);
        httpContext.Response.ContentType = contentType;
        httpContext.Response.ContentLength = payload.Length;
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return;
        }

        await httpContext.Response.Body.WriteAsync(payload, httpContext.RequestAborted);
    }
}

/// <summary>
/// Describes one published exact-version tree that should be mounted into the active host.
/// </summary>
/// <remarks>
/// When multiple <see cref="RazorDocsPublishedTreeMount" /> instances overlap, callers should treat the longest
/// <see cref="MountRootPath" /> as the winning mount because the request handler resolves mounts from most-specific to
/// least-specific roots before serving content.
/// </remarks>
/// <param name="MountRootPath">The request-path root where the tree should appear.</param>
/// <param name="FileProvider">The static file provider for the tree contents.</param>
internal sealed record RazorDocsPublishedTreeMount(
    string MountRootPath,
    IFileProvider FileProvider);

/// <summary>
/// Rewrites stable-root published-tree content so the same artifact can be served from different mount roots.
/// </summary>
/// <remarks>
/// Rewrites are mount-aware rather than file-aware. The active <see cref="RazorDocsPublishedTreeMount" /> decides which
/// root wins, and then the rewriter adjusts exported stable-root URLs so they point at that mounted surface. The
/// default stable <c>/docs</c> surface only needs HTML rewrites when the host adds a non-empty request
/// <c>PathBase</c>; when the mount root and route root are still <c>/docs</c> and no <c>PathBase</c> applies, the
/// exported HTML is already correct and is returned unchanged.
/// </remarks>
internal static class RazorDocsPublishedTreeContentRewriter
{
    private static readonly HtmlParser HtmlParser = new();
    private static readonly Regex DocsClientConfigRegex = new(
        @"window\.__razorDocsConfig\s*=\s*(\{.*?\})\s*;",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Rewrites stable-root HTML so docs-local links, assets, and client config point at the supplied mount root.
    /// </summary>
    /// <param name="html">The exported HTML document.</param>
    /// <param name="mountRootPath">The request-path root where the tree is being served.</param>
    /// <param name="previewRootPath">The live preview docs root that should stay untouched when encountered.</param>
    /// <param name="routeRootPath">The route-family root that owns archive and exact-version routes.</param>
    /// <param name="requestPathBase">The current host path base that should prefix rewritten app-relative docs URLs.</param>
    /// <returns>The rewritten HTML document.</returns>
    /// <remarks>
    /// This method rewrites exported stable-root docs links, assets, and the inline
    /// <c>window.__razorDocsConfig</c> payload matched by <see cref="DocsClientConfigRegex" /> so the document behaves
    /// like it was originally emitted for <paramref name="mountRootPath" />. As part of that rewrite, the legacy
    /// <c>docsVersionsUrl</c> client field is removed because version archive navigation is rendered server-side. When
    /// <paramref name="mountRootPath" /> and <paramref name="routeRootPath" /> are both the default <c>/docs</c>,
    /// rewrites only occur if <paramref name="requestPathBase" /> is non-empty so sub-path-hosted apps still emit
    /// <c>/some-base/docs/...</c> links.
    /// </remarks>
    internal static string RewriteHtml(
        string html,
        string mountRootPath,
        string previewRootPath = "/docs/next",
        string routeRootPath = DocsUrlBuilder.DocsEntryPath,
        string? requestPathBase = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeRootPath);

        if (string.Equals(mountRootPath, DocsUrlBuilder.DocsEntryPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(routeRootPath, DocsUrlBuilder.DocsEntryPath, StringComparison.OrdinalIgnoreCase)
            && !HasNonEmptyPathBase(requestPathBase))
        {
            return html;
        }

        var document = HtmlParser.ParseDocument(html);
        foreach (var element in document.QuerySelectorAll("[href]"))
        {
            RewriteAttributeValue(element, "href", mountRootPath, previewRootPath, routeRootPath, requestPathBase);
        }

        foreach (var element in document.QuerySelectorAll("[src]"))
        {
            RewriteAttributeValue(element, "src", mountRootPath, previewRootPath, routeRootPath, requestPathBase);
        }

        foreach (var element in document.QuerySelectorAll("[srcset]"))
        {
            var value = element.GetAttribute("srcset");
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var rewrittenValue = RewriteSrcSetValue(value, mountRootPath, previewRootPath, routeRootPath, requestPathBase);
            if (!string.Equals(value, rewrittenValue, StringComparison.Ordinal))
            {
                element.SetAttribute("srcset", rewrittenValue);
            }
        }

        foreach (var script in document.QuerySelectorAll("script:not([src])"))
        {
            var scriptContent = script.TextContent;
            if (string.IsNullOrWhiteSpace(scriptContent)
                || !scriptContent.Contains("__razorDocsConfig", StringComparison.Ordinal))
            {
                continue;
            }

            var rewrittenScript = RewriteDocsClientConfigScript(scriptContent, mountRootPath, requestPathBase);
            if (!string.Equals(scriptContent, rewrittenScript, StringComparison.Ordinal))
            {
                script.TextContent = rewrittenScript;
            }
        }

        var serializedHtml = document.DocumentElement?.OuterHtml ?? html;
        return html.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            ? "<!DOCTYPE html>" + Environment.NewLine + serializedHtml
            : serializedHtml;
    }

    /// <summary>
    /// Rewrites a published search-index payload so mounted document URLs stay inside the active docs surface.
    /// </summary>
    /// <param name="json">The exported search-index payload.</param>
    /// <param name="mountRootPath">The request-path root where the tree is being served.</param>
    /// <param name="previewRootPath">The live preview docs root that should stay untouched when encountered.</param>
    /// <param name="routeRootPath">The route-family root that owns archive and exact-version routes.</param>
    /// <param name="requestPathBase">The current host path base that should prefix rewritten app-relative docs URLs.</param>
    /// <returns>
    /// The original payload when the mount and route root are the default <c>/docs</c> surface without a non-empty path
    /// base, when the payload is not a JSON object with a top-level <c>documents</c> array, or when no eligible
    /// <c>documents[*].path</c> values require rewriting; otherwise a payload whose rewritten document paths stay
    /// inside the mounted docs root.
    /// </returns>
    /// <remarks>
    /// Only <c>documents[*].path</c> values are rewritten. Other JSON fields, including titles, metadata, and facet
    /// payloads, are preserved exactly as exported. Default stable mounts rooted at <c>/docs</c> are a no-op unless
    /// <paramref name="requestPathBase" /> is non-empty, because the exported payload already points at the default
    /// surface. Preview-root paths, archive paths such as <c>{RouteRootPath}/versions</c>, and already-versioned exact
    /// routes such as <c>{RouteRootPath}/v/1.2.3/guide.html</c> are preserved rather than rebased.
    /// When a rewrite does occur, the helper prepends the normalized request path base to eligible app-relative URLs,
    /// so <c>/docs/guide.html</c> becomes <c>/some-base/docs/v/1.2.3/guide.html</c> for an exact mount at
    /// <c>/docs/v/1.2.3</c>. Callers should not expect other JSON fields to change, and they must supply a non-empty
    /// <paramref name="requestPathBase" /> if stable mounts need virtual-directory rebasing.
    /// </remarks>
    internal static string RewriteSearchIndexJson(
        string json,
        string mountRootPath,
        string previewRootPath = "/docs/next",
        string routeRootPath = DocsUrlBuilder.DocsEntryPath,
        string? requestPathBase = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeRootPath);

        if (string.Equals(mountRootPath, DocsUrlBuilder.DocsEntryPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(routeRootPath, DocsUrlBuilder.DocsEntryPath, StringComparison.OrdinalIgnoreCase)
            && !HasNonEmptyPathBase(requestPathBase))
        {
            return json;
        }

        var node = JsonNode.Parse(json) as JsonObject;
        if (node?["documents"] is not JsonArray documents)
        {
            return json;
        }

        foreach (var document in documents.OfType<JsonObject>())
        {
            if (document["path"] is not JsonValue pathValue
                || !pathValue.TryGetValue<string>(out var path)
                || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            document["path"] = RewriteMountedDocsUrl(path, mountRootPath, previewRootPath, routeRootPath, requestPathBase);
        }

        return node.ToJsonString();
    }

    private static void RewriteAttributeValue(
        AngleSharp.Dom.IElement element,
        string attributeName,
        string mountRootPath,
        string previewRootPath,
        string routeRootPath,
        string? requestPathBase)
    {
        var value = element.GetAttribute(attributeName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var rewrittenValue = RewriteMountedDocsUrl(value, mountRootPath, previewRootPath, routeRootPath, requestPathBase);
        if (!string.Equals(value, rewrittenValue, StringComparison.Ordinal))
        {
            element.SetAttribute(attributeName, rewrittenValue);
        }
    }

    private static string RewriteDocsClientConfigScript(string scriptContent, string mountRootPath, string? requestPathBase)
    {
        return DocsClientConfigRegex.Replace(
            scriptContent,
            match =>
            {
                JsonObject configNode;
                try
                {
                    configNode = JsonNode.Parse(match.Groups[1].Value)!.AsObject();
                }
                catch (JsonException)
                {
                    return match.Value;
                }

                configNode["docsRootPath"] = PrefixPathBase(mountRootPath, requestPathBase);
                configNode["docsSearchUrl"] = PrefixPathBase(mountRootPath + "/search", requestPathBase);
                configNode["docsSearchIndexUrl"] = PrefixPathBase(mountRootPath + "/search-index.json", requestPathBase);
                configNode.AsObject().Remove("docsVersionsUrl");

                return $"window.__razorDocsConfig = {configNode.ToJsonString()};";
            });
    }

    private static string RewriteSrcSetValue(
        string srcSetValue,
        string mountRootPath,
        string previewRootPath,
        string routeRootPath,
        string? requestPathBase)
    {
        var rewrittenEntries = srcSetValue
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(
                entry =>
                {
                    if (string.IsNullOrWhiteSpace(entry))
                    {
                        return entry;
                    }

                    var separatorIndex = entry.IndexOf(' ');
                    if (separatorIndex < 0)
                    {
                        return RewriteMountedDocsUrl(entry, mountRootPath, previewRootPath, routeRootPath, requestPathBase);
                    }

                    var url = entry[..separatorIndex];
                    var descriptor = entry[separatorIndex..];
                    return RewriteMountedDocsUrl(url, mountRootPath, previewRootPath, routeRootPath, requestPathBase) + descriptor;
                });

        return string.Join(", ", rewrittenEntries);
    }

    private static string RewriteMountedDocsUrl(
        string value,
        string mountRootPath,
        string previewRootPath,
        string routeRootPath,
        string? requestPathBase)
    {
        if (!value.StartsWith("/", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
            {
                var rewrittenPath = RewriteMountedDocsPath(
                    absoluteUri.AbsolutePath,
                    mountRootPath,
                    previewRootPath,
                    routeRootPath,
                    requestPathBase);
                if (rewrittenPath is null)
                {
                    return value;
                }

                return absoluteUri.GetLeftPart(UriPartial.Authority) + rewrittenPath + absoluteUri.Query + absoluteUri.Fragment;
            }

            return value;
        }

        var suffixIndex = value.IndexOfAny(['?', '#']);
        var path = suffixIndex >= 0 ? value[..suffixIndex] : value;
        var suffix = suffixIndex >= 0 ? value[suffixIndex..] : string.Empty;
        var rewrittenRelativePath = RewriteMountedDocsPath(path, mountRootPath, previewRootPath, routeRootPath, requestPathBase);
        return rewrittenRelativePath is null ? value : rewrittenRelativePath + suffix;
    }

    private static string? RewriteMountedDocsPath(
        string path,
        string mountRootPath,
        string previewRootPath,
        string routeRootPath,
        string? requestPathBase)
    {
        var archivePath = DocsUrlBuilder.JoinPath(routeRootPath, "versions");
        var versionPrefix = DocsUrlBuilder.JoinPath(routeRootPath, "v");
        if (DocsUrlBuilder.IsUnderRoot(path, mountRootPath)
            || DocsUrlBuilder.IsUnderRoot(path, archivePath)
            || DocsUrlBuilder.IsUnderRoot(path, previewRootPath)
            || path.StartsWith(versionPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return PrefixPathBase(path, requestPathBase);
        }

        if (string.Equals(path, DocsUrlBuilder.DocsVersionsPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(DocsUrlBuilder.DocsVersionsPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return PrefixPathBase(archivePath + path[DocsUrlBuilder.DocsVersionsPath.Length..], requestPathBase);
        }

        if (string.Equals(path, DocsUrlBuilder.DocsVersionPrefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(DocsUrlBuilder.DocsVersionPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return PrefixPathBase(versionPrefix + path[DocsUrlBuilder.DocsVersionPrefix.Length..], requestPathBase);
        }

        if (string.Equals(path, DocsUrlBuilder.DocsEntryPath, StringComparison.OrdinalIgnoreCase))
        {
            return PrefixPathBase(mountRootPath, requestPathBase);
        }

        if (!path.StartsWith(DocsUrlBuilder.DocsEntryPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return PrefixPathBase(mountRootPath + path[DocsUrlBuilder.DocsEntryPath.Length..], requestPathBase);
    }

    private static string PrefixPathBase(string path, string? requestPathBase)
    {
        if (!HasNonEmptyPathBase(requestPathBase))
        {
            return path;
        }

        var normalizedPathBase = requestPathBase!.Trim();
        if (normalizedPathBase.Length > 1 && normalizedPathBase.EndsWith("/", StringComparison.Ordinal))
        {
            normalizedPathBase = normalizedPathBase[..^1];
        }

        return DocsUrlBuilder.IsUnderRoot(path, normalizedPathBase) || string.Equals(path, normalizedPathBase, StringComparison.OrdinalIgnoreCase)
            ? path
            : normalizedPathBase + path;
    }

    private static bool HasNonEmptyPathBase(string? requestPathBase)
    {
        return !string.IsNullOrWhiteSpace(requestPathBase)
               && !string.Equals(requestPathBase, "/", StringComparison.Ordinal);
    }
}
