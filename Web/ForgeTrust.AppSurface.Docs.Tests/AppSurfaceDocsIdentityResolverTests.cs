using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsIdentityResolverTests
{
    [Fact]
    public void Identity_ShouldUseDefaults_WhenIdentityOptionsAreEmpty()
    {
        var resolver = new AppSurfaceDocsIdentityResolver(
            new AppSurfaceDocsOptions(),
            new DocsUrlBuilder(new AppSurfaceDocsOptions()));

        Assert.Equal("AppSurface Docs", resolver.Identity.DisplayName);
        Assert.Equal("/docs", resolver.Identity.HomeHref);
        Assert.Null(resolver.Identity.Logo);
        Assert.Empty(resolver.Identity.Favicons);
    }

    [Fact]
    public void Identity_ShouldResolveConfiguredLogoAndFavicons()
    {
        var resolver = new AppSurfaceDocsIdentityResolver(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    DisplayName = "Client Docs",
                    HomeHref = "~/reference",
                    Logo = new AppSurfaceDocsLogoOptions
                    {
                        Path = "/brand/logo.svg",
                        AltText = "Client mark"
                    },
                    Favicon = new AppSurfaceDocsFaviconOptions
                    {
                        SvgPath = "/brand/favicon.svg",
                        IcoPath = "~/favicon.ico",
                        PngPath = "/brand/favicon.png"
                    }
                }
            },
            new DocsUrlBuilder(new AppSurfaceDocsOptions()));

        Assert.Equal("Client Docs", resolver.Identity.DisplayName);
        Assert.Equal("~/reference", resolver.Identity.HomeHref);
        Assert.Equal(new AppSurfaceDocsResolvedLogo("/brand/logo.svg", "Client mark"), resolver.Identity.Logo);
        Assert.Collection(
            resolver.Identity.Favicons,
            favicon => Assert.Equal(new AppSurfaceDocsResolvedFavicon("/brand/favicon.svg", "image/svg+xml"), favicon),
            favicon => Assert.Equal(new AppSurfaceDocsResolvedFavicon("~/favicon.ico", "image/x-icon"), favicon),
            favicon => Assert.Equal(new AppSurfaceDocsResolvedFavicon("/brand/favicon.png", "image/png"), favicon));
    }

    [Fact]
    public void Identity_ShouldTolerateNullNestedOptions_WhenConstructedDirectly()
    {
        var resolver = new AppSurfaceDocsIdentityResolver(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    DisplayName = "Client Docs",
                    Logo = null!,
                    Favicon = null!
                }
            },
            new DocsUrlBuilder(new AppSurfaceDocsOptions()));

        Assert.Equal("Client Docs", resolver.Identity.DisplayName);
        Assert.Null(resolver.Identity.Logo);
        Assert.Empty(resolver.Identity.Favicons);
    }

    [Theory]
    [InlineData("~/brand/logo.svg", true, "~/brand/logo.svg", "")]
    [InlineData("/", true, "/", "")]
    [InlineData("~/https://example.test/logo.svg", false, null, "remote URL")]
    [InlineData("/data:image/svg+xml;base64,PHN2Zy8+", false, null, "remote URL")]
    public void IdentityPath_ShouldNormalizeOrRejectBrowserPaths(
        string value,
        bool expectedResult,
        string? expectedPath,
        string expectedErrorFragment)
    {
        var result = AppSurfaceDocsIdentityPath.TryNormalizeBrowserPath(value, out var normalizedPath, out var error);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedPath, normalizedPath);
        if (expectedResult)
        {
            Assert.True(string.IsNullOrWhiteSpace(error));
        }
        else
        {
            Assert.Contains(expectedErrorFragment, error, StringComparison.OrdinalIgnoreCase);
        }
    }
}
