using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.Playwright;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests;

[Trait("Category", "Integration")]
public sealed class AppSurfaceDevAuthMarkerPlaywrightTests : IClassFixture<AppSurfaceDevAuthMarkerPlaywrightFixture>
{
    private const string MarkerSelector = "[data-appsurface-dev-auth=marker]";
    private const string SummarySelector = ".appsurface-dev-auth-marker__summary";
    private const string DetailsSelector = ".appsurface-dev-auth-marker__details";
    private const string FollowingControlSelector = "#following-host-control";
    private readonly AppSurfaceDevAuthMarkerPlaywrightFixture _fixture;

    public AppSurfaceDevAuthMarkerPlaywrightTests(AppSurfaceDevAuthMarkerPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MobileMarker_At390_ExpandsWithoutIntersectingFollowingHostControl()
    {
        await using var context = await CreateContextAsync(390, 844);
        var page = await context.NewPageAsync();
        await SetMarkerContentAsync(page, DevAuthMarkerTestHost.Render());

        await AssertMobileStylesAsync(page);
        var followingBefore = await RequiredBoxAsync(page.Locator(FollowingControlSelector));

        var summary = page.Locator(SummarySelector);
        await summary.FocusAsync();
        await summary.PressAsync("Space");

        Assert.True(await page.Locator(DetailsSelector).EvaluateAsync<bool>("element => element.open"));
        var markerAfter = await RequiredBoxAsync(page.Locator(MarkerSelector));
        var followingAfter = await RequiredBoxAsync(page.Locator(FollowingControlSelector));
        Assert.True(followingAfter.Y > followingBefore.Y, "Expanding the in-flow marker should push following host content down.");
        Assert.True(markerAfter.Y + markerAfter.Height <= followingAfter.Y, "The expanded marker must not intersect following host content.");
    }

    [Fact]
    public async Task MobileMarker_WithStartExpanded_UsesSameNonOverlappingFlow()
    {
        await using var context = await CreateContextAsync(390, 844);
        var page = await context.NewPageAsync();
        var markerHtml = DevAuthMarkerTestHost.Render(configureMarker: options => options.StartExpanded = true);
        await SetMarkerContentAsync(page, markerHtml);

        await AssertMobileStylesAsync(page);
        Assert.True(await page.Locator(DetailsSelector).EvaluateAsync<bool>("element => element.open"));
        var marker = await RequiredBoxAsync(page.Locator(MarkerSelector));
        var following = await RequiredBoxAsync(page.Locator(FollowingControlSelector));
        Assert.True(marker.Y + marker.Height <= following.Y, "The initially expanded marker must reserve flow space before host content.");
    }

    [Fact]
    public async Task MobileMarker_At320_WrapsLongContentWithoutHorizontalOverflow()
    {
        await using var context = await CreateContextAsync(320, 568);
        var page = await context.NewPageAsync();
        var longValue = new string('W', 112);
        var markerHtml = DevAuthMarkerTestHost.Render(
            configureDevAuth: options =>
            {
                options.Users.Add("long", user => user.DisplayName(longValue).Subject(longValue));
                for (var index = 1; index <= 8; index++)
                {
                    var id = $"persona-{index}";
                    options.Users.Add(id, user => user.DisplayName($"Persona{index}{new string('X', 40)}").Subject($"subject-{index}"));
                }
            },
            configureMarker: options => options.StartExpanded = true,
            selectedPersonaId: "long");
        await SetMarkerContentAsync(page, markerHtml);

        var widths = await page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]");
        Assert.True(widths[0] <= widths[1], $"Expected no horizontal document overflow, but scrollWidth={widths[0]} and clientWidth={widths[1]}.");

        var marker = await RequiredBoxAsync(page.Locator(MarkerSelector));
        var following = await RequiredBoxAsync(page.Locator(FollowingControlSelector));
        Assert.True(marker.Y + marker.Height <= following.Y, "Wrapped marker content must remain separated from following host content.");

        await AssertTabOrderFromSummaryAsync(page);
    }

    [Fact]
    public async Task Marker_At640_IsStatic_AndAt641_IsFixed()
    {
        await using var context = await CreateContextAsync(640, 800);
        var page = await context.NewPageAsync();
        await SetMarkerContentAsync(page, DevAuthMarkerTestHost.Render());

        await AssertMobileStylesAsync(page);

        await page.SetViewportSizeAsync(641, 800);
        var styles = await ReadMarkerStylesAsync(page);
        Assert.Equal("fixed", styles[0]);
        Assert.Equal("16px", styles[1]);
        Assert.Equal("16px", styles[2]);
        Assert.Equal("2147483647", styles[3]);
    }

