using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(AppSurfaceDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsSearchFragmentNavigationRegressionTests
{
    private const string SearchQuery = "data-rw-form-collection-template";
    private const string ResultPath = "/docs/api/javascript/razorwire#attribute-data-rw-form-collection-template";
    private const string ResultFragment = "#attribute-data-rw-form-collection-template";
    private readonly AppSurfaceDocsPlaywrightFixture _fixture;

    public AppSurfaceDocsSearchFragmentNavigationRegressionTests(AppSurfaceDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SearchPage_ResultWithFragment_PreservesFragmentOnNavigation()
    {
        // Regression: ISSUE-001 — search-result frame navigation dropped destination fragments.
        // Found by /qa on 2026-07-30.
        // Report: .gstack/qa-reports/qa-report-localhost-6187-2026-07-30.md
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/search?q={Uri.EscapeDataString(SearchQuery)}");

        var result = page.Locator($"#docs-search-page-results a[href='{ResultPath}']");
        await result.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });

        await result.ClickAsync();
        await page.WaitForFunctionAsync(
            "fragment => window.location.hash === fragment",
            ResultFragment,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Equal($"{_fixture.DocsUrl}/api/javascript/razorwire{ResultFragment}", page.Url);
        Assert.Equal(1, await page.Locator(ResultFragment).CountAsync());
    }
}
