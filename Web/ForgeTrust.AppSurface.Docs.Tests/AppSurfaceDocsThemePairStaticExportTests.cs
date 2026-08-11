using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.AppSurface.Web.TagHelpers;
using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;

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
            <!DOCTYPE html><html data-as-theme="appsurface"><head><script data-as-theme-preference-bootstrap nonce="request-preference-nonce">window.preferenceBootstrap = true;</script><style nonce="request-theme-nonce" data-as-theme-critical>html{--as-canvas:#f8fafc;}</style><style data-docs-theme-critical nonce="request-docs-nonce">html{--docs-color-surface-canvas:var(--as-canvas);}</style><script nonce="preserve-me">window.staticExport = true;</script></head><body></body></html>
            """;

        var rewritten = AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs");
        var document = new HtmlParser().ParseDocument(rewritten);

        Assert.DoesNotContain("request-theme-nonce", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("request-docs-nonce", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("request-preference-nonce", rewritten, StringComparison.Ordinal);
        Assert.Null(document.QuerySelector("script[data-as-theme-preference-bootstrap]")?.GetAttribute("nonce"));
        Assert.Null(document.QuerySelector("style[data-as-theme-critical]")?.GetAttribute("nonce"));
        Assert.Null(document.QuerySelector("style[data-docs-theme-critical]")?.GetAttribute("nonce"));
        Assert.Equal("preserve-me", document.QuerySelector("script:not([data-as-theme-preference-bootstrap])")?.GetAttribute("nonce"));
    }

    [Fact]
    public void PublishedTreeRewrite_ShouldPreserveUnrelatedThemeLikeAttributesOnTheDefaultStableMount()
    {
        const string html = "<!DOCTYPE html><html data-as-theme=\"appsurface\"><head><style data-as-theme-critical>html{--as-canvas:#f8fafc;}</style><style data-docs-theme-critical>html{--docs-color-surface-canvas:var(--as-canvas);}</style><style data-as-theme-critical-extra nonce=\"preserve-suffixed-critical\">html{}</style><style data-as-theme-critical nonce-extra=\"preserve-suffixed-nonce\">html{}</style><style data-theme-note=\"data-docs-theme-critical\" nonce=\"preserve-marker-value\">html{}</style><script nonce=\"preserve-me\">window.staticExport = true;</script></head><body></body></html>";

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

    [Fact]
    public void PublishedTreeRewrite_ShouldPreserveTheGraphiteWebRootAndDocsCompatibilityMetadata()
    {
        var pair = AppSurfaceThemePair.Graphite();
        var resolution = new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, pair.Light, pair.Dark);
        var themeDocument = AppSurfaceThemeDocumentSerializer.Serialize(resolution);
        var docsTheme = new AppSurfaceDocsThemeResolver(
            new AppSurfaceDocsOptions(),
            new StubThemeResolver(resolution)).Theme;
        var liveHtml = $"""
            <!DOCTYPE html><html {themeDocument.RootAttributes} data-docs-theme-preset="{docsTheme.PresetAttribute}" style="{themeDocument.RootStyle}"><head>{themeDocument.HeadContent}<style data-docs-theme-critical>{docsTheme.CriticalCss}</style></head><body></body></html>
            """;

        var rewritten = AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(liveHtml, "/docs/v/1.2.3");
        var document = new HtmlParser().ParseDocument(rewritten);

        Assert.Equal("graphite", document.DocumentElement?.GetAttribute("data-as-theme"));
        Assert.Equal("system", document.DocumentElement?.GetAttribute("data-as-theme-mode"));
        Assert.Equal("appsurface-dark", document.DocumentElement?.GetAttribute("data-docs-theme-preset"));
        Assert.Contains("--as-canvas: #f7f7f8;", rewritten, StringComparison.Ordinal);
        Assert.Contains("--as-canvas: #080a0d;", rewritten, StringComparison.Ordinal);
        Assert.Contains("--docs-color-surface-canvas:var(--as-canvas);", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedTreeRewrite_ShouldKeepTheVerifiedPreferenceBootstrapAndRemoveItsRequestNonce()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options => options.Pairs.Add(AppSurfaceThemePair.AppSurface()));
        services.AddAppSurfaceWebThemePreferences(options => options.StorageKey = "docs-theme");
        using var provider = services.BuildServiceProvider();
        var themeDocument = provider.GetRequiredService<IAppSurfaceThemeDocumentProvider>().GetDocument();
        var preferenceHead = RenderHead(provider, "live-preference-nonce");
        var docsTheme = new AppSurfaceDocsThemeResolver(
            new AppSurfaceDocsOptions(),
            new StubThemeResolver(CreateSystemResolution())).Theme;
        var liveHtml = $"""
            <!DOCTYPE html><html {themeDocument.RootAttributes} style="{themeDocument.RootStyle}"><head>{preferenceHead}<style data-docs-theme-critical nonce="live-docs-nonce">{docsTheme.CriticalCss}</style></head><body></body></html>
            """;

        var staticHtml = AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(liveHtml, "/docs/v/1.2.3");
        var document = new HtmlParser().ParseDocument(staticHtml);
        var bootstrap = Assert.Single(document.QuerySelectorAll("script[data-as-theme-preference-bootstrap]"));

        Assert.Null(bootstrap.GetAttribute("nonce"));
        Assert.Equal(AppSurfaceThemePreferenceCsp.ScriptHash, ToCspSha256(bootstrap.TextContent));
        Assert.Null(document.QuerySelector("style[data-as-theme-critical]")?.GetAttribute("nonce"));
        Assert.Null(document.QuerySelector("style[data-docs-theme-critical]")?.GetAttribute("nonce"));
        Assert.Single(document.QuerySelectorAll("style[data-as-theme-critical]"));
        Assert.Single(document.QuerySelectorAll("style[data-docs-theme-critical]"));
    }

    private static AppSurfaceThemeResolution CreateSystemResolution()
    {
        var pair = AppSurfaceThemePair.AppSurface();
        return new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, pair.Light, pair.Dark);
    }

    private static string RenderHead(IServiceProvider provider, string nonce)
    {
        var helper = new AppSurfaceThemeHeadTagHelper(
            provider.GetRequiredService<IAppSurfaceThemeDocumentProvider>(),
            provider)
        {
            Nonce = nonce
        };
        var output = new TagHelperOutput(
            "appsurface-theme-head",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        helper.Process(
            new TagHelperContext(new TagHelperAttributeList(), new Dictionary<object, object>(), "preference-head"),
            output);

        return output.Content.GetContent();
    }

    private static string ToCspSha256(string value) =>
        "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class StubThemeResolver(AppSurfaceThemeResolution resolution) : IAppSurfaceThemeResolver
    {
        public AppSurfaceThemeResolution ResolveDefault() => resolution;
    }
}
