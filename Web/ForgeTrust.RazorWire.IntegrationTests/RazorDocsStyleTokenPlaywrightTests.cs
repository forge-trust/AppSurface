using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsStyleTokenPlaywrightTests
{
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsStyleTokenPlaywrightTests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RazorDocsSurfaces_ResolveTokenizedComputedStyles()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 1000
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/search?q={Uri.EscapeDataString(_fixture.SearchQuery)}");
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#docs-search-page-results .docs-search-result').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.WaitForSelectorAsync(".docs-search-result mark", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        var pageTypeFacet = page.Locator("[data-rw-facet-key='pageType']:not([disabled])").First;
        var filterValue = await pageTypeFacet.GetAttributeAsync("data-rw-facet-value");
        Assert.False(string.IsNullOrWhiteSpace(filterValue));
        await pageTypeFacet.ClickAsync();
        await page.WaitForSelectorAsync("#docs-search-page-active-filters .docs-search-page-active-filter", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });
        await page.FocusAsync("#docs-search-page-input");

        var searchStyles = await page.EvaluateAsync<string[]>(
            """
            () => {
              const style = (selector) => window.getComputedStyle(document.querySelector(selector));
              return [
                style('#docs-search-page-input').borderTopColor,
                style('#docs-search-page-input').boxShadow,
                style('.docs-search-result-title a').color,
                style('.docs-search-page-active-filter').backgroundColor,
                style('.docs-search-page-active-filter').color,
                style('.docs-search-result mark').backgroundColor
              ];
            }
            """);

        AssertCssColor(searchStyles[0], "34, 211, 238");
        AssertCssColor(searchStyles[1], "34, 211, 238");
        AssertCssColor(searchStyles[2], "226, 232, 240");
        AssertCssColor(searchStyles[3], "8, 47, 73");
        AssertCssColor(searchStyles[4], "207, 250, 254");
        AssertCssColor(searchStyles[5], "34, 211, 238");

        await RazorDocsRouteHelper.GotoFirstAvailableAsync(
            page,
            _fixture.DocsUrl,
            "/examples/razorwire-mvc",
            "/examples/razorwire-mvc/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });
        await page.ClickAsync("#docs-page-outline a[href='#files-behind-the-hero-flow']");
        await page.WaitForFunctionAsync(
            """
            () => document
              .querySelector("#docs-page-outline a[href='#files-behind-the-hero-flow']")
              ?.getAttribute('aria-current') === 'location'
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        var detailStyles = await page.EvaluateAsync<string[]>(
            """
            () => {
              const activeOutline = document.querySelector("#docs-page-outline a[href='#files-behind-the-hero-flow']");
              const style = (selector) => window.getComputedStyle(document.querySelector(selector));
              return [
                window.getComputedStyle(activeOutline, '::before').backgroundColor,
                style('.docs-content--markdown a').color,
                style('.docs-content--markdown :not(pre) > code').borderTopColor,
                style('.docs-content--markdown :not(pre) > code').backgroundColor
              ];
            }
            """);

        AssertCssColor(detailStyles[0], "34, 211, 238");
        AssertCssColor(detailStyles[1], "125, 211, 252");
        AssertCssColor(detailStyles[2], "51, 65, 85");
        AssertCssColor(detailStyles[3], "15, 23, 42");

        await RazorDocsRouteHelper.GotoFirstAvailableAsync(
            page,
            _fixture.DocsUrl,
            "/releases/unreleased",
            "/releases/unreleased.md.html");
        await page.WaitForSelectorAsync(".docs-provenance-strip", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });
        await page.WaitForSelectorAsync(".docs-trust-bar", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        var trustStyles = await page.EvaluateAsync<string[]>(
            """
            () => {
              const style = (selector) => window.getComputedStyle(document.querySelector(selector));
              return [
                style('.docs-provenance-strip').borderTopColor,
                style('.docs-provenance-label').color,
                style('.docs-trust-bar').borderTopColor,
                style('.docs-trust-bar-label').color
              ];
            }
            """);

        AssertCssColor(trustStyles[0], "51, 65, 85");
        AssertCssColor(trustStyles[1], "103, 232, 249");
        AssertCssColor(trustStyles[2], "34, 211, 238");
        AssertCssColor(trustStyles[3], "103, 232, 249");
    }

    private static void AssertCssColor(string actual, string expectedRgbChannels)
    {
        Assert.Contains(expectedRgbChannels, actual, StringComparison.Ordinal);
    }
}
