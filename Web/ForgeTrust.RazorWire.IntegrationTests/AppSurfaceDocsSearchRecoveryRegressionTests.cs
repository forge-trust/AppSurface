using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(AppSurfaceDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsSearchRecoveryRegressionTests
{
    private const string SearchIndexPath = "/docs/search-index.json";
    private readonly AppSurfaceDocsPlaywrightFixture _fixture;

    public AppSurfaceDocsSearchRecoveryRegressionTests(AppSurfaceDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SearchPage_RecoveryLinkNavigatesToBrowseDestination()
    {
        // Regression: QA-001 - recovery cards must remain plain navigation links.
        // Found by /qa on 2026-05-19.
        // Report: .gstack/qa-reports/qa-report-localhost-6163-2026-05-19.md
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await page.WaitForSelectorAsync("#docs-search-page-recovery", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        var recoveryLink = page.Locator("#docs-search-page-recovery a[href='/docs/sections/troubleshooting']");
        await ExpectSingleRecoveryLinkAsync(recoveryLink);

        await recoveryLink.ClickAsync();
        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path",
            "/docs/sections/troubleshooting",
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    [Fact]
    public async Task SearchPage_RecoveryLinkNavigates_WhenIndexFails()
    {
        // Regression: QA-001 - failed index loads must not trap recovery navigation.
        // Found by /qa on 2026-05-19.
        // Report: .gstack/qa-reports/qa-report-localhost-6163-2026-05-19.md
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.RouteAsync(
            $"**{SearchIndexPath}",
            async route =>
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 503,
                    ContentType = "application/json",
                    Body = "{}"
                });
            });

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await page.WaitForSelectorAsync("#docs-search-page-failure", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        var recoveryLink = page.Locator("#docs-search-page-recovery a[href='/docs/sections/troubleshooting']");
        await ExpectSingleRecoveryLinkAsync(recoveryLink);

        await recoveryLink.ClickAsync();
        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path",
            "/docs/sections/troubleshooting",
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task ExpectSingleRecoveryLinkAsync(ILocator recoveryLink)
    {
        Assert.Equal(1, await recoveryLink.CountAsync());
        Assert.True(await recoveryLink.IsVisibleAsync());
    }
}
