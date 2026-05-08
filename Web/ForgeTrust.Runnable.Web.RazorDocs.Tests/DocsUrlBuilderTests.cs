using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public sealed class DocsUrlBuilderTests
{
    [Fact]
    public void Constructor_ShouldDefaultDocsRootsFromVersioningState()
    {
        var disabledBuilder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = null!
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = false
                }
            });
        var enabledBuilder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = null!
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            });

        Assert.Equal("/docs", disabledBuilder.CurrentDocsRootPath);
        Assert.Equal("/docs", disabledBuilder.RouteRootPath);
        Assert.Equal("/docs", disabledBuilder.DocsEntryRootPath);
        Assert.Equal("/docs/versions", disabledBuilder.DocsVersionsRootPath);
        Assert.Equal("/docs/search", disabledBuilder.Routes.Search);
        Assert.Equal("/docs/search-index.json?refresh=1", disabledBuilder.Routes.SearchIndexRefresh);
        Assert.Equal("/docs/versions", disabledBuilder.Routes.Versions);
        Assert.Equal("/docs", enabledBuilder.RouteRootPath);
        Assert.Equal("/docs/next", enabledBuilder.CurrentDocsRootPath);
        Assert.Equal("/docs/versions", enabledBuilder.BuildVersionsUrl());
        Assert.Equal("/docs/v/1.2.3", enabledBuilder.BuildVersionRootUrl("1.2.3"));
    }

    [Fact]
    public void Constructor_ShouldDefaultLiveRootFromCustomRouteRoot()
    {
        var disabledBuilder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    RouteRootPath = "/foo/bar"
                }
            });
        var enabledBuilder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    RouteRootPath = "/foo/bar"
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            });

        Assert.Equal("/foo/bar", disabledBuilder.RouteRootPath);
        Assert.Equal("/foo/bar", disabledBuilder.CurrentDocsRootPath);
        Assert.Equal("/foo/bar/search", disabledBuilder.Routes.Search);
        Assert.Equal("/foo/bar/versions", disabledBuilder.Routes.Versions);
        Assert.Equal("/foo/bar", enabledBuilder.RouteRootPath);
        Assert.Equal("/foo/bar/next", enabledBuilder.CurrentDocsRootPath);
        Assert.Equal("/foo/bar/versions", enabledBuilder.BuildVersionsUrl());
        Assert.Equal("/foo/bar/v/1.2.3", enabledBuilder.BuildVersionRootUrl("1.2.3"));
    }

    [Fact]
    public void Constructor_ShouldNotInferRouteRootFromConfiguredVersionedDocsRoot()
    {
        var builder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/foo/bar/next"
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            });

        Assert.Equal("/docs", builder.RouteRootPath);
        Assert.Equal("/foo/bar/next", builder.CurrentDocsRootPath);
        Assert.Equal("/docs/versions", builder.Routes.Versions);
        Assert.Equal("/docs/v/1.2.3", builder.BuildVersionRootUrl("1.2.3"));
    }

    [Fact]
    public void Constructor_ShouldSupportRootRouteFamilyWithVersioning()
    {
        var builder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    RouteRootPath = "/"
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            });

        Assert.Equal("/", builder.RouteRootPath);
        Assert.Equal("/next", builder.CurrentDocsRootPath);
        Assert.Equal("/versions", builder.BuildVersionsUrl());
        Assert.Equal("/v/1.2.3", builder.BuildVersionRootUrl("1.2.3"));
        Assert.Equal("/next/search-index.json?refresh=1", builder.Routes.SearchIndexRefresh);
    }

    [Fact]
    public void Constructor_ShouldTrimTrailingSlashFromConfiguredDocsRootPath()
    {
        var builder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = " /docs/preview/ "
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            });

        Assert.Equal("/docs/preview", builder.CurrentDocsRootPath);
    }

    [Fact]
    public void Constructor_ShouldNormalizeRelativeConfiguredDocsRootPathToAppRelativePath()
    {
        var builder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "docs/custom-preview"
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            });

        Assert.Equal("/docs/custom-preview", builder.CurrentDocsRootPath);
    }

    [Fact]
    public void Constructor_ShouldTreatMissingVersioningAsDisabled_AndPreserveAlreadyNormalizedRoot()
    {
        var builder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/docs/preview"
                },
                Versioning = null!
            });

        Assert.False(builder.VersioningEnabled);
        Assert.Equal("/docs/preview", builder.CurrentDocsRootPath);
    }

    [Fact]
    public void BuildVersionUrls_ShouldEncodeVersionAndCanonicalPath()
    {
        var builder = new DocsUrlBuilder(new RazorDocsOptions());

        var versionRoot = builder.BuildVersionRootUrl(" release/1 ");
        var versionDoc = builder.BuildVersionDocUrl(" release/1 ", "guides/Getting Started#install now");

        Assert.Equal("/docs/v/release%2F1", versionRoot);
        Assert.Equal("/docs/v/release%2F1/guides/Getting%20Started#install%20now", versionDoc);
    }

    [Fact]
    public void Builder_ShouldHandleRootMountedDocsSurfaceWithoutDoubleSlashes()
    {
        var builder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/"
                }
            });

        Assert.Equal("/", builder.BuildHomeUrl());
        Assert.Equal("/search", builder.BuildSearchUrl());
        Assert.Equal("/search-index.json", builder.BuildSearchIndexUrl());
        Assert.Equal("/search.css", builder.BuildAssetUrl("search.css"));
        Assert.Equal("/guides/start.md", builder.BuildDocUrl("guides/start.md"));
        Assert.Equal("/sections/concepts", builder.BuildSectionUrl(DocPublicSection.Concepts));
        Assert.True(builder.IsCurrentDocsPath("/guides/start.md.html"));
        Assert.True(builder.IsCurrentDocsPath("/search"));
        Assert.True(builder.IsCurrentDocsPath("/Namespaces/ForgeTrust.Runnable.Web.html"));
        Assert.False(builder.IsCurrentDocsPath("/privacy.html"));
        Assert.False(builder.IsCurrentDocsPath("guides/start.md.html"));
    }

    [Fact]
    public void IsCurrentDocsPath_ShouldMatchPathsUnderConfiguredRoot()
    {
        var builder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/docs/next"
                }
            });

        Assert.True(builder.IsCurrentDocsPath("/docs/next"));
        Assert.True(builder.IsCurrentDocsPath("/docs/next/guides/start.md.html"));
        Assert.True(builder.IsCurrentDocsPath("/DOCS/NEXT/search"));
        Assert.False(builder.IsCurrentDocsPath("/docs"));
        Assert.False(builder.IsCurrentDocsPath("/docs/v/1.0"));
        Assert.False(builder.IsCurrentDocsPath(null));
    }

    [Theory]
    [InlineData("/docs", "", "/docs")]
    [InlineData("/docs", null, "/docs")]
    [InlineData("/docs", " ", "/docs")]
    [InlineData("/docs", "\t", "/docs")]
    [InlineData("/", "", "/")]
    [InlineData("/", null, "/")]
    [InlineData("/", " ", "/")]
    [InlineData("/", "\t", "/")]
    public void BuildDocUrl_ShouldReturnDocsRoot_WhenRelativePathIsBlank(string docsRootPath, string? relativePath, string expected)
    {
        var builder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = docsRootPath
                }
            });

        var href = builder.BuildDocUrl(relativePath!);

        Assert.Equal(expected, href);
    }

    [Theory]
    [InlineData("/docs", "", "/docs")]
    [InlineData("/docs", null, "/docs")]
    [InlineData("/docs", " ", "/docs")]
    [InlineData("/docs", "\t", "/docs")]
    [InlineData("/", "", "/")]
    [InlineData("/", null, "/")]
    [InlineData("/", " ", "/")]
    [InlineData("/", "\t", "/")]
    public void JoinPath_ShouldReturnDocsRoot_WhenRelativePathIsBlank(string docsRootPath, string? relativePath, string expected)
    {
        var href = DocsUrlBuilder.JoinPath(docsRootPath, relativePath!);

        Assert.Equal(expected, href);
    }
}
