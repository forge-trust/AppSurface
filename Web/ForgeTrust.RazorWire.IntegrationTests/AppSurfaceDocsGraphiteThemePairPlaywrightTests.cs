using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppSurfaceDocsGraphiteThemePairCollection : ICollectionFixture<AppSurfaceDocsGraphitePlaywrightFixture>
{
    public const string Name = "AppSurfaceDocsGraphiteThemePairCollection";
}

/// <summary>
/// Verifies the shared Graphite pair through a separately configured Docs host rather than changing the default Docs fixture.
/// </summary>
[Collection(AppSurfaceDocsGraphiteThemePairCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsGraphiteThemePairPlaywrightTests
{
    internal const string GraphitePreferenceStorageKey = "appsurface_graphite_docs_test_theme";

    private readonly AppSurfaceDocsGraphitePlaywrightFixture _fixture;

    public AppSurfaceDocsGraphiteThemePairPlaywrightTests(AppSurfaceDocsGraphitePlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    public static IEnumerable<object[]> SystemColorSchemes()
    {
        yield return [ColorScheme.Light, "#f7f7f8", "#17212b", "light dark"];
        yield return [ColorScheme.Dark, "#080a0d", "#f8fafc", "light dark"];
    }

    [Theory]
    [MemberData(nameof(SystemColorSchemes))]
    public async Task GraphiteSystemBridge_ShouldRenderTheExpectedBranchOnTheCanonicalDetailRoute(
        ColorScheme colorScheme,
        string expectedCanvas,
        string expectedText,
        string expectedColorScheme)
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = colorScheme,
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync(_fixture.CanonicalDetailUrl);
        await AssertCanonicalDetailRouteAsync(page, response);
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 60_000
        });

        var values = await page.EvaluateAsync<string[]>(
            """
            () => {
              const root = document.documentElement;
              const style = getComputedStyle(root);
              const bodyStyle = getComputedStyle(document.body);
              const parseRgb = value => {
                const match = value.match(/^rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$/);
                return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : null;
              };
              const parseHex = value => {
                const match = value.match(/^#([0-9a-f]{6})$/i);
                return match
                  ? [
                      Number.parseInt(match[1].slice(0, 2), 16),
                      Number.parseInt(match[1].slice(2, 4), 16),
                      Number.parseInt(match[1].slice(4, 6), 16)
                    ]
                  : null;
              };
              const luminance = rgb => rgb
                .map(channel => channel / 255)
                .map(channel => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4)
                .reduce((result, channel, index) => result + channel * [0.2126, 0.7152, 0.0722][index], 0);
              const foreground = parseRgb(bodyStyle.color);
              const background = parseRgb(bodyStyle.backgroundColor)
                ?? parseHex(style.getPropertyValue('--as-canvas').trim());
              const bodyContrast = foreground && background
                ? (Math.max(luminance(foreground), luminance(background)) + 0.05)
                    / (Math.min(luminance(foreground), luminance(background)) + 0.05)
                : 0;
              return [
                window.location.pathname,
                root.getAttribute('data-as-theme') ?? '',
                root.getAttribute('data-as-theme-mode') ?? '',
                root.getAttribute('data-docs-theme-preset') ?? '',
                style.getPropertyValue('--as-canvas').trim(),
                style.getPropertyValue('--as-text').trim(),
                style.colorScheme,
                document.querySelector('style[data-docs-theme-critical]')?.textContent ?? '',
                String(bodyContrast >= 4.5),
                bodyStyle.color,
                bodyStyle.backgroundColor
              ];
            }
            """);

        Assert.Equal("/docs/examples/razorwire-mvc", values[0]);
        Assert.Equal("graphite", values[1]);
        Assert.Equal("system", values[2]);
        Assert.Equal("appsurface-dark", values[3]);
        Assert.Equal(expectedCanvas, values[4]);
        Assert.Equal(expectedText, values[5]);
        Assert.Equal(expectedColorScheme, values[6]);
        Assert.Contains("--docs-color-surface-canvas:var(--as-canvas);", values[7], StringComparison.Ordinal);
        Assert.Contains("--docs-color-syntax-type:var(--as-link);", values[7], StringComparison.Ordinal);
        Assert.True(
            string.Equals("true", values[8], StringComparison.Ordinal),
            $"Expected the rendered body text and resolved semantic canvas to meet 4.5:1 but found foreground '{values[9]}' on background '{values[10]}'.");
    }

    [Fact]
    public async Task GraphiteSystemBridge_ShouldKeepForcedColorTitlesReadable()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Dark,
            ForcedColors = ForcedColors.Active,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await page.WaitForSelectorAsync(".docs-gradient-title", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 60_000
        });

        var values = await page.EvaluateAsync<string[]>(
            """
            () => {
              const title = document.querySelector('.docs-gradient-title');
              const style = getComputedStyle(title);
              return [
                window.matchMedia('(forced-colors: active)').matches ? 'active' : 'none',
                style.color,
                style.backgroundImage,
                style.webkitTextFillColor
              ];
            }
            """);

        Assert.Equal("active", values[0]);
        Assert.NotEqual("rgba(0, 0, 0, 0)", values[1]);
        Assert.Equal("none", values[2]);
        Assert.NotEqual("rgba(0, 0, 0, 0)", values[3]);
    }

    [Fact]
    public async Task GraphiteDocs_ShouldMatchCommittedDesktopVisualBaselines()
    {
        // The committed PNGs are reviewed macOS Chromium captures. Linux Chromium produces materially
        // different pixels for the same page, while the contract tests above keep the browser behavior
        // covered on every platform.
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        foreach (var mode in GetVisualBaselineModes())
        {
            await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                ColorScheme = mode.ColorScheme,
                ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
            });
            if (mode.StoredPreference is not null)
            {
                await context.AddInitScriptAsync(
                    $"(() => localStorage.setItem('{GraphitePreferenceStorageKey}', '{mode.StoredPreference}'))();");
            }

            foreach (var route in GetVisualBaselineRoutes())
            {
                var page = await context.NewPageAsync();
                await page.GotoAsync($"{_fixture.DocsUrl}{route.Path}");
                await page.WaitForSelectorAsync(route.ReadySelector, new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 60_000
                });
                await page.WaitForFunctionAsync(
                    "expectedMode => document.documentElement.dataset.asThemeMode === expectedMode",
                    mode.StoredPreference ?? "system",
                    new PageWaitForFunctionOptions { Timeout = 60_000 });
                if (string.Equals(route.Name, "search", StringComparison.Ordinal))
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
                await AppSurfaceDocsScreenshotBaseline.AssertMatchesAsync(
                    page,
                    $"{route.Name}-{mode.Name}-1440x1000.png",
                    Path.Join(AppContext.BaseDirectory, "TestResults", "AppSurfaceDocsGraphite"));
                await page.CloseAsync();
            }
        }
    }

    [Theory]
    [InlineData(AppSurfaceThemeMode.Light, "#f7f7f8", "#17212b", "light")]
    [InlineData(AppSurfaceThemeMode.Dark, "#080a0d", "#f8fafc", "dark")]
    public void GraphiteFixedProviders_ShouldEmitOnlyTheSelectedBranch(
        AppSurfaceThemeMode mode,
        string expectedCanvas,
        string expectedText,
        string expectedColorScheme)
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceTheming(options =>
        {
            options.DefaultTheme = new AppSurfaceThemeId("graphite");
            options.DefaultMode = mode;
            options.Pairs.Add(AppSurfaceThemePair.Graphite());
        });
        services.AddAppSurfaceWebTheming();
        using var provider = services.BuildServiceProvider();

        var document = provider.GetRequiredService<IAppSurfaceThemeDocumentProvider>().GetDocument();

        Assert.Equal("graphite", document.RootThemeId);
        Assert.Equal(mode.ToString().ToLowerInvariant(), document.RootThemeMode);
        Assert.Equal(expectedColorScheme, document.RootStyle["color-scheme: ".Length..^1]);
        Assert.Contains($"--as-canvas: {expectedCanvas};", document.HeadContent, StringComparison.Ordinal);
        Assert.Contains($"--as-text: {expectedText};", document.HeadContent, StringComparison.Ordinal);
        Assert.DoesNotContain("@media (prefers-color-scheme: dark)", document.HeadContent, StringComparison.Ordinal);
    }

    private static IEnumerable<GraphiteVisualBaselineMode> GetVisualBaselineModes()
    {
        yield return new GraphiteVisualBaselineMode("system-light", ColorScheme.Light, null);
        yield return new GraphiteVisualBaselineMode("system-dark", ColorScheme.Dark, null);
        yield return new GraphiteVisualBaselineMode("preference-light", ColorScheme.Dark, "light");
        yield return new GraphiteVisualBaselineMode("preference-dark", ColorScheme.Light, "dark");
    }

    private static IEnumerable<GraphiteVisualBaselineRoute> GetVisualBaselineRoutes()
    {
        yield return new GraphiteVisualBaselineRoute("home", string.Empty, "main h1");
        yield return new GraphiteVisualBaselineRoute("search", "/search", ".docs-gradient-title");
        yield return new GraphiteVisualBaselineRoute("detail", "/examples/razorwire-mvc", "#docs-page-outline");
        yield return new GraphiteVisualBaselineRoute("packages", "/packages", "main h1");
        yield return new GraphiteVisualBaselineRoute("release", "/releases/unreleased", ".docs-trust-bar");
    }

    private async Task AssertCanonicalDetailRouteAsync(IPage page, IResponse? response)
    {
        const string expectedPath = "/docs/examples/razorwire-mvc";
        var actualPath = new Uri(page.Url).AbsolutePath;
        if (response is { Ok: true }
            && string.Equals(actualPath, expectedPath, StringComparison.Ordinal))
        {
            return;
        }

        await page.GotoAsync(_fixture.DocsUrl);
        await page.WaitForSelectorAsync("main", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 60_000
        });
        var routeInventory = await page.Locator("a[href]").EvaluateAllAsync<string[]>(
            "anchors => [...new Set(anchors.map(anchor => new URL(anchor.href, document.baseURI).pathname).filter(path => path.startsWith('/docs/')))].sort()");

        throw new InvalidOperationException(
            $"Graphite's canonical Docs detail route must resolve to '{expectedPath}', but navigation returned status "
            + $"'{response?.Status.ToString() ?? "no response"}' at '{actualPath}'. Docs route inventory: "
            + $"{(routeInventory.Length == 0 ? "(no /docs/ links found)" : string.Join(", ", routeInventory))}.");
    }

    private sealed record GraphiteVisualBaselineMode(string Name, ColorScheme ColorScheme, string? StoredPreference);

    private sealed record GraphiteVisualBaselineRoute(string Name, string Path, string ReadySelector);
}

