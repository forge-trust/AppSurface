using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.ReleaseContracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsConceptSwitchingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docs-concepts-host-" + Guid.NewGuid().ToString("N"));
    private const string Html = "<!doctype html><html><head><title>Guide</title></head><body><main><h1>Guide</h1><p>Readable guide content.</p></main></body></html>";
    private static readonly string SourcePin = new('a', 64);
    private static readonly string TargetPin = new('b', 64);

    [Fact]
    public async Task Choices_RequireVerifiedSourceAndUsePathBaseAndPhysicalMappings()
    {
        var fixture = Create();
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        var context = Context("/docs/versions/switch/" + key + ".html", "/base");
        Assert.True(await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance));
        Assert.Equal(200, context.Response.StatusCode);
        var html = Body(context);
        Assert.Contains("This guide moved", html);
        Assert.Contains("/base/docs/v/2/export/new.html?docsFrom=" + key, html);
        Assert.Contains("/base/docs/v/1/export/old.html", html);
        Assert.DoesNotContain("hidden-version", html);
        Assert.Contains("script-src 'none'", context.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.False(await fixture.Adapter.TryHandleAsync(Context("/docs/versions/elsewhere"), NullLogger.Instance));
        foreach (var invalid in new[] { new string('0', 64), "bad", key.ToUpperInvariant(), "../index" })
        {
            var missing = Context("/docs/versions/switch/" + invalid + ".html");
            Assert.True(await fixture.Adapter.TryHandleAsync(missing, NullLogger.Instance));
            Assert.Equal(404, missing.Response.StatusCode);
            Assert.Empty(Body(missing));
        }

        File.WriteAllText(Path.Combine(_root, "1/export/old.html"), Html + "tampered");
        var changed = Context("/docs/versions/switch/" + key + ".html");
        await fixture.Adapter.TryHandleAsync(changed, NullLogger.Instance);
        Assert.Equal(404, changed.Response.StatusCode);
    }

    [Theory]
    [InlineData("runtime.js", "text/javascript; charset=utf-8")]
    [InlineData("switch.css", "text/css; charset=utf-8")]
    public async Task PackageAssets_ServeExactBytesAndHeadWithoutBody(string name, string contentType)
    {
        var fixture = Create();
        var context = Context("/docs/versions/switch/" + name);
        Assert.True(await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance));
        var expected = name == "runtime.js" ? DocConceptSwitchRuntime.ReadRuntimeBytes() : DocConceptSwitchRuntime.ReadStylesheetBytesForCaller();
        Assert.Equal(expected, ((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal(contentType, context.Response.ContentType);
        var head = Context("/docs/versions/switch/" + name);
        head.Request.Method = "HEAD";
        await fixture.Adapter.TryHandleAsync(head, NullLogger.Instance);
        Assert.Equal(expected.Length, head.Response.ContentLength);
        Assert.Empty(Body(head));
    }

    [Theory]
    [InlineData("runtime.js")]
    [InlineData("switch.css")]
    [InlineData("choices")]
    public async Task ResponseWrite_AwaitsAsynchronousTransportAndHeadNeverWrites(string resource)
    {
        var fixture = Create();
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        var path = "/docs/versions/switch/" + (resource == "choices" ? key + ".html" : resource);
        var context = Context(path);
        await using var transport = new YieldingWriteStream();
        context.Response.Body = transport;

        var response = fixture.Adapter.TryHandleAsync(context, NullLogger.Instance);
        try
        {
            await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(response.IsCompleted);
        }
        finally
        {
            transport.ReleaseWrites();
        }
        Assert.True(await response);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(transport.Writes > 0);
        Assert.Equal(context.Response.ContentLength, transport.Length);
        if (resource == "choices")
        {
            Assert.Contains("This guide moved", Body(context));
        }
        else
        {
            var expected = resource == "runtime.js" ? DocConceptSwitchRuntime.ReadRuntimeBytes()
                : DocConceptSwitchRuntime.ReadStylesheetBytesForCaller();
            Assert.Equal(expected, transport.ToArray());
        }

        var head = Context(path);
        head.Request.Method = "HEAD";
        await using var headTransport = new YieldingWriteStream();
        head.Response.Body = headTransport;
        Assert.True(await fixture.Adapter.TryHandleAsync(head, NullLogger.Instance));
        Assert.Equal(context.Response.ContentLength, head.Response.ContentLength);
        Assert.Equal(0, headTransport.Writes);
        Assert.Equal(0, headTransport.Length);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("/docs/versions/switch/", true)]
    [InlineData("/docs/versions/switch/missing", true)]
    public async Task MissingRequestPathOrChoiceExtensionCannotResolveASource(string? path, bool handled)
    {
        var fixture = Create();
        var context = Context(path ?? string.Empty);
        if (path is null) context.Request.Path = default;
        Assert.Equal(handled, await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance));
        if (handled) Assert.Equal(404, context.Response.StatusCode);
        Assert.Empty(Body(context));
    }

    [Fact]
    public void Guide_OnlyValidatedIncomingKeyClaimsAMoveAndEncodesItsReturn()
    {
        var fixture = Create();
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        using var provider = new PhysicalFileProvider(Path.Combine(_root, "2"));
        var mount = new AppSurfaceDocsPublishedTreeMount("/docs/v/2", provider);
        var context = Context("/docs/v/2/export/new.html", "/base");
        context.Request.QueryString = new QueryString("?docsFrom=" + key);
        var html = fixture.Adapter.RewriteGuide(context, mount, "export/new.html", Html, NullLogger.Instance);
        var document = new HtmlParser().ParseDocument(html);
        Assert.NotNull(document.QuerySelector("[data-docs-version-notice]"));
        Assert.Contains("same documented concept", document.QuerySelector("[data-docs-version-notice]")!.TextContent);
        Assert.Contains("/base/docs/v/1/export/old.html", html);
        Assert.Equal(DocConceptSwitchRuntime.RuntimeSubresourceIntegrity, document.QuerySelector("script[data-docs-switch-runtime]")!.GetAttribute("integrity"));
        Assert.True(html.IndexOf("data-docs-version-notice", StringComparison.Ordinal) < html.IndexOf("<h1", StringComparison.Ordinal));
        foreach (var query in new[] { "?docsFrom=" + new string('0', 64), "?docsFrom=" + key + "&docsFrom=" + key, "?notice=unsafe&returnUrl=https://evil.example" })
        {
            context.Request.QueryString = new QueryString(query);
            var neutral = fixture.Adapter.RewriteGuide(context, mount, "export/new.html", Html, NullLogger.Instance);
            Assert.Null(new HtmlParser().ParseDocument(neutral).QuerySelector("[data-docs-version-notice]"));
        }
        Assert.Equal(Html, fixture.Adapter.RewriteGuide(context, mount, "not-indexed.html", Html, NullLogger.Instance));
        Assert.Equal("<p>No shell</p>", fixture.Adapter.RewriteGuide(context, mount, "export/new.html", "<p>No shell</p>", NullLogger.Instance));
    }

    [Theory]
    [InlineData(800, true)]
    [InlineData(1, false)]
    public async Task Overflow_KeepsGuideAndReturnsBounded503Choice(int limit, bool recoveryFits)
    {
        var fixture = Create(limit);
        using var provider = new PhysicalFileProvider(Path.Combine(_root, "1"));
        var mount = new AppSurfaceDocsPublishedTreeMount("/docs/v/1", provider);
        var html = fixture.Adapter.RewriteGuide(Context("/docs/v/1/export/old.html"), mount, "export/old.html", Html, NullLogger.Instance);
        Assert.Contains("Readable guide content.", html);
        Assert.DoesNotContain("data-docs-switch-runtime", html);
        Assert.Equal(recoveryFits, html.Contains("data-docs-version-unavailable", StringComparison.Ordinal));
        if (limit > Encoding.UTF8.GetByteCount(Html))
        {
            var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
            var context = Context("/docs/versions/switch/" + key + ".html");
            await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance);
            Assert.Equal(503, context.Response.StatusCode);
            Assert.True(Encoding.UTF8.GetByteCount(Body(context)) <= limit);
            Assert.Contains("Version archive", Body(context));
        }
    }

    [Fact]
    public async Task CanonicalFragment_IsRetainedAfterTransitionQueryAndPhysicalVerification()
    {
        var fixture = Create(canonicalFragment: true);
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        var context = Context("/docs/versions/switch/" + key + ".html");
        await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Contains("/docs/v/2/export/new.html?docsFrom=" + key + "#intro", Body(context));
        using var provider = new PhysicalFileProvider(Path.Combine(_root, "2"));
        var mount = new AppSurfaceDocsPublishedTreeMount("/docs/v/2", provider);
        var html = fixture.Adapter.RewriteGuide(Context("/docs/v/2/export/new.html"), mount, "export/new.html", Html, NullLogger.Instance);
        Assert.Contains("data-docs-version-anchor-ids", html);
        Assert.Contains("#intro", html);
    }

    [Fact]
    public async Task StandaloneRecovery_WhenNothingFitsReturnsEmpty503()
    {
        var fixture = Create(1, htmlContents: "x");
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        var context = Context("/docs/versions/switch/" + key + ".html");
        Assert.True(await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance));
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(0, context.Response.ContentLength);
        Assert.Empty(Body(context));
        Assert.Contains("connect-src 'none'", context.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public async Task ChoiceHead_AndMissingOriginal_DoNotWritePageContent()
    {
        var fixture = Create();
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        var context = Context("/docs/versions/switch/" + key + ".html");
        context.Request.Method = "HEAD";
        await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(context.Response.ContentLength > 0);
        Assert.Empty(Body(context));
        File.Delete(Path.Combine(_root, "1/export/old.html"));
        var missing = Context("/docs/versions/switch/" + key + ".html");
        await fixture.Adapter.TryHandleAsync(missing, NullLogger.Instance);
        Assert.Equal(404, missing.Response.StatusCode);
    }

    [Fact]
    public async Task PublishedHandler_UsesExactRuntimeCspAndPreservesGuide()
    {
        var fixture = Create();
        using var provider = new PhysicalFileProvider(Path.Combine(_root, "2"));
        var version = fixture.Versions[1];
        var mount = new AppSurfaceDocsPublishedTreeMount("/docs/v/2", provider, version.ExactTreePath,
            archiveVerificationState: AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
            verifiedReleaseArchive: version.VerifiedReleaseArchive);
        var handler = new AppSurfaceDocsPublishedTreeHandler([mount], "/docs/next", conceptSwitching: fixture.Adapter);
        var context = Context("/docs/v/2/export/new.html");
        Assert.True(await handler.TryHandleAsync(context));
        Assert.Equal(200, context.Response.StatusCode);
        var csp = context.Response.Headers.ContentSecurityPolicy.ToString();
        Assert.Contains("sandbox allow-same-origin allow-scripts", csp);
        Assert.Contains("script-src '" + DocConceptSwitchRuntime.RuntimeCspHashSource + "'", csp);
        Assert.Contains("connect-src 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Contains("Readable guide content.", Body(context));
    }

    [Fact]
    public void UnsupportedShellAndUnrelatedMountKeepGuideReadable()
    {
        var fixture = Create();
        using var provider = new PhysicalFileProvider(Path.Combine(_root, "2"));
        var mount = new AppSurfaceDocsPublishedTreeMount("/docs/v/2", provider);
        var collision = Html.Replace("<main>", "<main><span data-docs-version-switch>Existing shell</span>", StringComparison.Ordinal);
        var rejected = fixture.Adapter.RewriteGuide(Context("/docs/v/2/export/new.html"), mount, "export/new.html", collision, NullLogger.Instance);
        Assert.Contains("Readable guide content.", rejected);
        Assert.Contains("data-docs-version-unavailable", rejected);
        Assert.DoesNotContain("data-docs-switch-runtime", rejected);
        var unrelated = new AppSurfaceDocsPublishedTreeMount("/other/v/2", provider);
        Assert.Equal(Html, fixture.Adapter.RewriteGuide(Context("/other/v/2/export/new.html"), unrelated, "export/new.html", Html, NullLogger.Instance));
    }

    [Fact]
    public void UnexpectedQueryFeatureFailurePreservesGuideWithoutAuthorizingTheRuntime()
    {
        var fixture = Create();
        using var provider = new PhysicalFileProvider(Path.Combine(_root, "2"));
        var mount = new AppSurfaceDocsPublishedTreeMount("/docs/v/2", provider);
        var context = Context("/docs/v/2/export/new.html");
        context.Features.Set<IQueryFeature>(new FailingQueryFeature());

        var result = fixture.Adapter.RewriteGuide(context, mount, "export/new.html", Html, NullLogger.Instance);

        Assert.Contains("Readable guide content.", result);
        Assert.Contains("Version switching is unavailable for this guide.", result);
        Assert.DoesNotContain("secret-query-value", result);
        Assert.DoesNotContain("data-docs-switch-runtime", result);
        Assert.False(AppSurfaceDocsConceptSwitching.IsRuntimeAuthorized(context));
    }

    [Fact]
    public async Task UnexpectedChoiceProjectionFailureReturnsVerifiedGenericRecovery()
    {
        var fixture = Create();
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        var context = Context("/docs/versions/switch/" + key + ".html");
        context.Features.Set<IHttpRequestFeature>(new FailingPathBaseFeature
        {
            Method = context.Request.Method,
            Path = context.Request.Path,
            Scheme = context.Request.Scheme,
            QueryString = string.Empty
        });

        Assert.True(await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance));

        Assert.Equal(503, context.Response.StatusCode);
        var result = Body(context);
        Assert.Contains("Version switching is unavailable for this guide.", result);
        Assert.Contains("/docs/versions", result);
        Assert.Contains("/docs/v/1/export/old.html", result);
        Assert.DoesNotContain("secret-path-base", result);
        Assert.DoesNotContain("This guide moved", result);
        Assert.Contains("script-src 'none'", context.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Theory]
    [InlineData("guides/setup", "guides/setup")]
    [InlineData("", "Documentation")]
    public async Task LegacyCanonicalPageWithoutTitleUsesAnHonestFallbackLabel(string route, string label)
    {
        var fixture = Create();
        var release = new DocConceptReleaseInput("1", SourcePin, true, true, true, false, null,
            [new DocConceptCanonicalRouteFact(route, "export/old.html", null, true)], null);
        var index = DocConceptIdentityIndex.BuildCombined([release], [], DocConceptValidationMode.Strict, 4194304).Index;
        var adapter = new AppSurfaceDocsConceptSwitching(index, fixture.Versions,
            new DocsUrlBuilder(new AppSurfaceDocsOptions { Versioning = new AppSurfaceDocsVersioningOptions { Enabled = true } }),
            "Default", 4194304);
        var identity = new DocConceptPageIdentity("1", SourcePin, route, null, true);
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", identity);
        var context = Context("/docs/versions/switch/" + key + ".html");

        Assert.True(await adapter.TryHandleAsync(context, NullLogger.Instance));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Contains("Choose a version of " + label, Body(context));
        Assert.Contains("/docs/v/1/export/old.html", Body(context));
        Assert.DoesNotContain("This guide moved", Body(context));
    }

    [Fact]
    public async Task UnavailableAndNoLocaleChoicesNeverManufactureDestinationsOrExposeHiddenReleases()
    {
        var fixture = Create();
        var input = new DocConceptReleaseInput("1", SourcePin, true, true, true, false, null,
            [new DocConceptCanonicalRouteFact("guides/setup", "export/old.html", null, true)],
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "appsurface-docs-concepts-v1",
                identityCoverage = "complete",
                pages = new[]
            { new { canonicalRoutePath = "guides/setup", locale = (string?)null, conceptId = "setup", title = "Setup", anchorIds = Array.Empty<string>() } }
            }));
        var index = DocConceptIdentityIndex.BuildCombined([input], [], DocConceptValidationMode.Strict, 4194304).Index;
        var unavailable = fixture.Versions[1] with { IsAvailable = false };
        var hidden = fixture.Versions[1] with { Version = "hidden-version", Visibility = AppSurfaceDocsVersionVisibility.Hidden };
        var adapter = new AppSurfaceDocsConceptSwitching(index, [fixture.Versions[0], unavailable, hidden],
            new DocsUrlBuilder(new AppSurfaceDocsOptions { Versioning = new AppSurfaceDocsVersioningOptions { Enabled = true } }), "Default", 4194304);
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        var context = Context("/docs/versions/switch/" + key + ".html");
        await adapter.TryHandleAsync(context, NullLogger.Instance);
        Assert.Contains("This version is unavailable", Body(context));
        Assert.DoesNotContain("hidden-version", Body(context));
        Assert.DoesNotContain("/docs/v/2/", Body(context));
    }

    [Fact]
    public async Task ArchivedForgedRuntimeMarkerCannotAuthorizeThePackagedScript()
    {
        var fixture = Create();
        var html = Html.Replace("</head>", "<script data-docs-switch-runtime defer src=\"/docs/versions/switch/runtime.js\" integrity=\"" + DocConceptSwitchRuntime.RuntimeSubresourceIntegrity + "\"></script></head>", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_root, "2/export/new.html"), html);
        using var provider = new PhysicalFileProvider(Path.Combine(_root, "2"));
        var bytes = Encoding.UTF8.GetBytes(html);
        var archive = new AppSurfaceDocsVerifiedReleaseArchive(new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>
        {
            ["export/new.html"] = new("export/new.html", bytes.Length, "text/html", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
        }, AppSurfaceDocsFrozenRouteManifest.Empty);
        var mount = new AppSurfaceDocsPublishedTreeMount("/docs/v/2", provider, Path.Combine(_root, "2"),
            archiveVerificationState: AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified, verifiedReleaseArchive: archive);
        var handler = new AppSurfaceDocsPublishedTreeHandler([mount], "/docs/next", conceptSwitching: fixture.Adapter);
        var context = Context("/docs/v/2/export/new.html");
        Assert.True(await handler.TryHandleAsync(context));
        Assert.Equal(200, context.Response.StatusCode);
        var csp = context.Response.Headers.ContentSecurityPolicy.ToString();
        Assert.Contains("script-src 'none'", csp);
        Assert.DoesNotContain(DocConceptSwitchRuntime.RuntimeCspHashSource, csp);
        Assert.DoesNotContain("allow-scripts", csp);
        Assert.Contains("Readable guide content.", Body(context));
        Assert.Contains("data-docs-version-unavailable", Body(context));
    }

    [Fact]
    public void IncomingProjection_IsReEnumeratedFromSnapshotAndUnknownKeysNeverCreateCachedRows()
    {
        var fixture = Create();
        using var provider = new PhysicalFileProvider(Path.Combine(_root, "2"));
        var mount = new AppSurfaceDocsPublishedTreeMount("/docs/v/2", provider);
        for (var index = 0; index < 5; index++)
        {
            var context = Context("/docs/v/2/export/new.html");
            context.Request.QueryString = new QueryString("?docsFrom=" + index.ToString("x64"));
            var html = fixture.Adapter.RewriteGuide(context, mount, "export/new.html", Html, NullLogger.Instance);
            var document = new HtmlParser().ParseDocument(html);
            using var data = JsonDocument.Parse(document.QuerySelector("[data-docs-version-data]")!.TextContent);
            Assert.Equal(1, data.RootElement.GetProperty("incoming").GetArrayLength());
            Assert.Equal(DocConceptIdentityIndex.CreateSourceKey("Default", Source()), data.RootElement.GetProperty("incoming")[0].GetProperty("sourceKey").GetString());
            Assert.Null(document.QuerySelector("[data-docs-version-notice]"));
        }
    }

    [Theory]
    [InlineData("setup", "guides/setup", "Setup", null, "This guide is available", false)]
    [InlineData("setup", "guides/setup", "Updated setup", null, "This guide has a different title", true)]
    [InlineData("other", "other", "Other", null, "No documented counterpart", false)]
    [InlineData(null, "other", "Other", null, "No verified counterpart", false)]
    [InlineData("setup", "autre", "Autre", "fr", "No documentation in this language", false)]
    public async Task Choices_ExplainEachEvidenceStateAndOnlyMatchedTitleChangesGetNotices(string? concept, string route, string title, string? locale, string expected, bool notice)
    {
        var fixture = Create(targetConcept: concept, targetRoute: route, targetTitle: title, targetLocale: locale);
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        var context = Context("/docs/versions/switch/" + key + ".html");
        await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance);
        Assert.Contains(expected, Body(context));
        if (expected == "No documented counterpart") Assert.Contains("does not establish whether the feature exists", Body(context));
        using var provider = new PhysicalFileProvider(Path.Combine(_root, "2"));
        var mount = new AppSurfaceDocsPublishedTreeMount("/docs/v/2", provider);
        var destination = Context("/docs/v/2/export/new.html");
        destination.Request.QueryString = new QueryString("?docsFrom=" + key);
        var output = fixture.Adapter.RewriteGuide(destination, mount, "export/new.html", Html, NullLogger.Instance);
        Assert.Equal(notice, new HtmlParser().ParseDocument(output).QuerySelector("[data-docs-version-notice]") is not null);
    }

    [Theory]
    [InlineData("guides", true, "Browse this section")]
    [InlineData("", false, "Documentation home")]
    public async Task Recovery_UsesVerifiedSectionOrHomeAndTargetLocalSearch(string route, bool landing, string expectedAction)
    {
        var fixture = Create(targetConcept: "other", targetRoute: route, targetLanding: landing, targetSearch: true);
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", Source());
        var context = Context("/docs/versions/switch/" + key + ".html");
        await fixture.Adapter.TryHandleAsync(context, NullLogger.Instance);
        var output = Body(context);
        Assert.Contains(expectedAction, output);
        Assert.Contains("/docs/v/2/search", output);
        Assert.Contains("/docs/v/2/export/new.html", output);
        Assert.Contains("/docs/v/1/export/old.html", output);
    }

    private (AppSurfaceDocsConceptSwitching Adapter, AppSurfaceDocsResolvedVersion[] Versions) Create(int limit = 4194304, bool canonicalFragment = false, string htmlContents = Html, string? targetConcept = "setup", string targetRoute = "guides/configuration", string targetTitle = "Configuration", string? targetLocale = null, bool targetLanding = false, bool targetSearch = false)
    {
        var inputs = new List<DocConceptReleaseInput>();
        var versions = new List<AppSurfaceDocsResolvedVersion>();
        foreach (var (version, pin, route, artifact, title) in new[]
        {
            ("1", SourcePin, "guides/setup", "export/old.html", "Setup"),
            ("2", TargetPin, targetRoute, "export/new.html", targetTitle)
        })
        {
            var root = Path.Combine(_root, version);
            Directory.CreateDirectory(Path.Combine(root, "export"));
            File.WriteAllText(Path.Combine(root, artifact), htmlContents);
            var bytes = Encoding.UTF8.GetBytes(htmlContents);
            var files = new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>
            {
                [artifact] = new(artifact, bytes.Length, "text/html", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
            };
            var archive = new AppSurfaceDocsVerifiedReleaseArchive(files, AppSurfaceDocsFrozenRouteManifest.Empty);
            versions.Add(new AppSurfaceDocsResolvedVersion(version, "Version " + version, null, root, "/docs/v/" + version,
                AppSurfaceDocsVersionSupportState.Current, AppSurfaceDocsVersionVisibility.Public, AppSurfaceDocsVersionAdvisoryState.None,
                true, null, pin, AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified, archive));
            var locale = version == "1" ? null : targetLocale;
            var concept = version == "1" ? "setup" : targetConcept;
            var index = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "appsurface-docs-concepts-v1",
                identityCoverage = concept is null ? "partial" : "complete",
                pages = new[] { new { canonicalRoutePath = route, locale, conceptId = concept, title, anchorIds = new[] { "intro" }, sectionLanding = version == "2" && targetLanding } }
            });
            if (targetSearch && version == "2") File.WriteAllText(Path.Combine(root, "search.html"), "<h1>Search</h1>");
            inputs.Add(new DocConceptReleaseInput(version, pin, true, true, true, targetSearch && version == "2", route.Length == 0 ? "" : null,
                [new DocConceptCanonicalRouteFact(route, artifact + (canonicalFragment ? "#intro" : ""), locale, true)], index));
        }
        var result = DocConceptIdentityIndex.BuildCombined(inputs, [], DocConceptValidationMode.Strict, 4194304);
        Assert.True(result.IsValid, string.Join(";", result.Issues.Select(issue => issue.Detail)));
        var urls = new DocsUrlBuilder(new AppSurfaceDocsOptions { Versioning = new AppSurfaceDocsVersioningOptions { Enabled = true } });
        return (new AppSurfaceDocsConceptSwitching(result.Index, versions.ToArray(), urls, "Default", limit), versions.ToArray());
    }

    private static DocConceptPageIdentity Source() => new("1", SourcePin, "guides/setup", null, true);
    private static DefaultHttpContext Context(string path, string pathBase = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.PathBase = pathBase;
        context.Response.Body = new MemoryStream();
        return context;
    }
    private static string Body(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class YieldingWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WriteStarted => _writeStarted.Task;
        internal int Writes { get; private set; }
        internal void ReleaseWrites() => _releaseWrites.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult();
            await _releaseWrites.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await base.WriteAsync(buffer, cancellationToken);
            Writes++;
        }
    }

    private sealed class FailingQueryFeature : IQueryFeature
    {
        public IQueryCollection Query
        {
            get => throw new InvalidOperationException("secret-query-value");
            set => throw new NotSupportedException();
        }
    }

    private sealed class FailingPathBaseFeature : HttpRequestFeature, IHttpRequestFeature
    {
        private bool _failed;

        string IHttpRequestFeature.PathBase
        {
            get
            {
                if (!_failed)
                {
                    _failed = true;
                    throw new InvalidOperationException("secret-path-base");
                }
                return base.PathBase;
            }
            set => base.PathBase = value;
        }
    }
}
