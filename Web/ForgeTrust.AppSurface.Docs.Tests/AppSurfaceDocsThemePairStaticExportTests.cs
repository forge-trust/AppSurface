using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web.Theming;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsThemePairStaticExportTests
{
    [Fact]
    public void PublishedTreeRewrite_ShouldPreserveThemeRootAndCriticalStyles()
    {
        const string html = """
            <!DOCTYPE html><html data-as-theme="appsurface" data-as-theme-mode="system" data-as-theme-schema="1" style="color-scheme: light dark;"><head><meta name="color-scheme" content="light dark" /><style data-as-theme-critical>html{--as-canvas:#f8fafc;}</style><style data-docs-theme-critical>html{--docs-color-surface-canvas:var(--as-canvas);}</style></head><body><a href="/docs/getting-started">Start</a></body></html>
            """;

        var rewritten = AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs/v/1.2.3");

        Assert.Contains("data-as-theme=\"appsurface\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-as-theme-mode=\"system\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-as-theme-schema=\"1\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("color-scheme: light dark;", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-as-theme-critical", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-docs-theme-critical", rewritten, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/v/1.2.3/getting-started\"", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedTreeRewrite_ShouldRemoveThemeNoncesOnTheDefaultStableMount()
    {
        const string html = """
            <!DOCTYPE html><html data-as-theme="appsurface"><head><style nonce="request-theme-nonce" data-as-theme-critical>html{--as-canvas:#f8fafc;}</style><style data-docs-theme-critical nonce="request-docs-nonce">html{--docs-color-surface-canvas:var(--as-canvas);}</style><script nonce="preserve-me">window.staticExport = true;</script></head><body></body></html>
            """;

        var rewritten = AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs");
        var document = new HtmlParser().ParseDocument(rewritten);

        Assert.Null(document.QuerySelector("style[data-as-theme-critical]")?.GetAttribute("nonce"));
        Assert.Null(document.QuerySelector("style[data-docs-theme-critical]")?.GetAttribute("nonce"));
        Assert.Equal("preserve-me", document.QuerySelector("script")?.GetAttribute("nonce"));
    }

    [Fact]
    public void PublishedTreeRewrite_ShouldNotParseStableOutputForAnUnrelatedNonce()
    {
        const string html = "<!DOCTYPE html><html data-as-theme=\"appsurface\"><head><style data-as-theme-critical>html{--as-canvas:#f8fafc;}</style><style data-docs-theme-critical>html{--docs-color-surface-canvas:var(--as-canvas);}</style><script nonce=\"preserve-me\">window.staticExport = true;</script></head><body></body></html>";

        Assert.Equal(html, AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs"));
    }

    [Fact]
    public void PublishedTreeRewrite_ShouldMatchTheLiveCanonicalThemePayloadAfterNonceRemoval()
    {
        var pair = AppSurfaceThemePair.AppSurface();
        var resolution = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, pair.Light, pair.Dark);
        var themeDocument = AppSurfaceThemeDocumentSerializer.Serialize(resolution);
        var liveHeadContent = AppSurfaceThemeDocumentSerializer.SerializeHeadContent(themeDocument, "live-theme-nonce");
        var docsTheme = new AppSurfaceDocsThemeResolver(
            new AppSurfaceDocsOptions(),
            new StubThemeResolver(resolution)).Theme;
        var liveHtml = $"""
            <!DOCTYPE html><html {themeDocument.RootAttributes} style="{themeDocument.RootStyle}"><head>{liveHeadContent}<style data-docs-theme-critical nonce="live-docs-nonce">{docsTheme.CriticalCss}</style><script nonce="preserve-me">window.staticExport = true;</script></head><body><a href="/docs/getting-started">Start</a></body></html>
            """;

        var staticHtml = AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(liveHtml, "/docs/v/1.2.3");
        var parser = new HtmlParser();
        var live = parser.ParseDocument(liveHtml);
        var archived = parser.ParseDocument(staticHtml);

        Assert.Equal(live.DocumentElement?.GetAttribute("data-as-theme"), archived.DocumentElement?.GetAttribute("data-as-theme"));
        Assert.Equal(live.DocumentElement?.GetAttribute("data-as-theme-mode"), archived.DocumentElement?.GetAttribute("data-as-theme-mode"));
        Assert.Equal(live.DocumentElement?.GetAttribute("data-as-theme-schema"), archived.DocumentElement?.GetAttribute("data-as-theme-schema"));
        Assert.Equal(live.DocumentElement?.GetAttribute("style"), archived.DocumentElement?.GetAttribute("style"));
        Assert.Equal(
            live.QuerySelector("style[data-as-theme-critical]")?.TextContent,
            archived.QuerySelector("style[data-as-theme-critical]")?.TextContent);
        Assert.Equal(
            live.QuerySelector("style[data-docs-theme-critical]")?.TextContent,
            archived.QuerySelector("style[data-docs-theme-critical]")?.TextContent);
        Assert.Null(archived.QuerySelector("style[data-as-theme-critical]")?.GetAttribute("nonce"));
        Assert.Null(archived.QuerySelector("style[data-docs-theme-critical]")?.GetAttribute("nonce"));
        Assert.Equal("preserve-me", archived.QuerySelector("script")?.GetAttribute("nonce"));
    }

    private sealed class StubThemeResolver(AppSurfaceThemeResolution resolution) : IAppSurfaceThemeResolver
    {
        public AppSurfaceThemeResolution ResolveDefault() => resolution;
    }
}
