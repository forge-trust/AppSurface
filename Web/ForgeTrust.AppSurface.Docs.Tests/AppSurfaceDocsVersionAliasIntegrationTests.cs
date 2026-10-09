using System.Net;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsVersionAliasIntegrationTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "appsurface-alias-integration", Guid.NewGuid().ToString("N"));

    public AppSurfaceDocsVersionAliasIntegrationTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false, "/docs", "")]
    [InlineData(true, "/docs", "")]
    [InlineData(true, "/guide/product", "/base")]
    [InlineData(true, "/", "")]
    public async Task Aliases_ServeOwnContentSearchCanonicalAndFrozenRedirect(bool named, string family, string pathBase)
    {
        var catalog = Catalog();
        await using var app = BuildHost(catalog, named, family, pathBase);
        await app.StartAsync();
        using var client = Client(app);
        var root = pathBase + Join(family, "a/stable");
        var page = await client.GetAsync(root + "/guide");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("stable-page", html);
        Assert.Contains(root + "/search", html);
        Assert.Contains(pathBase + Join(family, "v/1.0/guide"), html);
        Assert.DoesNotContain("preview-page", html);
        var search = await client.GetStringAsync(root + "/search-index.json");
        Assert.Contains(root + "/guide#part", search);
        var redirect = await client.GetAsync(root + "/guide.md?source=test");
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal(root + "/guide?source=test", redirect.Headers.Location!.OriginalString);
        Assert.Equal("no-store", redirect.Headers.CacheControl!.ToString());
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, root + "/guide"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        var asset = await client.GetAsync(root + "/search.css");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("no-store", asset.Headers.CacheControl!.ToString());
        var exact = await client.GetStringAsync(pathBase + Join(family, "v/1.0/guide"));
        Assert.Contains("stable-page", exact);
        var archive = await client.GetStringAsync(pathBase + Join(family, "versions"));
        Assert.Contains("Named release entries", archive);
        Assert.Contains("Open live source docs", archive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedUnknownHiddenMissingUnsafeAndUnsupportedRequestsNeverFallThrough(bool named)
    {
        await using var app = BuildHost(Catalog(), named, "/docs", "");
        await app.StartAsync();
        using var client = Client(app);
        foreach (var path in new[] { "/docs/a", "/docs/a/missing", "/docs/a/hidden/guide", "/docs/a/stable/only-preview" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Documentation page not found", body);
            Assert.Contains("Browse documentation versions", body);
            Assert.Contains("href=\"/docs/versions\"", body);
            // Regression: recovery must leave the content island and update browser history.
            // Found by /review on2026-10-09; report .gstack/qa-reports/issue176-review-20261009.
            var recoveryDocument = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(body);
            Assert.Equal("_top", recoveryDocument.QuerySelector("a[href='/docs/versions']")!.GetAttribute("data-turbo-frame"));
            Assert.DoesNotContain("host-fallback", body);
            Assert.DoesNotContain("preview-page", body);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
            Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            Assert.Equal("no-store", head.Headers.CacheControl!.ToString());
        }

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options })
        {
            var response = await client.SendAsync(new HttpRequestMessage(method, "/docs/a/stable/guide"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal(new[] { "GET", "HEAD" }, response.Content.Headers.Allow);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        }

        var unsafeResponse = await RawRequest(app, "/docs/a/stable/../guide", "GET", authenticated: false);
        Assert.Contains("404", unsafeResponse.Split('\n')[0]);
        Assert.Contains("Documentation page not found", unsafeResponse);
        Assert.DoesNotContain("host-fallback", unsafeResponse);
        var unsafePost = await RawRequest(app, "/docs/a/stable/../guide", "POST", authenticated: false);
        Assert.Contains("405", unsafePost.Split('\n')[0]);
        var apple = await client.GetStringAsync("/docs/apple");
        Assert.Equal("host-fallback", apple);
    }

    [Fact]
    public async Task NamedRawOwnershipKeepsEveryHostPolicyAndDoesNotWriteRecoveryBeforeAuthorization()
    {
        await using var app = BuildHost(Catalog(), true, "/docs", "/base", protect: true);
        await app.StartAsync();
        using var client = Client(app);
        foreach (var path in new[] { "/base/docs/a/stable/guide", "/base/docs/a/unknown", "/base/docs/a/stable/%73ecret" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.DoesNotContain("Documentation page not found", await response.Content.ReadAsStringAsync());
        }

        var unsafeResponse = await RawRequest(app, "/base/docs/a/stable/../guide", "GET", authenticated: false);
        Assert.Contains("401", unsafeResponse.Split('\n')[0]);
        Assert.DoesNotContain("Documentation page not found", unsafeResponse);
        client.DefaultRequestHeaders.Add("X-Test-Auth", "yes");
        var success = await client.GetAsync("/base/docs/a/stable/guide");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var authorizedUnsafe = await RawRequest(app, "/base/docs/a/stable/../guide", "GET", authenticated: true);
        Assert.Contains("404", authorizedUnsafe.Split('\n')[0]);
        Assert.Contains("Documentation page not found", authorizedUnsafe);
    }

    [Fact]
    public void NamedActiveAliasesRequireHookAndInactiveCompositionDoesNot()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BuildHost(Catalog(), true, "/docs", "", installHook: false));
        Assert.Contains("ASDOCSALIAS011", error.Message);
        using var inactive = BuildHost(Catalog(includeAliases: false), true, "/docs", "", installHook: false);
    }

    [Fact]
    public void HookInstalledOnAnotherBuilderDoesNotSatisfyNamedFinalization()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BuildHost(Catalog(), true, "/docs", "", hookOnOtherBuilder: true));
        Assert.Contains("ASDOCSALIAS011", error.Message);
    }

    [Fact]
    public void MissingHookDiagnosticIdentifiesTheValidatedNamedInstance()
    {
        using var diagnostics = new AliasDiagnosticLoggerProvider();
        Assert.Throws<InvalidOperationException>(() => BuildHost(Catalog(), true, "/docs", "", installHook: false, diagnostics: diagnostics));
        var entry = Assert.Single(diagnostics.Entries, entry => Equals(entry["AliasDiagnosticCode"], "ASDOCSALIAS011"));
        Assert.Equal("Public", entry["InstanceName"]);
        Assert.Equal("missing-pipeline-hook", entry["AliasReason"]);
        Assert.Contains("UseAppSurfaceDocsAliases", entry["AliasAction"]!.ToString());
        Assert.EndsWith("#asdocsalias011", entry["DocumentationReference"]!.ToString());
    }

    [Fact]
    public async Task ActualEndpointPreservesRolesSchemesCustomMetadataAndSingleConventionMaterialization()
    {
        var materializations = new Dictionary<string, int>();
        await using var app = BuildHost(Catalog(), true, "/docs", "", protect: true, conventions: endpoints =>
        {
            endpoints.RequireAuthorization(new AuthorizeAttribute { Roles = "reader", AuthenticationSchemes = "alternate" });
            endpoints.WithMetadata(new AliasHostMetadata("expected"));
            endpoints.Add(builder =>
            {
                if (builder is RouteEndpointBuilder route && route.RoutePattern.RawText == "/docs/a/{**path}")
                {
                    materializations[builder.DisplayName!] = materializations.GetValueOrDefault(builder.DisplayName!) + 1;
                }
            });
        });
        await app.StartAsync();
        using var client = Client(app);
        var owner = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints),
            endpoint => endpoint.Metadata.GetMetadata<AppSurfaceDocsAliasOwnershipMetadata>() is not null);
        Assert.Equal("Public", owner.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>()!.Name);
        Assert.Equal("expected", owner.Metadata.GetMetadata<AliasHostMetadata>()!.Value);
        Assert.Contains(owner.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Roles == "reader" && data.AuthenticationSchemes == "alternate");
        Assert.Equal(1, Assert.Single(materializations).Value);

        foreach (var path in new[] { "/docs/a/stable/guide", "/docs/a/unknown", "/docs/a/stable/%73ecret" })
        {
            var unauthenticated = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            Assert.Equal("no-store", unauthenticated.Headers.CacheControl!.ToString());
            client.DefaultRequestHeaders.Add("X-Test-Auth", "no-role");
            client.DefaultRequestHeaders.Add("X-Test-Scheme", "alternate");
            var forbidden = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal("no-store", forbidden.Headers.CacheControl!.ToString());
            Assert.DoesNotContain("Documentation page not found", await forbidden.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Remove("X-Test-Auth");
            client.DefaultRequestHeaders.Remove("X-Test-Scheme");
        }

        client.DefaultRequestHeaders.Add("X-Test-Auth", "yes");
        var wrongScheme = await client.GetAsync("/docs/a/stable/guide");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongScheme.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Scheme", "alternate");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/docs/a/stable/guide")).StatusCode);
        Assert.Equal(1, Assert.Single(materializations).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DefaultAndFallbackPoliciesRunOnHealthyUnknownUnsafeAndZeroMountRequests(bool zeroMounts, bool useDefaultPolicy)
    {
        var catalog = Catalog();
        if (zeroMounts)
        {
            File.WriteAllText(catalog, "{\"versions\":[],\"aliases\":[{\"name\":\"stable\",\"version\":\"missing\"}]}");
        }

        await using var app = BuildHost(catalog, true, "/docs", "", conventions: useDefaultPolicy ? endpoints => endpoints.RequireAuthorization() : null, authorization: options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireClaim("second", "yes").Build();
            options.FallbackPolicy = useDefaultPolicy ? null : options.DefaultPolicy;
        });
        await app.StartAsync();
        using var client = Client(app);
        foreach (var path in new[] { "/docs/a/stable/guide", "/docs/a/unknown", "/docs/a/stable/%73ecret" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-Auth", "yes");
            var authorized = await client.GetAsync(path);
            Assert.Equal(!zeroMounts && path.EndsWith("/guide", StringComparison.Ordinal)
                ? HttpStatusCode.OK : HttpStatusCode.NotFound, authorized.StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Auth");
        }
    }

    [Fact]
    public async Task AllowAnonymousRetainsNormalHostSemanticsAndOwnedCacheContract()
    {
        await using var app = BuildHost(Catalog(), true, "/docs", "", protect: true,
            conventions: endpoints => endpoints.AllowAnonymous());
        await app.StartAsync();
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/docs/a/stable/guide")).StatusCode);
        var response = await client.GetAsync("/docs/a/unknown");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task NormalizedRawOwnerBypassesStaticFilesAndAnotherNamedInstance()
    {
        var catalog = Catalog();
        var secondCatalog = Path.Join(_root, "second-catalog.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(catalog))!;
        json["aliases"]![0]!["version"] = "2.0-preview";
        File.WriteAllText(secondCatalog, json.ToJsonString());
        var staticRoot = Path.Join(_root, "static");
        Directory.CreateDirectory(Path.Join(staticRoot, "docs"));
        File.WriteAllText(Path.Join(staticRoot, "docs", "guide.html"), "static-host-secret");
        await using var app = BuildHost(catalog, true, "/docs", "", protect: true,
            staticRoot: staticRoot, secondCatalog: secondCatalog);
        await app.StartAsync();
        using var client = Client(app);
        Assert.Contains("preview-page", await client.GetStringAsync("/other/a/stable/guide"));
        foreach (var target in new[] { "/docs/a/stable/../../guide.html", "/docs/a/stable/../../../other/a/stable/guide" })
        {
            var anonymous = await RawRequest(app, target, "GET", authenticated: false);
            Assert.Contains("401", anonymous.Split('\n')[0]);
            Assert.DoesNotContain("static-host-secret", anonymous);
            Assert.DoesNotContain("preview-page", anonymous);
            var authorized = await RawRequest(app, target, "GET", authenticated: true);
            Assert.Contains("404", authorized.Split('\n')[0]);
            Assert.Contains("Documentation page not found", authorized);
            Assert.DoesNotContain("static-host-secret", authorized);
            Assert.DoesNotContain("preview-page", authorized);
        }
    }

    [Fact]
    public async Task ZeroHealthyMountsStillOwnNamespace()
    {
        var catalog = Path.Join(_root, "broken.json");
        File.WriteAllText(catalog, "{\"versions\":[],\"aliases\":[{\"name\":\"stable\",\"version\":\"missing\"}]}");
        await using var app = BuildHost(catalog, true, "/docs", "");
        await app.StartAsync();
        using var client = Client(app);
        var response = await client.GetAsync("/docs/a/stable/guide");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Documentation page not found", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("a.html", false)]
    [InlineData("a/guide.html", false)]
    [InlineData("other.html", true)]
    public void RecommendedInventoryOrFrozenDeclaredNamespaceCollisionFailsStartup(string file, bool frozen)
    {
        var catalog = Catalog(collisionFile: file, frozenCollision: frozen);
        var exception = Assert.Throws<InvalidOperationException>(() => BuildHost(catalog, true, "/docs", ""));
        Assert.Contains("ASDOCSALIAS009", exception.Message);
    }

    [Theory]
    [InlineData("/docs/a")]
    [InlineData("/docs/a/next")]
    public void ActiveAliasesRejectOverlappingLiveRoot(string liveRoot)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BuildHost(Catalog(), true, "/docs", "", liveRoot: liveRoot));
        Assert.Contains("ASDOCSALIAS009", exception.Message);
    }

    [Fact]
    public void ExistingLiveRootValidationStillRejectsTheRouteFamilyRoot()
    {
        var exception = Assert.Throws<OptionsValidationException>(() => BuildHost(Catalog(), true, "/docs", "", liveRoot: "/docs"));
        Assert.Contains("route-family root", exception.Message);
    }

    [Fact]
    public async Task AliasRetargetRequiresRestartAndPreservesOtherLabelsAndExactReleases()
    {
        var catalog = Catalog();
        await using var first = BuildHost(catalog, true, "/docs", "");
        await first.StartAsync();
        using var firstClient = Client(first);
        var firstPage = await firstClient.GetStringAsync("/docs/a/stable/guide");
        Assert.Contains("stable-page", firstPage);
        var document = JsonSerializer.Deserialize<AppSurfaceDocsVersionCatalog>(File.ReadAllText(catalog), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        document.Aliases[0].Version = "2.0-preview";
        File.WriteAllText(catalog, JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        Assert.Contains("stable-page", await firstClient.GetStringAsync("/docs/a/stable/guide"));
        await using var second = BuildHost(catalog, true, "/docs", "");
        await second.StartAsync();
        using var secondClient = Client(second);
        Assert.Contains("preview-page", await secondClient.GetStringAsync("/docs/a/stable/guide"));
        Assert.Contains("/docs/v/2.0-preview/guide", await secondClient.GetStringAsync("/docs/a/stable/guide"));
        Assert.Contains("stable-page", await secondClient.GetStringAsync("/docs/a/v1/guide"));
        Assert.Contains("stable-page", await secondClient.GetStringAsync("/docs/v/1.0/guide"));
        Assert.Contains("preview-page", await secondClient.GetStringAsync("/docs/a/preview/guide"));
    }

    [Fact]
    public async Task CoveredMutationAndNewUnlistedFilesRefuseWithoutOtherTreeFallback()
    {
        var catalog = Catalog();
        await using var app = BuildHost(catalog, true, "/docs", "");
        await app.StartAsync();
        using var client = Client(app);
        Assert.Contains("stable-page", await client.GetStringAsync("/docs/a/stable/guide"));
        File.WriteAllText(Path.Join(_root, "stable", "guide.html"), "<html>changed release bytes</html>");
        File.WriteAllText(Path.Join(_root, "stable", "new.html"), "<html>unlisted release bytes</html>");
        File.WriteAllText(Path.Join(_root, "stable", ".appsurface-docs-route-manifest.json"),
            "{\"schema\":\"appsurface-docs-route-manifest-v1\",\"entries\":[{\"canonicalRoutePath\":\"only-preview\",\"declaredAliases\":[\"guide.md\"]}]}");
        foreach (var suffix in new[] { "guide", "new" })
        {
            var response = await client.GetAsync("/docs/a/stable/" + suffix);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Documentation page not found", body);
            Assert.DoesNotContain("changed release bytes", body);
            Assert.DoesNotContain("unlisted release bytes", body);
            Assert.DoesNotContain("preview-page", body);
        }

        // The frozen redirect identity remains the startup snapshot, never the modified manifest's new target.
        var redirect = await client.GetAsync("/docs/a/stable/guide.md");
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal("/docs/a/stable/guide", redirect.Headers.Location!.OriginalString);
        Assert.Equal("no-store", redirect.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task LargeArchiveHtmlRefusalUsesTheSameRecoveryAndNoHeadBody()
    {
        var catalog = Catalog();
        // The tree is verified at startup with the default 4MiB limit. Mutation must still refuse a later large body.
        await using var app = BuildHost(catalog, true, "/docs", "");
        await app.StartAsync();
        File.WriteAllText(Path.Join(_root, "stable", "guide.html"), new string('x', 5 * 1024 * 1024));
        using var client = Client(app);
        var response = await client.GetAsync("/docs/a/stable/guide");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Documentation page not found", await response.Content.ReadAsStringAsync());
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/docs/a/stable/guide"));
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(20000)]
    public async Task ManyLabelsShareTargetProvidersAndCachesAndRetainAllArchiveRows(int count)
    {
        var catalog = Catalog();
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(catalog))!.AsObject();
        var aliases = new System.Text.Json.Nodes.JsonArray();
        for (var index = 0; index < count; index++)
        {
            aliases.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = $"label-{index}", ["version"] = "1.0" });
        }
        document["aliases"] = aliases;
        File.WriteAllText(catalog, document.ToJsonString());
        await using var app = BuildHost(catalog, true, "/docs", "");
        var runtime = app.Services.GetRequiredService<AppSurfaceDocsInstanceRegistry>().GetRequiredRuntime("Public");
        var environment = app.Services.GetRequiredService<IWebHostEnvironment>();
        AppSurfaceDocsVersionCatalogService FreshCatalog() => new(runtime.Options, environment,
            NullLogger<AppSurfaceDocsVersionCatalogService>.Instance);
        _ = FreshCatalog().GetCatalog(); // One warmup including exact-tree verification, JSON parse and alias resolution.
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        var measuredCatalog = FreshCatalog();
        Assert.Equal(count, measuredCatalog.GetCatalog().Aliases.Count);
        clock.Stop();
        var resolutionMilliseconds = clock.Elapsed.TotalMilliseconds;
        var resolutionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        var controller = new DocsController(runtime.Aggregator, runtime.DocsUrlBuilder, measuredCatalog,
            runtime.FeaturedPageResolver, runtime.Options, environment, NullLogger<DocsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        _ = controller.Versions(); // One projection warmup; the catalog is already resolved.
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        clock.Restart();
        var model = Assert.IsType<AppSurfaceDocsVersionArchiveViewModel>(Assert.IsType<ViewResult>(controller.Versions()).Model);
        clock.Stop();
        var projectionMilliseconds = clock.Elapsed.TotalMilliseconds;
        var projectionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Assert.Equal(count, model.Aliases.Count);
        var (mounts, providers) = AppSurfaceDocsWebModule.BuildPublishedTreeMounts(runtime.VersionCatalogService.GetCatalog(), runtime.DocsUrlBuilder);
        try
        {
            Assert.Equal(2, providers.Count);
            var moving = mounts.Where(mount => mount.AliasName is not null).ToArray();
            Assert.Equal(count, moving.Length);
            Assert.All(moving, mount =>
            {
                Assert.Same(moving[0].FileProvider, mount.FileProvider);
                Assert.Same(moving[0].FrozenRouteManifest, mount.FrozenRouteManifest);
            });
        }
        finally
        {
            foreach (var provider in providers) provider.Dispose();
        }
        await app.StartAsync();
        using var client = Client(app);
        foreach (var index in new[] { 0, count / 2, count - 1 })
        {
            Assert.Contains("stable-page", await client.GetStringAsync($"/docs/a/label-{index}/guide"));
        }
        _ = await client.GetStringAsync("/docs/versions"); // One complete rendering + HTTP warmup.
        allocationStart = GC.GetTotalAllocatedBytes(precise: true);
        clock.Restart();
        var responseBytes = await client.GetByteArrayAsync("/docs/versions");
        clock.Stop();
        var renderingMilliseconds = clock.Elapsed.TotalMilliseconds;
        var renderingAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationStart;
        var html = Encoding.UTF8.GetString(responseBytes);
        var parsed = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var rows = parsed.QuerySelectorAll(".docs-version-alias-row");
        Assert.Equal(count, rows.Length);
        Assert.Contains("label-0", rows[0].TextContent);
        Assert.Contains($"label-{count - 1}", rows[^1].TextContent);
        File.WriteAllText(Path.Join(Path.GetTempPath(), $"issue176-scale-{count}.json"), JsonSerializer.Serialize(new
        {
            aliasCount = count,
            exactTargetCount = 2,
            aliasTargetCount = 1,
            providers = 2,
            aliasProviderIdentities = 1,
            aliasFrozenCacheIdentities = 1,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            configuration = "Release",
            fixture = "deterministic synthetic two verified trees; labels label-0 through label-N",
            measuredAtUtc = DateTimeOffset.UtcNow,
            warmup = "one fresh catalog load, one projection, one full HTTP archive render",
            resolution = new
            {
                elapsedMilliseconds = resolutionMilliseconds,
                allocatedBytes = resolutionAllocatedBytes,
                scope = "current thread; includes read, parse, exact archive verification and alias resolution"
            },
            projection = new
            {
                elapsedMilliseconds = projectionMilliseconds,
                allocatedBytes = projectionAllocatedBytes,
                scope = "current thread; public controller Versions projection with an already resolved catalog"
            },
            rendering = new
            {
                elapsedMilliseconds = renderingMilliseconds,
                allocatedBytes = renderingAllocatedBytes,
                scope = "process-wide allocations including background work; complete archive Razor render and localhost HTTP transfer",
                responseBytes = responseBytes.Length
            },
            rowCount = rows.Length,
            first = "label-0",
            last = $"label-{count - 1}",
            selectedRequests = new[] { 0, count / 2, count - 1 },
            supportedCapacityClaim = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private WebApplication BuildHost(string catalog, bool named, string family, string pathBase, bool protect = false,
        bool installHook = true, string? liveRoot = null, Action<IEndpointConventionBuilder>? conventions = null,
        Action<AuthorizationOptions>? authorization = null, string? staticRoot = null, string? secondCatalog = null,
        bool hookOnOtherBuilder = false, AliasDiagnosticLoggerProvider? diagnostics = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(AppSurfaceDocsWebModule).Assembly.FullName,
            ContentRootPath = _root
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (diagnostics is not null)
        {
            builder.Logging.AddProvider(diagnostics);
        }
        var primarySource = Path.Join(_root, "primary-source");
        Directory.CreateDirectory(primarySource);
        var values = new Dictionary<string, string?>
        {
            ["Docs:Routing:RouteRootPath"] = family,
            ["Docs:Routing:DocsRootPath"] = liveRoot ?? Join(family, "next"),
            ["Docs:Source:RepositoryRoot"] = primarySource,
            ["Docs:Versioning:Enabled"] = "true",
            ["Docs:Versioning:CatalogPath"] = catalog,
            ["Docs:Versioning:TrustedReleaseRootPath"] = _root
        };
        AppSurfaceDocsInstance? handle = null;
        AppSurfaceDocsInstance? second = null;
        if (named)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            handle = builder.Services.AddAppSurfaceDocs("Public", config.GetSection("Docs"));
            if (secondCatalog is not null)
            {
                var secondarySource = Path.Join(_root, "other-source");
                Directory.CreateDirectory(secondarySource);
                var secondaryValues = new Dictionary<string, string?>(values)
                {
                    ["Docs:Routing:RouteRootPath"] = "/other",
                    ["Docs:Routing:DocsRootPath"] = "/other/next",
                    ["Docs:Versioning:CatalogPath"] = secondCatalog,
                    ["Docs:Source:RepositoryRoot"] = secondarySource
                };
                second = builder.Services.AddAppSurfaceDocs("Other", new ConfigurationBuilder().AddInMemoryCollection(secondaryValues).Build().GetSection("Docs"));
            }
        }
        else
        {
            builder.Configuration.AddInMemoryCollection(values.ToDictionary(pair => pair.Key.Replace("Docs:", "AppSurfaceDocs:"), pair => pair.Value));
            builder.Services.AddAppSurfaceDocs();
        }

        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(AppSurfaceDocsWebModule).Assembly);
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, AliasTestAuthenticationHandler>("test", _ => { })
            .AddScheme<AuthenticationSchemeOptions, AliasTestAuthenticationHandler>("alternate", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("first", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("second", policy => policy.RequireClaim("second", "yes"));
            authorization?.Invoke(options);
        });
        var app = builder.Build();
        app.UsePathBase(pathBase);
        if (named && installHook)
        {
            var hookBuilder = hookOnOtherBuilder ? ((IApplicationBuilder)app).New() : app;
            hookBuilder.UseAppSurfaceDocsAliases();
            hookBuilder.UseAppSurfaceDocsAliases();
        }
        if (!named)
        {
            new AppSurfaceDocsWebModule().ConfigureWebApplication(new StartupContext([], new AppSurfaceDocsWebModule()), app);
        }

        app.UseStatusCodePages(context =>
        {
            context.HttpContext.Response.Headers.CacheControl = "public, max-age=100";
            return context.HttpContext.Response.WriteAsync("host-status-page");
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        if (staticRoot is not null)
        {
            var staticProvider = new PhysicalFileProvider(staticRoot);
            app.Lifetime.ApplicationStopped.Register(staticProvider.Dispose);
            app.UseStaticFiles(new StaticFileOptions { FileProvider = staticProvider });
        }
        IEndpointRouteBuilder endpoints = app;
        if (named)
        {
            var endpointConventions = handle!.MapEndpoints(endpoints);
            if (protect)
            {
                endpointConventions.RequireAuthorization("first", "second");
            }

            // All normal host conventions apply to the actual mapped endpoints exactly once.
            ConfigureConventions(endpointConventions);
            second?.MapEndpoints(endpoints).AllowAnonymous();
            endpoints.FinalizeAppSurfaceDocsInstances();
        }
        else
        {
            new AppSurfaceDocsWebModule().ConfigureEndpoints(new StartupContext([], new AppSurfaceDocsWebModule()), endpoints);
        }

        endpoints.MapFallback(context => context.Response.WriteAsync("host-fallback"));
        return app;

        void ConfigureConventions(IEndpointConventionBuilder endpointConventions) => conventions?.Invoke(endpointConventions);
    }

    private string Catalog(bool includeAliases = true, string? collisionFile = null, bool frozenCollision = false)
    {
        var stable = Tree("stable", "stable-page", false);
        var preview = Tree("preview", "preview-page", true);
        if (collisionFile is not null)
        {
            var path = Path.Join(preview, collisionFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "<html>namespace collision</html>");
        }
        if (frozenCollision)
        {
            File.WriteAllText(Path.Join(preview, ".appsurface-docs-route-manifest.json"),
                "{\"schema\":\"appsurface-docs-route-manifest-v1\",\"entries\":[{\"sourcePath\":\"guide.md\",\"canonicalRoutePath\":\"guide\",\"recoveryAliases\":[\"guide.md\"],\"declaredAliases\":[\"a/old\"]}]}");
        }
        var catalog = Path.Join(_root, "catalog.json");
        var aliases = includeAliases ? new[] { new { name = "stable", version = "1.0", visibility = "Public" },
            new { name = "preview", version = "2.0-preview", visibility = "Public" },
            new { name = "v1", version = "1.0", visibility = "Public" }, new { name = "hidden", version = "1.0", visibility = "Hidden" } } : [];
        File.WriteAllText(catalog, JsonSerializer.Serialize(new
        {
            recommendedVersion = "2.0-preview",
            versions = new[] { new { version = "1.0", exactTreePath = "stable", releaseManifestSha256 = Pin(stable) },
                new { version = "2.0-preview", exactTreePath = "preview", releaseManifestSha256 = Pin(preview) } },
            aliases
        }));
        return catalog;
    }

    private string Tree(string directory, string text, bool onlyPreview)
    {
        var root = Path.Join(_root, directory);
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "index", "search", "guide" }.Concat(onlyPreview ? new[] { "only-preview" } : []))
        {
            File.WriteAllText(Path.Join(root, name + ".html"), $"<!doctype html><html><head><link rel=\"canonical\" href=\"/docs/{name}\"></head><body><h1>{text}</h1><a href=\"/docs/search\">Search</a></body></html>");
        }
        File.WriteAllText(Path.Join(root, "search-index.json"), "{\"documents\":[{\"path\":\"/docs/guide#part\",\"title\":\"Guide\"}]}");
        File.WriteAllText(Path.Join(root, "search.css"), "body { color: black; }");
        File.WriteAllText(Path.Join(root, "search-client.js"), "window.searchReady=true;");
        File.WriteAllText(Path.Join(root, "minisearch.min.js"), "window.MiniSearch={};");
        File.WriteAllText(Path.Join(root, ".appsurface-docs-route-manifest.json"),
            "{\"schema\":\"appsurface-docs-route-manifest-v1\",\"entries\":[{\"sourcePath\":\"guide.md\",\"canonicalRoutePath\":\"guide\",\"recoveryAliases\":[\"guide.md\"],\"declaredAliases\":[]}]}");
        return root;
    }

    private static string Pin(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != AppSurfaceDocsReleaseArchiveVerifier.FileName)
            .Select(path => new
            {
                path = Path.GetRelativePath(root, path).Replace('\\', '/'),
                length = new FileInfo(path).Length,
                contentType = (string?)null,
                hashAlgorithm = "sha256",
                sha256 = Hash(File.ReadAllBytes(path))
            }).OrderBy(file => file.path).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = AppSurfaceDocsReleaseArchiveVerifier.Schema, files });
        File.WriteAllBytes(Path.Join(root, AppSurfaceDocsReleaseArchiveVerifier.FileName), bytes);
        return Hash(bytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Join(string root, string suffix) => root.TrimEnd('/') + "/" + suffix;
    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };

    private static async Task<string> RawRequest(WebApplication app, string target, string method, bool authenticated)
    {
        var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);
        await using var stream = client.GetStream();
        var auth = authenticated ? "X-Test-Auth: yes\r\n" : "";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{method} {target} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n{auth}Connection: close\r\nContent-Length: 0\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private sealed class AliasDiagnosticLoggerProvider : ILoggerProvider
    {
        internal System.Collections.Concurrent.ConcurrentQueue<IReadOnlyDictionary<string, object?>> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new AliasDiagnosticLogger(Entries);
        public void Dispose() { }

        private sealed class AliasDiagnosticLogger(System.Collections.Concurrent.ConcurrentQueue<IReadOnlyDictionary<string, object?>> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    var fields = values.ToDictionary(pair => pair.Key, pair => pair.Value);
                    if (fields.ContainsKey("AliasDiagnosticCode"))
                    {
                        entries.Enqueue(fields);
                    }
                }
            }
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class AliasTestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public AliasTestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
            UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Auth")
                || Scheme.Name == "alternate" && Request.Headers["X-Test-Scheme"] != "alternate")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            var claims = new List<Claim> { new(ClaimTypes.Name, "reader"), new("second", "yes") };
            if (Request.Headers["X-Test-Auth"] == "yes") claims.Add(new Claim(ClaimTypes.Role, "reader"));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed record AliasHostMetadata(string Value);
}