    [Fact]
    public async Task DesktopMarker_At1280_PreservesOverlayContract()
    {
        await using var context = await CreateContextAsync(1280, 800);
        var page = await context.NewPageAsync();
        await SetMarkerContentAsync(page, DevAuthMarkerTestHost.Render());

        var styles = await ReadMarkerStylesAsync(page);
        Assert.Equal("fixed", styles[0]);
        Assert.Equal("16px", styles[1]);
        Assert.Equal("16px", styles[2]);
        Assert.Equal("2147483647", styles[3]);
        Assert.Equal("360px", styles[4]);
        Assert.NotEqual("none", styles[5]);
    }

    [Fact]
    public async Task MobileMarker_KeyboardTraversal_ReachesEveryEnabledControlThenHost()
    {
        await using var context = await CreateContextAsync(390, 844);
        var page = await context.NewPageAsync();
        await SetMarkerContentAsync(page, DevAuthMarkerTestHost.Render());

        var summary = page.Locator(SummarySelector);
        await summary.FocusAsync();
        await summary.PressAsync("Space");
        Assert.True(await page.Locator(DetailsSelector).EvaluateAsync<bool>("element => element.open"));

        await AssertTabOrderFromSummaryAsync(page);

        await summary.FocusAsync();
        await summary.PressAsync("Enter");
        Assert.False(await page.Locator(DetailsSelector).EvaluateAsync<bool>("element => element.open"));
    }

    private async Task<IBrowserContext> CreateContextAsync(int width, int height)
    {
        return await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = width,
                Height = height,
            },
        });
    }

    private static async Task SetMarkerContentAsync(IPage page, string markerHtml)
    {
        var document = $$"""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <style>html,body{margin:0;padding:0}.host-spacer{height:700px}#{{FollowingControlSelector[1..]}}{display:block;width:160px;height:44px;margin-left:auto;margin-right:8px}</style>
            </head>
            <body>
              <header>Application chrome</header>
              <div class="host-spacer" aria-hidden="true"></div>
              {{markerHtml}}
              <button id="{{FollowingControlSelector[1..]}}" type="button">Following host action</button>
            </body>
            </html>
            """;

        await page.SetContentAsync(document);
        Assert.Equal(1, await page.Locator(MarkerSelector).CountAsync());
    }

    private static async Task AssertMobileStylesAsync(IPage page)
    {
        var styles = await ReadMarkerStylesAsync(page);
        Assert.Equal("static", styles[0]);
        Assert.Equal("auto", styles[1]);
        Assert.Equal("auto", styles[2]);
        Assert.Equal("auto", styles[3]);
        Assert.Equal("none", styles[4]);
        Assert.Equal("none", styles[5]);
    }

    private static async Task<string[]> ReadMarkerStylesAsync(IPage page)
    {
        return await page.Locator(MarkerSelector).EvaluateAsync<string[]>(
            """
            element => {
              const style = getComputedStyle(element);
              return [style.position, style.right, style.bottom, style.zIndex, style.maxWidth, style.boxShadow];
            }
            """);
    }

    private static async Task<LocatorBoundingBoxResult> RequiredBoxAsync(ILocator locator)
    {
        var box = await locator.BoundingBoxAsync();
        return Assert.IsType<LocatorBoundingBoxResult>(box);
    }

    private static async Task AssertTabOrderFromSummaryAsync(IPage page)
    {
        var summary = page.Locator(SummarySelector);
        await summary.FocusAsync();

        var expectedControls = page.Locator($"{MarkerSelector} button, {MarkerSelector} a, {FollowingControlSelector}");
        var count = await expectedControls.CountAsync();
        Assert.True(count >= 4, "Expected persona actions, marker links, and the following host control in the focus order.");

        for (var index = 0; index < count; index++)
        {
            await page.Keyboard.PressAsync("Tab");
            var isFocused = await expectedControls.Nth(index).EvaluateAsync<bool>("element => element === document.activeElement");
            Assert.True(isFocused, $"Expected focus to reach enabled control {index + 1} of {count} in DOM order.");
        }

        Assert.True(await page.Locator(FollowingControlSelector).EvaluateAsync<bool>("element => element === document.activeElement"));
    }
}

public sealed class AppSurfaceDevAuthMarkerPlaywrightFixture : IAsyncLifetime
{
    private static readonly Lazy<int> PlaywrightInstall = new(
        () => Microsoft.Playwright.Program.Main(["install", "chromium"]),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private IBrowser? _browser;
    private IPlaywright? _playwright;

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("The Playwright browser is not initialized.");

    public async Task InitializeAsync()
    {
        EnsurePlaywrightInstalled();
        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
            });
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        var browser = _browser;
        _browser = null;
        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        var playwright = _playwright;
        _playwright = null;
        playwright?.Dispose();
    }

    private static void EnsurePlaywrightInstalled()
    {
        var exitCode = PlaywrightInstall.Value;
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Playwright browser install failed with exit code {exitCode}.");
        }
    }
}
