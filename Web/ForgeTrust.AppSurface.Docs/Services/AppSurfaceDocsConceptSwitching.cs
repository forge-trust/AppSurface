using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>Caller-owned HTTP, physical URL and DOM adapter over the immutable shared concept index.</summary>
/// <remarks>
/// The adapter does not modify archived bytes. It projects one page at a time, verifies source files before exposing
/// standalone choices, and bounds the complete added UI including incoming transition records. Request context is a
/// lookup key only: all notice text and return links come from validated, page-local rows in this host snapshot.
/// </remarks>
internal sealed class AppSurfaceDocsConceptSwitching
{
    private static readonly object RuntimeAuthorizationKey = new();
    private readonly DocConceptIdentityIndex _index;
    private readonly IReadOnlyList<AppSurfaceDocsResolvedVersion> _versions;
    private readonly DocsUrlBuilder _urls;
    private readonly string _instance;
    private readonly int _limit;

    internal AppSurfaceDocsConceptSwitching(DocConceptIdentityIndex index,
        IReadOnlyList<AppSurfaceDocsResolvedVersion> versions, DocsUrlBuilder urls, string instance, int limit)
    {
        _index = index;
        _versions = versions.Where(version => version.Visibility == AppSurfaceDocsVersionVisibility.Public).ToArray();
        _urls = urls;
        _instance = instance;
        _limit = limit;
    }

