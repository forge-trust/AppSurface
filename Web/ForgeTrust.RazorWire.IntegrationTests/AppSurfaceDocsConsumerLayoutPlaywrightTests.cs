using System.Net;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsConsumerLayoutPlaywrightTests
{
    private const string ConsumerLayoutSentinel = "data-appsurface-docs-consumer-layout=\"host\"";

    [Fact]
    public async Task ConsumerGenericLayout_ShouldNotReplaceDocsShellOrLeaveSearchLoading()
    {
        await using var appHost = await AppSurfaceDocsInProcessHost.StartConsumerAsync("http://127.0.0.1:0");
        var docsSearchUrl = $"{appHost.BaseUrl}/docs/search";

        using var client = new HttpClient();
        using var response = await client.GetAsync(docsSearchUrl);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(ConsumerLayoutSentinel, html, StringComparison.Ordinal);
        Assert.Contains("data-docs-theme-preset=", html, StringComparison.Ordinal);
        Assert.Matches("href=\"/_content/ForgeTrust\\.AppSurface\\.Docs/css/site\\.gen\\.css\\?v=[^\"]+\"", html);
        Assert.Matches("href=\"/docs/search\\.css\\?v=[^\"]+\"", html);
        Assert.Contains("window.__appSurfaceDocsConfig", html, StringComparison.Ordinal);
        Assert.Matches("src=\"/docs/minisearch\\.min\\.js\\?v=[^\"]+\"", html);
        Assert.Matches("src=\"/docs/search-client\\.js\\?v=[^\"]+\"", html);

        Assert.Equal(0, Microsoft.Playwright.Program.Main(["install", "chromium"]));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var indexRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIndexResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/docs/search-index.json", async route =>
        {
            indexRequested.TrySetResult();
            await releaseIndexResponse.Task;
            await route.ContinueAsync();
        });

        var indexResponseTask = page.WaitForResponseAsync(
            candidate => candidate.Url.EndsWith("/docs/search-index.json", StringComparison.Ordinal));
        var navigationTask = page.GotoAsync(docsSearchUrl);

        try
        {
            await indexRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("true", await page.GetAttributeAsync("#docs-search-page-results", "aria-busy"));
            Assert.Equal(3, await page.Locator("#docs-search-page-results .docs-search-result-skeleton").CountAsync());
        }
        finally
        {
            releaseIndexResponse.TrySetResult();
        }

        var indexResponse = await indexResponseTask;
        await navigationTask;

        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)indexResponse.Status);
        await page.WaitForFunctionAsync(
            """
            () => {
              const results = document.getElementById('docs-search-page-results');
              return results && results.getAttribute('aria-busy') === 'false';
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Equal("false", await page.GetAttributeAsync("#docs-search-page-results", "aria-busy"));
        Assert.Equal(0, await page.Locator("#docs-search-page-results .docs-search-result-skeleton").CountAsync());
        Assert.Equal(0, await page.Locator($"html[{ConsumerLayoutSentinel}]").CountAsync());
    }
}
