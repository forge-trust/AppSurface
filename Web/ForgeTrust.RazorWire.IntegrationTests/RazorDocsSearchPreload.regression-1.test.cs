using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

// Regression: ISSUE-001 — /docs/search emitted an unused preload warning on initial load.
// Found by /qa on 2026-04-18
// Report: .gstack/qa-reports/qa-report-localhost-5000-2026-04-18.md

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsSearchPreloadRegression1Tests
{
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsSearchPreloadRegression1Tests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SearchPage_DoesNotEmitPreloadCredentialMismatchWarnings()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var warnings = new ConcurrentQueue<string>();

        page.Console += (_, message) =>
        {
            if (string.Equals(message.Type, "warning", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Enqueue(message.Text);
            }
        };

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await WaitForSearchPageSettledAsync(page);
        await page.WaitForTimeoutAsync(3000);

        Assert.DoesNotContain(
            warnings,
            warning => warning.Contains("preload", StringComparison.OrdinalIgnoreCase)
                || warning.Contains("credentials mode", StringComparison.OrdinalIgnoreCase));
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
              const hasIndexResource = performance
                .getEntriesByType('resource')
                .some(entry => entry.name.includes('/docs/search-index.json'));
              return results
                && results.getAttribute('aria-busy') === 'false'
                && (!failure || failure.hidden)
                && hasIndexResource;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }
}
