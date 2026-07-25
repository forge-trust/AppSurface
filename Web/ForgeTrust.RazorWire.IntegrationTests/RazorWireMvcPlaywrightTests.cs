using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using CliWrap;
using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using CliCommandResult = CliWrap.CommandResult;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireMvcPlaywrightTests
{
    private readonly RazorWireMvcPlaywrightFixture _fixture;

    public RazorWireMvcPlaywrightTests(RazorWireMvcPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RuntimeScripts_LoadFromPackagePathsAndExposeBrowserGlobals()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);

        var contract = await page.EvaluateAsync<RuntimeContractProbe>(
            """
            () => ({
                RuntimeInitialized: window.RazorWireInitialized === true,
                IslandsInitialized: window.RazorWireIslandsInitialized === true,
                HasRuntimeGlobal: Boolean(window.RazorWire),
                FailureMode: window.RazorWire?.config?.failureMode,
                HasFormFailureManager: Boolean(window.RazorWire?.formFailureManager),
                RuntimeScriptPath: document.querySelector('script[src*="/_content/ForgeTrust.RazorWire/razorwire/razorwire.js"]')?.getAttribute('src') || '',
                IslandsScriptPath: document.querySelector('script[src*="/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js"]')?.getAttribute('src') || ''
            })
            """);

        Assert.True(contract.RuntimeInitialized);
        Assert.True(contract.IslandsInitialized);
        Assert.True(contract.HasRuntimeGlobal);
        Assert.True(contract.HasFormFailureManager);
        Assert.Equal("auto", contract.FailureMode);
        Assert.Contains("/_content/ForgeTrust.RazorWire/razorwire/razorwire.js", contract.RuntimeScriptPath, StringComparison.Ordinal);
        Assert.Contains("/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js", contract.IslandsScriptPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BundledTurbo_DrivesNavigationWithoutExternalRequestsOrFullReload()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var externalRequests = new ConcurrentQueue<string>();
        var expectedOrigin = new Uri(_fixture.BaseUrl).GetLeftPart(UriPartial.Authority);

        await page.RouteAsync(
            "**/*",
            async route =>
            {
                var requestUri = new Uri(route.Request.Url);
                var requestOrigin = requestUri.GetLeftPart(UriPartial.Authority);
                if (requestUri.Scheme is "http" or "https"
                    && !string.Equals(requestOrigin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    externalRequests.Enqueue(route.Request.Url);
                    await route.AbortAsync();
                    return;
                }

                await route.ContinueAsync();
            });

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/DeterministicRuntime?state=first");

        Assert.Equal(
            1,
            await page.Locator("script[src*='/_content/ForgeTrust.RazorWire/razorwire/turbo.es2017-umd.js']").CountAsync());

        var sentinel = Guid.NewGuid().ToString("N");
        await page.EvaluateAsync(
            """
            value => {
                window.__razorWireDeterministicRuntimeSentinel = value;
                document.addEventListener("turbo:load", () => {
                    const proof = document.querySelector("[data-deterministic-runtime-proof]");
                    if (!proof) return;

                    proof.dataset.renderKind = "turbo:load";
                    proof.textContent = "turbo:load";
                }, { once: true });
            }
            """,
            sentinel);

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Show second state" }).ClickAsync();
        await page.WaitForSelectorAsync("[data-deterministic-runtime-state='second']");
        await page.WaitForSelectorAsync("[data-deterministic-runtime-proof][data-render-kind='turbo:load']");

        Assert.Equal(
            sentinel,
            await page.EvaluateAsync<string>("() => window.__razorWireDeterministicRuntimeSentinel"));
        Assert.Equal(
            "turbo:load",
            await page.Locator("[data-deterministic-runtime-proof]").TextContentAsync());
        Assert.Empty(externalRequests);
    }

    [Fact]
    public async Task BundledTurbo_HashOnlyNavigationPreservesDocumentWithoutRequest()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/DeterministicRuntime?state=first");

        var documentRequests = new ConcurrentQueue<string>();
        page.Request += (_, request) =>
        {
            if (string.Equals(request.ResourceType, "document", StringComparison.Ordinal))
            {
                documentRequests.Enqueue(request.Url);
            }
        };

        var sentinel = Guid.NewGuid().ToString("N");
        await page.EvaluateAsync(
            """
            value => {
                window.__razorWireHashSentinel = value;
                window.__razorWireHashVisitCount = 0;
                window.__razorWireHashLoadCount = 0;
                document.addEventListener("turbo:visit", () => window.__razorWireHashVisitCount++);
                document.addEventListener("turbo:load", () => window.__razorWireHashLoadCount++);
            }
            """,
            sentinel);

        await page.Locator("[data-deterministic-hash-link]").ClickAsync();
        await page.WaitForFunctionAsync("() => window.location.hash === '#deterministic-hash-target'");

        var proof = await page.EvaluateAsync<HashNavigationProbe>(
            """
            () => ({
                Sentinel: window.__razorWireHashSentinel,
                VisitCount: window.__razorWireHashVisitCount,
                LoadCount: window.__razorWireHashLoadCount
            })
            """);

        Assert.Equal(sentinel, proof.Sentinel);
        Assert.Equal(1, proof.VisitCount);
        Assert.Equal(1, proof.LoadCount);
        Assert.Empty(documentRequests);
    }

    [Fact]
    public async Task BundledTurbo_ParentFrameTarget_UsesResolvedParentIdHeader()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/DeterministicRuntime?state=first");
        await page.EvaluateAsync(
            """
            () => {
                document.getElementById("grandparent-frame").dataset.identity = "preserved";
            }
            """);

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Replace parent frame" }).ClickAsync();
        await page.WaitForSelectorAsync("#parent-frame [data-parent-frame-state='updated']");

        Assert.Equal(
            "parent-frame",
            await page.GetAttributeAsync("#parent-frame [data-parent-frame-state='updated']", "data-observed-turbo-frame"));
        Assert.Equal(
            "preserved",
            await page.GetAttributeAsync("#grandparent-frame", "data-identity"));
        Assert.Equal(1, await page.Locator("#grandparent-frame").CountAsync());
        Assert.Equal(1, await page.Locator("#parent-frame").CountAsync());
    }

    [Fact]
    public async Task BundledTurbo_ExecutesInlineStreamsAndRemovesDuplicateSiblings()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var pageErrors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => pageErrors.Enqueue(error);

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/DeterministicRuntime?state=first");
        await page.EvaluateAsync(
            """
            () => {
                document.body.insertAdjacentHTML("beforeend", `
                    <turbo-stream action="before" target="stream-insertion-target">
                        <template><div id="stream-duplicate-sibling" data-stream-value="new">new</div></template>
                    </turbo-stream>
                    <turbo-stream action="after" target="stream-insertion-target">
                        <template><div id="stream-after-sibling">after</div></template>
                    </turbo-stream>
                    <turbo-stream action="append" target="stream-inline-target">
                        <template><span data-inline-stream-result>executed</span></template>
                    </turbo-stream>
                `);
            }
            """);

        await page.WaitForSelectorAsync("#stream-duplicate-sibling[data-stream-value='new']");
        await page.WaitForSelectorAsync("#stream-after-sibling");
        await page.WaitForSelectorAsync("#stream-inline-target [data-inline-stream-result]");

        Assert.Equal(1, await page.Locator("#stream-duplicate-sibling").CountAsync());
        Assert.Equal("new", await page.Locator("#stream-duplicate-sibling").TextContentAsync());
        Assert.True(await page.Locator("#stream-insertion-target").EvaluateAsync<bool>(
            "target => target.previousElementSibling?.id === 'stream-duplicate-sibling'"));
        Assert.True(await page.Locator("#stream-insertion-target").EvaluateAsync<bool>(
            "target => target.nextElementSibling?.id === 'stream-after-sibling'"));
        Assert.Equal(1, await page.Locator("[data-inline-stream-result]").CountAsync());
        Assert.Empty(pageErrors);
    }

    [Fact]
    public async Task FormInteractionsSample_ReplacesPageLocalJavaScriptForConditionalAndCollectionRows()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var browserMessages = CaptureBrowserMessages(page);

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormInteractions");
        await WaitForFormInteractionsReadyAsync(page, browserMessages);

        Assert.True(await page.Locator("script[src*='/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js']").CountAsync() > 0);
        Assert.Equal(0, await page.Locator("[data-form-interactions-proof] script").CountAsync());

        await page.ClickAsync("[data-rw-form-collection-duplicate]");
        await page.WaitForSelectorAsync("input[name='Actions[1].Title']");
        Assert.Equal("Call parent", await page.InputValueAsync("input[name='Actions[1].Title']"));
        Assert.Equal(string.Empty, await page.InputValueAsync("input[name='Actions[1].Id']"));

        await page.ClickAsync("[data-rw-form-collection-add]");
        await page.WaitForSelectorAsync("input[name='Actions[2].Title']");
        await page.FillAsync("input[name='Actions[2].Title']", "Email teacher");

        await page.ClickAsync("button[data-rw-form-collection-remove='mark']");
        var markData = await page.EvaluateAsync<FormDataProbe>(
            """
            () => {
              const form = document.querySelector('[data-form-interactions-proof]');
              const data = new FormData(form);
              return {
                DeleteValue: data.get('Actions[0].Delete'),
                TitleValue: data.get('Actions[0].Title'),
                IndexValues: data.getAll('Actions.index')
              };
            }
            """);

        Assert.Equal("true", markData.DeleteValue);
        Assert.Null(markData.TitleValue);
        Assert.Contains("0", markData.IndexValues);

        await page.CheckAsync("input[name='ExpectedNoAction']");
        await page.WaitForFunctionAsync(
            """
            () => document.getElementById('draft-action')?.hidden === true
              && [...document.querySelectorAll('#draft-action input[name$=".Title"]')].every(input => input.disabled)
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        var hiddenData = await page.EvaluateAsync<FormDataProbe>(
            """
            () => {
              const data = new FormData(document.querySelector('[data-form-interactions-proof]'));
              return {
                DeleteValue: data.get('Actions[0].Delete'),
                TitleValue: data.get('Actions[2].Title'),
                IndexValues: data.getAll('Actions.index')
              };
            }
            """);

        Assert.Equal("true", hiddenData.DeleteValue);
        Assert.Null(hiddenData.TitleValue);
        Assert.Contains("0", hiddenData.IndexValues);
        Assert.DoesNotContain("2", hiddenData.IndexValues);

        var submitResponse = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button[type='submit']"),
            response => response.Url.Contains("/Reactivity/SubmitFormInteractions", StringComparison.Ordinal));
        var statusCode = (HttpStatusCode)submitResponse.Status;
        Assert.True(
            statusCode is HttpStatusCode.OK or HttpStatusCode.Found,
            $"Expected accepted POST or redirect response from {submitResponse.Url}, got {statusCode}.");
    }

    [Fact]
    public async Task FormInteractionsSample_RedisplaysValidationErrorsForInvalidPostedState()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var browserMessages = CaptureBrowserMessages(page);

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormInteractions");
        await WaitForFormInteractionsReadyAsync(page, browserMessages);

        await page.EvaluateAsync(
            """
            () => {
              document.querySelector('input[name="ExpectedNoAction"]').checked = true;
              document.querySelector('input[name="Actions[0].Title"]').disabled = false;
            }
            """);

        var response = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button[type='submit']"),
            response => response.Url.Contains("/Reactivity/SubmitFormInteractions", StringComparison.Ordinal)
                        && response.Status == StatusCodes.Status422UnprocessableEntity);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Status);
        await page.WaitForSelectorAsync("text=Clear draft action rows before marking that no action is expected.");
        Assert.True(await page.Locator("input[name='Actions[0].Title']").CountAsync() > 0);
        Assert.Equal("Call parent", await page.InputValueAsync("input[name='Actions[0].Title']"));
    }

    [Fact]
    public async Task PageNavigationSample_TracksHashActiveStateAndClosesMobilePanel()
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

        await page.GotoAsync($"{_fixture.BaseUrl}/Navigation/PageNavigation#workflow");
        await page.WaitForFunctionAsync(
            """
            () => window.RazorWire?.pageNavigationManager
              && document.querySelector('.brochure-page-nav a[href="#workflow"]')?.getAttribute('aria-current') === 'location'
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.True(await page.Locator("script[src*='/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js']").CountAsync() > 0);
        Assert.Equal("false", await page.GetAttributeAsync(".brochure-page-nav [data-rw-page-nav-toggle]", "aria-expanded"));
        await page.WaitForFunctionAsync(
            """
            () => {
              const active = document.querySelector('.brochure-page-nav a[href="#workflow"]');
              const panel = document.getElementById('brochure-sections-panel');
              const activeStyle = active ? getComputedStyle(active) : null;
              return panel?.getAttribute('data-rw-page-nav-panel-state') === 'closed'
                && active?.textContent?.trim() === 'Buyer Workflow'
                && activeStyle?.display !== 'none'
                && activeStyle?.boxShadow === 'none';
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        await page.ClickAsync(".brochure-page-nav [data-rw-page-nav-toggle]");
        await page.WaitForFunctionAsync(
            """
            () => document.querySelector('.brochure-page-nav [data-rw-page-nav-toggle]')?.getAttribute('aria-expanded') === 'true'
              && document.getElementById('brochure-sections-panel')?.getAttribute('data-rw-page-nav-panel-state') === 'open'
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        await page.ClickAsync(".brochure-page-nav a[href='#replacement']");
        await page.WaitForFunctionAsync(
            """
            () => {
              const active = document.querySelector('.brochure-page-nav a[href="#replacement"]');
              const toggle = document.querySelector('.brochure-page-nav [data-rw-page-nav-toggle]');
              const panel = document.getElementById('brochure-sections-panel');
              const panelStyle = panel ? getComputedStyle(panel) : null;
              return window.location.hash === '#replacement'
                && active?.getAttribute('aria-current') === 'location'
                && active?.getAttribute('data-rw-page-nav-active') === 'true'
                && active?.textContent?.trim() === 'Runtime Ownership'
                && toggle?.getAttribute('aria-expanded') === 'false'
                && panel?.getAttribute('data-rw-page-nav-panel-state') === 'closed'
                && panelStyle?.overflow === 'hidden'
                && getComputedStyle(active).display !== 'none'
                && getComputedStyle(active).boxShadow === 'none';
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    [Fact]
    public async Task PageNavigationSample_UpdatesActiveStateWhileScrolling()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1280,
                Height = 720
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.BaseUrl}/Navigation/PageNavigation");
        await page.WaitForFunctionAsync(
            """
            () => window.RazorWire?.pageNavigationManager
              && document.querySelector('.brochure-page-nav a[href="#trust"]')?.getAttribute('aria-current') === 'location'
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        await page.EvaluateAsync("() => document.getElementById('state')?.scrollIntoView({ block: 'start' })");
        await page.WaitForFunctionAsync(
            """
            () => document.querySelector('.brochure-page-nav a[href="#state"]')?.getAttribute('aria-current') === 'location'
              && document.querySelector('.brochure-page-nav a[href="#state"]')?.getAttribute('data-rw-page-nav-active') === 'true'
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        await page.EvaluateAsync("() => document.getElementById('handoff')?.scrollIntoView({ block: 'start' })");
        await page.WaitForFunctionAsync(
            """
            () => document.querySelector('.brochure-page-nav a[href="#handoff"]')?.getAttribute('aria-current') === 'location'
              && document.querySelectorAll('.brochure-page-nav [data-rw-page-nav-active="true"]').length === 1
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    [Fact]
    public async Task PageNavigationSample_RevealsActiveLinkInsideScrollableNavWithoutMovingDocument()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1280,
                Height = 720
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.BaseUrl}/Navigation/PageNavigation#replacement");
        await page.WaitForFunctionAsync(
            """
            () => window.RazorWire?.pageNavigationManager
              && document.querySelector('.brochure-page-nav a[href="#replacement"]')?.getAttribute('aria-current') === 'location'
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        var probe = await page.EvaluateAsync<PageNavigationRevealProbe>(
            """
            () => {
                const panel = document.getElementById('brochure-sections-panel');
                const links = [...document.querySelectorAll('.brochure-page-nav [data-rw-page-nav-link]')];
                if (!panel) throw new Error('Missing page-navigation panel.');

                panel.style.maxHeight = '96px';
                panel.style.overflowY = 'auto';
                panel.style.scrollPaddingTop = '12px';
                panel.style.scrollPaddingBottom = '12px';
                panel.scrollTop = 0;
                for (const link of links) {
                    link.style.display = 'block';
                    link.style.minHeight = '48px';
                }

                window.scrollTo(0, 240);
                const windowScrollBefore = window.scrollY;
                window.RazorWire.pageNavigationManager.syncActiveLinkVisibility();

                return {
                    PanelScrollTop: panel.scrollTop,
                    WindowScrollBefore: windowScrollBefore,
                    WindowScrollAfter: window.scrollY
                };
            }
            """);

        Assert.True(probe.PanelScrollTop > 0);
        Assert.InRange(Math.Abs(probe.WindowScrollAfter - probe.WindowScrollBefore), 0, 0.5);
    }

    [Fact]
    public async Task PageNavigationSample_NoJavaScriptFallbackAnchorsWork()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize
            {
                Width = 1280,
                Height = 720
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.BaseUrl}/Navigation/PageNavigation");

        Assert.Equal(1, await page.Locator(".brochure-page-nav a[href='#workflow']").CountAsync());
        Assert.Equal(1, await page.Locator("#workflow").CountAsync());

        await page.ClickAsync(".brochure-page-nav a[href='#replacement']");

        Assert.EndsWith("#replacement", page.Url, StringComparison.Ordinal);
        Assert.Equal(1, await page.Locator("#replacement").CountAsync());
        Assert.Equal(0, await page.Locator(".brochure-page-nav [data-rw-page-nav-active='true']").CountAsync());
    }

    [Fact]
    public async Task RuntimeIslands_OnlyStrategyHydratesClientModuleAndClearsServerContent()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);

        await page.EvaluateAsync(
            """
            () => {
                window.RazorWireIslandModules = { PlaywrightClientIsland: "/js/playwright-client-island.js" };
                const island = document.createElement('section');
                island.id = 'playwright-client-island';
                island.setAttribute('data-rw-module', 'PlaywrightClientIsland');
                island.setAttribute('data-rw-strategy', 'only');
                island.setAttribute('data-rw-props', JSON.stringify({ label: 'ready' }));
                island.innerHTML = '<p id="server-placeholder">server content</p>';
                document.body.appendChild(island);
                document.dispatchEvent(new Event('turbo:load'));
            }
            """);

        await page.WaitForFunctionAsync(
            """
            () => {
                const island = document.getElementById('playwright-client-island');
                return island?.getAttribute('data-rw-hydrated') === 'true'
                    && island?.dataset.clientMounted === 'true'
                    && island?.textContent === 'client:ready'
                    && document.getElementById('server-placeholder') === null;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    [Fact]
    public async Task PublishMessage_BroadcastsToOtherSession()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var username = $"playwright-{unique}";
        var message = $"hello from playwright {unique}";

        await using var senderContext = await _fixture.Browser.NewContextAsync();
        await using var receiverContext = await _fixture.Browser.NewContextAsync();

        var senderPage = await senderContext.NewPageAsync();
        var receiverPage = await receiverContext.NewPageAsync();

        await senderPage.GotoAsync(_fixture.ReactivityUrl);
        await receiverPage.GotoAsync(_fixture.ReactivityUrl);

        await WaitForStreamConnectedAsync(senderPage);
        await WaitForStreamConnectedAsync(receiverPage);
        await WaitForUserListReadyAsync(senderPage);
        await WaitForUserListReadyAsync(receiverPage);

        var registerResponse = await RegisterUserAndWaitForPostAsync(senderPage, username);
        Assert.True(registerResponse.Ok, $"RegisterUser POST failed with status {(int)registerResponse.Status}.");

        await WaitForUserInListAsync(receiverPage, username);

        var publishResponse = await PublishMessageAndWaitForPostAsync(senderPage, message);
        Assert.True(publishResponse.Ok, $"PublishMessage POST failed with status {(int)publishResponse.Status}.");

        await WaitForMessageAsync(receiverPage, unique);
        await WaitForMessageAsync(senderPage, unique);
    }

    [Fact]
    public async Task PublishMessage_PersistsAfterNavigatingAwayAndBack()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var message = $"persist after nav {unique}";

        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);

        var publishResponse = await PublishMessageAndWaitForPostAsync(page, message);
        Assert.True(publishResponse.Ok, $"PublishMessage POST failed with status {(int)publishResponse.Status}.");
        await WaitForMessageAsync(page, unique);

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Home", Exact = true }).First.ClickAsync();
        await WaitForPathAsync(page, "/");

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Reactivity", Exact = true }).First.ClickAsync();
        await WaitForPathAsync(page, "/Reactivity");
        await WaitForStreamConnectedAsync(page);
        await WaitForMessageAsync(page, unique);
    }

    [Fact]
    public async Task PublishMessage_PersistsAfterFullReload()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var message = $"persist after reload {unique}";

        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);

        var publishResponse = await PublishMessageAndWaitForPostAsync(page, message);
        Assert.True(publishResponse.Ok, $"PublishMessage POST failed with status {(int)publishResponse.Status}.");
        await WaitForMessageAsync(page, unique);

        await page.ReloadAsync();
        await WaitForPathAsync(page, "/Reactivity");
        await WaitForStreamConnectedAsync(page);
        await WaitForMessageAsync(page, unique);
    }

    [Fact]
    public async Task RegisterTwoUsers_FromSingleSession_WithoutRefresh_AntiforgeryAllowsBothPosts()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var userOne = $"user-a-{unique}";
        var userTwo = $"user-b-{unique}";

        await using var actorContext = await _fixture.Browser.NewContextAsync();
        await using var observerContext = await _fixture.Browser.NewContextAsync();

        var actorPage = await actorContext.NewPageAsync();
        var observerPage = await observerContext.NewPageAsync();

        await actorPage.GotoAsync(_fixture.ReactivityUrl);
        await observerPage.GotoAsync(_fixture.ReactivityUrl);

        await WaitForStreamConnectedAsync(actorPage);
        await WaitForStreamConnectedAsync(observerPage);
        await WaitForUserListReadyAsync(actorPage);
        await WaitForUserListReadyAsync(observerPage);

        await PlantNoRefreshMarkerAsync(actorPage);

        var registerOneResponse = await RegisterUserAndWaitForPostAsync(actorPage, userOne);
        Assert.True(registerOneResponse.Ok, $"First RegisterUser POST failed with status {(int)registerOneResponse.Status}.");
        await WaitForUserInListAsync(observerPage, userOne);

        var registerTwoResponse = await RegisterUserAndWaitForPostAsync(actorPage, userTwo);
        Assert.True(registerTwoResponse.Ok, $"Second RegisterUser POST failed with status {(int)registerTwoResponse.Status}.");
        await WaitForUserInListAsync(observerPage, userTwo);

        await AssertNoPageRefreshAsync(actorPage, _fixture.ReactivityUrl);
    }

    [Fact]
    public async Task RegisterUser_WithoutAntiforgeryToken_IsRejected()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);

        var response = await page.EvaluateAsync<int>(
            @"async () => {
                const body = new URLSearchParams({ username: 'invalid-no-token' });
                const res = await fetch('/Reactivity/RegisterUser', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body
                });
                return res.status;
            }");

        Assert.Equal(400, response);
    }

    [Fact]
    public async Task RegisterUser_SetsSecureUsernameCookie()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);

        var registerResponse = await RegisterUserAndWaitForPostAsync(page, "secure-cookie-user");
        Assert.True(registerResponse.Ok, $"RegisterUser POST failed with status {(int)registerResponse.Status}.");

        var usernameCookie = Assert.Single(
            await context.CookiesAsync(_fixture.BaseUrl),
            cookie => cookie.Name == "razorwire-username");

        Assert.Equal("secure-cookie-user", usernameCookie.Value);
        Assert.True(usernameCookie.Secure);
        Assert.True(usernameCookie.HttpOnly);
        Assert.Equal(SameSiteAttribute.Lax, usernameCookie.SameSite);
    }

    [Fact]
    public async Task IncrementCounter_ButtonHasAccessibleName()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForCounterReadyAsync(page);

        var incrementButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Increment counter",
            Exact = true
        });

        await incrementButton.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });
    }

    [Fact]
    public async Task ReadmeQuickstart_ReactivityCounter_UpdatesScoresWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);
        await WaitForCounterReadyAsync(page);

        await PlantNoRefreshMarkerAsync(page);
        var initialInstance = await GetIntTextAsync(page, "#instance-score-value");
        var initialSession = await GetIntTextAsync(page, "#session-score-value");
        var initialClientCount = await GetIntInputValueAsync(page, "#client-count-input");

        var incrementButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Increment counter",
            Exact = true
        });
        var incrementResponse = await page.RunAndWaitForResponseAsync(
            () => incrementButton.ClickAsync(),
            response => response.Url.Contains("/Reactivity/IncrementCounter", StringComparison.OrdinalIgnoreCase)
                        && response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = 45_000 });

        Assert.True(incrementResponse.Ok, $"README quickstart IncrementCounter POST failed with status {(int)incrementResponse.Status}.");
        await ExpectCounterValuesAsync(
            page,
            expectedInstance: initialInstance + 1,
            expectedSession: initialSession + 1,
            expectedClientCount: initialClientCount + 1);
        await AssertNoPageRefreshAsync(page, _fixture.ReactivityUrl);
    }

    [Fact]
    public async Task IncrementCounter_SingleSession_UpdatesValuesWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);
        await WaitForCounterReadyAsync(page);

        await PlantNoRefreshMarkerAsync(page);
        var initialInstance = await GetIntTextAsync(page, "#instance-score-value");
        var initialSession = await GetIntTextAsync(page, "#session-score-value");
        var initialClientCount = await GetIntInputValueAsync(page, "#client-count-input");

        var firstResponse = await SubmitAndWaitForPostAsync(page, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(firstResponse.Ok, $"First IncrementCounter POST failed with status {(int)firstResponse.Status}.");
        await ExpectCounterValuesAsync(
            page,
            expectedInstance: initialInstance + 1,
            expectedSession: initialSession + 1,
            expectedClientCount: initialClientCount + 1);

        var secondResponse = await SubmitAndWaitForPostAsync(page, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(secondResponse.Ok, $"Second IncrementCounter POST failed with status {(int)secondResponse.Status}.");
        await ExpectCounterValuesAsync(
            page,
            expectedInstance: initialInstance + 2,
            expectedSession: initialSession + 2,
            expectedClientCount: initialClientCount + 2);

        await AssertNoPageRefreshAsync(page, _fixture.ReactivityUrl);
    }

    [Fact]
    public async Task IncrementCounter_MultiSession_TracksSessionIndependentlyAndInstanceGlobally()
    {
        await using var firstContext = await _fixture.Browser.NewContextAsync();
        await using var secondContext = await _fixture.Browser.NewContextAsync();

        var firstPage = await firstContext.NewPageAsync();
        var secondPage = await secondContext.NewPageAsync();

        await firstPage.GotoAsync(_fixture.ReactivityUrl);
        await secondPage.GotoAsync(_fixture.ReactivityUrl);

        await WaitForStreamConnectedAsync(firstPage);
        await WaitForStreamConnectedAsync(secondPage);
        await WaitForCounterReadyAsync(firstPage);
        await WaitForCounterReadyAsync(secondPage);

        var firstInitialInstance = await GetIntTextAsync(firstPage, "#instance-score-value");
        var secondInitialInstance = await GetIntTextAsync(secondPage, "#instance-score-value");
        var globalBaseline = Math.Max(firstInitialInstance, secondInitialInstance);

        var firstIncrementResponse = await SubmitAndWaitForPostAsync(firstPage, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(firstIncrementResponse.Ok, $"First session IncrementCounter POST failed with status {(int)firstIncrementResponse.Status}.");
        await ExpectCounterValuesAsync(firstPage, expectedInstance: globalBaseline + 1, expectedSession: 1, expectedClientCount: 1);

        var secondIncrementResponse = await SubmitAndWaitForPostAsync(secondPage, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(secondIncrementResponse.Ok, $"Second session IncrementCounter POST failed with status {(int)secondIncrementResponse.Status}.");
        await ExpectCounterValuesAsync(secondPage, expectedInstance: globalBaseline + 2, expectedSession: 1, expectedClientCount: 1);
    }

    [Fact]
    public async Task IncrementCounter_SessionScorePersists_AcrossAllPageNavigation()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);
        await WaitForCounterReadyAsync(page);

        var incrementResponse = await SubmitAndWaitForPostAsync(page, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(incrementResponse.Ok, $"IncrementCounter POST failed with status {(int)incrementResponse.Status}.");
        await ExpectCounterValuesAsync(page, expectedSession: 1);

        await NavigateViaHeaderAndAssertSessionScoreAsync(page, linkText: "Home", expectedPath: "/", expectedSessionScore: 1);
        await NavigateViaHeaderAndAssertSessionScoreAsync(page, linkText: "Navigation", expectedPath: "/Navigation", expectedSessionScore: 1);
        await NavigateViaHeaderAndAssertSessionScoreAsync(page, linkText: "Reactivity", expectedPath: "/Reactivity", expectedSessionScore: 1);
    }

    [Fact]
    public async Task UnknownRoute_ShowsBrandedNotFoundPage_With404Status()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var missingUrl = $"{_fixture.BaseUrl}/missing-playwright-route";
        var response = await page.GotoAsync(missingUrl);

        Assert.NotNull(response);
        Assert.Equal(404, response!.Status);
        Assert.Equal(missingUrl, page.Url);

        await page.WaitForSelectorAsync("h1", new PageWaitForSelectorOptions
        {
            Timeout = 15_000
        });

        var heading = await page.TextContentAsync("h1");
        Assert.NotNull(heading);
        Assert.Contains("drifted out of orbit", heading, StringComparison.OrdinalIgnoreCase);

        var snapshot = await page.TextContentAsync("main");
        Assert.NotNull(snapshot);
        Assert.Contains("/missing-playwright-route", snapshot, StringComparison.Ordinal);

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Return Home", Exact = true }).ClickAsync();
        await WaitForPathAsync(page, "/");
    }

    [Fact]
    public async Task FormFailures_ServerValidation_RendersHandledLocalErrorWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[action*='SubmitValidationFailure']",
            "/Reactivity/SubmitValidationFailure");

        Assert.Equal(422, response.Status);
        Assert.Equal("true", await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#validation-errors", "Display name is required.");
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_ServerValidation_TrimsDisplayNameBeforeLengthValidation()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);
        await page.FillAsync("#failure-display-name", "  12345678901234567890  ");

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[action*='SubmitValidationFailure']",
            "/Reactivity/SubmitValidationFailure");

        Assert.Equal(200, response.Status);
        await WaitForTextAsync(page, "#validation-result", "Saved 12345678901234567890.");
        var validationErrors = await page.Locator("#validation-errors").InnerTextAsync();
        Assert.DoesNotContain("Display name must be 20 characters or fewer.", validationErrors);
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_MissingAntiforgery_RendersDevelopmentDiagnosticInLocalTarget()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[action*='AntiforgeryFailureDemo']",
            "/Reactivity/AntiforgeryFailureDemo");

        Assert.Equal(400, response.Status);
        Assert.Equal("true", await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#antiforgery-errors", "Antiforgery token validation failed");
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_AuthorizationFailure_RendersRuntimeFallbackWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[data-rw-form-failure-target='authorization-errors']",
            "/Reactivity/AuthorizationFailureDemo");

        Assert.Equal(403, response.Status);
        Assert.Null(await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#authorization-errors", "Session may have expired");
        await WaitForTextAsync(page, "#authorization-errors", "You may need to refresh or sign in again before submitting this form.");
        var generatedError = page.Locator("#authorization-errors [data-rw-form-error-generated='true']");
        await Assertions.Expect(generatedError).ToHaveAttributeAsync("role", "status");
        await Assertions.Expect(generatedError).ToHaveAttributeAsync("aria-live", "polite");
        await Assertions.Expect(generatedError).ToHaveAttributeAsync("tabindex", "-1");
        var generatedErrorId = await generatedError.GetAttributeAsync("id");
        Assert.False(string.IsNullOrWhiteSpace(generatedErrorId));
        var describedBy = await page.Locator("form[data-rw-form-failure-target='authorization-errors']").GetAttributeAsync("aria-describedby");
        Assert.NotNull(describedBy);
        Assert.Contains(generatedErrorId!, describedBy, StringComparison.Ordinal);
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_MalformedFailure_RendersRuntimeFallbackWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[data-rw-form-failure-target='custom-errors']",
            "/Reactivity/MalformedFailureDemo");

        Assert.Equal(400, response.Status);
        Assert.Null(await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#custom-errors", "We could not submit this form");
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_UnhandledServerFailure_RendersRuntimeFallbackWithoutHandledHeader()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[data-rw-form-failure-target='server-errors']",
            "/Reactivity/ServerFailureDemo");

        Assert.Equal(500, response.Status);
        Assert.Null(await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#server-errors", "Something went wrong");
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    private static async Task WaitForStreamConnectedAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            "() => document.body.getAttribute('data-rw-stream-reactivity') === 'connected'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private sealed class RuntimeContractProbe
    {
        public bool RuntimeInitialized { get; set; }

        public bool IslandsInitialized { get; set; }

        public bool HasRuntimeGlobal { get; set; }

        public string? FailureMode { get; set; }

        public bool HasFormFailureManager { get; set; }

        public string RuntimeScriptPath { get; set; } = string.Empty;

        public string IslandsScriptPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Describes the browser-sampled form payload used by the form-interactions integration proof.
    /// </summary>
    /// <remarks>
    /// Values come from <c>new FormData(form)</c> so the payload reflects disabled-control
    /// filtering and sparse ASP.NET Core <c>.index</c> submission semantics.
    /// </remarks>
    private sealed class FormDataProbe
    {
        /// <summary>
        /// Gets or sets the posted value for the app-owned delete marker being inspected.
        /// </summary>
        public string? DeleteValue { get; set; }

        /// <summary>
        /// Gets or sets the posted value for the inspected title field, or <see langword="null" /> when the field is disabled.
        /// </summary>
        public string? TitleValue { get; set; }

        /// <summary>
        /// Gets or sets all posted sparse <c>Actions.index</c> marker values in submission order.
        /// </summary>
        public string[] IndexValues { get; set; } = [];
    }

    /// <summary>
    /// Describes the browser-sampled readiness state used when form-interactions enhancement times out.
    /// </summary>
    /// <remarks>
    /// The timeout path combines static marker presence, loaded runtime state, enhancement state,
    /// and public manager diagnostics so failures explain whether markup, script loading, or runtime
    /// initialization broke.
    /// </remarks>
    private sealed class FormInteractionsReadinessProbe
    {
        /// <summary>
        /// Gets or sets a value indicating whether a form-toggle marker exists on the page.
        /// </summary>
        public bool HasToggleMarker { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a form-collection marker exists on the page.
        /// </summary>
        public bool HasCollectionMarker { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the split form-interactions runtime script is present.
        /// </summary>
        public bool HasRuntimeScript { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the runtime bootstrap flag was set.
        /// </summary>
        public bool Initialized { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether <c>window.RazorWire.formInteractionsManager</c> is available.
        /// </summary>
        public bool HasManager { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a form was marked enhanced by the runtime.
        /// </summary>
        public bool Enhanced { get; set; }

        /// <summary>
        /// Gets or sets the current public diagnostic messages exposed by the form-interactions manager.
        /// </summary>
        public string[] Diagnostics { get; set; } = [];
    }

    /// <summary>
    /// Describes browser state sampled after a same-document hash navigation.
    /// </summary>
    private sealed class HashNavigationProbe
    {
        /// <summary>
        /// Gets or sets the page-owned sentinel used to prove the document was preserved.
        /// </summary>
        public string Sentinel { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of observed <c>turbo:visit</c> events.
        /// </summary>
        public int VisitCount { get; set; }

        /// <summary>
        /// Gets or sets the number of observed <c>turbo:load</c> events.
        /// </summary>
        public int LoadCount { get; set; }
    }

    /// <summary>
    /// Describes the Playwright-to-C# deserialization contract for the page-navigation active-link reveal probe.
    /// </summary>
    /// <remarks>
    /// Values are sampled in the browser and must remain stable for deserialization. Future payload changes should
    /// preserve these member names or update the browser evaluation script and assertions together.
    /// </remarks>
    private sealed class PageNavigationRevealProbe
    {
        /// <summary>
        /// Gets or sets the reveal panel's <c>element.scrollTop</c>, sampled in CSS pixels.
        /// </summary>
        /// <remarks>The browser may report fractional scroll offsets, so the value is modeled as a <see cref="double" />.</remarks>
        public double PanelScrollTop { get; set; }

        /// <summary>
        /// Gets or sets <c>window.scrollY</c> before the reveal action, sampled in CSS pixels.
        /// </summary>
        /// <remarks>The browser may report fractional scroll offsets, so the value is modeled as a <see cref="double" />.</remarks>
        public double WindowScrollBefore { get; set; }

        /// <summary>
        /// Gets or sets <c>window.scrollY</c> after the reveal action, sampled in CSS pixels.
        /// </summary>
        /// <remarks>The browser may report fractional scroll offsets, so the value is modeled as a <see cref="double" />.</remarks>
        public double WindowScrollAfter { get; set; }
    }

    private static async Task WaitForMessageAsync(IPage page, string token)
    {
        await page.WaitForFunctionAsync(
            "args => document.querySelector('#messages')?.innerText?.includes(args.token) === true",
            new { token },
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task WaitForTextAsync(IPage page, string selector, string text)
    {
        await page.WaitForFunctionAsync(
            "args => document.querySelector(args.selector)?.innerText?.includes(args.text) === true",
            new { selector, text },
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task WaitForUserListReadyAsync(IPage page)
    {
        await page.WaitForSelectorAsync("#active-user-list", new PageWaitForSelectorOptions
        {
            Timeout = 30_000
        });
        await page.WaitForSelectorAsync("#user-list-items", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Attached
        });
        await page.WaitForFunctionAsync(
            @"() => {
                const list = document.querySelector('#active-user-list');
                return list instanceof HTMLElement
                    && !list.innerText.includes('Connecting to presence...');
            }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.WaitForFunctionAsync(
            @"() => {
                const count = document.querySelector('#user-count');
                return count instanceof HTMLElement
                    && /^\d+\s+ONLINE$/i.test(count.innerText.trim());
            }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task WaitForRegisterFormReadyAsync(IPage page)
    {
        const string formSelector = "#register-form";

        await page.WaitForSelectorAsync(formSelector, new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 30_000
        });
        await page.WaitForFunctionAsync(
            @"args => {
                const form = document.querySelector(args.formSelector);
                if (!(form instanceof HTMLFormElement)) {
                    return false;
                }

                const token = form.querySelector(""input[name='__RequestVerificationToken']"");
                const input = form.querySelector('#register-username');
                const submit = form.querySelector(""button[type='submit']"");

                return token instanceof HTMLInputElement
                    && token.value.trim().length > 0
                    && input instanceof HTMLInputElement
                    && !input.disabled
                    && submit instanceof HTMLButtonElement
                    && !submit.disabled;
            }",
            new { formSelector },
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task WaitForUserInListAsync(IPage page, string username)
    {
        await page.WaitForFunctionAsync(
            @"args => Array
                .from(document.querySelectorAll('#user-list-items li'))
                .some(item => item.textContent?.includes(args.username) === true)",
            new { username },
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task<IResponse> RegisterUserAndWaitForPostAsync(IPage page, string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username cannot be null or whitespace.", nameof(username));
        }

        const string formSelector = "#register-form";
        const string inputSelector = "#register-username";
        const string registerPath = "/Reactivity/RegisterUser";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await WaitForRegisterFormReadyAsync(page);

                return await page.RunAndWaitForResponseAsync(
                    () => page.EvaluateAsync(
                        @"args => {
                            const form = document.querySelector(args.formSelector);
                            if (!(form instanceof HTMLFormElement)) {
                                throw new Error('Register form was not found.');
                            }

                            const input = document.querySelector(args.inputSelector);
                            if (!(input instanceof HTMLInputElement)) {
                                throw new Error('Register username input was not found.');
                            }

                            input.value = args.username;
                            input.dispatchEvent(new Event('input', { bubbles: true }));
                            form.requestSubmit();
                        }",
                        new { formSelector, inputSelector, username }),
                    response => response.Url.Contains(registerPath, StringComparison.OrdinalIgnoreCase)
                                && response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase),
                    new PageRunAndWaitForResponseOptions { Timeout = 45_000 });
            }
            catch (PlaywrightException ex) when (
                attempt == 1 &&
                IsRetryableFormReplacementFailure(
                    ex,
                    "Register form was not found.",
                    "Register username input was not found."))
            {
                // Retry once to absorb in-flight register form replacement races on slower runners.
                await page.WaitForTimeoutAsync(300);
            }
        }

        throw new InvalidOperationException("RegisterUser request did not complete after retry.");
    }

    private static async Task<IResponse> PublishMessageAndWaitForPostAsync(IPage page, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Message cannot be null or whitespace.", nameof(message));
        }

        const string formSelector = "#message-form";
        const string publishPath = "/Reactivity/PublishMessage";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await page.WaitForSelectorAsync(formSelector, new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 30_000
                });

                return await page.RunAndWaitForResponseAsync(
                    () => page.EvaluateAsync(
                        @"args => {
                            const form = document.querySelector(args.formSelector);
                            if (!(form instanceof HTMLFormElement)) {
                                throw new Error('Publish form was not found.');
                            }

                            const input = form.querySelector(""input[name='message']"");
                            if (!(input instanceof HTMLInputElement)) {
                                throw new Error('Publish message input was not found.');
                            }

                            input.value = args.message;
                            input.dispatchEvent(new Event('input', { bubbles: true }));
                            form.requestSubmit();
                        }",
                        new { formSelector, message }),
                    response => response.Url.Contains(publishPath, StringComparison.OrdinalIgnoreCase)
                                && response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase),
                    new PageRunAndWaitForResponseOptions { Timeout = 45_000 });
            }
            catch (PlaywrightException ex) when (
                attempt == 1 &&
                IsRetryableFormReplacementFailure(
                    ex,
                    "Publish form was not found.",
                    "Publish message input was not found."))
            {
                // Retry once to absorb in-flight form replacement races on slower CI runners.
                await page.WaitForTimeoutAsync(300);
            }
        }

        throw new InvalidOperationException("PublishMessage request did not complete after retry.");
    }

    private static bool IsRetryableFormReplacementFailure(PlaywrightException ex, params string[] expectedMarkers)
    {
        if (ex.Message.Contains("Execution context was destroyed", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var marker in expectedMarkers)
        {
            if (ex.Message.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WaitForCounterReadyAsync(IPage page)
    {
        var selectorOptions = new PageWaitForSelectorOptions { Timeout = 30_000 };
        await page.WaitForSelectorAsync("[data-counter-form]", selectorOptions);
        await page.WaitForSelectorAsync("#instance-score-value", selectorOptions);
        await page.WaitForSelectorAsync("#session-score-value", selectorOptions);
        await page.WaitForSelectorAsync("#client-count-input", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Attached
        });
        await page.WaitForFunctionAsync(
            @"() => {
                const form = document.querySelector('[data-counter-form]');
                if (!(form instanceof HTMLFormElement)) {
                    return false;
                }

                const token = form.querySelector(""input[name='__RequestVerificationToken']"");
                const marker = form.querySelector(""input[name='__RazorWireForm']"");
                const clientCount = form.querySelector('#client-count-input');
                const submit = form.querySelector(""button[type='submit']"");

                return form.getAttribute('data-rw-form') === 'true'
                    && token instanceof HTMLInputElement
                    && token.value.trim().length > 0
                    && marker instanceof HTMLInputElement
                    && marker.value === '1'
                    && clientCount instanceof HTMLInputElement
                    && clientCount.value.trim().length > 0
                    && submit instanceof HTMLButtonElement
                    && !submit.disabled;
            }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task<IResponse> SubmitAndWaitForPostAsync(IPage page, string formSelector, string path)
    {
        await page.WaitForSelectorAsync(formSelector, new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 15_000
        });

        return await page.RunAndWaitForResponseAsync(
            () => page.Locator(formSelector).EvaluateAsync(
                @"form => {
                    if (!(form instanceof HTMLFormElement)) {
                        throw new Error('Target element is not an HTML form.');
                    }
                    form.requestSubmit();
                }"),
            response => response.Url.Contains(path, StringComparison.OrdinalIgnoreCase)
                        && response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = 45_000 });
    }

    private static async Task<int> GetIntTextAsync(IPage page, string selector)
    {
        var text = await page.Locator(selector).InnerTextAsync();
        var trimmed = text.Trim();
        if (int.TryParse(trimmed, out var parsed))
        {
            return parsed;
        }

        throw new FormatException($"GetIntTextAsync could not parse an integer from selector '{selector}'. Raw value: '{trimmed}'.");
    }

    private static async Task<int> GetIntInputValueAsync(IPage page, string selector)
    {
        var value = await page.EvaluateAsync<string>("selector => document.querySelector(selector)?.getAttribute('value') ?? ''", selector);
        var trimmed = value.Trim();
        if (int.TryParse(trimmed, out var parsed))
        {
            return parsed;
        }

        throw new FormatException($"GetIntInputValueAsync could not parse an integer from selector '{selector}'. Raw value: '{trimmed}'.");
    }

    private static async Task ExpectCounterValuesAsync(IPage page, int expectedInstance, int expectedSession, int expectedClientCount)
    {
        await page.WaitForFunctionAsync(
            @"args => {
                const instance = document.querySelector('#instance-score-value')?.textContent?.trim();
                const session = document.querySelector('#session-score-value')?.textContent?.trim();
                const clientCount = document.querySelector('#client-count-input')?.getAttribute('value')?.trim();
                return instance === String(args.expectedInstance)
                    && session === String(args.expectedSession)
                    && clientCount === String(args.expectedClientCount);
            }",
            new { expectedInstance, expectedSession, expectedClientCount },
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task ExpectCounterValuesAsync(IPage page, int expectedSession)
    {
        await page.WaitForFunctionAsync(
            "args => document.querySelector('#session-score-value')?.textContent?.trim() === String(args.expectedSession)",
            new { expectedSession },
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task NavigateViaHeaderAndAssertSessionScoreAsync(IPage page, string linkText, string expectedPath, int expectedSessionScore)
    {
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = linkText, Exact = true }).First.ClickAsync();
        await WaitForPathAsync(page, expectedPath);
        await WaitForCounterReadyAsync(page);
        await ExpectCounterValuesAsync(page, expectedSessionScore);
    }

    private static async Task WaitForPathAsync(IPage page, string expectedPath)
    {
        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path",
            expectedPath,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static ConcurrentQueue<string> CaptureBrowserMessages(IPage page)
    {
        var messages = new ConcurrentQueue<string>();
        page.Console += (_, message) =>
        {
            messages.Enqueue($"{message.Type}: {message.Text}");
        };
        page.PageError += (_, exception) =>
        {
            messages.Enqueue($"pageerror: {exception}");
        };
        page.RequestFailed += (_, request) =>
        {
            messages.Enqueue($"requestfailed: {request.Url} {request.Failure}");
        };

        return messages;
    }

    /// <summary>
    /// Waits until the RazorWire form-interactions runtime has loaded, exposed its manager, and enhanced the sample form.
    /// </summary>
    /// <param name="page">The Playwright page hosting the sample.</param>
    /// <param name="browserMessages">Thread-safe browser console, page error, and request failure messages captured during navigation.</param>
    /// <returns>A task that completes when the sample is enhanced.</returns>
    /// <remarks>
    /// On timeout, the helper samples browser readiness markers and public RazorWire diagnostics before
    /// throwing so test failures identify whether markup, asset loading, or runtime initialization is missing.
    /// </remarks>
    private static async Task WaitForFormInteractionsReadyAsync(IPage page, ConcurrentQueue<string> browserMessages)
    {
        try
        {
            await page.WaitForFunctionAsync(
                """
                () => window.RazorWire?.formInteractionsManager
                  && document.querySelector('[data-rw-form-interactions-enhanced="true"]')
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 15_000 });
        }
        catch (TimeoutException exception)
        {
            var probe = await page.EvaluateAsync<FormInteractionsReadinessProbe>(
                """
                () => ({
                    HasToggleMarker: Boolean(document.querySelector('[data-rw-form-toggle]')),
                    HasCollectionMarker: Boolean(document.querySelector('[data-rw-form-collection]')),
                    HasRuntimeScript: Boolean(document.querySelector('script[src*="/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js"]')),
                    Initialized: window.RazorWireFormInteractionsInitialized === true,
                    HasManager: Boolean(window.RazorWire?.formInteractionsManager),
                    Enhanced: Boolean(document.querySelector('[data-rw-form-interactions-enhanced="true"]')),
                    Diagnostics: window.RazorWire?.formInteractionsManager?.getDiagnostics?.().map(diagnostic => diagnostic.message) ?? []
                })
                """);
            var message = $"""
                Timed out waiting for RazorWire form interactions to enhance the sample form.
                Probe: markers toggle={probe.HasToggleMarker}, collection={probe.HasCollectionMarker}; script={probe.HasRuntimeScript}; initialized={probe.Initialized}; manager={probe.HasManager}; enhanced={probe.Enhanced}; diagnostics=[{string.Join("; ", probe.Diagnostics)}].
                Browser messages: {string.Join(" | ", browserMessages.ToArray())}
                """;

            throw new TimeoutException(message, exception);
        }
    }

    private static async Task PlantNoRefreshMarkerAsync(IPage page)
    {
        await page.EvaluateAsync("() => { window.__noRefreshMarker = Date.now().toString(); }");
    }

    private static async Task AssertNoPageRefreshAsync(IPage page, string expectedUrl)
    {
        var markerExists = await page.EvaluateAsync<bool>("() => !!window.__noRefreshMarker");
        Assert.True(markerExists, "Expected window.__noRefreshMarker to persist, but it was missing (likely full page refresh).");
        Assert.Equal(expectedUrl.TrimEnd('/'), page.Url.TrimEnd('/'));
    }
}

public sealed class RazorWireMvcPlaywrightFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim PlaywrightInstallLock = new(1, 1);
    private static bool _playwrightInstalled;
    private static readonly Regex ListeningUrlRegex = new(@"Now listening on:\s*(https?://\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmptyUserCountRegex = new(
        "id=\"user-count\"[^>]*>\\s*0(?:\\s|<)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ConcurrentQueue<string> _appLogs = new();
    private readonly TaskCompletionSource<string> _boundBaseUrlSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CliWrapProcessLease? _appProcess;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; private set; } = string.Empty;
    public string ReactivityUrl { get; private set; } = string.Empty;
    public string FormFailuresUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await EnsurePlaywrightInstalledAsync();

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        StartExampleApp("http://127.0.0.1:0");
        var boundBaseUrl = await WaitForBoundBaseUrlAsync(TimeSpan.FromSeconds(60));
        var browserBaseUrl = ReplaceLoopbackIpHostWithLocalhost(boundBaseUrl);

        BaseUrl = browserBaseUrl;
        ReactivityUrl = $"{browserBaseUrl}/Reactivity";
        FormFailuresUrl = $"{browserBaseUrl}/Reactivity/FormFailures";

        await WaitForAppReadyAsync(boundBaseUrl, TimeSpan.FromSeconds(60));
        await WarmReactivitySurfaceAsync(boundBaseUrl, TimeSpan.FromSeconds(60));
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (Browser is not null)
            {
                await Browser.DisposeAsync();
            }

            _playwright?.Dispose();
        }
        finally
        {
            await DisposeAppProcessAsync();
        }
    }

    private static async Task EnsurePlaywrightInstalledAsync()
    {
        if (_playwrightInstalled)
        {
            return;
        }

        await PlaywrightInstallLock.WaitAsync();
        try
        {
            if (_playwrightInstalled)
            {
                return;
            }

            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
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

    private void StartExampleApp(string baseUrl)
    {
        const string readmeProjectPath = "examples/razorwire-mvc/RazorWireWebExample.csproj";
        var repoRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var projectPath = TestPathUtils.PathUnder(repoRoot, readmeProjectPath);

        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("Could not find RazorWire MVC example project.", projectPath);
        }

        var appProcess = CliWrapProcessLease.Start(Cli.Wrap("dotnet")
            .WithArguments(BuildExampleAppArguments(repoRoot, readmeProjectPath))
            .WithWorkingDirectory(repoRoot)
            .WithEnvironmentVariables(new Dictionary<string, string?>
            {
                ["ASPNETCORE_URLS"] = baseUrl,
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(CaptureAppLog))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(CaptureAppLog)));
        _appProcess = appProcess;

        _ = appProcess.Completion.ContinueWith(
            task =>
            {
                if (appProcess.IsCancellationRequested)
                {
                    return;
                }

                _boundBaseUrlSource.TrySetException(
                    new InvalidOperationException($"RazorWire MVC example {DescribeExampleAppExit(task)} before publishing a listening URL.{Environment.NewLine}{GetRecentLogs()}"));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static IReadOnlyList<string> BuildExampleAppArguments(string repoRoot, string projectPath)
    {
        var configuration = ResolveCurrentConfiguration();
        var targetFramework = "net10.0";
        var builtAssemblyPath = Path.Combine(
            repoRoot,
            "examples",
            "razorwire-mvc",
            "bin",
            configuration,
            targetFramework,
            "RazorWireWebExample.dll");

        if (File.Exists(builtAssemblyPath))
        {
            return [builtAssemblyPath];
        }

        return ["run", "--project", projectPath, "--no-launch-profile", "--configuration", configuration];
    }

    private static string DescribeExampleAppExit(Task<CliCommandResult> task)
    {
        if (task.IsFaulted)
        {
            return $"failed with {task.Exception.GetBaseException().Message}";
        }

        if (task.IsCanceled)
        {
            return "was canceled";
        }

        return $"exited with code {task.Result.ExitCode}";
    }

    private async ValueTask DisposeAppProcessAsync()
    {
        if (_appProcess is null)
        {
            return;
        }

        await _appProcess.DisposeAsync();
        _appProcess = null;
    }

    private static string ResolveCurrentConfiguration()
    {
        var baseDirectoryParts = AppContext.BaseDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < baseDirectoryParts.Length - 1; index++)
        {
            if (string.Equals(baseDirectoryParts[index], "bin", StringComparison.OrdinalIgnoreCase))
            {
                return baseDirectoryParts[index + 1];
            }
        }

        return "Debug";
    }

    private async Task<string> WaitForBoundBaseUrlAsync(TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var registration = timeoutCts.Token.Register(
            () => _boundBaseUrlSource.TrySetException(
                new TimeoutException($"RazorWire MVC example did not publish a listening URL within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}")));

        return await _boundBaseUrlSource.Task;
    }

    private static string ReplaceLoopbackIpHostWithLocalhost(string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);

        if (!IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            return baseUrl;
        }

        var builder = new UriBuilder(uri)
        {
            Host = "localhost"
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private async Task WaitForAppReadyAsync(string baseUrl, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var client = new HttpClient();

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            if (_appProcess is null)
            {
                throw new InvalidOperationException($"RazorWire MVC example exited before it became ready.{Environment.NewLine}{GetRecentLogs()}");
            }

            if (_appProcess.Completion.IsCompleted)
            {
                throw new InvalidOperationException($"RazorWire MVC example {DescribeExampleAppExit(_appProcess.Completion)} before it became ready.{Environment.NewLine}{GetRecentLogs()}");
            }

            try
            {
                using var response = await client.GetAsync(baseUrl, timeoutCts.Token);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Application is still starting.
            }
            catch (TaskCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"RazorWire MVC example did not become ready within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
            }
            catch (TaskCanceledException) when (!timeoutCts.IsCancellationRequested)
            {
                // Retry until global timeout.
            }

            try
            {
                await Task.Delay(250, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"RazorWire MVC example did not become ready within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
            }
        }

        throw new TimeoutException($"RazorWire MVC example did not become ready within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
    }

    private async Task WarmReactivitySurfaceAsync(string baseUrl, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute)
        };

        using var _ = await WaitForOkAsync(client, "/Reactivity", timeoutCts.Token);
        await WaitForEmptyUserListAsync(client, "/Reactivity/UserList", timeoutCts.Token);
        using var __ = await WaitForOkAsync(client, "/Reactivity/FormFailures", timeoutCts.Token);
    }

    private async Task<HttpResponseMessage> WaitForOkAsync(HttpClient client, string path, CancellationToken token)
    {
        while (true)
        {
            try
            {
                var response = await client.GetAsync(path, token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException)
            {
                // Application is still starting.
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                // Retry until the overall warm-up timeout expires.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }

            await DelayWarmupRetryAsync(token);
        }

        throw new TimeoutException($"RazorWire MVC warm-up timed out waiting for '{path}'.{Environment.NewLine}{GetRecentLogs()}");
    }

    private async Task WaitForEmptyUserListAsync(HttpClient client, string path, CancellationToken token)
    {
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(path, token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var html = await response.Content.ReadAsStringAsync(token);
                    if (IsEmptyUserListMarkup(html))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Application is still starting.
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                // Retry until the overall warm-up timeout expires.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }

            await DelayWarmupRetryAsync(token);
        }

        throw new TimeoutException($"RazorWire MVC warm-up timed out waiting for an empty '{path}' surface.{Environment.NewLine}{GetRecentLogs()}");
    }

    private static bool IsEmptyUserListMarkup(string html)
    {
        return html.Contains("id=\"active-user-list\"", StringComparison.Ordinal)
               && html.Contains("id=\"user-list-items\"", StringComparison.Ordinal)
               && html.Contains("id=\"user-list-empty\"", StringComparison.Ordinal)
               && EmptyUserCountRegex.IsMatch(html);
    }

    private async Task DelayWarmupRetryAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Caller converts cancellation into a timeout with recent logs.
        }
    }

    private void CaptureAppLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var match = ListeningUrlRegex.Match(message);
        if (match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri))
        {
            var normalizedBaseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            _boundBaseUrlSource.TrySetResult(normalizedBaseUrl);
        }

        _appLogs.Enqueue(message);
        while (_appLogs.Count > 200)
        {
            _appLogs.TryDequeue(out _);
        }
    }

    private string GetRecentLogs()
    {
        return string.Join(Environment.NewLine, _appLogs);
    }

    private sealed class CliWrapProcessLease : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly CommandTask<CliCommandResult> _commandTask;

        private CliWrapProcessLease(CliWrap.Command command)
        {
            _commandTask = command.ExecuteAsync(_cancellation.Token);
        }

        public Task<CliCommandResult> Completion => _commandTask.Task;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public static CliWrapProcessLease Start(CliWrap.Command command)
        {
            return new CliWrapProcessLease(command);
        }

        public async ValueTask DisposeAsync()
        {
            using var cancellation = _cancellation;
            using var commandTask = _commandTask;

            await cancellation.CancelAsync();

            try
            {
                await commandTask.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // CliWrap converts cancellation into process-tree termination for this test child process.
            }
            catch (TimeoutException)
            {
                // Best-effort fixture cleanup should not hide the original test failure.
            }
        }
    }

}
