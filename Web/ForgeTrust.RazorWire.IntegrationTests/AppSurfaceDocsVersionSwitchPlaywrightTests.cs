using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(AppSurfaceDocsVersionSwitchIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsVersionSwitchPlaywrightTests : IClassFixture<AppSurfaceDocsVersionSwitchFixture>
{
    private readonly AppSurfaceDocsVersionSwitchFixture _versions;
    private readonly AppSurfaceDocsVersionSwitchPlaywrightFixture _browser;

    public AppSurfaceDocsVersionSwitchPlaywrightTests(
        AppSurfaceDocsVersionSwitchFixture versions,
        AppSurfaceDocsVersionSwitchPlaywrightFixture browser)
    {
        _versions = versions;
        _browser = browser;
    }

    [Fact]
    public async Task StaticSwitch_UsesEarlyMetaCspAndPreservesMovedConceptAndVerifiedFragment()
    {
        var pagesRoot = await _versions.EnsureStaticPublicationAsync();
        await using var host = await AppSurfaceDocsVersionSwitchStaticHost.StartAsync(pagesRoot);
        await using var context = await _browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var targetRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.setup", "en");
        var sourceUrl = _versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute) + "#configure";
        var targetUrl = _versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", targetRoute);
        var targetPath = new Uri(targetUrl).AbsolutePath;

        var sourceResponse = await page.GotoAsync(sourceUrl);
        Assert.NotNull(sourceResponse);
        Assert.Equal(200, sourceResponse.Status);
        Assert.Equal("CSS1Compat", await page.EvaluateAsync<string>("() => document.compatMode"));
        Assert.Null(await sourceResponse.HeaderValueAsync("Content-Security-Policy"));

        var policy = Assert.IsType<string>(await page.Locator("meta[http-equiv='Content-Security-Policy']").GetAttributeAsync("content"));
        Assert.Contains("connect-src 'none'", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sandbox", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("frame-ancestors", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.OrdinalIgnoreCase);
        var metaIndex = await page.EvaluateAsync<int>(
            "() => Array.from(document.head.children).indexOf(document.querySelector('meta[http-equiv=\"Content-Security-Policy\"]'))");
        Assert.Equal(0, metaIndex);

        var runtimeScript = page.Locator("script[data-docs-switch-runtime]");
        var runtimePath = Assert.IsType<string>(await runtimeScript.GetAttributeAsync("src"));
        Assert.Contains("/docs/versions/switch/runtime.js", runtimePath, StringComparison.Ordinal);
        var sri = Assert.IsType<string>(await runtimeScript.GetAttributeAsync("integrity"));
        using var client = new HttpClient();
        var runtimeBytes = await client.GetByteArrayAsync(new Uri(new Uri(host.BaseUrl), runtimePath));
        Assert.Equal("sha256-" + Convert.ToBase64String(SHA256.HashData(runtimeBytes)), sri);
        var scriptDirective = policy.Split(';').Select(directive => directive.Trim())
            .Single(directive => directive.StartsWith("script-src ", StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"script-src '{sri}'", scriptDirective);

        var toggle = page.Locator("button[data-docs-version-toggle]");
        await toggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await toggle.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var options = page.Locator("[data-docs-version-options]");
        Assert.Equal("true", await toggle.GetAttributeAsync("aria-expanded"));
        Assert.False(await options.IsHiddenAsync());
        await page.Keyboard.PressAsync("Escape");
        Assert.Equal("false", await toggle.GetAttributeAsync("aria-expanded"));
        Assert.True(await options.IsHiddenAsync());

        await toggle.ClickAsync();
        var counterpart = page.Locator("[data-docs-version-options] a[href*='/docs/v/2.0.0/']").First;
        var counterpartHref = Assert.IsType<string>(await counterpart.GetAttributeAsync("href"));
        Assert.Equal(targetPath, new Uri(new Uri(host.BaseUrl), counterpartHref).AbsolutePath);
        Assert.Contains("docsFrom=", counterpartHref, StringComparison.Ordinal);
        await counterpart.ClickAsync();
        await page.WaitForFunctionAsync(
            "expectedPath => window.location.pathname === expectedPath && window.location.hash === '#configure'",
            targetPath,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Contains("Getting started with configuration", await page.Locator("main h1").First.TextContentAsync(), StringComparison.Ordinal);
        Assert.Equal("CSS1Compat", await page.EvaluateAsync<string>("() => document.compatMode"));
        Assert.Contains("This guide has a different location in 2.0.0.", await page.Locator("[data-docs-version-notice]").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal("configure", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''"));
        var describedBy = await page.Locator("#configure").GetAttributeAsync("aria-describedby");
        Assert.Equal(1, (describedBy ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(value => value == "docs-version-explanation"));
        var canonicalHref = await page.Locator("link[rel='canonical']").GetAttributeAsync("href");
        Assert.Equal(targetPath, new Uri(new Uri(host.BaseUrl), Assert.IsType<string>(canonicalHref)).AbsolutePath);
        await page.SetViewportSizeAsync(320, 900);
        await page.EvaluateAsync("() => { document.documentElement.style.zoom = '2'; }");
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth"));
        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    [Fact]
    public async Task StaticSwitch_PartialDomLifecycleClaimsOnlyTheCurrentVerifiedDocument()
    {
        var pagesRoot = await _versions.EnsureStaticPublicationAsync();
        await using var host = await AppSurfaceDocsVersionSwitchStaticHost.StartAsync(pagesRoot);
        await using var context = await _browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var targetRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.setup", "en");
        var sourceUrl = _versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute);
        var targetUrl = _versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", targetRoute);

        var sourceResponse = await page.GotoAsync(sourceUrl);
        Assert.NotNull(sourceResponse);
        Assert.Equal(200, sourceResponse.Status);
        var sourceKey = await page.EvaluateAsync<string>(
            "() => JSON.parse(document.querySelector('script[data-docs-version-data]').textContent).sourceKey");
        Assert.Matches("^[0-9a-f]{64}$", sourceKey);

        // Fetch the independently published counterpart outside the sandboxed browser, then
        // model the markup replacement performed by a partial-page navigation framework.
        using var client = new HttpClient();
        var targetHtml = await client.GetStringAsync(targetUrl);
        Assert.Contains("Getting started with configuration", targetHtml, StringComparison.Ordinal);
        var transitionUrl = targetUrl + "?docsFrom=" + Uri.EscapeDataString(sourceKey) + "#configure";

        await page.EvaluateAsync("""
            ({ html, transitionUrl }) => {
                const parsed = new DOMParser().parseFromString(html, 'text/html');
                parsed.querySelectorAll('script[data-docs-switch-runtime]').forEach(script => script.remove());
                document.body.replaceChildren(...Array.from(parsed.body.childNodes, node => document.importNode(node, true)));
                history.pushState({}, '', transitionUrl);

                const lifecycleEvents = [
                    'turbo:render',
                    'turbo:load',
                    'turbo:frame-load',
                    'razorwire:page-nav:active-change'
                ];
                for (let pass = 0; pass < 2; pass++) {
                    lifecycleEvents.forEach(name => document.dispatchEvent(new Event(name)));
                }
            }
            """, new { html = targetHtml, transitionUrl });

        Assert.Contains("Getting started with configuration", await page.Locator("main h1").First.TextContentAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await page.Locator("[data-docs-version-notice]").CountAsync());
        var targetPolicy = await page.Locator("meta[http-equiv='Content-Security-Policy']").GetAttributeAsync("content");
        Assert.Contains("connect-src 'none'", targetPolicy ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("This guide has a different location in 2.0.0.",
            await page.Locator("[data-docs-version-notice]").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal("configure", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''"));
        var describedBy = await page.Locator("#configure").GetAttributeAsync("aria-describedby");
        Assert.Equal(1, (describedBy ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(value => value == "docs-version-explanation"));

        await page.EvaluateAsync("""
            sourceUrl => {
                history.pushState({}, '', sourceUrl);
                for (let pass = 0; pass < 2; pass++) {
                    document.dispatchEvent(new Event('turbo:render'));
                    document.dispatchEvent(new Event('turbo:load'));
                    document.dispatchEvent(new Event('turbo:frame-load'));
                    document.dispatchEvent(new Event('razorwire:page-nav:active-change'));
                }
            }
            """, sourceUrl);

        Assert.Equal(0, await page.Locator("[data-docs-version-notice]").CountAsync());
        Assert.Equal(0, await page.Locator("#configure[aria-describedby~='docs-version-explanation']").CountAsync());
        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    [Fact]
    public async Task StaticChoicePage_NoJavaScriptOffersDirectTargetForMovedConcept()
    {
        var pagesRoot = await _versions.EnsureStaticPublicationAsync();
        await using var host = await AppSurfaceDocsVersionSwitchStaticHost.StartAsync(pagesRoot);
        await using var context = await _browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var targetRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.setup", "en");
        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));

        Assert.True(await page.Locator("a[data-docs-version-fallback]").IsVisibleAsync());
        Assert.True(await page.Locator("button[data-docs-version-toggle]").IsHiddenAsync());
        IResponse? choiceResponse = null;
        page.Response += (_, response) =>
        {
            if (response.Request.ResourceType == "document"
                && new Uri(response.Url).AbsolutePath.Contains("/docs/versions/switch/", StringComparison.Ordinal))
            {
                choiceResponse = response;
            }
        };
        await page.Locator("a[data-docs-version-fallback]").ClickAsync();
        await page.WaitForSelectorAsync("main.docs-version-choice", new PageWaitForSelectorOptions
        {
            Timeout = 15_000,
            State = WaitForSelectorState.Visible
        });

        Assert.NotNull(choiceResponse);
        Assert.Null(await choiceResponse.HeaderValueAsync("Content-Security-Policy"));
        var bodyText = await page.Locator("main.docs-version-choice").InnerTextAsync();
        Assert.Contains("This guide moved", bodyText, StringComparison.Ordinal);
        Assert.Contains("2.0.0", bodyText, StringComparison.Ordinal);
        Assert.Contains("Return to original guide", bodyText, StringComparison.Ordinal);
        Assert.Equal(0, await page.Locator("script[data-docs-switch-runtime]").CountAsync());
        await AssertChoicePageFocusAndTargetsAsync(page, "main.docs-version-choice h1");
        await page.ReloadAsync();
        await AssertChoicePageFocusAndTargetsAsync(page, "main.docs-version-choice h1");
        await AssertChoicePageResponsiveLayoutAsync(page);

        var targetLink = page.Locator("main.docs-version-choice a").Filter(new LocatorFilterOptions { HasText = "Open this guide" }).First;
        var targetHref = Assert.IsType<string>(await targetLink.GetAttributeAsync("href"));
        Assert.Equal(new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", targetRoute)).AbsolutePath,
            new Uri(new Uri(host.BaseUrl), targetHref).AbsolutePath);
        Assert.Contains("docsFrom=", targetHref, StringComparison.Ordinal);

        await targetLink.ClickAsync();
        await page.WaitForFunctionAsync(
            "expectedPath => window.location.pathname === expectedPath",
            new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", targetRoute)).AbsolutePath,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        Assert.Contains("Getting started with configuration", await page.Locator("main h1").First.TextContentAsync(), StringComparison.Ordinal);
        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    // Value: protects=static plain-link recovery reaches target-version search without a foreign-language counterpart;
    // fails_when=a foreign-only concept hides valid selected-language search or becomes the selected counterpart;
    // why_new=the previous static absence case did not include a counterpart in another language; seam=none;
    // no_claim=partial-page network navigation or translation quality.
    [Theory]
    [InlineData("guides.removed")]
    [InlineData("guides.locale-example")]
    public async Task StaticChoicePage_NoJavaScriptOffersTargetLocalSearchForKnownAbsence(string conceptId)
    {
        var pagesRoot = await _versions.EnsureStaticPublicationAsync();
        await using var host = await AppSurfaceDocsVersionSwitchStaticHost.StartAsync(pagesRoot);
        await using var context = await _browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", conceptId, "en");
        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));
        var fallbackHref = Assert.IsType<string>(await page.Locator("a[data-docs-version-fallback]").GetAttributeAsync("href"));
        var selectedReasonHref = Assert.IsType<string>(await page.Locator("[data-docs-version-options] a[href*='docs-switch-version-']").First.GetAttributeAsync("href"));

        await page.GotoAsync(new Uri(new Uri(host.BaseUrl), fallbackHref).AbsoluteUri);
        await page.WaitForSelectorAsync("main.docs-version-choice", new PageWaitForSelectorOptions
        {
            Timeout = 15_000,
            State = WaitForSelectorState.Visible
        });
        await AssertChoicePageFocusAndTargetsAsync(page, "main.docs-version-choice h1");
        await page.ReloadAsync();
        await AssertChoicePageFocusAndTargetsAsync(page, "main.docs-version-choice h1");
        await AssertChoicePageResponsiveLayoutAsync(page);

        await page.GotoAsync(new Uri(new Uri(host.BaseUrl), selectedReasonHref).AbsoluteUri);
        var selectedReasonId = new Uri(new Uri(host.BaseUrl), selectedReasonHref).Fragment.TrimStart('#');
        await AssertChoicePageFocusAndTargetsAsync(page, $"main.docs-version-choice h2#{selectedReasonId}");
        await AssertChoicePageResponsiveLayoutAsync(page);

        var bodyText = await page.Locator("main.docs-version-choice").InnerTextAsync();
        Assert.Contains("No documented counterpart", bodyText, StringComparison.Ordinal);
        Assert.Equal(0, await page.Locator("main.docs-version-choice a[href*='/docs/v/2.0.0/fr/']").CountAsync());
        var searchLink = page.Locator("main.docs-version-choice a").Filter(new LocatorFilterOptions { HasText = "Search this version" }).First;
        var searchHref = Assert.IsType<string>(await searchLink.GetAttributeAsync("href"));
        Assert.Contains("/docs/v/2.0.0/", searchHref, StringComparison.Ordinal);
        using var client = new HttpClient();
        var searchResponse = await client.GetAsync(new Uri(new Uri(host.BaseUrl), searchHref));
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        Assert.Contains("Search", await searchResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await page.GotoAsync(new Uri(new Uri(host.BaseUrl), searchHref).AbsoluteUri);
        Assert.Equal("CSS1Compat", await page.EvaluateAsync<string>("() => document.compatMode"));
        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    [Fact]
    public async Task HostedChoicePage_NoJavaScriptFocusesGenericAndSelectedReasons()
    {
        await using var host = await StartHostedHostAsync();
        await using var context = await _browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.removed", "en");
        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));
        var fallbackHref = Assert.IsType<string>(await page.Locator("a[data-docs-version-fallback]").GetAttributeAsync("href"));
        var selectedReasonHref = Assert.IsType<string>(await page.Locator("[data-docs-version-options] a[href*='docs-switch-version-']").First.GetAttributeAsync("href"));

        await page.GotoAsync(new Uri(new Uri(host.BaseUrl), fallbackHref).AbsoluteUri);
        await AssertChoicePageFocusAndTargetsAsync(page, "main.docs-version-choice h1");
        await page.ReloadAsync();
        await AssertHostedChoicePageReloadKeyboardAsync(page).WaitAsync(TimeSpan.FromSeconds(5));
        await AssertChoicePageTargetsAsync(page);
        await AssertChoicePageResponsiveLayoutAsync(page);

        await page.GotoAsync(new Uri(new Uri(host.BaseUrl), selectedReasonHref).AbsoluteUri);
        var selectedReasonId = new Uri(new Uri(host.BaseUrl), selectedReasonHref).Fragment.TrimStart('#');
        await AssertChoicePageFocusAndTargetsAsync(page, $"main.docs-version-choice h2#{selectedReasonId}");
        await AssertChoicePageResponsiveLayoutAsync(page);
    }

    [Fact]
    public async Task StaticSwitch_RejectsTamperedRuntimeUnrelatedScriptAndConnections()
    {
        var pagesRoot = await _versions.EnsureStaticPublicationAsync();
        await using var host = await AppSurfaceDocsVersionSwitchStaticHost.StartAsync(pagesRoot);
        await using var context = await _browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.AddInitScriptAsync("window.__changedRuntimeExecuted = false; window.__unrelatedRuntimeExecuted = false;");
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var requestedUri = new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));
        const string unrelatedBody = "window.__unrelatedRuntimeExecuted = true;";
        var unrelatedSri = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(unrelatedBody)));
        await AddDocumentMutationRouteAsync(page, requestedUri.AbsolutePath, html => AddUnrelatedScript(html, unrelatedSri));

        await page.RouteAsync("**/docs/versions/switch/runtime.js", async route =>
        {
            var response = await route.FetchAsync();
            var altered = await response.TextAsync() + "\nwindow.__changedRuntimeExecuted = true;";
            await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = altered });
        });
        await page.RouteAsync("**/docs/unrelated-version-switch.js", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "text/javascript",
            Body = unrelatedBody
        }));

        var response = await page.GotoAsync(requestedUri.ToString());
        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        Assert.Null(await response.HeaderValueAsync("Content-Security-Policy"));
        Assert.True(await page.Locator("a[data-docs-version-fallback]").IsVisibleAsync());
        Assert.True(await page.Locator("button[data-docs-version-toggle]").IsHiddenAsync());
        Assert.False(await page.EvaluateAsync<bool>("() => window.__changedRuntimeExecuted || window.__unrelatedRuntimeExecuted"));

        var connectionAttempts = 0;
        await page.RouteAsync("**/docs/connection-probe", route =>
        {
            connectionAttempts++;
            return route.FulfillAsync(new RouteFulfillOptions { Status = 200, Body = "ok" });
        });
        var fetchError = await page.EvaluateAsync<string>("async () => { try { await fetch('/docs/connection-probe'); return ''; } catch (error) { return String(error); } }");
        Assert.NotEmpty(fetchError);
        Assert.Equal(0, connectionAttempts);
        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("missing")]
    public async Task StaticSwitch_WrongOrMissingIntegrityKeepsNoScriptFallbackAvailable(string integrityMode)
    {
        var pagesRoot = await _versions.EnsureStaticPublicationAsync();
        await using var host = await AppSurfaceDocsVersionSwitchStaticHost.StartAsync(pagesRoot);
        await using var context = await _browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var requestedUri = new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));
        await AddDocumentMutationRouteAsync(page, requestedUri.AbsolutePath, html => MutateRuntimeIntegrity(html, integrityMode));

        var response = await page.GotoAsync(requestedUri.ToString());
        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        Assert.Null(await response.HeaderValueAsync("Content-Security-Policy"));
        var policy = Assert.IsType<string>(await page.Locator("meta[http-equiv='Content-Security-Policy']").GetAttributeAsync("content"));
        Assert.Contains("connect-src 'none'", policy, StringComparison.OrdinalIgnoreCase);
        Assert.True(await page.Locator("a[data-docs-version-fallback]").IsVisibleAsync());
        Assert.True(await page.Locator("button[data-docs-version-toggle]").IsHiddenAsync());
        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    [Fact]
    public async Task HostedSwitch_UsesPinnedRuntimeAndPreservesMovedConceptAndVerifiedFragment()
    {
        await using var host = await StartHostedHostAsync();
        await using var context = await _browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var targetRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.setup", "en");
        var reusedRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.security", "en");
        Assert.Equal(sourceRoute, reusedRoute);
        Assert.NotEqual(sourceRoute, targetRoute);
        var sourceUrl = _versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute) + "#configure";
        var targetPath = new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", targetRoute)).AbsolutePath;

        var sourceResponse = await page.GotoAsync(sourceUrl);
        Assert.NotNull(sourceResponse);
        Assert.Equal(200, sourceResponse.Status);
        Assert.Equal("CSS1Compat", await page.EvaluateAsync<string>("() => document.compatMode"));
        Assert.Contains("Initial setup", await page.Locator("main h1").First.TextContentAsync(), StringComparison.Ordinal);

        var runtimeScript = page.Locator("script[data-docs-switch-runtime]");
        var runtimePath = Assert.IsType<string>(await runtimeScript.GetAttributeAsync("src"));
        Assert.Contains("/docs/versions/switch/runtime.js", runtimePath, StringComparison.Ordinal);
        var sri = Assert.IsType<string>(await runtimeScript.GetAttributeAsync("integrity"));
        Assert.StartsWith("sha256-", sri, StringComparison.Ordinal);
        using var client = new HttpClient();
        var runtimeBytes = await client.GetByteArrayAsync(new Uri(new Uri(host.BaseUrl), runtimePath));
        Assert.Equal("sha256-" + Convert.ToBase64String(SHA256.HashData(runtimeBytes)), sri);

        var csp = await sourceResponse.HeaderValueAsync("Content-Security-Policy");
        Assert.NotNull(csp);
        Assert.Contains("sandbox", csp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allow-scripts", csp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connect-src 'none'", csp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"'{sri}'", csp, StringComparison.Ordinal);

        var dataJson = await page.Locator("script[type='application/json'][data-docs-version-data]").TextContentAsync();
        Assert.Contains("\"version\":\"1.0.0\"", dataJson, StringComparison.Ordinal);
        Assert.Contains("\"locale\":\"en\"", dataJson, StringComparison.Ordinal);
        var toggle = page.Locator("button[data-docs-version-toggle]");
        await toggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Equal("false", await toggle.GetAttributeAsync("aria-expanded"));
        Assert.Equal(0, await page.Locator("[data-docs-version-notice]").CountAsync());

        await toggle.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var options = page.Locator("[data-docs-version-options]");
        Assert.Equal("true", await toggle.GetAttributeAsync("aria-expanded"));
        Assert.False(await options.IsHiddenAsync());
        var initialPath = new Uri(page.Url).AbsolutePath;
        await page.Keyboard.PressAsync("ArrowDown");
        Assert.Equal(initialPath, new Uri(page.Url).AbsolutePath);
        Assert.True(await toggle.EvaluateAsync<bool>("button => button === document.activeElement"));
        await page.Keyboard.PressAsync("Escape");
        Assert.Equal("false", await toggle.GetAttributeAsync("aria-expanded"));
        Assert.True(await options.IsHiddenAsync());
        Assert.True(await toggle.EvaluateAsync<bool>("button => button === document.activeElement"));

        await toggle.ClickAsync();
        var counterpartLink = page.Locator("[data-docs-version-options] a[href*='/docs/v/2.0.0/']").First;
        var counterpartHref = Assert.IsType<string>(await counterpartLink.GetAttributeAsync("href"));
        Assert.Contains("docsFrom=", counterpartHref, StringComparison.Ordinal);
        Assert.Contains(targetRoute.Split('#')[0], Uri.UnescapeDataString(counterpartHref), StringComparison.OrdinalIgnoreCase);

        await counterpartLink.FocusAsync();
        await counterpartLink.PressAsync("Enter");
        await page.WaitForFunctionAsync(
            "expectedPath => window.location.pathname === expectedPath",
            targetPath,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.WaitForSelectorAsync("[data-docs-version-notice]", new PageWaitForSelectorOptions
        {
            Timeout = 15_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Contains("Getting started with configuration", await page.Locator("main h1").First.TextContentAsync(), StringComparison.Ordinal);
        Assert.Equal("CSS1Compat", await page.EvaluateAsync<string>("() => document.compatMode"));
        Assert.Contains("This guide moved in 2.0.0", await page.Locator("[data-docs-version-notice]").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await page.Locator("[data-docs-version-notice]").CountAsync());
        Assert.Equal("configure", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''"));
        var describedBy = await page.Locator("#configure").GetAttributeAsync("aria-describedby");
        Assert.Equal(1, (describedBy ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(value => value == "docs-version-explanation"));

        var canonicalHref = await page.Locator("link[rel='canonical']").GetAttributeAsync("href");
        Assert.Equal(targetPath, new Uri(new Uri(host.BaseUrl), Assert.IsType<string>(canonicalHref)).AbsolutePath);
        var returnHref = await page.Locator("[data-docs-version-notice] a").GetAttributeAsync("href");
        Assert.Equal(new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute)).AbsolutePath,
            new Uri(new Uri(host.BaseUrl), Assert.IsType<string>(returnHref)).AbsolutePath);

        await page.GoBackAsync();
        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path && document.querySelectorAll('[data-docs-version-notice]').length === 0",
            new Uri(sourceUrl).AbsolutePath,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.GoForwardAsync();
        await page.WaitForFunctionAsync(
            "() => window.location.pathname === " + System.Text.Json.JsonSerializer.Serialize(targetPath)
                + " && document.querySelectorAll('[data-docs-version-notice]').length === 1",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    [Fact]
    public async Task HostedSwitch_CaseInsensitiveMountWithExactPhysicalPathKeepsEnhancementAndNotice()
    {
        await using var host = await StartHostedHostAsync();
        await using var context = await _browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var targetRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.setup", "en");

        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));
        var sourceKey = await page.EvaluateAsync<string>(
            "() => JSON.parse(document.querySelector('[data-docs-version-data]').textContent).sourceKey");
        var sourceToggle = page.Locator("button[data-docs-version-toggle]");
        await sourceToggle.ClickAsync();
        var normalDestination = page.Locator("[data-docs-version-options] a[href*='/docs/v/2.0.0/']").First;
        var normalDestinationHref = Assert.IsType<string>(await normalDestination.GetAttributeAsync("href"));
        var canonicalTargetPath = new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", targetRoute)).AbsolutePath;
        Assert.Equal(canonicalTargetPath, new Uri(new Uri(host.BaseUrl), normalDestinationHref).AbsolutePath);
        Assert.Contains("docsFrom=" + sourceKey, normalDestinationHref, StringComparison.Ordinal);

        var uppercasePath = canonicalTargetPath.Replace("/docs/v/", "/DOCS/V/", StringComparison.Ordinal);
        var uppercaseUrl = new Uri(new Uri(host.BaseUrl), uppercasePath + "?docsFrom=" + Uri.EscapeDataString(sourceKey) + "#configure").AbsoluteUri;
        var response = await page.GotoAsync(uppercaseUrl);

        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        Assert.Null(response.Request.RedirectedFrom);
        Assert.Equal(uppercasePath, new Uri(response.Url).AbsolutePath);
        Assert.Contains("Getting started with configuration", await page.Locator("main h1").First.TextContentAsync(), StringComparison.Ordinal);
        await page.Locator("button[data-docs-version-toggle]").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });
        Assert.Contains("This guide moved in 2.0.0", await page.Locator("[data-docs-version-notice]").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Contains("same documented concept", await page.Locator("[data-docs-version-notice]").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal("configure", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''"));
        Assert.Contains("connect-src 'none'", await response.HeaderValueAsync("Content-Security-Policy") ?? string.Empty, StringComparison.Ordinal);

        var returnHref = await page.Locator("[data-docs-version-notice] a").GetAttributeAsync("href");
        Assert.Equal(new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute)).AbsolutePath,
            new Uri(new Uri(host.BaseUrl), Assert.IsType<string>(returnHref)).AbsolutePath);
        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    [Fact]
    public async Task HostedSwitch_UnrelatedStaleAndWrongLocaleKeysDoNotClaimAConceptMatch()
    {
        await using var host = await StartHostedHostAsync();
        await using var context = await _browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var movedRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.setup", "en");
        var unrelatedRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.title-change", "en");
        var wrongDestinationRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.security", "en");
        var wrongLocaleRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.locale-example", "fr");

        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));
        var sourceKey = await page.EvaluateAsync<string>(
            "() => JSON.parse(document.querySelector('[data-docs-version-data]').textContent).sourceKey");
        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", unrelatedRoute));
        var unrelatedKey = await page.EvaluateAsync<string>(
            "() => JSON.parse(document.querySelector('[data-docs-version-data]').textContent).sourceKey");

        var movedUri = new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", movedRoute));
        await page.GotoAsync(movedUri + "?docsFrom=" + Uri.EscapeDataString(unrelatedKey));
        Assert.Equal(0, await page.Locator("[data-docs-version-notice]").CountAsync());

        await page.GotoAsync(movedUri + "?docsFrom=" + new string('0', 64));
        Assert.Equal(0, await page.Locator("[data-docs-version-notice]").CountAsync());

        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", wrongDestinationRoute)
            + "?docsFrom=" + Uri.EscapeDataString(sourceKey));
        Assert.Equal(0, await page.Locator("[data-docs-version-notice]").CountAsync());

        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", wrongLocaleRoute)
            + "?docsFrom=" + Uri.EscapeDataString(sourceKey));
        Assert.Equal(0, await page.Locator("[data-docs-version-notice]").CountAsync());
    }

    // Value: protects=hosted plain-link choices distinguish evidence and retain verified target-language recovery;
    // fails_when=known absence or uncertainty hides local search or switches to a foreign-language counterpart;
    // why_new=the locale row previously expected the wrong missing-language outcome; seam=none;
    // no_claim=partial-page network navigation or translation quality.
    [Theory]
    [InlineData("known-absent", "guides.removed", "", "No documented counterpart")]
    [InlineData("unknown", "", "Legacy guide with unknown identity", "No verified counterpart")]
    [InlineData("locale", "guides.locale-example", "", "No documented counterpart")]
    public async Task HostedChoicePage_ExplainsEvidenceAndKeepsNoScriptRecoveryLocal(
        string scenario,
        string conceptId,
        string legacyTitle,
        string expectedHeading)
    {
        await using var host = await StartHostedHostAsync();
        await using var context = await _browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var sourceRoute = conceptId.Length > 0
            ? _versions.GetCanonicalRoutePath("1.0.0", conceptId, "en")
            : _versions.GetCanonicalRoutePathByTitle("1.0.0", legacyTitle, "en");
        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));

        var fallback = page.Locator("a[data-docs-version-fallback]");
        Assert.True(await fallback.IsVisibleAsync());
        Assert.True(await page.Locator("button[data-docs-version-toggle]").IsHiddenAsync());
        await fallback.ClickAsync();
        await page.WaitForSelectorAsync("main.docs-version-choice", new PageWaitForSelectorOptions
        {
            Timeout = 15_000,
            State = WaitForSelectorState.Visible
        });

        var bodyText = await page.Locator("main.docs-version-choice").InnerTextAsync();
        Assert.Contains(expectedHeading, bodyText, StringComparison.Ordinal);
        var versionLocale = page.Locator("main.docs-version-choice section > p")
            .Filter(new LocatorFilterOptions { HasTextRegex = new Regex("^2[.]0[.]0 · en$") });
        Assert.Equal(1, await versionLocale.CountAsync());
        Assert.Contains("Return to original guide", bodyText, StringComparison.Ordinal);
        Assert.Equal(0, await page.Locator("script[data-docs-switch-runtime]").CountAsync());

        var targetLocalSearch = page.Locator("main.docs-version-choice a[href='/docs/v/2.0.0/search']");
        Assert.True(await targetLocalSearch.CountAsync() > 0);
        var originalReturn = page.Locator("main.docs-version-choice a").Filter(new LocatorFilterOptions { HasText = "Return to original guide" }).Last;
        var href = Assert.IsType<string>(await originalReturn.GetAttributeAsync("href"));
        Assert.Contains("/docs/v/1.0.0/", href, StringComparison.Ordinal);

        if (scenario == "locale")
        {
            Assert.DoesNotContain("/docs/v/2.0.0/fr/", bodyText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await page.Locator("main.docs-version-choice a[href*='/docs/v/2.0.0/fr/']").CountAsync());
        }

        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    [Fact]
    public async Task HostedChoicePage_NoJavaScriptOffersMovedCounterpartAndDirectTargetLocalSearch()
    {
        await using var host = await StartHostedHostAsync();
        await using var context = await _browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var targetRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.setup", "en");
        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));

        await page.Locator("a[data-docs-version-fallback]").ClickAsync();
        await page.WaitForSelectorAsync("main.docs-version-choice", new PageWaitForSelectorOptions
        {
            Timeout = 15_000,
            State = WaitForSelectorState.Visible
        });
        Assert.Contains("This guide moved", await page.Locator("main.docs-version-choice").InnerTextAsync(), StringComparison.Ordinal);

        var targetLink = page.Locator("main.docs-version-choice a").Filter(new LocatorFilterOptions { HasText = "Open this guide" }).First;
        var targetHref = Assert.IsType<string>(await targetLink.GetAttributeAsync("href"));
        Assert.Contains(targetRoute.Split('#')[0], Uri.UnescapeDataString(targetHref), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docsFrom=", targetHref, StringComparison.Ordinal);
        await targetLink.ClickAsync();

        await page.WaitForFunctionAsync(
            "expectedPath => window.location.pathname === expectedPath",
            new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", targetRoute)).AbsolutePath,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        Assert.Contains("Getting started with configuration", await page.Locator("main h1").First.TextContentAsync(), StringComparison.Ordinal);
        Assert.Contains("This guide moved in 2.0.0", await page.Locator("[data-docs-version-notice]").InnerTextAsync(), StringComparison.Ordinal);

        _versions.AssertOriginalArchiveHashesUnchanged();
    }

    [Fact]
    public async Task HostedSwitch_ExplainsTitleChangeAndKeepsSameRouteCanonical()
    {
        await using var host = await StartHostedHostAsync();
        await using var context = await _browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.title-change", "en");
        var targetRoute = _versions.GetCanonicalRoutePath("2.0.0", "guides.title-change", "en");
        Assert.Equal(sourceRoute, targetRoute);

        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));
        await page.Locator("[data-docs-version-toggle]").ClickAsync();
        var targetLink = page.Locator("[data-docs-version-options] a[href*='/docs/v/2.0.0/']").First;
        await targetLink.ClickAsync();
        await page.WaitForSelectorAsync("[data-docs-version-notice]", new PageWaitForSelectorOptions
        {
            Timeout = 15_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Contains("Current title", await page.Locator("main h1").First.TextContentAsync(), StringComparison.Ordinal);
        Assert.Contains("different title", await page.Locator("[data-docs-version-notice]").InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
        var canonicalHref = await page.Locator("link[rel='canonical']").GetAttributeAsync("href");
        Assert.Equal(new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "2.0.0", targetRoute)).AbsolutePath,
            new Uri(new Uri(host.BaseUrl), Assert.IsType<string>(canonicalHref)).AbsolutePath);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("missing")]
    public async Task HostedSwitch_UntrustedScriptIntegrityLeavesPlainLinkAvailable(string integrityMode)
    {
        await using var host = await StartHostedHostAsync();
        await using var context = await _browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var routePath = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var requestedUri = new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", routePath));
        await AddDocumentMutationRouteAsync(page, requestedUri.AbsolutePath, html => MutateRuntimeIntegrity(html, integrityMode));

        var response = await page.GotoAsync(requestedUri.ToString());
        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        Assert.True(await page.Locator("a[data-docs-version-fallback]").IsVisibleAsync());
        Assert.True(await page.Locator("button[data-docs-version-toggle]").IsHiddenAsync());
        Assert.Equal(0, await page.Locator("[data-docs-version-notice]").CountAsync());
    }

    [Fact]
    public async Task HostedSwitch_ChangedRuntimeAndUnrelatedScriptAreBlockedByResponsePolicy()
    {
        await using var host = await StartHostedHostAsync();
        await using var context = await _browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.AddInitScriptAsync("window.__changedRuntimeExecuted = false; window.__unrelatedRuntimeExecuted = false;");

        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.setup", "en");
        var requestedUri = new Uri(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));
        const string unrelatedBody = "window.__unrelatedRuntimeExecuted = true;";
        var unrelatedSri = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(unrelatedBody)));
        await AddDocumentMutationRouteAsync(page, requestedUri.AbsolutePath, html => AddUnrelatedScript(html, unrelatedSri));

        await page.RouteAsync("**/versions/switch/runtime.js", async route =>
        {
            var response = await route.FetchAsync();
            var altered = await response.TextAsync() + "\nwindow.__changedRuntimeExecuted = true;";
            await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = altered });
        });

        await page.RouteAsync("**/unrelated-version-switch.js", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "text/javascript",
            Body = unrelatedBody
        }));

        var response = await page.GotoAsync(requestedUri.ToString());

        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        Assert.Contains("sandbox", await response.HeaderValueAsync("Content-Security-Policy") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(await page.Locator("a[data-docs-version-fallback]").IsVisibleAsync());
        Assert.True(await page.Locator("button[data-docs-version-toggle]").IsHiddenAsync());
        Assert.False(await page.EvaluateAsync<bool>("() => window.__changedRuntimeExecuted || window.__unrelatedRuntimeExecuted"));
    }

    private async Task<AppSurfaceDocsInProcessHost> StartHostedHostAsync() =>
        await AppSurfaceDocsInProcessHost.StartAsync(
            "http://127.0.0.1:0",
            _versions.CreateHostedHostArguments(),
            configureServices: null);

    private static async Task AddDocumentMutationRouteAsync(IPage page, string path, Func<string, string> mutate)
    {
        await page.RouteAsync("**" + path, async route =>
        {
            if (route.Request.ResourceType == "document"
                && string.Equals(new Uri(route.Request.Url).AbsolutePath, path, StringComparison.OrdinalIgnoreCase))
            {
                var response = await route.FetchAsync();
                var html = await response.TextAsync();
                await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = mutate(html) });
                return;
            }

            await route.ContinueAsync();
        });
    }

    private static string MutateRuntimeIntegrity(string html, string mode)
    {
        var document = new HtmlParser().ParseDocument(html);
        var script = document.QuerySelector("script[data-docs-switch-runtime]")
            ?? throw new InvalidOperationException("The hosted guide did not contain the packaged switch runtime tag.");
        if (mode == "wrong")
        {
            script.SetAttribute("integrity", "sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        }
        else if (mode == "missing")
        {
            script.RemoveAttribute("integrity");
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The test requested an unsupported integrity mutation.");
        }

        return document.DocumentElement.OuterHtml;
    }

    private static string AddUnrelatedScript(string html, string integrity)
    {
        var document = new HtmlParser().ParseDocument(html);
        var script = document.CreateElement("script");
        script.SetAttribute("src", "/docs/unrelated-version-switch.js");
        script.SetAttribute("integrity", integrity);
        script.SetAttribute("crossorigin", "anonymous");
        document.Head?.AppendChild(script);
        return document.DocumentElement.OuterHtml;
    }

    private static async Task AssertChoicePageFocusAndTargetsAsync(IPage page, string expectedHeadingSelector)
    {
        Assert.Equal(0, await page.Locator("script").CountAsync());
        // Poll from the test host because page timers can stop when JavaScript is disabled.
        // Fragment focus can settle after navigation completes; require the same exact target.
        try
        {
            var timeout = TimeSpan.FromSeconds(5);
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("Timeout 5000ms exceeded while waiting for native focus.");
                }

                if (await page.EvaluateAsync<bool>(
                    "selector => document.activeElement === document.querySelector(selector)",
                    expectedHeadingSelector).WaitAsync(remaining))
                {
                    break;
                }

                remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50));
                }
            }
        }
        catch (TimeoutException exception)
        {
            string focusState;
            try
            {
                focusState = await page.EvaluateAsync<string>("""
                selector => JSON.stringify({
                    url: location.href,
                    visibility: document.visibilityState,
                    documentFocused: document.hasFocus(),
                    activeElement: document.activeElement?.outerHTML ?? null,
                    expectedElement: document.querySelector(selector)?.outerHTML ?? null
                })
                """, expectedHeadingSelector);
            }
            catch (PlaywrightException snapshotException)
            {
                focusState = $"snapshot unavailable: {snapshotException.Message}";
            }
            throw new TimeoutException($"Expected focus on {expectedHeadingSelector}; focus state: {focusState}", exception);
        }
        var unexpectedFocus = await page.EvaluateAsync<string>(
            "selector => document.activeElement === document.querySelector(selector) ? '' : (document.activeElement?.outerHTML ?? '<none>')",
            expectedHeadingSelector);
        Assert.True(unexpectedFocus.Length == 0,
            $"Expected focus on {expectedHeadingSelector}; active element: {unexpectedFocus}");
        await AssertChoicePageTargetsAsync(page);
    }

    private static async Task AssertHostedChoicePageReloadKeyboardAsync(IPage page)
    {
        Assert.Equal(0, await page.Locator("script").CountAsync());
        // Reload may restore scroll history without focusing the heading. The URL target
        // must still identify the heading, and the next native Tab must reach recovery.
        Assert.True(await page.Locator("main.docs-version-choice h1#docs-switch-choices[tabindex='-1']:target")
            .IsVisibleAsync());
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>(
            "() => document.activeElement === document.querySelector('main.docs-version-choice a')"));
    }

    private static async Task AssertChoicePageTargetsAsync(IPage page)
    {
        Assert.True(await page.EvaluateAsync<bool>("""
            () => {
                const links = Array.from(document.querySelectorAll('main.docs-version-choice a'));
                return links.length > 0 && links.every(link => {
                    const bounds = link.getBoundingClientRect();
                    return bounds.width >= 44 && bounds.height >= 44;
                });
            }
            """));
        Assert.True(await page.EvaluateAsync<bool>("""
            () => {
                const luminance = color => {
                    const channels = color.match(/[0-9.]+/g).slice(0, 3).map(Number).map(value => {
                        const channel = value / 255;
                        return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
                    });
                    return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
                };
                const background = luminance(getComputedStyle(document.body).backgroundColor);
                const contrast = color => {
                    const foreground = luminance(color);
                    return (Math.max(background, foreground) + 0.05) / (Math.min(background, foreground) + 0.05);
                };
                const text = [
                    document.querySelector('main.docs-version-choice'),
                    document.querySelector('main.docs-version-choice h1'),
                    ...document.querySelectorAll('main.docs-version-choice h2'),
                    ...document.querySelectorAll('main.docs-version-choice a')
                ];
                return text.length > 0 && text.every(element => contrast(getComputedStyle(element).color) >= 4.5);
            }
            """));
    }

    private static async Task AssertChoicePageResponsiveLayoutAsync(IPage page)
    {
        await page.SetViewportSizeAsync(320, 900);
        await page.EvaluateAsync("() => { document.documentElement.style.zoom = '2'; }");
        Assert.False(await page.EvaluateAsync<bool>("""
            () => Math.max(document.documentElement.scrollWidth, document.body.scrollWidth)
                > document.documentElement.clientWidth
            """));
    }
}
