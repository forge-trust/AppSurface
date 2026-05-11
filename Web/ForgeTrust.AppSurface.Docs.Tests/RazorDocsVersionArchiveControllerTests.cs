using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RazorDocsVersionArchiveControllerTests : IDisposable
{
    private readonly string _tempDirectory;

    public RazorDocsVersionArchiveControllerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "razordocs-version-archive-controller-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Versions_ShouldReturnArchiveViewModel_WhenVersioningIsEnabled()
    {
        var versionTree = CreateExactTree("1.0.0");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.0.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.0.0",
                        ExactTreePath = versionTree,
                        SupportState = RazorDocsVersionSupportState.Current
                    }
                ]
            });
        var controller = CreateController(catalogPath);

        var result = controller.Versions();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<RazorDocsVersionArchiveViewModel>(view.Model);
        Assert.Equal("Documentation versions", model.Heading);
        Assert.Equal("/docs/next", model.PreviewHref);
        var version = Assert.Single(model.Versions);
        Assert.Equal("/docs/v/1.0.0", version.Href);
        Assert.True(version.IsRecommended);
        Assert.True(version.IsAvailable);
    }

    [Fact]
    public void VersionEntry_ShouldRenderFallbackArchive_WhenRecommendedVersionIsUnavailable()
    {
        var brokenTree = Path.Combine(_tempDirectory, "broken");
        Directory.CreateDirectory(brokenTree);
        File.WriteAllText(Path.Combine(brokenTree, "index.html"), "<html>broken</html>");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "2.0.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = brokenTree,
                        SupportState = RazorDocsVersionSupportState.Current
                    }
                ]
            });
        var controller = CreateController(catalogPath);

        var result = controller.VersionEntry();

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Versions", view.ViewName);
        var model = Assert.IsType<RazorDocsVersionArchiveViewModel>(view.Model);
        Assert.Contains("No healthy recommended release tree", model.AvailabilityMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/docs/next", model.PreviewHref);
        Assert.Single(model.Versions);
        Assert.False(model.Versions[0].IsAvailable);
        Assert.DoesNotContain(_tempDirectory, model.Versions[0].AvailabilityMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionEntry_ShouldRenderPathBaseAwareAvailabilityMessage_WhenRecommendedVersionIsUnavailable()
    {
        var brokenTree = Path.Combine(_tempDirectory, "broken-path-base");
        Directory.CreateDirectory(brokenTree);
        File.WriteAllText(Path.Combine(brokenTree, "index.html"), "<html>broken</html>");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "2.0.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = brokenTree,
                        SupportState = RazorDocsVersionSupportState.Current
                    }
                ]
            });
        var controller = CreateController(catalogPath, pathBase: "/some-base");

        var result = controller.VersionEntry();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<RazorDocsVersionArchiveViewModel>(view.Model);
        Assert.Contains("/some-base/docs", model.AvailabilityMessage, StringComparison.Ordinal);
        Assert.Equal("/docs/next", model.PreviewHref);
        Assert.Equal("/docs/versions", model.VersionsHref);
    }

    [Fact]
    public void Versions_ShouldKeepArchiveHrefsAppRelative_ForViewPathBaseHandling()
    {
        var versionTree = CreateExactTree("2.1.0");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "2.1.0",
                        ExactTreePath = versionTree,
                        SupportState = RazorDocsVersionSupportState.Current
                    }
                ]
            });
        var controller = CreateController(catalogPath, pathBase: "/some-base");

        var result = controller.Versions();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<RazorDocsVersionArchiveViewModel>(view.Model);
        Assert.Equal("/docs/next", model.PreviewHref);
        Assert.Equal("/docs/versions", model.VersionsHref);
        var version = Assert.Single(model.Versions);
        Assert.Equal("/docs/v/2.1.0", version.Href);
    }

    [Fact]
    public void VersionEntry_ShouldRedirectToLiveHome_WhenVersioningIsDisabled()
    {
        var controller = CreateController(catalogPath: null, versioningEnabled: false, docsRootPath: "/docs");

        var result = controller.VersionEntry();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/docs", redirect.Url);
    }

    [Fact]
    public void Versions_ShouldRedirectToLiveHome_WhenVersioningIsDisabled()
    {
        var controller = CreateController(catalogPath: null, versioningEnabled: false, docsRootPath: "/docs");

        var result = controller.Versions();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/docs", redirect.Url);
    }

    [Fact]
    public void Versions_ShouldPreserveCatalogOrder()
    {
        var firstTree = CreateExactTree("1.10.0");
        var secondTree = CreateExactTree("1.2.0");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.10.0",
                        ExactTreePath = firstTree,
                        SupportState = RazorDocsVersionSupportState.Maintained
                    },
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = secondTree,
                        SupportState = RazorDocsVersionSupportState.Current
                    }
                ]
            });
        var controller = CreateController(catalogPath);

        var result = controller.Versions();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<RazorDocsVersionArchiveViewModel>(view.Model);
        Assert.Collection(
            model.Versions,
            first => Assert.Equal("1.10.0", first.Version),
            second => Assert.Equal("1.2.0", second.Version));
    }

    [Fact]
    public void Versions_ShouldSurfaceKnownSupportAndAdvisoryLabels_AndSkipInvalidCatalogEntries()
    {
        var deprecatedTree = CreateExactTree("1.1.0");
        var archivedTree = CreateExactTree("1.0.0");
        var customTree = CreateExactTree("0.9.0");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.1.0",
                        ExactTreePath = deprecatedTree,
                        SupportState = RazorDocsVersionSupportState.Deprecated,
                        AdvisoryState = RazorDocsVersionAdvisoryState.Vulnerable
                    },
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.0.0",
                        ExactTreePath = archivedTree,
                        SupportState = RazorDocsVersionSupportState.Archived,
                        AdvisoryState = RazorDocsVersionAdvisoryState.SecurityRisk
                    },
                    new RazorDocsPublishedVersion
                    {
                        Version = "0.9.0",
                        ExactTreePath = customTree,
                        SupportState = (RazorDocsVersionSupportState)999,
                        AdvisoryState = (RazorDocsVersionAdvisoryState)999
                    }
                ]
            });
        var controller = CreateController(catalogPath);

        var result = controller.Versions();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<RazorDocsVersionArchiveViewModel>(view.Model);
        Assert.Collection(
            model.Versions,
            deprecated =>
            {
                Assert.Equal("Deprecated", deprecated.SupportStateLabel);
                Assert.Equal("Vulnerable", deprecated.AdvisoryLabel);
            },
            archived =>
            {
                Assert.Equal("Archived", archived.SupportStateLabel);
                Assert.Equal("Security risk", archived.AdvisoryLabel);
            });
        Assert.DoesNotContain(model.Versions, version => version.Version == "0.9.0");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private DocsController CreateController(
        string? catalogPath,
        bool versioningEnabled = true,
        string docsRootPath = "/docs/next",
        string pathBase = "")
    {
        var docsOptions = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = docsRootPath
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = versioningEnabled,
                CatalogPath = catalogPath
            }
        };
        var docsUrlBuilder = new DocsUrlBuilder(docsOptions);
        var aggregatorLogger = A.Fake<ILogger<DocAggregator>>();
        var aggregator = new DocAggregator(
            Array.Empty<IDocHarvester>(),
            docsOptions,
            new TestWebHostEnvironment { ContentRootPath = _tempDirectory, WebRootPath = _tempDirectory },
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new PassthroughSanitizer(),
            aggregatorLogger);
        var versionCatalogService = new RazorDocsVersionCatalogService(
            docsOptions,
            new TestWebHostEnvironment { ContentRootPath = _tempDirectory, WebRootPath = _tempDirectory },
            NullLogger<RazorDocsVersionCatalogService>.Instance);

        return new DocsController(
            aggregator,
            docsUrlBuilder,
            versionCatalogService,
            NullLogger<DocsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request =
                    {
                        PathBase = pathBase
                    }
                }
            }
        };
    }

    private string CreateExactTree(string version)
    {
        var root = Path.Combine(_tempDirectory, version);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<html>ok</html>");
        File.WriteAllText(Path.Combine(root, "search.html"), "<html>search</html>");
        File.WriteAllText(Path.Combine(root, "search-index.json"), "{\"documents\":[]}");
        File.WriteAllText(Path.Combine(root, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Combine(root, "search-client.js"), "window.__searchClientLoaded = true;");
        File.WriteAllText(Path.Combine(root, "outline-client.js"), "window.__outlineClientLoaded = true;");
        File.WriteAllText(Path.Combine(root, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");
        return root;
    }

    private string WriteCatalog(RazorDocsVersionCatalog catalog)
    {
        var path = Path.Combine(_tempDirectory, "catalog.json");
        File.WriteAllText(path, JsonSerializer.Serialize(catalog));
        return path;
    }

    private sealed class PassthroughSanitizer : IRazorDocsHtmlSanitizer
    {
        public string Sanitize(string html)
        {
            return html;
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "RazorDocsTests";

        public IFileProvider WebRootFileProvider { get; set; } = null!;

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
