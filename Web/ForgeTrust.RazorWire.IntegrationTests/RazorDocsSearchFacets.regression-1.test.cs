using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

// Regression: ISSUE-002 — the search workspace rendered an empty Status filter group.
// Found by /qa on 2026-04-18
// Report: .gstack/qa-reports/qa-report-localhost-5000-2026-04-18.md

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsSearchFacetsRegression1Tests
{
    private const string SearchIndexPath = "/docs/search-index.json";
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsSearchFacetsRegression1Tests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SearchPage_HidesEmptyStatusFacetGroup_AndPreservesSelectedStatusFromUrl()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var payload = CreateSearchPayload(
            new
            {
                id = "guides/getting-started",
                path = "/docs/guides/getting-started",
                title = "Getting Started",
                summary = "Start here",
                headings = Array.Empty<string>(),
                bodyText = "setup and install",
                snippet = "setup and install",
                pageType = "guide",
                audience = string.Empty,
                component = "CLI",
                aliases = Array.Empty<string>(),
                keywords = Array.Empty<string>(),
                status = string.Empty,
                navGroup = "Guides",
                order = 1,
                relatedPages = Array.Empty<string>(),
                breadcrumbs = Array.Empty<string>()
            },
            new
            {
                id = "examples/quick-start",
                path = "/docs/examples/quick-start",
                title = "Quick Start",
                summary = "Run the example",
                headings = Array.Empty<string>(),
                bodyText = "example walk-through",
                snippet = "example walk-through",
                pageType = "example",
                audience = string.Empty,
                component = "SDK",
                aliases = Array.Empty<string>(),
                keywords = Array.Empty<string>(),
                status = string.Empty,
                navGroup = "Examples",
                order = 2,
                relatedPages = Array.Empty<string>(),
                breadcrumbs = Array.Empty<string>()
            });

        await page.RouteAsync(
            $"**{SearchIndexPath}",
            async route =>
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = payload
                });
            });

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await WaitForSearchPageSettledAsync(page);

        Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Status" }).CountAsync());
        Assert.Equal(0, await page.Locator("[data-rw-facet-key='status']").CountAsync());
        var componentSelect = page.Locator("select[data-rw-facet-key='component']");
        Assert.Equal(1, await componentSelect.CountAsync());
        var componentLabelId = await componentSelect.First.GetAttributeAsync("aria-labelledby");
        Assert.False(string.IsNullOrWhiteSpace(componentLabelId));
        Assert.Equal("Component", await page.Locator($"#{componentLabelId}").TextContentAsync());

        await page.GotoAsync($"{_fixture.DocsUrl}/search?status=beta");
        await WaitForSearchPageSettledAsync(page);

        Assert.Equal(1, await page.GetByRole(AriaRole.Heading, new() { Name = "Status" }).CountAsync());
        var selectedStatusFacet = page.Locator("[data-rw-facet-key='status'][data-rw-facet-value='beta']");
        Assert.Equal(1, await selectedStatusFacet.CountAsync());
        Assert.Equal("true", await selectedStatusFacet.First.GetAttributeAsync("aria-pressed"));
    }

    private static string CreateSearchPayload(params object[] documents)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            metadata = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                version = "1",
                engine = "minisearch"
            },
            documents
        });
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
              return results && results.getAttribute('aria-busy') === 'false';
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }
}
