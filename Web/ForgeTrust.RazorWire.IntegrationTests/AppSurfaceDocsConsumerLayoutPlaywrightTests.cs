using System.Net;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsConsumerLayoutPlaywrightTests
{
    private const string ConsumerLayoutSentinel = "data-appsurface-docs-consumer-layout=\"host\"";

    [Fact]
    public async Task ConsumerGenericLayout_ShouldNotReplaceDocsShellOrLeaveSearchLoading()
    {
        await using var appHost = await AppSurfaceDocsInProcessHost.StartConsumerAsync("http://127.0.0.1:0");
        var docsSearchUrl = $"{appHost.BaseUrl}/docs/search";

        using var client = new HttpClient();
        using var response = await client.GetAsync(docsSearchUrl);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(ConsumerLayoutSentinel, html, StringComparison.Ordinal);
        Assert.Contains("data-docs-theme-preset=", html, StringComparison.Ordinal);
        Assert.Matches("href=\"/_content/ForgeTrust\\.AppSurface\\.Docs/css/site\\.gen\\.css\\?v=[^\"]+\"", html);
        Assert.Matches("href=\"/docs/search\\.css\\?v=[^\"]+\"", html);
        Assert.Contains("window.__appSurfaceDocsConfig", html, StringComparison.Ordinal);
        Assert.Matches("src=\"/docs/minisearch\\.min\\.js\\?v=[^\"]+\"", html);
        Assert.Matches("src=\"/docs/search-client\\.js\\?v=[^\"]+\"", html);

        Assert.Equal(0, Microsoft.Playwright.Program.Main(["install", "chromium"]));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var indexRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIndexResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/docs/search-index.json", async route =>
        {
            indexRequested.TrySetResult();
            await releaseIndexResponse.Task;
            await route.ContinueAsync();
        });

        var indexResponseTask = page.WaitForResponseAsync(
            candidate => candidate.Url.EndsWith("/docs/search-index.json", StringComparison.Ordinal));
        var navigationTask = page.GotoAsync(docsSearchUrl);

        try
        {
            await indexRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("true", await page.GetAttributeAsync("#docs-search-page-results", "aria-busy"));
            Assert.Equal(3, await page.Locator("#docs-search-page-results .docs-search-result-skeleton").CountAsync());
        }
        finally
        {
            releaseIndexResponse.TrySetResult();
        }

        var indexResponse = await indexResponseTask;
        await navigationTask;

        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)indexResponse.Status);
        await page.WaitForFunctionAsync(
            """
            () => {
              const results = document.getElementById('docs-search-page-results');
              return results && results.getAttribute('aria-busy') === 'false';
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Equal("false", await page.GetAttributeAsync("#docs-search-page-results", "aria-busy"));
        Assert.Equal(0, await page.Locator("#docs-search-page-results .docs-search-result-skeleton").CountAsync());
        Assert.Equal(0, await page.Locator($"html[{ConsumerLayoutSentinel}]").CountAsync());
    }

    [Fact]
    public async Task ConsumerFixture_ShouldApplyAndPersistAccessibleBrowserLocalThemePreferences()
    {
        await using var appHost = await AppSurfaceDocsInProcessHost.StartConsumerAsync("http://127.0.0.1:0");
        var docsSearchUrl = $"{appHost.BaseUrl}/docs/search";

        Assert.Equal(0, Microsoft.Playwright.Program.Main(["install", "chromium"]));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ColorScheme = ColorScheme.Dark });
        await context.AddInitScriptAsync(
            """
            (() => {
              if (localStorage.getItem("appsurface_docs_theme") === null) {
                localStorage.setItem("appsurface_docs_theme", "light");
              }
              window.__appsurfaceThemePreferenceEvents = [];
              window.addEventListener("appsurface-theme-preference-change", event => {
                window.__appsurfaceThemePreferenceEvents.push(event.detail);
              });
            })();
            """);
        var page = await context.NewPageAsync();

        await page.GotoAsync(docsSearchUrl);
        await page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.asThemeMode === 'light' && document.querySelector('[data-as-theme-preference-control]')?.hidden === false",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Equal("light", await page.GetAttributeAsync("html", "data-as-theme-mode"));
        Assert.Equal(
            "#f8fafc",
            await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).getPropertyValue('--as-canvas').trim()"));
        Assert.Equal("light dark", await page.EvaluateAsync<string>("() => document.documentElement.style.colorScheme"));
        Assert.Equal("light", await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).colorScheme"));
        Assert.True(await page.Locator("[data-as-theme-preference-control] input[value='light']").IsCheckedAsync());
        Assert.Equal(3, await page.Locator("[data-as-theme-preference-control] input[type='radio'][name='appsurface-theme-preference']").CountAsync());

        var darkChoice = page.Locator("[data-as-theme-preference-control] input[value='dark']");
        await darkChoice.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.asThemeMode === 'dark'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.True(await darkChoice.IsCheckedAsync());
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => localStorage.getItem('appsurface_docs_theme')"));
        Assert.Equal("light dark", await page.EvaluateAsync<string>("() => document.documentElement.style.colorScheme"));
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).colorScheme"));
        Assert.Equal("/docs/search", await page.EvaluateAsync<string>("() => window.location.pathname"));
        var change = await page.EvaluateAsync<string[]>(
            "() => window.__appsurfaceThemePreferenceEvents.at(-1) && [window.__appsurfaceThemePreferenceEvents.at(-1).mode, window.__appsurfaceThemePreferenceEvents.at(-1).persistence, window.__appsurfaceThemePreferenceEvents.at(-1).source]");
        Assert.Equal(["dark", "stored", "control"], change);

        var secondPage = await context.NewPageAsync();
        await secondPage.GotoAsync($"{docsSearchUrl}?theme-preference=second");
        await secondPage.WaitForFunctionAsync(
            "() => document.documentElement.dataset.asThemeMode === 'dark'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        Assert.Equal("dark", await secondPage.GetAttributeAsync("html", "data-as-theme-mode"));

        await secondPage.EvaluateAsync("() => localStorage.clear()");
        await page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.asThemeMode === 'system'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.True(await page.Locator("[data-as-theme-preference-control] input[value='system']").IsCheckedAsync());
        change = await page.EvaluateAsync<string[]>(
            "() => window.__appsurfaceThemePreferenceEvents.at(-1) && [window.__appsurfaceThemePreferenceEvents.at(-1).mode, window.__appsurfaceThemePreferenceEvents.at(-1).persistence, window.__appsurfaceThemePreferenceEvents.at(-1).source]");
        Assert.Equal(["system", "system", "storage"], change);
    }

    [Fact]
    public async Task ConsumerFixture_ShouldKeepSystemUsableAndReportSessionSelectionWhenStorageIsBlocked()
    {
        await using var appHost = await AppSurfaceDocsInProcessHost.StartConsumerAsync("http://127.0.0.1:0");

        Assert.Equal(0, Microsoft.Playwright.Program.Main(["install", "chromium"]));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ColorScheme = ColorScheme.Light });
        await context.AddInitScriptAsync(
            """
            (() => {
              Object.defineProperty(window, "localStorage", {
                configurable: true,
                get: () => { throw new DOMException("blocked", "SecurityError"); }
              });
              window.__appsurfaceThemePreferenceEvents = [];
              window.addEventListener("appsurface-theme-preference-change", event => {
                window.__appsurfaceThemePreferenceEvents.push(event.detail);
              });
            })();
            """);
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{appHost.BaseUrl}/docs/search");
        await page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.asThemeMode === 'system' && document.querySelector('[data-as-theme-preference-control]')?.hidden === false",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        Assert.Equal(
            "#f8fafc",
            await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).getPropertyValue('--as-canvas').trim()"));

        await page.GotoAsync($"{appHost.BaseUrl}/docs/start-here/appsurface-evaluator");
        await page.WaitForSelectorAsync(".docs-detail-title", new PageWaitForSelectorOptions { Timeout = 30_000 });
        Assert.Equal(
            "rgb(15, 23, 42)",
            await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('.docs-detail-title')).color"));

        await page.GotoAsync($"{appHost.BaseUrl}/docs/search");
        await page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.asThemeMode === 'system' && document.querySelector('[data-as-theme-preference-control]')?.hidden === false",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.Locator("[data-as-theme-preference-control] input[value='dark']").ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.asThemeMode === 'dark'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        var change = await page.EvaluateAsync<string[]>(
            "() => window.__appsurfaceThemePreferenceEvents.at(-1) && [window.__appsurfaceThemePreferenceEvents.at(-1).mode, window.__appsurfaceThemePreferenceEvents.at(-1).persistence, window.__appsurfaceThemePreferenceEvents.at(-1).source]");

        Assert.Equal(["dark", "session", "control"], change);
    }
}
