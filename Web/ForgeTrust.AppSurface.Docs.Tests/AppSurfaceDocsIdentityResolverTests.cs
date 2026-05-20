using System.Text.Encodings.Web;
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

        Assert.Equal("Documentation", resolver.Identity.DisplayName);
        Assert.Equal("/docs", resolver.Identity.HomeHref);
        Assert.Null(resolver.Identity.Logo);
        Assert.Empty(resolver.Identity.Favicons);
        Assert.Null(resolver.Identity.WordmarkHighlightText);
        Assert.Null(resolver.Identity.WordmarkHighlightColor);
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
                    Wordmark = new AppSurfaceDocsWordmarkOptions
                    {
                        HighlightText = "Docs",
                        HighlightColor = "#3B82F6"
                    },
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
        Assert.Equal("Docs", resolver.Identity.WordmarkHighlightText);
        Assert.Equal("#3b82f6", resolver.Identity.WordmarkHighlightColor);
        Assert.Equal(new AppSurfaceDocsResolvedLogo("/brand/logo.svg", "Client mark"), resolver.Identity.Logo);
        Assert.Collection(
            resolver.Identity.Favicons,
            favicon => Assert.Equal(new AppSurfaceDocsResolvedFavicon("/brand/favicon.svg", "image/svg+xml"), favicon),
            favicon => Assert.Equal(new AppSurfaceDocsResolvedFavicon("~/favicon.ico", "image/x-icon"), favicon),
            favicon => Assert.Equal(new AppSurfaceDocsResolvedFavicon("/brand/favicon.png", "image/png"), favicon));
    }

    [Fact]
    public void Identity_ShouldExposeReadOnlyFavicons()
    {
        var resolver = new AppSurfaceDocsIdentityResolver(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    Favicon = new AppSurfaceDocsFaviconOptions
                    {
                        SvgPath = "/brand/favicon.svg"
                    }
                }
            },
            new DocsUrlBuilder(new AppSurfaceDocsOptions()));

        var faviconList = Assert.IsAssignableFrom<IList<AppSurfaceDocsResolvedFavicon>>(resolver.Identity.Favicons);
        Assert.True(faviconList.IsReadOnly);
        Assert.Throws<NotSupportedException>(
            () => faviconList.Add(new AppSurfaceDocsResolvedFavicon("/other.svg", "image/svg+xml")));
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
                    Wordmark = null!,
                    Favicon = null!
                }
            },
            new DocsUrlBuilder(new AppSurfaceDocsOptions()));

        Assert.Equal("Client Docs", resolver.Identity.DisplayName);
        Assert.Null(resolver.Identity.Logo);
        Assert.Empty(resolver.Identity.Favicons);
        Assert.Null(resolver.Identity.WordmarkHighlightText);
        Assert.Null(resolver.Identity.WordmarkHighlightColor);
    }

    [Fact]
    public void Identity_ShouldIgnoreHighlight_WhenConstructedDirectlyWithNonMatchingHighlightText()
    {
        var resolver = new AppSurfaceDocsIdentityResolver(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    DisplayName = "Client Docs",
                    Wordmark = new AppSurfaceDocsWordmarkOptions
                    {
                        HighlightText = "Platform",
                        HighlightColor = "#38bdf8"
                    }
                }
            },
            new DocsUrlBuilder(new AppSurfaceDocsOptions()));

        Assert.Equal("Client Docs", resolver.Identity.DisplayName);
        Assert.Null(resolver.Identity.WordmarkHighlightText);
        Assert.Null(resolver.Identity.WordmarkHighlightColor);
    }

    [Fact]
    public void Identity_ShouldIgnoreInvalidHighlightColor_WhenConstructedDirectly()
    {
        var resolver = new AppSurfaceDocsIdentityResolver(
            new AppSurfaceDocsOptions
            {
                Identity = new AppSurfaceDocsIdentityOptions
                {
                    DisplayName = "Client Docs",
                    Wordmark = new AppSurfaceDocsWordmarkOptions
                    {
                        HighlightText = "Docs",
                        HighlightColor = "var(--brand)"
                    }
                }
            },
            new DocsUrlBuilder(new AppSurfaceDocsOptions()));

        Assert.Equal("Docs", resolver.Identity.WordmarkHighlightText);
        Assert.Null(resolver.Identity.WordmarkHighlightColor);
    }

    [Fact]
    public void WordmarkHtml_ShouldEncodePlainText()
    {
        var identity = new AppSurfaceDocsResolvedIdentity(
            "Client <Docs>",
            "/docs",
            null,
            []);

        var html = RenderWordmark(identity, "docs-wordmark");

        Assert.Equal("<span class=\"docs-wordmark\">Client &lt;Docs&gt;</span>", html);
    }

    [Fact]
    public void WordmarkHtml_ShouldEncodeHighlightSegments_AndCssClass()
    {
        var identity = new AppSurfaceDocsResolvedIdentity(
            "Client <Docs>",
            "/docs",
            null,
            [])
        {
            WordmarkHighlightText = "<Docs>",
            WordmarkHighlightColor = "#3b82f6"
        };

        var html = RenderWordmark(identity, "docs-wordmark \"quoted\"", "h1");

        Assert.Equal(
            "<h1 class=\"docs-wordmark &quot;quoted&quot;\" style=\"--docs-brand-wordmark-highlight-color:#3b82f6\">Client <span class=\"docs-wordmark-highlight\">&lt;Docs&gt;</span></h1>",
            html);
    }

    [Fact]
    public void WordmarkHtml_ShouldRenderPlainText_WhenResolvedHighlightDoesNotMatch()
    {
        var identity = new AppSurfaceDocsResolvedIdentity(
            "Client Docs",
            "/docs",
            null,
            [])
        {
            WordmarkHighlightText = "Platform",
            WordmarkHighlightColor = "#3b82f6"
        };

        var html = RenderWordmark(identity, "docs-wordmark");

        Assert.Equal("<span class=\"docs-wordmark\" style=\"--docs-brand-wordmark-highlight-color:#3b82f6\">Client Docs</span>", html);
    }

    [Fact]
    public void WordmarkHtml_ShouldRejectUnsupportedElementNames()
    {
        var identity = new AppSurfaceDocsResolvedIdentity(
            "Client Docs",
            "/docs",
            null,
            []);

        Assert.Throws<ArgumentOutOfRangeException>(() => AppSurfaceDocsWordmarkHtml.Render(identity, "docs-wordmark", "div"));
    }

    [Theory]
    [InlineData("~/brand/logo.svg", true, "~/brand/logo.svg", "")]
    [InlineData("/", true, "/", "")]
    [InlineData("~//cdn.example/logo.svg", false, null, "protocol-relative")]
    [InlineData("//cdn.example/logo.svg", false, null, "protocol-relative")]
    [InlineData("~/https://example.test/logo.svg", false, null, "remote URL")]
    [InlineData("/data:image/svg+xml;base64,PHN2Zy8+", false, null, "remote URL")]
    [InlineData("/brand/logo.svg?cache=1", false, null, "query string")]
    [InlineData("/brand/logo.svg#mark", false, null, "fragment")]
    [InlineData("/brand\\logo.svg", false, null, "forward slashes")]
    [InlineData("/brand/../logo.svg", false, null, "traversal")]
    [InlineData("brand/logo.svg", false, null, "app-root browser path")]
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

    [Theory]
    [InlineData("#3B82F6", true, "#3b82f6", "")]
    [InlineData("#0af", true, "#0af", "")]
    [InlineData("  #38bdf8  ", true, "#38bdf8", "")]
    [InlineData("blue", false, null, "CSS hex color")]
    [InlineData("var(--brand)", false, null, "CSS hex color")]
    [InlineData("#12", false, null, "CSS hex color")]
    [InlineData("#12345g", false, null, "CSS hex color")]
    public void IdentityPath_ShouldNormalizeOrRejectCssHexColors(
        string value,
        bool expectedResult,
        string? expectedColor,
        string expectedErrorFragment)
    {
        var result = AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(value, out var normalizedColor, out var error);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedColor, normalizedColor);
        if (expectedResult)
        {
            Assert.True(string.IsNullOrWhiteSpace(error));
        }
        else
        {
            Assert.Contains(expectedErrorFragment, error, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string RenderWordmark(AppSurfaceDocsResolvedIdentity identity, string cssClass, string elementName = "span")
    {
        using var writer = new StringWriter();
        AppSurfaceDocsWordmarkHtml.Render(identity, cssClass, elementName).WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }
}