    /// <summary>Serves package assets and a verified source's bounded no-script choice or 503 recovery page.</summary>
    internal async Task<bool> TryHandleAsync(HttpContext context, ILogger logger)
    {
        var prefix = DocsUrlBuilder.JoinPath(_urls.RouteRootPath, "versions/switch/");
        var request = context.Request.Path.Value ?? string.Empty;
        if (!request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var name = request[prefix.Length..];
        if (name is "runtime.js" or "switch.css")
        {
            var bytes = name == "runtime.js" ? DocConceptSwitchRuntime.ReadRuntimeBytes()
                : DocConceptSwitchRuntime.ReadStylesheetBytesForCaller();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = name == "runtime.js" ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.ContentLength = bytes.Length;
            if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
            return true;
        }

        var key = name.EndsWith(".html", StringComparison.Ordinal) ? name[..^5] : string.Empty;
        var source = FindSource(key);
        if (source is null || !SourceIsReadable(source.Value.Release, source.Value.Page))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return true;
        }

        var (release, page) = source.Value;
        string? html;
        try
        {
            html = DocConceptPresentation.Choices(Title(page), release.Version, PageUrl(context, release, page),
                AppUrl(context, _urls.BuildVersionsUrl()), AssetUrl(context, "switch.css"),
                ChoiceViews(context, release, page), _limit);
            context.Response.StatusCode = StatusCodes.Status200OK;
        }
        catch (DocConceptArtifactOverflowException overflow)
        {
            AppSurfaceDocsConceptDiagnostics.Rejected(logger, "docs-switch-artifact-overflow", "choice-page", "switch-choice", _limit, overflow.ObservedBytes);
            html = Recovery(context, release, page, standalone: true);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppSurfaceDocsConceptDiagnostics.Unexpected(logger, context, exception);
            html = Recovery(context, release, page, standalone: true);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers["Content-Security-Policy"] = "sandbox allow-same-origin; default-src 'self'; script-src 'none'; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; worker-src 'none'; style-src 'self'; img-src 'self' data:; font-src 'self'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var output = Encoding.UTF8.GetBytes(html ?? string.Empty);
        context.Response.ContentLength = output.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(output, context.RequestAborted);
        return true;
    }

    /// <summary>Inserts complete bounded optional UI above a verified guide's first heading, preserving readable input on failure.</summary>
    internal string RewriteGuide(HttpContext context, AppSurfaceDocsPublishedTreeMount mount,
        string artifactPath, string html, ILogger logger)
    {
        context.Items.Remove(RuntimeAuthorizationKey);
        var release = _index.PublicAvailableReleases.FirstOrDefault(candidate => candidate.IsPublic && candidate.IsAvailable
            && string.Equals(_urls.BuildVersionRootUrl(candidate.Version), mount.CanonicalRootPath, StringComparison.OrdinalIgnoreCase));
        if (release is null) return html;
        var pages = release.Pages.Where(page => page.IsServeable && string.Equals(page.ArtifactPath?.Split('#')[0], artifactPath, StringComparison.Ordinal)).ToArray();
        if (pages.Length != 1) return html;
        var page = pages[0];
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        var title = document.QuerySelector("h1");
        if (title?.ParentElement is null || document.Head is null) return html;
        try
        {
            if (document.QuerySelector("[data-docs-version-switch], [data-docs-version-data], [data-docs-switch-runtime]") is not null)
                throw new InvalidDataException("The original shell collides with package-owned switch markup.");
            var view = GuideView(context, release, page, mount.MountRootPath);
            var incomingKey = context.Request.Query.TryGetValue("docsFrom", out var keys) && keys.Count == 1 ? keys[0] : null;
            var markup = DocConceptPresentation.Guide(view, DocConceptSwitchRuntime.RuntimeSubresourceIntegrity, _limit, incomingKey);
            Insert(parser, document.Head, markup.Head, null);
            Insert(parser, title.ParentElement, markup.Body, title);
            context.Items[RuntimeAuthorizationKey] = true;
            return document.DocumentElement.OuterHtml;
        }
        catch (DocConceptArtifactOverflowException overflow)
        {
            AppSurfaceDocsConceptDiagnostics.Rejected(logger, "docs-switch-artifact-overflow", "guide", "switch-fragment", _limit, overflow.ObservedBytes);
        }
        catch (InvalidDataException)
        {
            AppSurfaceDocsConceptDiagnostics.Rejected(logger, "docs-switch-runtime-invalid", "guide", "switch-shell", _limit);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppSurfaceDocsConceptDiagnostics.Unexpected(logger, context, exception);
        }

        var recovery = Recovery(context, release, page, standalone: false);
        if (recovery is null) return html;
        Insert(parser, title.ParentElement, recovery, title);
        return document.DocumentElement.OuterHtml;
    }

    /// <summary>Checks a request-local authorization marker set only after trusted projection completed.</summary>
    internal static bool IsRuntimeAuthorized(HttpContext context) => context.Items.ContainsKey(RuntimeAuthorizationKey);

    private DocConceptGuideView GuideView(HttpContext context, DocConceptReleaseSnapshot release, DocConceptCanonicalPage page, string mountRoot)
    {
        var identity = Identity(release, page);
        var key = DocConceptIdentityIndex.CreateSourceKey(_instance, identity);
        var choices = new List<DocConceptChoiceLink>();
        foreach (var version in _versions.Where(version => !string.Equals(version.Version, release.Version, StringComparison.Ordinal)))
        {
            var resolution = _index.Resolve(identity, version.Version, version.ReleaseManifestSha256 ?? string.Empty);
            var href = resolution.TargetRelease is { } target && resolution.TargetPage is { } counterpart
                ? WithContext(PageUrl(context, target, counterpart), key)
                : ChoiceUrl(context, key) + "#" + DocConceptPresentation.ChoiceAnchor(version.Version);
            choices.Add(new DocConceptChoiceLink(version.Version, version.Label, href, resolution.TargetPage?.Anchors));
        }
        var destination = Destination(release, page);
        var incoming = _index.GetIncoming(identity, _index.EnumeratePublicPages().Select(item => item.Identity), _instance).Select(row =>
        {
            var sourceRelease = _index.PublicAvailableReleases.Single(item => item.Version == row.SourceIdentity.Version
                && item.ReleaseManifestSha256 == row.SourceIdentity.ReleaseManifestSha256);
            return new DocConceptIncomingView(row.SourceKey, row.Reason.ToString(), Notice(row.Reason, release.Version),
                PageUrl(context, sourceRelease, row.SourcePage), sourceRelease.Version, Title(row.SourcePage), destination);
        });
        return new DocConceptGuideView(destination, Title(page), key, ChoiceUrl(context, key),
            AppUrl(context, _urls.BuildVersionsUrl()), AssetUrl(context, "runtime.js"), AssetUrl(context, "switch.css"), choices, incoming,
            AppUrl(context, DocsUrlBuilder.BuildDocUrl(mountRoot, page.ArtifactPath!)));
    }

    private IEnumerable<DocConceptChoiceView> ChoiceViews(HttpContext context, DocConceptReleaseSnapshot source, DocConceptCanonicalPage page)
    {
        var identity = Identity(source, page);
        var key = DocConceptIdentityIndex.CreateSourceKey(_instance, identity);
        foreach (var version in _versions.Where(version => !string.Equals(version.Version, source.Version, StringComparison.Ordinal)))
        {
            var resolution = _index.Resolve(identity, version.Version, version.ReleaseManifestSha256 ?? string.Empty);
            var actions = new List<DocConceptAction>();
            var target = resolution.TargetRelease;
            if (target is not null)
            {
                if (resolution.TargetPage is { } counterpart)
                    actions.Add(new DocConceptAction("Open this guide", WithContext(PageUrl(context, target, counterpart), key)));
                else
                {
                    if (resolution.FallbackSection is { } section)
                        actions.Add(new DocConceptAction("Browse this section", PageUrl(context, target, section)));
                    if (resolution.SearchAvailable)
                        actions.Add(new DocConceptAction("Search this version", AppUrl(context, _urls.BuildVersionDocUrl(target.Version, "search"))));
                    if (resolution.HomeRoutePath is { } home && target.Pages.FirstOrDefault(item => item.RoutePath == home && item.Locale == page.Locale) is { } homePage)
                        actions.Add(new DocConceptAction("Documentation home", PageUrl(context, target, homePage)));
                }
            }
            var reason = resolution.Reason;
            yield return new DocConceptChoiceView(version.Version, page.Locale ?? "Default language", Heading(reason),
                Explanation(reason, version.Version), actions, PageUrl(context, source, page));
        }
    }

    private (DocConceptReleaseSnapshot Release, DocConceptCanonicalPage Page)? FindSource(string key)
    {
        if (key.Length != 64 || !key.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f')) return null;
        foreach (var release in _index.PublicAvailableReleases.Where(release => release.IsPublic && release.IsAvailable))
            foreach (var page in release.Pages.Where(page => page.IsServeable))
                if (string.Equals(DocConceptIdentityIndex.CreateSourceKey(_instance, Identity(release, page)), key, StringComparison.Ordinal)) return (release, page);
        return null;
    }

    private bool SourceIsReadable(DocConceptReleaseSnapshot release, DocConceptCanonicalPage page)
    {
        var version = _versions.FirstOrDefault(item => item.Version == release.Version
            && string.Equals(item.ReleaseManifestSha256, release.ReleaseManifestSha256, StringComparison.OrdinalIgnoreCase));
        var path = page.ArtifactPath?.Split('#')[0];
        if (version?.ExactTreePath is null || path is null
            || version.VerifiedReleaseArchive?.TryGetFile(path, out var file) != true) return false;
        try
        {
            _ = AppSurfaceDocsConceptFileReader.Read(version.ExactTreePath, path, file.Sha256, _limit, file.Length);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    private string? Recovery(HttpContext context, DocConceptReleaseSnapshot release, DocConceptCanonicalPage page, bool standalone) =>
        DocConceptPresentation.Recovery(AppUrl(context, _urls.BuildVersionsUrl()), PageUrl(context, release, page), _limit, standalone);

    private DocConceptDestination Destination(DocConceptReleaseSnapshot release, DocConceptCanonicalPage page) =>
        new(_instance, release.Version, release.ReleaseManifestSha256, page.Locale, page.RoutePath);

    private static DocConceptPageIdentity Identity(DocConceptReleaseSnapshot release, DocConceptCanonicalPage page) =>
        new(release.Version, release.ReleaseManifestSha256, page.RoutePath, page.Locale, page.LocaleIsKnown);

    private string PageUrl(HttpContext context, DocConceptReleaseSnapshot release, DocConceptCanonicalPage page)
    {
        if (page.ArtifactPath is null || !page.IsServeable) throw new InvalidDataException("The destination has no verified physical artifact.");
        return AppUrl(context, DocsUrlBuilder.BuildDocUrl(_urls.BuildVersionRootUrl(release.Version), page.ArtifactPath));
    }

    private string AssetUrl(HttpContext context, string name) => AppUrl(context, DocsUrlBuilder.JoinPath(_urls.RouteRootPath, "versions/switch/" + name));
    private string ChoiceUrl(HttpContext context, string key) => AssetUrl(context, key + ".html");
    private static string AppUrl(HttpContext context, string path) => context.Request.PathBase.Add(new PathString(path)).Value!;
    private static string WithContext(string path, string key)
    {
        var parts = path.Split('#', 2);
        return parts[0] + (parts[0].Contains('?') ? "&" : "?") + "docsFrom=" + key
            + (parts.Length == 2 ? "#" + parts[1] : string.Empty);
    }

    private static void Insert(HtmlParser parser, IElement parent, string fragment, INode? before)
    {
        foreach (var node in parser.ParseFragment(fragment, parent).ToArray()) parent.InsertBefore(node, before);
    }

    private static string Notice(DocConceptResolutionReason reason, string version) => reason switch
    {
        DocConceptResolutionReason.MovedPage => $"This guide moved in {version}. You are reading the same documented concept.",
        DocConceptResolutionReason.RenamedPage => $"This guide has a different title in {version}. You are reading the same documented concept.",
        _ => string.Empty
    };

    private static string Title(DocConceptCanonicalPage page) =>
        page.Title.Length > 0 ? page.Title : page.RoutePath.Length > 0 ? page.RoutePath : "Documentation";

    private static string Heading(DocConceptResolutionReason reason) => reason switch
    {
        DocConceptResolutionReason.SamePage => "This guide is available",
        DocConceptResolutionReason.MovedPage => "This guide moved",
        DocConceptResolutionReason.RenamedPage => "This guide has a different title",
        DocConceptResolutionReason.KnownAbsent => "No documented counterpart",
        DocConceptResolutionReason.TargetUnavailable => "This version is unavailable",
        DocConceptResolutionReason.NoSelectedLocaleDocs => "No documentation in this language",
        _ => "No verified counterpart"
    };

    private static string Explanation(DocConceptResolutionReason reason, string version) => reason switch
    {
        DocConceptResolutionReason.SamePage => $"Open the same guide in {version}.",
        DocConceptResolutionReason.MovedPage => $"The same documented concept is at a different location in {version}.",
        DocConceptResolutionReason.RenamedPage => $"The same documented concept uses a different title in {version}.",
        DocConceptResolutionReason.KnownAbsent => $"There is no documented counterpart for this guide in {version}. This does not establish whether the feature exists in that version.",
        DocConceptResolutionReason.TargetUnavailable => "This version cannot currently be read. Choose another version or return to the original guide.",
        DocConceptResolutionReason.NoSelectedLocaleDocs => "This version has no verified documentation in the selected language. Your language has not been changed.",
        _ => "Available identity information cannot establish a counterpart. You can browse this version using the verified recovery actions below."
    };
}
