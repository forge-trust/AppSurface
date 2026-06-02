using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocsRecoveryLinkBuilderTests
{
    [Fact]
    public void BuildRecoveryLinks_ShouldReturnHarvestFreeRouteSafeDefaults()
    {
        var builder = new DocsRecoveryLinkBuilder(new DocsUrlBuilder(new AppSurfaceDocsOptions()));

        var links = builder.BuildRecoveryLinks();

        Assert.Collection(
            links,
            link =>
            {
                Assert.Equal("Search documentation", link.Title);
                Assert.Equal("/docs/search", link.Href);
                Assert.Equal(DocsRecoveryLinkKind.Primary, link.Kind);
                Assert.False(link.IgnoreDuringStaticExport);
            },
            link =>
            {
                Assert.Equal("Start Here", link.Title);
                Assert.Equal("/docs/sections/start-here", link.Href);
                Assert.Equal(DocsRecoveryLinkKind.Secondary, link.Kind);
                Assert.True(link.IgnoreDuringStaticExport);
            },
            link =>
            {
                Assert.Equal("Packages", link.Title);
                Assert.Equal("/docs/sections/packages", link.Href);
                Assert.Equal(DocsRecoveryLinkKind.Secondary, link.Kind);
                Assert.True(link.IgnoreDuringStaticExport);
            },
            link =>
            {
                Assert.Equal("Docs home", link.Title);
                Assert.Equal("/docs", link.Href);
                Assert.Equal(DocsRecoveryLinkKind.Secondary, link.Kind);
                Assert.True(link.IgnoreDuringStaticExport);
            });
    }

    [Fact]
    public void BuildRecoveryLinks_ShouldUseCurrentVersionedDocsRoot()
    {
        var builder = new DocsRecoveryLinkBuilder(
            new DocsUrlBuilder(
                new AppSurfaceDocsOptions
                {
                    Routing = new AppSurfaceDocsRoutingOptions
                    {
                        RouteRootPath = "/foo/bar",
                        DocsRootPath = "/foo/bar/next"
                    },
                    Versioning = new AppSurfaceDocsVersioningOptions
                    {
                        Enabled = true
                    }
                }));

        var links = builder.BuildRecoveryLinks();

        Assert.Contains(links, link => link.Title == "Search documentation" && link.Href == "/foo/bar/next/search");
        Assert.Contains(links, link => link.Title == "Start Here" && link.Href == "/foo/bar/next/sections/start-here");
        Assert.Contains(links, link => link.Title == "Packages" && link.Href == "/foo/bar/next/sections/packages");
        Assert.Contains(links, link => link.Title == "Docs home" && link.Href == "/foo/bar/next");
    }

    [Fact]
    public void Constructor_ShouldRejectNullUrlBuilder()
    {
        Assert.Throws<ArgumentNullException>(() => new DocsRecoveryLinkBuilder(null!));
    }
}
