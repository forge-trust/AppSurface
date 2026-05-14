using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

// Regression: ISSUE-001 — the docs shell search CTA could land on the failure state
// while the MiniSearch runtime script was still loading during Turbo navigation.
// Found by /qa on 2026-04-21
// Report: .gstack/qa-reports/qa-report-127-0-0-1-2026-04-21.md

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsSearchTurboCtaRegression1Tests
{
    private const string MiniSearchRuntimePathPattern = "**/docs/minisearch.min.js*";
    private const string SearchWorkspaceLinkSelector = "#docs-search-shell a[href$='/docs/search']";
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsSearchTurboCtaRegression1Tests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SearchWorkspaceCta_WaitsForMiniSearchRuntime_WhenTurboNavigationOutrunsScriptLoad()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var delayedRuntimeRequests = 0;

        await page.RouteAsync(
            MiniSearchRuntimePathPattern,
            async route =>
            {
                delayedRuntimeRequests++;
                await Task.Delay(750);
                await route.ContinueAsync();
            });

        await page.GotoAsync(_fixture.DocsUrl);
        await page.WaitForSelectorAsync(SearchWorkspaceLinkSelector, new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        await page.ClickAsync(SearchWorkspaceLinkSelector);
        await WaitForPathAsync(page, "/docs/search");
        await WaitForSearchPageSettledAsync(page);

        Assert.True(delayedRuntimeRequests > 0);
        Assert.False(await page.Locator("#docs-search-page-failure").IsVisibleAsync());
        Assert.Equal(
            "Search is ready. Try a starter query or browse by filter.",
            await page.TextContentAsync("#docs-search-page-status"));
        Assert.True(await page.Locator("[data-rw-facet-key='pageType']").First.IsVisibleAsync());
    }

    private static async Task WaitForSearchPageSettledAsync(IPage page)
    {
        await page.WaitForSelectorAsync("#docs-search-page-input", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Attached
        });
        await page.WaitForFunctionAsync(
            """
            () => {
              const results = document.getElementById('docs-search-page-results');
              const failure = document.getElementById('docs-search-page-failure');
              return results
                && results.getAttribute('aria-busy') === 'false'
                && (!failure || failure.hidden);
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task WaitForPathAsync(IPage page, string expectedPath)
    {
        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path",
            expectedPath,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }
}
