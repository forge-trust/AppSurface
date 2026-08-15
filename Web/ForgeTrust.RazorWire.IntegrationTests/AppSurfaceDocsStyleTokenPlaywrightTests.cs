using System.Buffers.Binary;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Testing;
using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(AppSurfaceDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsStyleTokenPlaywrightTests
{
    private const string AppSurfaceLightBaselineDirectory = "Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsLight";

    private readonly AppSurfaceDocsPlaywrightFixture _fixture;

    public AppSurfaceDocsStyleTokenPlaywrightTests(AppSurfaceDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task FixedAppSurfaceLightPreset_ShouldRemainLightWithoutPreferenceBootstrap(ColorScheme browserColorScheme)
    {
        await using var host = await StartAppSurfaceLightHostAsync();
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = browserColorScheme,
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{host.BaseUrl}/docs/search");
        await page.WaitForSelectorAsync("#docs-search-page-input", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 60_000
        });
        await page.FocusAsync("#docs-search-page-input");

        var values = await page.EvaluateAsync<string[]>(
            """
            () => {
              const root = document.documentElement;
              const rootStyle = getComputedStyle(root);
              const inputStyle = getComputedStyle(document.getElementById('docs-search-page-input'));
              const amberSample = document.createElement('span');
              amberSample.className = 'text-amber-100';
              const emeraldSample = document.createElement('span');
              emeraldSample.className = 'text-emerald-100';
              const roseSample = document.createElement('span');
              roseSample.className = 'text-rose-100';
              const skySample = document.createElement('span');
              skySample.className = 'text-sky-100';
              const tealSample = document.createElement('span');
              tealSample.className = 'text-teal-100';
              const exampleBadge = document.createElement('span');
              exampleBadge.className = 'docs-page-badge docs-page-badge--example';
              const apiBadge = document.createElement('span');
              apiBadge.className = 'docs-page-badge docs-page-badge--api-reference';
              const internalsBadge = document.createElement('span');
              internalsBadge.className = 'docs-page-badge docs-page-badge--internals';
              document.body.append(
                amberSample,
                emeraldSample,
                roseSample,
                skySample,
                tealSample,
                exampleBadge,
                apiBadge,
                internalsBadge);
              const amberColor = getComputedStyle(amberSample).color;
              const emeraldColor = getComputedStyle(emeraldSample).color;
              const roseColor = getComputedStyle(roseSample).color;
              const skyColor = getComputedStyle(skySample).color;
              const tealColor = getComputedStyle(tealSample).color;
              const exampleBadgeColor = getComputedStyle(exampleBadge).color;
              const apiBadgeColor = getComputedStyle(apiBadge).color;
              const internalsBadgeColor = getComputedStyle(internalsBadge).color;
              amberSample.remove();
              emeraldSample.remove();
              roseSample.remove();
              skySample.remove();
              tealSample.remove();
              exampleBadge.remove();
              apiBadge.remove();
              internalsBadge.remove();
              return [
                root.dataset.docsThemePreset ?? '',
                rootStyle.colorScheme,
                rootStyle.getPropertyValue('--docs-color-surface-canvas').trim(),
                rootStyle.getPropertyValue('--docs-color-link').trim(),
                String(document.querySelectorAll('script[data-as-theme-preference-bootstrap]').length),
                String(document.querySelectorAll('fieldset[data-as-theme-preference-control]').length),
                inputStyle.backgroundColor,
                inputStyle.color,
                inputStyle.boxShadow,
                amberColor,
                emeraldColor,
                roseColor,
                skyColor,
                tealColor,
                rootStyle.getPropertyValue('--color-amber-950').trim(),
                rootStyle.getPropertyValue('--color-emerald-950').trim(),
                rootStyle.getPropertyValue('--color-rose-950').trim(),
                rootStyle.getPropertyValue('--color-sky-950').trim(),
                exampleBadgeColor,
                apiBadgeColor,
                internalsBadgeColor,
                String(root.hasAttribute('appsurface-theme-root')),
                root.getAttribute('data-as-theme') ?? '',
                root.getAttribute('data-as-theme-mode') ?? '',
                root.getAttribute('data-as-theme-color-scheme-conflict') ?? ''
              ];
            }
            """);

        Assert.Equal("appsurface-light", values[0]);
        Assert.Equal("light", values[1]);
        Assert.Equal("#f8fafc", values[2]);
        Assert.Equal("#1e3a8a", values[3]);
        Assert.Equal("0", values[4]);
        Assert.Equal("0", values[5]);
        AssertCssColor(values[6], "248, 250, 252");
        AssertCssColor(values[7], "30, 41, 59");
        Assert.Contains("30, 64, 175", values[8], StringComparison.Ordinal);
        AssertCssColor(values[9], "133, 77, 14");
        AssertCssColor(values[10], "22, 101, 52");
        AssertCssColor(values[11], "185, 28, 28");
        AssertCssColor(values[12], "29, 78, 216");
        AssertCssColor(values[13], "15, 118, 110");
        Assert.Equal("rgba(241, 245, 249, 0.56)", values[14]);
        Assert.Equal("rgba(241, 245, 249, 0.56)", values[15]);
        Assert.Equal("rgba(241, 245, 249, 0.56)", values[16]);
        Assert.Equal("rgba(241, 245, 249, 0.56)", values[17]);
        AssertCssColor(values[18], "22, 101, 52");
        AssertCssColor(values[19], "29, 78, 216");
        AssertCssColor(values[20], "133, 77, 14");
        Assert.Equal("false", values[21]);
        Assert.Equal(string.Empty, values[22]);
        Assert.Equal(string.Empty, values[23]);
        Assert.Equal(string.Empty, values[24]);
    }

    [Fact]
    public void FixedAppSurfaceLightPreset_ShouldCommitTheExpectedVisualBaselineManifest()
    {
        var expected = new[]
        {
            new AppSurfaceLightBaselineManifestEntry("home-desktop-1440x1000.png", 1440),
            new AppSurfaceLightBaselineManifestEntry("search-desktop-1440x1000.png", 1440),
            new AppSurfaceLightBaselineManifestEntry("detail-desktop-1440x1000.png", 1440),
            new AppSurfaceLightBaselineManifestEntry("packages-desktop-1440x1000.png", 1440),
            new AppSurfaceLightBaselineManifestEntry("release-desktop-1440x1000.png", 1440),
            new AppSurfaceLightBaselineManifestEntry("home-mobile-390x844.png", 390),
            new AppSurfaceLightBaselineManifestEntry("search-mobile-390x844.png", 390),
            new AppSurfaceLightBaselineManifestEntry("detail-mobile-390x844.png", 390)
        };
        var configured = GetAppSurfaceLightBaselineViewports()
            .SelectMany(
                viewport => GetAppSurfaceLightBaselineRoutes(viewport.Name)
                    .Select(route => new AppSurfaceLightBaselineManifestEntry(route.BaselineFileName, viewport.Width)))
            .ToArray();

        Assert.Equal(expected, configured);

        var repositoryRoot = ForgeTrust.AppSurface.Core.PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var baselineDirectory = TestPathUtils.PathUnder(
            repositoryRoot,
            "Web",
            "ForgeTrust.RazorWire.IntegrationTests",
            "VisualBaselines",
            "AppSurfaceDocsLight");
        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> pngHeader = [73, 72, 68, 82];
        foreach (var baseline in expected)
        {
            var baselinePath = TestPathUtils.PathUnder(baselineDirectory, baseline.FileName);
            var bytes = File.ReadAllBytes(baselinePath);

            Assert.True(bytes.Length >= 24, $"Baseline '{baseline.FileName}' is too short to be a PNG.");
            Assert.True(bytes.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature), $"Baseline '{baseline.FileName}' has an invalid PNG signature.");
            Assert.True(bytes.AsSpan(12, pngHeader.Length).SequenceEqual(pngHeader), $"Baseline '{baseline.FileName}' does not begin with an IHDR chunk.");
            Assert.Equal(baseline.ViewportWidth, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, sizeof(int))));
            Assert.True(
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, sizeof(int))) > 0,
                $"Baseline '{baseline.FileName}' has no rendered content height.");
        }
    }

    [Fact]
    public async Task FixedAppSurfaceLightPreset_ShouldMatchCommittedMacVisualBaselines()
    {
        // The committed screenshots are reviewed macOS Chromium captures. Unit, rendered-layout, export, and
        // browser-contract tests keep the fixed light preset covered on every platform.
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        await using var host = await StartAppSurfaceLightHostAsync();
        var docsUrl = $"{host.BaseUrl}/docs";
        await WaitForAppSurfaceLightDocsReadyAsync(host);

        foreach (var viewport in GetAppSurfaceLightBaselineViewports())
        {
            await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                ColorScheme = ColorScheme.Dark,
                ViewportSize = new ViewportSize { Width = viewport.Width, Height = viewport.Height }
            });

            foreach (var route in GetAppSurfaceLightBaselineRoutes(viewport.Name))
            {
                var page = await context.NewPageAsync();
                await page.GotoAsync($"{docsUrl}{route.Path}");
                await page.WaitForSelectorAsync(route.ReadySelector, new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 60_000
                });
                await page.WaitForFunctionAsync(
                    "() => document.documentElement.dataset.docsThemePreset === 'appsurface-light'",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 60_000 });
                if (route.IsSearch)
                {
                    await WaitForVisualBaselineSearchReadyAsync(page);
                }

                await AppSurfaceDocsScreenshotBaseline.AssertMatchesAsync(
                    page,
                    route.BaselineFileName,
                    Path.Join(AppContext.BaseDirectory, "TestResults", "AppSurfaceDocsLight"),
                    AppSurfaceLightBaselineDirectory);
                await page.CloseAsync();
            }
        }
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
        var tracingStarted = false;
        Exception? primaryFailure = null;
        try
        {
            await context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true
            });
            tracingStarted = true;

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
            await page.WaitForSelectorAsync(".docs-detail-layout", new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000
            });

            var compact = await page.EvaluateAsync<bool[]>(
                """
                () => {
                  const outline = document.getElementById('docs-page-outline');
                  const toggle = outline?.querySelector("[data-doc-outline-toggle='true']");
                  return [Boolean(toggle) && getComputedStyle(toggle).display !== 'none', document.documentElement.scrollWidth <= window.innerWidth];
                }
                """);
            Assert.True(compact[0]);
            Assert.True(compact[1]);

            // Stay above the 80rem desktop breakpoint when CI reserves space for a vertical scrollbar.
            await page.SetViewportSizeAsync(1366, 900);
            // Let Chromium paint the resized grid without pre-asserting the layout contract: a regression must reach
            // the typed snapshot below so its diagnostics are captured instead of timing out first.
            await page.EvaluateAsync(
                """
                () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))
                """);

            var wide = await page.EvaluateAsync<DocsOutlineLayoutSnapshot>(
                """
                () => {
                  const outline = document.getElementById('docs-page-outline');
                  const primary = document.querySelector('.docs-detail-primary');
                  const toggle = outline?.querySelector("[data-doc-outline-toggle='true']");
                  const visualViewport = window.visualViewport;
                  const describe = element => {
                    if (!element) {
                      return {
                        Exists: false,
                        Bounds: null,
                        Display: null,
                        Position: null,
                        GridColumn: null,
                        Width: null,
                        MinWidth: null,
                        MaxWidth: null,
                        OverflowX: null
                      };
                    }

                    const bounds = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return {
                      Exists: true,
                      Bounds: {
                        Left: bounds.left,
                        Top: bounds.top,
                        Right: bounds.right,
                        Bottom: bounds.bottom,
                        Width: bounds.width,
                        Height: bounds.height
                      },
                      Display: style.display,
                      Position: style.position,
                      GridColumn: style.gridColumn,
                      Width: style.width,
                      MinWidth: style.minWidth,
                      MaxWidth: style.maxWidth,
                      OverflowX: style.overflowX
                    };
                  };

                  return {
                    SchemaVersion: 1,
                    WindowInnerWidth: window.innerWidth,
                    WindowInnerHeight: window.innerHeight,
                    BodyScrollWidth: document.body?.scrollWidth ?? null,
                    BodyClientWidth: document.body?.clientWidth ?? null,
                    DocumentElementScrollWidth: document.documentElement?.scrollWidth ?? null,
                    DocumentElementClientWidth: document.documentElement?.clientWidth ?? null,
                    VisualViewportWidth: visualViewport?.width ?? null,
                    VisualViewportHeight: visualViewport?.height ?? null,
                    OutlineExists: Boolean(outline),
                    PrimaryExists: Boolean(primary),
                    ToggleExists: Boolean(toggle),
                    OutlineEnhanced: outline?.dataset.outlineEnhanced ?? null,
                    ToggleAriaExpanded: toggle?.getAttribute('aria-expanded') ?? null,
                    ToggleDisplay: toggle ? getComputedStyle(toggle).display : null,
                    Primary: describe(primary),
                    Outline: describe(outline)
                  };
                }
                """);
            var evaluation = AppSurfaceDocsOutlineLayoutEvidence.Evaluate(wide);
            if (!evaluation.Passed)
            {
                var evidence = new AppSurfaceDocsOutlineLayoutEvidence();
                var capture = await evidence.CaptureIfFailedAsync(
                    evaluation,
                    wide,
                    async (path, _) =>
                    {
                        await page.ScreenshotAsync(new PageScreenshotOptions
                        {
                            Path = path,
                            Timeout = 30_000
                        });
                    },
                    async (path, _) =>
                    {
                        await context.Tracing.StopAsync(new TracingStopOptions { Path = path });
                    });

                Assert.NotNull(capture);
                // If the evidence trace did not finish, finally performs the one no-path stop instead.
                tracingStarted = !capture.TraceStopSucceeded;
                Assert.True(evaluation.Passed, AppSurfaceDocsOutlineLayoutEvidence.FormatFailureMessage(evaluation, capture));
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            if (tracingStarted)
            {
                if (primaryFailure is null)
                {
                    await context.Tracing.StopAsync();
                }
                else
                {
                    try
                    {
                        await context.Tracing.StopAsync();
                    }
                    catch (PlaywrightException)
                    {
                        // Preserve the pre-existing assertion or browser failure; trace cleanup is secondary evidence.
                    }
                }
            }
        }
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

    private static async Task WaitForVisualBaselineSearchReadyAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            """
            () => document.getElementById('docs-search-page-status')?.textContent ===
                'Search is ready. Try a starter query or browse by filter.'
              && document.getElementById('docs-search-page-results')?.getAttribute('aria-busy') === 'false'
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 60_000 });
    }

    private static Task<AppSurfaceDocsInProcessHost> StartAppSurfaceLightHostAsync()
    {
        return AppSurfaceDocsInProcessHost.StartAsync(
            "http://127.0.0.1:0",
            services => services.PostConfigure<AppSurfaceDocsOptions>(options =>
            {
                options.Theme = new AppSurfaceDocsThemeOptions
                {
                    Preset = AppSurfaceDocsThemePreset.AppSurfaceLight,
                    Colors = new AppSurfaceDocsThemeColorOptions
                    {
                        AccentColor = "#1e3a8a",
                        AccentStrongColor = "#1e40af",
                        LinkColor = "#1e3a8a",
                        VisitedLinkColor = "#5b21b6"
                    }
                };
            }));
    }

    private static async Task WaitForAppSurfaceLightDocsReadyAsync(AppSurfaceDocsInProcessHost host)
    {
        using var client = new HttpClient();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"{host.BaseUrl}/docs");
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            if (!html.Contains("id=\"docs-harvest-page\"", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The fixed AppSurfaceLight Docs host did not complete its initial harvest within 60 seconds.");
    }

    private static IEnumerable<AppSurfaceLightBaselineViewport> GetAppSurfaceLightBaselineViewports()
    {
        yield return new AppSurfaceLightBaselineViewport("desktop", 1440, 1000);
        yield return new AppSurfaceLightBaselineViewport("mobile", 390, 844);
    }

    private static IEnumerable<AppSurfaceLightBaselineRoute> GetAppSurfaceLightBaselineRoutes(string viewportName)
    {
        if (string.Equals(viewportName, "desktop", StringComparison.Ordinal))
        {
            yield return new AppSurfaceLightBaselineRoute("", "main h1", "home-desktop-1440x1000.png", false);
            yield return new AppSurfaceLightBaselineRoute("/search", ".docs-gradient-title", "search-desktop-1440x1000.png", true);
            yield return new AppSurfaceLightBaselineRoute("/examples/razorwire-mvc", "#docs-page-outline", "detail-desktop-1440x1000.png", false);
            yield return new AppSurfaceLightBaselineRoute("/packages", "main h1", "packages-desktop-1440x1000.png", false);
            yield return new AppSurfaceLightBaselineRoute("/releases/unreleased", ".docs-trust-bar", "release-desktop-1440x1000.png", false);
            yield break;
        }

        yield return new AppSurfaceLightBaselineRoute("", "main h1", "home-mobile-390x844.png", false);
        yield return new AppSurfaceLightBaselineRoute("/search", ".docs-gradient-title", "search-mobile-390x844.png", true);
        yield return new AppSurfaceLightBaselineRoute("/examples/razorwire-mvc", "#docs-page-outline", "detail-mobile-390x844.png", false);
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

    private sealed record AppSurfaceLightBaselineViewport(string Name, int Width, int Height);

    private sealed record AppSurfaceLightBaselineManifestEntry(string FileName, int ViewportWidth);

    private sealed record AppSurfaceLightBaselineRoute(
        string Path,
        string ReadySelector,
        string BaselineFileName,
        bool IsSearch);
}
