using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsSearchPreloadPlaywrightTests
{
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsSearchPreloadPlaywrightTests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Landing_LazyLoadsSearchIndexWithoutCredentialMismatchWarning()
    {
        // Ordinary docs pages now lazy-load search after focus/input/shortcut rather than preloading on first paint.
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var preloadWarnings = CaptureCredentialMismatchWarnings(page);

        await page.GotoAsync(_fixture.DocsUrl);
        await AssertSearchIndexResourceAbsentAsync(page);
        await page.FocusAsync("#docs-search-input");
        await WaitForSearchIndexResourceAsync(page);

        Assert.Empty(preloadWarnings);
        await AssertSearchIndexWasFetchedOnDemandAsync(page);
    }

    [Fact]
    public async Task AdvancedSearch_ReusesSearchIndexPreload_WithoutCredentialMismatchWarning()
    {
        // Regression: ISSUE-002 — docs search preload credentials mismatch emitted browser warnings and disabled preload reuse.
        // Found by /qa on 2026-04-18
        // Report: .gstack/qa-reports/qa-report-localhost-5189-2026-04-18.md
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var preloadWarnings = CaptureCredentialMismatchWarnings(page);

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await page.WaitForSelectorAsync("#docs-search-page-input", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        await WaitForSearchIndexResourceAsync(page);

        Assert.Empty(preloadWarnings);
        await AssertSearchIndexPreloadWasReusedAsync(page);
    }

    private static async Task WaitForSearchIndexResourceAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            "() => performance.getEntriesByType('resource').some(entry => entry.name.includes('/docs/search-index.json'))",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task AssertSearchIndexResourceAbsentAsync(IPage page)
    {
        var resourcePresent = await page.EvaluateAsync<bool>(
            "() => performance.getEntriesByType('resource').some(entry => entry.name.includes('/docs/search-index.json'))");

        Assert.False(resourcePresent);
    }

    private static List<string> CaptureCredentialMismatchWarnings(IPage page)
    {
        var warnings = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("search-index.json", StringComparison.OrdinalIgnoreCase)
                && message.Text.Contains("credentials mode does not match", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(message.Text);
            }
        };

        return warnings;
    }

    private static async Task AssertSearchIndexPreloadWasReusedAsync(IPage page)
    {
        var linkEntryCount = await page.EvaluateAsync<int>(
            "() => performance.getEntriesByType('resource').filter(entry => entry.name.includes('/docs/search-index.json') && entry.initiatorType === 'link').length");
        var fetchEntryCount = await page.EvaluateAsync<int>(
            "() => performance.getEntriesByType('resource').filter(entry => entry.name.includes('/docs/search-index.json') && entry.initiatorType === 'fetch').length");

        Assert.True(linkEntryCount >= 1, "Expected the docs search index preload entry to be recorded.");
        Assert.Equal(0, fetchEntryCount);
    }

    private static async Task AssertSearchIndexWasFetchedOnDemandAsync(IPage page)
    {
        var linkEntryCount = await page.EvaluateAsync<int>(
            "() => performance.getEntriesByType('resource').filter(entry => entry.name.includes('/docs/search-index.json') && entry.initiatorType === 'link').length");
        var fetchEntryCount = await page.EvaluateAsync<int>(
            "() => performance.getEntriesByType('resource').filter(entry => entry.name.includes('/docs/search-index.json') && entry.initiatorType === 'fetch').length");

        Assert.Equal(0, linkEntryCount);
        Assert.True(fetchEntryCount >= 1, "Expected the docs search index to be fetched after sidebar focus.");
    }
}
