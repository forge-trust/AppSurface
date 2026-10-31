using System.Net;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public async Task StaticChoicePage_NoJavaScriptOffersTargetLocalSearchForKnownAbsence()
    {
        var pagesRoot = await _versions.EnsureStaticPublicationAsync();
        await using var host = await AppSurfaceDocsVersionSwitchStaticHost.StartAsync(pagesRoot);
        await using var context = await _browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var sourceRoute = _versions.GetCanonicalRoutePath("1.0.0", "guides.removed", "en");
        await page.GotoAsync(_versions.BuildVersionPageUrl(host.BaseUrl, "1.0.0", sourceRoute));

        await page.Locator("a[data-docs-version-fallback]").ClickAsync();
        await page.WaitForSelectorAsync("main.docs-version-choice", new PageWaitForSelectorOptions
        {
            Timeout = 15_000,
            State = WaitForSelectorState.Visible
        });

        var bodyText = await page.Locator("main.docs-version-choice").InnerTextAsync();
        Assert.Contains("No documented counterpart", bodyText, StringComparison.Ordinal);
        var searchLink = page.Locator("main.docs-version-choice a").Filter(new LocatorFilterOptions { HasText = "Search this version" }).First;
        var searchHref = Assert.IsType<string>(await searchLink.GetAttributeAsync("href"));
        Assert.Contains("/docs/v/2.0.0/", searchHref, StringComparison.Ordinal);
        using var client = new HttpClient();
        var searchResponse = await client.GetAsync(new Uri(new Uri(host.BaseUrl), searchHref));
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        Assert.Contains("Search", await searchResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        _versions.AssertOriginalArchiveHashesUnchanged();
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

        await counterpartLink.ClickAsync();
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

    [Theory]
    [InlineData("known-absent", "guides.removed", "", "No documented counterpart")]
    [InlineData("unknown", "", "Legacy guide with unknown identity", "No verified counterpart")]
    [InlineData("locale", "guides.locale-example", "", "No documentation in this language")]
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
        Assert.Contains("2.0.0", bodyText, StringComparison.Ordinal);
        Assert.Contains("en", bodyText, StringComparison.Ordinal);
        Assert.Contains("Return to original guide", bodyText, StringComparison.Ordinal);
        Assert.Equal(0, await page.Locator("script[data-docs-switch-runtime]").CountAsync());

        if (scenario == "known-absent" || scenario == "unknown")
        {
            var targetLocalSearch = page.Locator("main.docs-version-choice a[href='/docs/v/2.0.0/search']");
            Assert.True(await targetLocalSearch.CountAsync() > 0);
            var originalReturn = page.Locator("main.docs-version-choice a").Filter(new LocatorFilterOptions { HasText = "Return to original guide" }).Last;
            var href = Assert.IsType<string>(await originalReturn.GetAttributeAsync("href"));
            Assert.Contains("/docs/v/1.0.0/", href, StringComparison.Ordinal);
        }
        else
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
}
