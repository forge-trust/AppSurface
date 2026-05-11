using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsPackageChooserPlaywrightTests
{
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsPackageChooserPlaywrightTests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PackageChooser_RendersPrimaryRecipeTrustBarAndReadNext()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/packages/README.md.html");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('h1')?.textContent?.trim() === 'AppSurface v0.1 package chooser'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.WaitForSelectorAsync(".docs-trust-bar", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        await page.WaitForSelectorAsync(".docs-content table tbody tr", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Equal("AppSurface v0.1 package chooser", (await page.TextContentAsync("h1"))?.Trim());
        Assert.Contains("v0.1 chooser", await page.InnerTextAsync(".docs-trust-bar"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet package add ForgeTrust.AppSurface.Web", await page.InnerTextAsync(".docs-content"), StringComparison.Ordinal);
        Assert.Equal(
            "/docs/examples/web-app/README.md.html",
            await page.GetAttributeAsync(".docs-content a[href='/docs/examples/web-app/README.md.html']", "href"));
        Assert.NotNull(await page.GetAttributeAsync(".docs-content a[href='/docs/releases/README.md.html']", "href"));

        var openApiRow = page.Locator(".docs-content table tbody tr:has-text('ForgeTrust.AppSurface.Web.OpenApi')").First;
        await openApiRow.WaitForAsync(new LocatorWaitForOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        Assert.Contains("Package README", await openApiRow.InnerTextAsync(), StringComparison.Ordinal);

        var openApiReadmeLink = openApiRow.Locator("a").First;
        await openApiReadmeLink.WaitForAsync(new LocatorWaitForOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        Assert.Equal(
            "/docs/Web/ForgeTrust.AppSurface.Web.OpenApi/README.md.html",
            await openApiReadmeLink.GetAttributeAsync("href"));
        await openApiReadmeLink.ClickAsync();
        await page.WaitForFunctionAsync(
            """
            () => window.location.pathname === '/docs/Web/ForgeTrust.AppSurface.Web.OpenApi/README.md.html'
              && document.querySelector('h1')?.textContent?.trim() === 'ForgeTrust.AppSurface.Web.OpenApi'
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        Assert.Equal(
            "/docs/Web/ForgeTrust.AppSurface.Web.OpenApi/README.md.html",
            new Uri(page.Url).AbsolutePath);
        Assert.Equal("ForgeTrust.AppSurface.Web.OpenApi", (await page.TextContentAsync("h1"))?.Trim());
    }

    [Fact]
    public async Task PackageChooser_MobileMatrixStaysScrollableWithVisibleCue()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 390,
                Height = 844
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/packages/README.md.html");
        await page.WaitForSelectorAsync(".docs-content table", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Contains("Swipe to compare package details on narrow screens.", await page.InnerTextAsync(".docs-content"));

        var overflowIsIntentional = await page.EvaluateAsync<bool>(
            """
            () => {
              const table = document.querySelector('.docs-content table');
              if (!table) {
                return false;
              }

              const styles = window.getComputedStyle(table);
              return styles.display === 'block'
                && styles.overflowX === 'auto'
                && table.scrollWidth > table.clientWidth;
            }
            """);

        Assert.True(overflowIsIntentional);
    }
}
