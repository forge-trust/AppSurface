using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

// Regression: package chooser Turbo-frame navigation dropped cross-page fragments.
// Found by /qa on 2026-06-03 while validating issue #443.

[Collection(AppSurfaceDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsPackageChooserHashNavigationRegression1Tests
{
    private readonly AppSurfaceDocsPlaywrightFixture _fixture;

    public AppSurfaceDocsPackageChooserHashNavigationRegression1Tests(AppSurfaceDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PackageChooserQuickstartLink_PreservesPackageFirstFragment_AfterTurboFrameNavigation()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1280,
                Height = 720
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/packages");
        await page.WaitForSelectorAsync(
            "main a[href='/docs/start-here/first-success-path#package-first-path']",
            new PageWaitForSelectorOptions { Timeout = 30_000, State = WaitForSelectorState.Visible });

        await page.ClickAsync("main a[href='/docs/start-here/first-success-path#package-first-path']");
        await page.WaitForFunctionAsync(
            """
            () => {
              const target = document.getElementById('package-first-path');
              const main = document.getElementById('main-content');
              if (!target || !main || window.location.hash !== '#package-first-path') {
                return false;
              }

              const targetRect = target.getBoundingClientRect();
              const mainRect = main.getBoundingClientRect();
              return targetRect.top >= mainRect.top - 2 && targetRect.top <= mainRect.top + 120;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Equal(
            "#package-first-path",
            await page.EvaluateAsync<string>("() => window.location.hash"));
    }
}
