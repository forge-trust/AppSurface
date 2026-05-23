using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(AppSurfaceDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsPackageChooserPlaywrightTests
{
    private readonly AppSurfaceDocsPlaywrightFixture _fixture;

    public AppSurfaceDocsPackageChooserPlaywrightTests(AppSurfaceDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PackageChooser_RendersPrimaryRecipeTrustBarAndReadNext()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/packages");
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
            "/docs/examples/web-app",
            await page.GetAttributeAsync(".docs-content a[href='/docs/examples/web-app']", "href"));
        Assert.NotNull(await page.GetAttributeAsync(".docs-content a[href='/docs/releases']", "href"));
        Assert.Equal(
            "/docs/releases/v0.1-preview",
            await page.GetAttributeAsync(".docs-content a[href='/docs/releases/v0.1-preview']", "href"));

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
            "/docs/web/forgetrust.appsurface.web.openapi",
            await openApiReadmeLink.GetAttributeAsync("href"));
        await openApiReadmeLink.ClickAsync();
        await page.WaitForFunctionAsync(
            """
            () => window.location.pathname === '/docs/web/forgetrust.appsurface.web.openapi'
              && document.querySelector('h1')?.textContent?.trim() === 'ForgeTrust.AppSurface.Web.OpenApi'
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        Assert.Equal(
            "/docs/web/forgetrust.appsurface.web.openapi",
            new Uri(page.Url).AbsolutePath);
        Assert.Equal("ForgeTrust.AppSurface.Web.OpenApi", (await page.TextContentAsync("h1"))?.Trim());
    }

    [Fact]
    public async Task PackageChooser_NavigatesToReleasePreviewAndUpgradePolicy()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/packages");
        await page.WaitForSelectorAsync(".docs-content a[href='/docs/releases/v0.1-preview']", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        await page.Locator(".docs-content a[href='/docs/releases/v0.1-preview']").First.ClickAsync();
        await WaitForPathAndHeadingAsync(page, "/docs/releases/v0.1-preview", "AppSurface v0.1.0 Release Preview");
        await page.WaitForSelectorAsync(".docs-trust-bar", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Equal("AppSurface v0.1.0 Release Preview", (await page.TextContentAsync("h1"))?.Trim());
        Assert.Contains("Release preview", await page.InnerTextAsync(".docs-trust-bar"), StringComparison.OrdinalIgnoreCase);

        await page.Locator(".docs-content a[href='/docs/releases/upgrade-policy']").First.ClickAsync();
        await WaitForPathAndHeadingAsync(page, "/docs/releases/upgrade-policy", "Pre-1.0 upgrade policy");
        Assert.Equal("Pre-1.0 upgrade policy", (await page.TextContentAsync("h1"))?.Trim());
    }

    [Fact]
    public async Task PublicPackageReadmes_LinkToReleasePreview()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/forgetrust.appsurface.core");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('h1')?.textContent?.trim() === 'ForgeTrust.AppSurface.Core'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        Assert.Equal(
            "/docs/releases/v0.1-preview",
            await page.GetAttributeAsync(".docs-content a[href='/docs/releases/v0.1-preview']", "href"));

        await page.GotoAsync($"{_fixture.DocsUrl}/web/forgetrust.appsurface.web.openapi");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('h1')?.textContent?.trim() === 'ForgeTrust.AppSurface.Web.OpenApi'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.Locator(".docs-content a[href='/docs/releases/v0.1-preview']").First.ClickAsync();
        await WaitForPathAndHeadingAsync(page, "/docs/releases/v0.1-preview", "AppSurface v0.1.0 Release Preview");
        Assert.Equal("AppSurface v0.1.0 Release Preview", (await page.TextContentAsync("h1"))?.Trim());
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

        await page.GotoAsync($"{_fixture.DocsUrl}/packages");
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

    private static async Task WaitForPathAsync(IPage page, string expectedPath)
    {
        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path",
            expectedPath,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task WaitForPathAndHeadingAsync(IPage page, string expectedPath, string expectedHeading)
    {
        await page.WaitForFunctionAsync(
            """
            expected => window.location.pathname === expected.path
              && document.querySelector('h1')?.textContent?.trim() === expected.heading
            """,
            new { path = expectedPath, heading = expectedHeading },
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }
}
