using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

// Regression: ISSUE-001 — docs outline links rendered but did not jump to their target sections.
// Found by /qa on 2026-04-22
// Report: .gstack/qa-reports/qa-report-localhost-5189-2026-04-22.md

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsWayfindingRegression1Tests
{
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsWayfindingRegression1Tests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OutlineLinks_UpdateHash_AndScrollTargetIntoView()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await RazorDocsRouteHelper.GotoFirstAvailableAsync(
            page,
            _fixture.DocsUrl,
            "/examples/razorwire-mvc",
            "/examples/razorwire-mvc/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        await page.ClickAsync("#docs-page-outline a[href='#files-behind-the-hero-flow']");
        await page.WaitForFunctionAsync(
            """
            () => {
              const target = document.getElementById('files-behind-the-hero-flow');
              if (!target || window.location.hash !== '#files-behind-the-hero-flow') {
                return false;
              }

              const rect = target.getBoundingClientRect();
              return rect.top >= 0 && rect.top <= 200;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Equal(
            "#files-behind-the-hero-flow",
            await page.EvaluateAsync<string>("() => window.location.hash"));
    }
}
