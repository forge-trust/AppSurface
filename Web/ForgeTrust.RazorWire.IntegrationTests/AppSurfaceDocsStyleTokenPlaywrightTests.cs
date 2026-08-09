using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(AppSurfaceDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsStyleTokenPlaywrightTests
{
    private readonly AppSurfaceDocsPlaywrightFixture _fixture;

    public AppSurfaceDocsStyleTokenPlaywrightTests(AppSurfaceDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AppSurfaceDocsSurfaces_ResolveTokenizedComputedStyles()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Dark,
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
                style('.docs-search-result-link').color,
                style('.docs-search-page-active-filter').backgroundColor,
                style('.docs-search-page-active-filter').color,
                style('.docs-search-result mark').backgroundColor
              ];
            }
            """);

        AssertCssColor(searchStyles[0], "147, 197, 253");
        AssertCssColor(searchStyles[1], "250, 204, 21");
        AssertCssColor(searchStyles[2], "248, 250, 252");
        Assert.All(searchStyles.Skip(3), AssertNonTransparentComputedValue);

        await AppSurfaceDocsRouteHelper.GotoFirstAvailableAsync(
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

        Assert.All(detailStyles, AssertNonTransparentComputedValue);

        await AppSurfaceDocsRouteHelper.GotoFirstAvailableAsync(
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
        await page.WaitForFunctionAsync(
            """
            () => {
              const trustBar = document.querySelector('.docs-trust-bar');
              const accentStrong = window
                .getComputedStyle(document.documentElement)
                .getPropertyValue('--as-accent-strong')
                .trim();
              if (!trustBar || !/^#[0-9a-f]{6}$/i.test(accentStrong)) {
                return false;
              }

              const expectedChannels = [
                Number.parseInt(accentStrong.slice(1, 3), 16) / 255,
                Number.parseInt(accentStrong.slice(3, 5), 16) / 255,
                Number.parseInt(accentStrong.slice(5, 7), 16) / 255
              ];
              const borderColor = window.getComputedStyle(trustBar).borderTopColor;
              const srgb = borderColor.match(
                /^color\(srgb\s+([0-9.]+)\s+([0-9.]+)\s+([0-9.]+)\s*\/\s*([0-9.]+)\)$/);
              const rgb = borderColor.match(
                /^rgba?\(\s*([0-9.]+)(?:,|\s)+([0-9.]+)(?:,|\s)+([0-9.]+)(?:\s*(?:,|\/)\s*([0-9.]+))?\s*\)$/);
              const actualChannels = srgb
                ? [Number(srgb[1]), Number(srgb[2]), Number(srgb[3]), Number(srgb[4])]
                : rgb
                  ? [Number(rgb[1]) / 255, Number(rgb[2]) / 255, Number(rgb[3]) / 255, rgb[4] ? Number(rgb[4]) : 1]
                  : null;
              const tolerance = 0.00001;
              return actualChannels !== null
                && expectedChannels.every((channel, index) => Math.abs(actualChannels[index] - channel) <= tolerance)
                && Math.abs(actualChannels[3] - 0.22) <= tolerance;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

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

        Assert.All(trustStyles, AssertNonTransparentComputedValue);
    }

    [Fact]
    public async Task DocsDefaultDarkTheme_ShouldRenderAcrossColorSchemeContextsAndForcedColors()
    {
        await AssertDocsThemeAsync(ColorScheme.Light);
        await AssertDocsThemeAsync(ColorScheme.Dark);

        await using var forcedContext = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Dark,
            ForcedColors = ForcedColors.Active,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }
        });
        var forcedPage = await forcedContext.NewPageAsync();
        await forcedPage.GotoAsync($"{_fixture.DocsUrl}/search?q={Uri.EscapeDataString(_fixture.SearchQuery)}");
        await WaitForSearchReadyAsync(forcedPage);

        var forced = await forcedPage.EvaluateAsync<string[]>(
            """
            () => {
              const root = document.documentElement;
              const input = document.getElementById('docs-search-page-input');
              const style = window.getComputedStyle(input);
              return [
                window.matchMedia('(forced-colors: active)').matches ? 'active' : 'none',
                window.getComputedStyle(root).getPropertyValue('--as-canvas').trim(),
                window.getComputedStyle(root).getPropertyValue('--as-text').trim(),
                window.getComputedStyle(root).getPropertyValue('--as-focus').trim(),
                window.getComputedStyle(root).getPropertyValue('--docs-color-border-default').trim(),
                window.getComputedStyle(root).getPropertyValue('--docs-color-link').trim(),
                window.getComputedStyle(root).getPropertyValue('--docs-color-syntax-keyword').trim(),
                window.getComputedStyle(root).getPropertyValue('--docs-focus-outline').trim(),
                style.backgroundColor,
                style.color,
                style.forcedColorAdjust,
                input.tagName,
                document.getElementById('docs-search-page-filters-toggle').tagName,
                document.querySelector('select.docs-search-page-select')?.tagName ?? ''
              ];
            }
            """);

        Assert.Equal("active", forced[0]);
        Assert.Equal("Canvas", forced[1]);
        Assert.Equal("CanvasText", forced[2]);
        Assert.Equal("Highlight", forced[3]);
        Assert.Equal("GrayText", forced[4]);
        Assert.Equal("LinkText", forced[5]);
        Assert.Equal("Highlight", forced[6]);
        Assert.Equal("2px solid Highlight", forced[7]);
        Assert.NotEqual("none", forced[10]);
        Assert.Equal("INPUT", forced[11]);
        Assert.Equal("BUTTON", forced[12]);
        Assert.Equal("SELECT", forced[13]);
        Assert.NotEqual("rgba(0, 0, 0, 0)", forced[8]);
        Assert.NotEqual("rgba(0, 0, 0, 0)", forced[9]);
    }

    [Fact]
    public async Task ThemeDocument_ShouldResolveSystemLightSystemDarkAndExplicitLightDarkBranches()
    {
        await AssertThemeDocumentAsync(AppSurfaceThemeMode.System, ColorScheme.Light, "#f8fafc", "#0f172a", "light dark");
        await AssertThemeDocumentAsync(AppSurfaceThemeMode.System, ColorScheme.Dark, "#0f172a", "#f8fafc", "light dark");
        await AssertThemeDocumentAsync(AppSurfaceThemeMode.Light, ColorScheme.Dark, "#f8fafc", "#0f172a", "light");
        await AssertThemeDocumentAsync(AppSurfaceThemeMode.Dark, ColorScheme.Light, "#0f172a", "#f8fafc", "dark");
    }

    [Fact]
    public async Task SearchTheme_ShouldPersistAcrossLoadingReadyEmptyFailureAndKeyboardStates()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Dark,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        var indexRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIndex = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/docs/search-index.json", async route =>
        {
            indexRequested.TrySetResult();
            await releaseIndex.Task;
            await route.ContinueAsync();
        });

        var navigation = page.GotoAsync($"{_fixture.DocsUrl}/search?q={Uri.EscapeDataString(_fixture.SearchQuery)}");
        try
        {
            await indexRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("true", await page.GetAttributeAsync("#docs-search-page-results", "aria-busy"));
            Assert.Equal(
                "#0f172a",
                await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).getPropertyValue('--as-canvas').trim()"));
        }
        finally
        {
            releaseIndex.TrySetResult();
        }

        await navigation;
        await WaitForSearchReadyAsync(page);
        Assert.Equal("false", await page.GetAttributeAsync("#docs-search-page-results", "aria-busy"));

        await page.Locator("#docs-search-page-filters-toggle").ClickAsync();
        var enabledFacet = page.Locator("[data-rw-facet-key]:not([disabled])").First;
        await enabledFacet.ClickAsync();
        await page.WaitForSelectorAsync("#docs-search-page-active-filters .docs-search-page-active-filter", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });
        Assert.NotEqual("rgba(0, 0, 0, 0)", await enabledFacet.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));

        var disabledFacet = page.Locator("[data-rw-facet-key][disabled]").First;
        Assert.True(await disabledFacet.CountAsync() > 0);
        Assert.Equal("0.45", await disabledFacet.EvaluateAsync<string>("element => getComputedStyle(element).opacity"));

        await page.Locator("#docs-search-page-filters-toggle").FocusAsync();
        await page.Keyboard.PressAsync("Shift+Tab");

        Assert.Equal("docs-search-page-input", await page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''"));
        Assert.Contains(
            "250, 204, 21",
            await page.Locator("#docs-search-page-input").EvaluateAsync<string>("element => getComputedStyle(element).boxShadow"),
            StringComparison.Ordinal);

        await page.GotoAsync($"{_fixture.DocsUrl}/search?q=noresultsforthemepairxyz");
        await WaitForSearchReadyAsync(page);
        await page.WaitForSelectorAsync(".docs-search-page-no-results", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });
        Assert.NotEqual("rgba(0, 0, 0, 0)", await page.Locator(".docs-search-page-no-results").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));

        var failurePage = await context.NewPageAsync();
        await failurePage.RouteAsync("**/docs/search-index.json", route => route.AbortAsync());
        await failurePage.GotoAsync($"{_fixture.DocsUrl}/search");
        await failurePage.WaitForSelectorAsync("#docs-search-page-failure:not([hidden])", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });
        Assert.NotEqual("rgba(0, 0, 0, 0)", await failurePage.Locator("#docs-search-page-failure").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));
    }

    [Fact]
    public async Task ThemeCriticalCss_ShouldRenderBeforeExternalStylesAndKeepTheOutlineResponsive()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Light,
            ViewportSize = new ViewportSize { Width = 1279, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var stylesheetRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStylesheets = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/*.css*", async route =>
        {
            stylesheetRequested.TrySetResult();
            await releaseStylesheets.Task;
            await route.ContinueAsync();
        });

        var navigation = page.GotoAsync($"{_fixture.DocsUrl}/search", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        try
        {
            await stylesheetRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await page.WaitForFunctionAsync(
                "() => document.documentElement?.getAttribute('data-as-theme') === 'appsurface'",
                null,
                new PageWaitForFunctionOptions { Timeout = 15_000 });
            var preCss = await page.EvaluateAsync<string[]>(
                """
                () => {
                  const style = getComputedStyle(document.documentElement);
                  return [style.getPropertyValue('--as-canvas').trim(), style.backgroundColor, style.color];
                }
                """);
            Assert.Equal("#0f172a", preCss[0]);
            AssertCssColor(preCss[1], "15, 23, 42");
            AssertCssColor(preCss[2], "248, 250, 252");
        }
        finally
        {
            releaseStylesheets.TrySetResult();
        }

        await navigation;
        await AppSurfaceDocsRouteHelper.GotoFirstAvailableAsync(
            page,
            _fixture.DocsUrl,
            "/examples/razorwire-mvc",
            "/examples/razorwire-mvc/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });

        var compact = await page.EvaluateAsync<bool[]>(
            """
            () => {
              const outline = document.getElementById('docs-page-outline');
              const toggle = outline.querySelector("[data-doc-outline-toggle='true']");
              return [getComputedStyle(toggle).display !== 'none', document.documentElement.scrollWidth <= window.innerWidth];
            }
            """);
        Assert.True(compact[0]);
        Assert.True(compact[1]);

        // Stay above the 80rem desktop breakpoint when CI reserves space for a vertical scrollbar.
        await page.SetViewportSizeAsync(1366, 900);
        await page.WaitForFunctionAsync(
            """
            () => {
              const outline = document.getElementById('docs-page-outline');
              const toggle = outline?.querySelector("[data-doc-outline-toggle='true']");
              const primary = document.querySelector('.docs-detail-primary');
              if (!outline || !toggle || !primary || getComputedStyle(toggle).display !== 'none') {
                return false;
              }

              const outlineBox = outline.getBoundingClientRect();
              const primaryBox = primary.getBoundingClientRect();
              return primaryBox.right <= outlineBox.left && document.documentElement.scrollWidth <= window.innerWidth;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        var wide = await page.EvaluateAsync<double[]>(
            """
            () => {
              const outline = document.getElementById('docs-page-outline');
              const toggle = outline.querySelector("[data-doc-outline-toggle='true']");
              const primary = document.querySelector('.docs-detail-primary');
              const outlineBox = outline.getBoundingClientRect();
              const primaryBox = primary.getBoundingClientRect();
              return [
                getComputedStyle(toggle).display === 'none' ? 1 : 0,
                primaryBox.right,
                outlineBox.left,
                document.documentElement.scrollWidth,
                window.innerWidth
              ];
            }
            """);
        Assert.Equal(1, wide[0]);
        Assert.True(wide[1] <= wide[2], $"The primary content extends to {wide[1]}, overlapping the outline starting at {wide[2]}.");
        Assert.True(wide[3] <= wide[4], $"The document width {wide[3]} exceeds the viewport width {wide[4]}.");
    }

    private async Task AssertDocsThemeAsync(ColorScheme colorScheme)
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = colorScheme,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.DocsUrl}/search?q={Uri.EscapeDataString(_fixture.SearchQuery)}");
        await WaitForSearchReadyAsync(page);

        var values = await page.EvaluateAsync<string[]>(
            """
            () => {
              const root = document.documentElement;
              const style = getComputedStyle(root);
              const input = document.getElementById('docs-search-page-input');
              return [
                root.getAttribute('data-as-theme'),
                root.getAttribute('data-as-theme-mode'),
                root.getAttribute('data-as-theme-schema'),
                style.getPropertyValue('--as-canvas').trim(),
                style.getPropertyValue('--as-text').trim(),
                style.colorScheme,
                input.tagName
              ];
            }
            """);

        Assert.Equal("appsurface", values[0]);
        Assert.Equal("dark", values[1]);
        Assert.Equal("1", values[2]);
        Assert.Equal("#0f172a", values[3]);
        Assert.Equal("#f8fafc", values[4]);
        Assert.Equal("dark", values[5]);
        Assert.Equal("INPUT", values[6]);
    }

    private async Task AssertThemeDocumentAsync(
        AppSurfaceThemeMode mode,
        ColorScheme browserColorScheme,
        string expectedCanvas,
        string expectedText,
        string expectedColorScheme)
    {
        var pair = AppSurfaceThemePair.AppSurface();
        var resolution = new AppSurfaceThemeResolution(pair.Id, mode, pair.Light, pair.Dark);
        var document = AppSurfaceThemeDocumentSerializer.Serialize(resolution);

        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = browserColorScheme,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();
        await page.SetContentAsync($"""
            <!DOCTYPE html><html {document.RootAttributes} style="{document.RootStyle}"><head>{document.HeadContent}</head><body><input id="theme-control" /></body></html>
            """);

        var values = await page.EvaluateAsync<string[]>(
            """
            () => {
              const root = document.documentElement;
              const style = getComputedStyle(root);
              return [
                root.getAttribute('data-as-theme-mode'),
                style.getPropertyValue('--as-canvas').trim(),
                style.getPropertyValue('--as-text').trim(),
                style.colorScheme,
                document.getElementById('theme-control').tagName
              ];
            }
            """);

        Assert.Equal(mode.ToString().ToLowerInvariant(), values[0]);
        Assert.Equal(expectedCanvas, values[1]);
        Assert.Equal(expectedText, values[2]);
        Assert.Equal(expectedColorScheme, values[3]);
        Assert.Equal("INPUT", values[4]);
    }

    private static async Task WaitForSearchReadyAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            "() => document.getElementById('docs-search-page-results')?.getAttribute('aria-busy') === 'false'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static void AssertCssColor(string actual, string expectedRgbChannels)
    {
        Assert.Contains(expectedRgbChannels, actual, StringComparison.Ordinal);
    }

    private static void AssertNonTransparentComputedValue(string actual)
    {
        Assert.False(string.IsNullOrWhiteSpace(actual));
        Assert.NotEqual("rgba(0, 0, 0, 0)", actual);
        Assert.NotEqual("transparent", actual);
        Assert.NotEqual("none", actual);
    }
}
