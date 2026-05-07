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
        Assert.Equal("/docs", disabledBuilder.DocsEntryRootPath);
        Assert.Equal("/docs/versions", disabledBuilder.DocsVersionsRootPath);
        Assert.Equal("/docs/next", enabledBuilder.CurrentDocsRootPath);
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
        Assert.Equal("/outline-client.js", builder.BuildAssetUrl("outline-client.js"));
        Assert.Equal("/guides/start.md", builder.BuildDocUrl("guides/start.md"));
        Assert.Equal("/sections/concepts", builder.BuildSectionUrl(DocPublicSection.Concepts));
        Assert.True(builder.IsCurrentDocsPath("/guides/start.md.html"));
        Assert.True(builder.IsCurrentDocsPath("/search"));
        Assert.True(builder.IsCurrentDocsPath("/outline-client.js"));
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
