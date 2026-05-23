using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocsUrlBuilderTests
{
    [Fact]
    public void Constructor_ShouldDefaultDocsRootsFromVersioningState()
    {
        var disabledBuilder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = null!
                },
                Versioning = new AppSurfaceDocsVersioningOptions
                {
                    Enabled = false
                }
            });
        var enabledBuilder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = null!
                },
                Versioning = new AppSurfaceDocsVersioningOptions
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
        Assert.Equal("/docs/_health", disabledBuilder.Routes.Health);
        Assert.Equal("/docs/_health.json", disabledBuilder.Routes.HealthJson);
        Assert.Equal("/docs/_routes", disabledBuilder.Routes.RouteInspector);
        Assert.Equal("/docs/_routes.json", disabledBuilder.Routes.RouteInspectorJson);
        Assert.Equal("/docs/versions", disabledBuilder.Routes.Versions);
        Assert.Equal("/docs", enabledBuilder.RouteRootPath);
        Assert.Equal("/docs/next", enabledBuilder.CurrentDocsRootPath);
        Assert.Equal("/docs/versions", enabledBuilder.BuildVersionsUrl());
        Assert.Equal("/docs/v/1.2.3", enabledBuilder.BuildVersionRootUrl("1.2.3"));
    }

    [Fact]
    public void Constructor_ShouldDefaultRoots_WhenRoutingOptionsAreMissing()
    {
        var builder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = null!
            });

        Assert.Equal("/docs", builder.RouteRootPath);
        Assert.Equal("/docs", builder.CurrentDocsRootPath);
        Assert.Equal("/docs/v", builder.DocsVersionPrefixPath);
    }

    [Fact]
    public void Constructor_ShouldDefaultLiveRootFromCustomRouteRoot()
    {
        var disabledBuilder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    RouteRootPath = "/foo/bar"
                }
            });
        var enabledBuilder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    RouteRootPath = "/foo/bar"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            });

        Assert.Equal("/foo/bar", disabledBuilder.RouteRootPath);
        Assert.Equal("/foo/bar", disabledBuilder.CurrentDocsRootPath);
        Assert.Equal("/foo/bar/search", disabledBuilder.Routes.Search);
        Assert.Equal("/foo/bar/_health", disabledBuilder.BuildHealthUrl());
        Assert.Equal("/foo/bar/_health.json", disabledBuilder.BuildHealthJsonUrl());
        Assert.Equal("/foo/bar/_routes", disabledBuilder.BuildRouteInspectorUrl());
        Assert.Equal("/foo/bar/_routes.json", disabledBuilder.BuildRouteInspectorJsonUrl());
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
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/foo/bar/next"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
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
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    RouteRootPath = "/"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
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
        Assert.Equal("/next/_health", builder.Routes.Health);
        Assert.Equal("/next/_health.json", builder.Routes.HealthJson);
        Assert.Equal("/next/_routes", builder.Routes.RouteInspector);
        Assert.Equal("/next/_routes.json", builder.Routes.RouteInspectorJson);
    }

    [Fact]
    public void Routes_ShouldSupportInitOnlyNamedConstruction()
    {
        var routes = new AppSurfaceDocsRouteReferences
        {
            Home = "/docs",
            Search = "/docs/search",
            SearchIndex = "/docs/search-index.json",
            SearchIndexRefresh = "/docs/search-index.json?refresh=1",
            Versions = "/docs/versions",
            Health = "/docs/_health",
            HealthJson = "/docs/_health.json",
            RouteInspector = "/docs/_routes",
            RouteInspectorJson = "/docs/_routes.json"
        };

        Assert.Equal("/docs", routes.Home);
        Assert.Equal("/docs/_health", routes.Health);
        Assert.Equal("/docs/_health.json", routes.HealthJson);
        Assert.Equal("/docs/_routes", routes.RouteInspector);
        Assert.Equal("/docs/_routes.json", routes.RouteInspectorJson);
    }

    [Fact]
    public void Routes_ShouldPreserveConstructorAndDeconstructionCompatibility()
    {
        var routes = new AppSurfaceDocsRouteReferences(
            "/docs",
            "/docs/search",
            "/docs/search-index.json",
            "/docs/search-index.json?refresh=1",
            "/docs/versions");

        var (home, search, searchIndex, searchIndexRefresh, versions) = routes;

        Assert.Equal("/docs", home);
        Assert.Equal("/docs/search", search);
        Assert.Equal("/docs/search-index.json", searchIndex);
        Assert.Equal("/docs/search-index.json?refresh=1", searchIndexRefresh);
        Assert.Equal("/docs/versions", versions);
        Assert.Equal(string.Empty, routes.Health);
        Assert.Equal(string.Empty, routes.HealthJson);
        Assert.Equal(string.Empty, routes.RouteInspector);
        Assert.Equal(string.Empty, routes.RouteInspectorJson);
    }

    [Fact]
    public void Routes_ShouldRoundTripHealthRoutes_ForFullConstructorAndDeconstruct()
    {
        var routes = new AppSurfaceDocsRouteReferences(
            "/docs",
            "/docs/search",
            "/docs/search-index.json",
            "/docs/search-index.json?refresh=1",
            "/docs/versions",
            "/docs/_health",
            "/docs/_health.json");

        var (home, search, searchIndex, searchIndexRefresh, versions, health, healthJson) = routes;

        Assert.Equal("/docs", home);
        Assert.Equal("/docs/search", search);
        Assert.Equal("/docs/search-index.json", searchIndex);
        Assert.Equal("/docs/search-index.json?refresh=1", searchIndexRefresh);
        Assert.Equal("/docs/versions", versions);
        Assert.Equal("/docs/_health", health);
        Assert.Equal("/docs/_health.json", healthJson);
    }

    [Fact]
    public void Routes_ShouldRoundTripDiagnosticsRoutes_ForFullConstructorAndDeconstruct()
    {
        var routes = new AppSurfaceDocsRouteReferences(
            "/docs",
            "/docs/search",
            "/docs/search-index.json",
            "/docs/search-index.json?refresh=1",
            "/docs/versions",
            "/docs/_health",
            "/docs/_health.json",
            "/docs/_routes",
            "/docs/_routes.json");

        var (home, search, searchIndex, searchIndexRefresh, versions, health, healthJson, routeInspector, routeInspectorJson) = routes;

        Assert.Equal("/docs", home);
        Assert.Equal("/docs/search", search);
        Assert.Equal("/docs/search-index.json", searchIndex);
        Assert.Equal("/docs/search-index.json?refresh=1", searchIndexRefresh);
        Assert.Equal("/docs/versions", versions);
        Assert.Equal("/docs/_health", health);
        Assert.Equal("/docs/_health.json", healthJson);
        Assert.Equal("/docs/_routes", routeInspector);
        Assert.Equal("/docs/_routes.json", routeInspectorJson);
    }

    [Fact]
    public void Constructor_ShouldTrimTrailingSlashFromConfiguredDocsRootPath()
    {
        var builder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = " /docs/preview/ "
                },
                Versioning = new AppSurfaceDocsVersioningOptions
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
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "docs/custom-preview"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
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
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
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
        var builder = new DocsUrlBuilder(new AppSurfaceDocsOptions());

        var versionRoot = builder.BuildVersionRootUrl(" release/1 ");
        var versionDoc = builder.BuildVersionDocUrl(" release/1 ", "guides/Getting Started#install now");

        Assert.Equal("/docs/v/release%2F1", versionRoot);
        Assert.Equal("/docs/v/release%2F1/guides/Getting%20Started#install%20now", versionDoc);
    }

    [Fact]
    public void BuildCanonicalHref_ShouldPreserveAppRelativeCanonicalUrl_WhenPublicOriginIsUnset()
    {
        var builder = new DocsUrlBuilder(new AppSurfaceDocsOptions());

        Assert.Null(builder.PublicOrigin);
        Assert.Equal("/docs/guides/intro", builder.BuildCanonicalHref("/docs/guides/intro"));
    }

    [Fact]
    public void BuildCanonicalHref_ShouldJoinConfiguredPublicOrigin()
    {
        var builder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    PublicOrigin = " https://forge-trust.com/ "
                }
            });

        Assert.Equal("https://forge-trust.com", builder.PublicOrigin);
        Assert.Equal(
            "https://forge-trust.com/docs/guides/intro",
            builder.BuildCanonicalHref("/docs/guides/intro"));
    }

    [Fact]
    public void BuildCanonicalHref_ShouldPreserveRootMountedAndVersionedRouteIdentity()
    {
        var builder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    RouteRootPath = "/",
                    PublicOrigin = "https://docs.example.com"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = "catalog.json"
                }
            });

        Assert.Equal("https://docs.example.com/install", builder.BuildCanonicalHref("/install"));
        Assert.Equal(
            "https://docs.example.com/v/0.1/install",
            builder.BuildCanonicalHref(builder.BuildVersionDocUrl("0.1", "install")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("docs/guides/intro")]
    [InlineData("//docs.example.com/guides/intro")]
    [InlineData("https://docs.example.com/docs/guides/intro")]
    public void BuildCanonicalHref_ShouldRejectNonAppRelativeCanonicalInput(string canonicalUrl)
    {
        var builder = new DocsUrlBuilder(new AppSurfaceDocsOptions());

        Assert.ThrowsAny<ArgumentException>(() => builder.BuildCanonicalHref(canonicalUrl));
    }

    [Theory]
    [InlineData("https://forge-trust.com/docs")]
    [InlineData("https://forge-trust.com?x=1")]
    [InlineData("https://forge-trust.com#docs")]
    [InlineData("https://user@forge-trust.com")]
    [InlineData("ftp://forge-trust.com")]
    [InlineData("forge-trust.com")]
    public void Constructor_ShouldRejectInvalidPublicOrigin(string publicOrigin)
    {
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                PublicOrigin = publicOrigin
            }
        };

        Assert.Throws<ArgumentException>(() => new DocsUrlBuilder(options));
    }

    [Fact]
    public void Builder_ShouldHandleRootMountedDocsSurfaceWithoutDoubleSlashes()
    {
        var builder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/"
                }
            });

        Assert.Equal("/", builder.BuildHomeUrl());
        Assert.Equal("/search", builder.BuildSearchUrl());
        Assert.Equal("/search-index.json", builder.BuildSearchIndexUrl());
        Assert.Equal("/search.css", builder.BuildAssetUrl("search.css"));
        Assert.Equal("/outline-client.js", builder.BuildAssetUrl("outline-client.js"));
        Assert.Equal("/guides/start.md", builder.BuildDocUrl("guides/start.md"));
        Assert.Equal("/sections/concepts", builder.BuildSectionUrl(DocPublicSection.Concepts));
        Assert.True(builder.IsCurrentDocsPath("/guides/start.md.html"));
        Assert.True(builder.IsCurrentDocsPath("/search"));
        Assert.True(builder.IsCurrentDocsPath("/outline-client.js"));
        Assert.True(builder.IsCurrentDocsPath("/Namespaces/ForgeTrust.AppSurface.Web.html"));
        Assert.False(builder.IsCurrentDocsPath("/privacy.html"));
        Assert.False(builder.IsCurrentDocsPath("guides/start.md.html"));
    }

    [Fact]
    public void IsCurrentDocsPath_ShouldMatchPathsUnderConfiguredRoot()
    {
        var builder = new DocsUrlBuilder(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
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
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
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
    [InlineData("/docs", "///", "/docs")]
    [InlineData("/", "///", "/")]
    public void JoinPath_ShouldReturnDocsRoot_WhenRelativePathIsBlank(string docsRootPath, string? relativePath, string expected)
    {
        var href = DocsUrlBuilder.JoinPath(docsRootPath, relativePath!);

        Assert.Equal(expected, href);
    }
}
