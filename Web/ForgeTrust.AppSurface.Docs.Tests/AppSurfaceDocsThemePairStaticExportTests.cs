using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsThemePairStaticExportTests
{
    [Fact]
    public void PublishedTreeRewrite_ShouldPreserveThemeRootAndCriticalStyles()
    {
        const string html = """
            <!DOCTYPE html><html data-as-theme="appsurface" data-as-theme-mode="system" style="color-scheme: light dark;"><head><meta name="color-scheme" content="light dark" /><style data-as-theme-critical>html{--as-canvas:#f8fafc;}</style><style data-docs-theme-critical>html{--docs-color-surface-canvas:var(--as-canvas);}</style></head><body><a href="/docs/getting-started">Start</a></body></html>
            """;

        var rewritten = AppSurfaceDocsPublishedTreeContentRewriter.RewriteHtml(html, "/docs/v/1.2.3");

        Assert.Contains("data-as-theme=\"appsurface\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-as-theme-mode=\"system\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("color-scheme: light dark;", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-as-theme-critical", rewritten, StringComparison.Ordinal);
        Assert.Contains("data-docs-theme-critical", rewritten, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/v/1.2.3/getting-started\"", rewritten, StringComparison.Ordinal);
    }
}
