using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>Defines the serial collection for the version-switch fixture's independent browser.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppSurfaceDocsVersionSwitchIntegrationCollection : ICollectionFixture<AppSurfaceDocsVersionSwitchPlaywrightFixture>
{
    /// <summary>Gets the xUnit collection name.</summary>
    public const string Name = "AppSurfaceDocsVersionSwitchIntegrationCollection";
}

/// <summary>Launches Chromium without starting the unrelated default Docs host or warming its full search index.</summary>
public sealed class AppSurfaceDocsVersionSwitchPlaywrightFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    /// <summary>Gets the shared headless Chromium instance used by the version-switch tests.</summary>
    public IBrowser Browser { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var browserState = await Task.Run(async () =>
        {
            Console.Error.WriteLine("[VersionSwitchBrowser] create:start");
            var playwright = await Playwright.CreateAsync();
            Console.Error.WriteLine("[VersionSwitchBrowser] create:complete");
            try
            {
                Console.Error.WriteLine("[VersionSwitchBrowser] launch:start");
                var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
                Console.Error.WriteLine("[VersionSwitchBrowser] launch:complete");
                return (Playwright: playwright, Browser: browser);
            }
            catch
            {
                playwright.Dispose();
                throw;
            }
        });

        _playwright = browserState.Playwright;
        Browser = browserState.Browser;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
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

}
