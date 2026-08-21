using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>
/// Exercises the progressive-enhancement contract for Docs rich authoring against the Durable public tutorial.
/// </summary>
[Collection(AppSurfaceDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsRichAuthoringPlaywrightTests
{
    private readonly AppSurfaceDocsPlaywrightFixture _fixture;

    /// <summary>
    /// Initializes the test with the shared Docs browser host.
    /// </summary>
    /// <param name="fixture">The shared Docs Playwright fixture.</param>
    public AppSurfaceDocsRichAuthoringPlaywrightTests(AppSurfaceDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Verifies that baseline content remains complete without JavaScript and enhances to manual WAI tabs with it.
    /// </summary>
    [Fact]
    public async Task DurableTutorial_RetainsTheCompleteBaselineAndEnhancesToManualTabs()
    {
        await using (var noScriptContext = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 900, Height = 700 }
        }))
        {
            var noScriptPage = await noScriptContext.NewPageAsync();
            await AppSurfaceDocsRouteHelper.GotoFirstAvailableAsync(
                noScriptPage,
                _fixture.DocsUrl,
                "/examples/durable-postgresql",
                "/examples/durable-postgresql/README.md.html");
            await noScriptPage.WaitForSelectorAsync("[data-appsurfacedocs-rich-tabs='true']", new PageWaitForSelectorOptions
            {
                Timeout = 30_000,
                State = WaitForSelectorState.Visible
            });

            Assert.Equal(2, await noScriptPage.Locator("[data-appsurfacedocs-rich-tab-panel='true']").CountAsync());
            Assert.Equal(0, await noScriptPage.Locator("[data-appsurfacedocs-rich-tabs='true'] [role='tab']").CountAsync());
            Assert.True(await noScriptPage.Locator(".docs-rich-tabs__baseline").IsVisibleAsync());
        }

        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 700 }
        });
        var page = await context.NewPageAsync();
        await AppSurfaceDocsRouteHelper.GotoFirstAvailableAsync(
            page,
            _fixture.DocsUrl,
            "/examples/durable-postgresql",
            "/examples/durable-postgresql/README.md.html");
        await page.WaitForSelectorAsync("[data-appsurfacedocs-rich-tabs-enhanced='true']", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Attached
        });

        var tabs = page.Locator("[data-appsurfacedocs-rich-tabs='true'] [role='tab']");
        var panels = page.Locator("[data-appsurfacedocs-rich-tab-panel='true']");
        Assert.Equal(2, await tabs.CountAsync());
        Assert.Equal("true", await tabs.Nth(0).GetAttributeAsync("aria-selected"));
        Assert.Equal("false", await tabs.Nth(1).GetAttributeAsync("aria-selected"));
        Assert.Equal(await panels.Nth(0).GetAttributeAsync("id"), await tabs.Nth(0).GetAttributeAsync("aria-controls"));
        Assert.Equal(await tabs.Nth(0).GetAttributeAsync("id"), await panels.Nth(0).GetAttributeAsync("aria-labelledby"));
        Assert.Equal("false", await panels.Nth(0).GetAttributeAsync("aria-hidden"));
        Assert.Equal("true", await panels.Nth(1).GetAttributeAsync("aria-hidden"));

        await tabs.Nth(0).FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        Assert.True(await tabs.Nth(1).EvaluateAsync<bool>("button => document.activeElement === button"));
        Assert.Equal("true", await tabs.Nth(0).GetAttributeAsync("aria-selected"));

        await page.Keyboard.PressAsync("Enter");
        Assert.Equal("false", await tabs.Nth(0).GetAttributeAsync("aria-selected"));
        Assert.Equal("true", await tabs.Nth(1).GetAttributeAsync("aria-selected"));
        Assert.False(await panels.Nth(0).IsVisibleAsync());
        Assert.True(await panels.Nth(1).IsVisibleAsync());

        await page.Keyboard.PressAsync("Home");
        Assert.True(await tabs.Nth(0).EvaluateAsync<bool>("button => document.activeElement === button"));
        Assert.Equal("true", await tabs.Nth(1).GetAttributeAsync("aria-selected"));

        await page.Keyboard.PressAsync(" ");
        Assert.Equal("true", await tabs.Nth(0).GetAttributeAsync("aria-selected"));
        Assert.Equal("false", await tabs.Nth(1).GetAttributeAsync("aria-selected"));

        await page.Keyboard.PressAsync("End");
        Assert.True(await tabs.Nth(1).EvaluateAsync<bool>("button => document.activeElement === button"));
        await page.Keyboard.PressAsync("ArrowLeft");
        Assert.True(await tabs.Nth(0).EvaluateAsync<bool>("button => document.activeElement === button"));

        await page.EvaluateAsync(
            """
            () => {
                const panel = document.querySelectorAll("[data-appsurfacedocs-rich-tab-panel='true']")[1];
                history.replaceState(null, "", `#${panel.id}`);
                window.dispatchEvent(new HashChangeEvent("hashchange"));
            }
            """);
        Assert.Equal("true", await tabs.Nth(1).GetAttributeAsync("aria-selected"));

        await page.EvaluateAsync(
            """
            () => {
                history.replaceState(null, "", "#%");
                window.dispatchEvent(new HashChangeEvent("hashchange"));
                const frame = document.getElementById("doc-content");
                frame?.insertAdjacentHTML(
                    "beforeend",
                    `<section data-appsurfacedocs-rich-tabs="true" data-appsurfacedocs-rich-tabs-token="turbo-token">
                        <p data-appsurfacedocs-rich-tabs-baseline="true">All paths are available below.</p>
                        <section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="Next">Next</section>
                        <section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="Later">Later</section>
                    </section>
                    <script data-doc-rich-authoring-client="true" data-appsurfacedocs-rich-tabs-tokens="turbo-token"></script>`);
                frame?.dispatchEvent(new Event("turbo:frame-load", { bubbles: true }));
            }
            """);
        Assert.Equal(4, await tabs.CountAsync());
        Assert.Equal("true", await tabs.Nth(1).GetAttributeAsync("aria-selected"));
        Assert.Equal("true", await page.Locator("[data-appsurfacedocs-rich-tabs-token='turbo-token']").GetAttributeAsync("data-appsurfacedocs-rich-tabs-enhanced"));

        await page.EvaluateAsync(
            """
            () => {
                const frame = document.getElementById("doc-content");
                frame?.insertAdjacentHTML(
                    "beforeend",
                    `<section id="invalid-rich-tabs-one" data-appsurfacedocs-rich-tabs="true" data-appsurfacedocs-rich-tabs-token="turbo-token"><p data-appsurfacedocs-rich-tabs-baseline="true">One panel remains visible.</p><section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="Only">Only</section></section>
                     <section id="invalid-rich-tabs-five" data-appsurfacedocs-rich-tabs="true" data-appsurfacedocs-rich-tabs-token="turbo-token"><p data-appsurfacedocs-rich-tabs-baseline="true">Five panels remain visible.</p>${["One", "Two", "Three", "Four", "Five"].map(label => `<section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="${label}">${label}</section>`).join("")}</section>
                     <section id="invalid-rich-tabs-blank" data-appsurfacedocs-rich-tabs="true" data-appsurfacedocs-rich-tabs-token="turbo-token"><p data-appsurfacedocs-rich-tabs-baseline="true">Blank labels remain visible.</p><section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="">Blank</section><section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="Second">Second</section></section>
                     <section id="invalid-rich-tabs-duplicate" data-appsurfacedocs-rich-tabs="true" data-appsurfacedocs-rich-tabs-token="turbo-token"><p data-appsurfacedocs-rich-tabs-baseline="true">Duplicate labels remain visible.</p><section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="Same">First</section><section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="same">Second</section></section>`);
                frame?.dispatchEvent(new Event("turbo:frame-load", { bubbles: true }));
            }
            """);
        var invalidTabs = page.Locator("[id^='invalid-rich-tabs-']");
        Assert.Equal(4, await invalidTabs.CountAsync());
        for (var index = 0; index < await invalidTabs.CountAsync(); index++)
        {
            var invalidTabsCase = invalidTabs.Nth(index);
            Assert.Null(await invalidTabsCase.GetAttributeAsync("data-appsurfacedocs-rich-tabs-enhanced"));
            Assert.Equal(0, await invalidTabsCase.Locator("[role='tab']").CountAsync());
            Assert.True(await invalidTabsCase.Locator("[data-appsurfacedocs-rich-tabs-baseline='true']").IsVisibleAsync());
        }

        await page.EvaluateAsync(
            """
            () => {
                const frame = document.getElementById("doc-content");
                frame?.insertAdjacentHTML(
                    "beforeend",
                    `<section data-appsurfacedocs-rich-tabs="true" data-appsurfacedocs-rich-tabs-token="forged-token">
                        <p data-appsurfacedocs-rich-tabs-baseline="true">Forged baseline remains visible.</p>
                        <section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="Forged first">First</section>
                        <section data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="Forged second">Second</section>
                    </section>`);
                frame?.dispatchEvent(new Event("turbo:frame-load", { bubbles: true }));
            }
            """);
        var forgedTabs = page.Locator("[data-appsurfacedocs-rich-tabs-token='forged-token']");
        Assert.Equal(4, await tabs.CountAsync());
        Assert.Null(await forgedTabs.GetAttributeAsync("data-appsurfacedocs-rich-tabs-enhanced"));
        Assert.True(await forgedTabs.Locator("[data-appsurfacedocs-rich-tabs-baseline='true']").IsVisibleAsync());
    }
}