/// <summary>
/// Starts a Graphite-specific Docs host without mutating the shared default Docs Playwright fixture.
/// </summary>
public sealed class AppSurfaceDocsGraphitePlaywrightFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim PlaywrightInstallLock = new(1, 1);
    private static bool _playwrightInstalled;
    private AppSurfaceDocsInProcessHost? _appHost;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public string DocsUrl { get; private set; } = string.Empty;

    public string CanonicalDetailUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await EnsurePlaywrightInstalledAsync();

        try
        {
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            _appHost = await AppSurfaceDocsInProcessHost.StartAsync("http://127.0.0.1:0", ConfigureGraphiteServices);
            DocsUrl = $"{_appHost.BaseUrl}/docs";
            CanonicalDetailUrl = $"{DocsUrl}/examples/razorwire-mvc";
            await WaitForInitialHarvestAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            var browser = Browser;
            Browser = null!;
            if (browser is not null)
            {
                await browser.DisposeAsync();
            }

            var playwright = _playwright;
            _playwright = null;
            playwright?.Dispose();
        }
        finally
        {
            var appHost = _appHost;
            _appHost = null;
            if (appHost is not null)
            {
                await appHost.DisposeAsync();
            }
        }
    }

    private static void ConfigureGraphiteServices(IServiceCollection services)
    {
        services.RemoveAll<AppSurfaceThemeRegistry>();
        services.RemoveAll<IAppSurfaceThemeRegistry>();
        services.RemoveAll<IAppSurfaceThemeResolver>();
        services.RemoveAll<IAppSurfaceThemeDocumentProvider>();
        services.AddAppSurfaceTheming(options =>
        {
            options.DefaultTheme = new AppSurfaceThemeId("graphite");
            options.DefaultMode = AppSurfaceThemeMode.System;
            options.Pairs.Add(AppSurfaceThemePair.Graphite());
        });
        services.AddAppSurfaceWebThemePreferences(
            options => options.StorageKey = AppSurfaceDocsGraphiteThemePairPlaywrightTests.GraphitePreferenceStorageKey);
    }

    private static async Task EnsurePlaywrightInstalledAsync()
    {
        await PlaywrightInstallLock.WaitAsync();
        try
        {
            if (_playwrightInstalled)
            {
                return;
            }

            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Playwright browser install failed with exit code {exitCode}.");
            }

            _playwrightInstalled = true;
        }
        finally
        {
            PlaywrightInstallLock.Release();
        }
    }

    private async Task WaitForInitialHarvestAsync()
    {
        using var client = new HttpClient();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync(DocsUrl);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            if (!html.Contains("id=\"docs-harvest-page\"", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The isolated Graphite Docs host did not complete its initial harvest within 60 seconds.");
    }
}
