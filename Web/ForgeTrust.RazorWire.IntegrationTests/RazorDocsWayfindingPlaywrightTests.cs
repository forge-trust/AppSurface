using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsWayfindingPlaywrightTests
{
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsWayfindingPlaywrightTests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DetailsPage_RendersOutline_AndSequenceWayfinding()
    {
        // Regression: ISSUE-002 — partial wayfinding navigation could update the URL before the
        // replacement doc content landed, making this assertion flaky on slower runners.
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/examples/razorwire-mvc/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        await page.WaitForSelectorAsync("#docs-page-wayfinding", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Contains(
            "Files Behind the Hero Flow",
            await page.InnerTextAsync("#docs-page-outline"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "#files-behind-the-hero-flow",
            await page.GetAttributeAsync("#docs-page-outline a[href='#files-behind-the-hero-flow']", "href"));

        const string nextDocPath = "/docs/Web/ForgeTrust.RazorWire/Docs/form-failures.md.html";
        const string nextDocHeading = "Failed Form UX";
        Assert.Equal(
            "/docs/Web/ForgeTrust.RazorWire/README.md.html",
            await page.GetAttributeAsync("[data-doc-wayfinding='previous']", "href"));
        Assert.Equal(
            nextDocPath,
            await page.GetAttributeAsync("[data-doc-wayfinding='next']", "href"));

        var initialContent = await page.Locator("#doc-content").InnerHTMLAsync();

        await page.ClickAsync("[data-doc-wayfinding='next']");
        await page.WaitForFunctionAsync(
            """
            (args) => {
              const island = document.getElementById('doc-content');
              const heading = document.querySelector('#doc-content h1');
              return window.location.pathname === args.path
                && Boolean(island)
                && island.innerHTML !== args.initialContent
                && heading?.textContent?.trim() === args.title;
            }
            """,
            new
            {
                path = nextDocPath,
                initialContent,
                title = nextDocHeading
            },
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Equal(nextDocHeading, (await page.Locator("#doc-content h1").First.TextContentAsync())?.Trim());
    }

    [Fact]
    public async Task DetailsFrameNavigation_LoadsOutlineClient_WhenMovingFromPageWithoutOutline()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 900
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await page.WaitForSelectorAsync("#docs-search-input", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        Assert.Equal(0, await page.Locator("#docs-page-outline").CountAsync());
        Assert.Equal(0, await page.Locator("script[data-doc-outline-client='true']").CountAsync());

        const string outlinePagePath = "/docs/examples/razorwire-mvc/README.md.html";
        var examplesSection = page.Locator("#docs-sidebar details").Filter(new LocatorFilterOptions
        {
            Has = page.Locator($"a[href='{outlinePagePath}']")
        }).First;
        if (!await examplesSection.EvaluateAsync<bool>("section => section.open"))
        {
            await examplesSection.Locator("summary span[aria-hidden='true']").ClickAsync();
        }

        var outlineLink = examplesSection.Locator($"a[href='{outlinePagePath}']").First;
        await outlineLink.WaitForAsync(new LocatorWaitForOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        await page.EvaluateAsync("() => { window.__rwFrameNavigationSentinel = 'alive'; }");
        await outlineLink.ClickAsync();

        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path",
            outlinePagePath,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        await page.WaitForFunctionAsync(
            """
            () => window.__rwFrameNavigationSentinel === "alive"
              && document.querySelector("#doc-content h1")?.textContent?.trim() === "RazorWire MVC Example"
              && document.getElementById("docs-page-outline")?.dataset.outlineEnhanced === "true"
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Equal(1, await page.Locator("#docs-page-outline").CountAsync());
        Assert.Equal("true", await page.GetAttributeAsync("#docs-page-outline", "data-outline-enhanced"));
    }

    [Fact]
    public async Task DesktopOutline_StaysInRightRail_AndMarksActiveSection()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 900
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/examples/razorwire-mvc/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Equal(1, await page.Locator("#docs-page-outline").CountAsync());
        Assert.True(await page.Locator(".docs-detail-layout").EvaluateAsync<bool>(
            """
            layout => {
              const primary = layout.querySelector(".docs-detail-primary");
              const outline = layout.querySelector("#docs-page-outline");
              return Boolean(primary && outline && primary.compareDocumentPosition(outline) & Node.DOCUMENT_POSITION_FOLLOWING);
            }
            """));
        Assert.False(await page.Locator("#docs-page-outline .docs-outline-toggle").IsVisibleAsync());
        Assert.Equal(
            "sticky",
            await page.Locator("#docs-page-outline").EvaluateAsync<string>("element => getComputedStyle(element).position"));

        await page.ClickAsync("#docs-page-outline a[href='#files-behind-the-hero-flow']");
        await page.WaitForFunctionAsync(
            """
            () => document
              .querySelector("#docs-page-outline a[href='#files-behind-the-hero-flow']")
              ?.getAttribute("aria-current") === "location"
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    [Fact]
    public async Task DesktopOutline_KeepsClickedAdjacentHeadingActiveAfterHashNavigation()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 900
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/Web/ForgeTrust.AppSurface.Web/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        await page.ClickAsync("#docs-page-outline a[href='#endpoint-routing']");
        await page.WaitForFunctionAsync(
            """
            () => {
              const target = document.getElementById('endpoint-routing');
              return window.location.hash === '#endpoint-routing'
                && target
                && target.getBoundingClientRect().top >= 0
                && target.getBoundingClientRect().top <= 140;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await page.WaitForFunctionAsync(
            """
            () => document
              .querySelector("#docs-page-outline a[href='#endpoint-routing']")
              ?.getAttribute("aria-current") === "location"
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Equal(
            "location",
            await page.GetAttributeAsync("#docs-page-outline a[href='#endpoint-routing']", "aria-current"));
        Assert.Null(await page.EvaluateAsync<string?>(
            """() => document.querySelector("#docs-page-outline a[href='#conventional-404-pages']")?.getAttribute("aria-current") ?? null"""));
    }

    [Fact]
    public async Task DesktopOutline_UpdatesActiveSection_WhenScrollingInsideLongSection()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 900
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/Web/ForgeTrust.RazorWire/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        await page.EvaluateAsync(
            """
            () => {
              const main = document.getElementById('main-content');
              const heroProof = document.getElementById('hero-proof');
              const rootTop = main?.getBoundingClientRect().top ?? 0;
              const targetTop = heroProof?.getBoundingClientRect().top ?? 0;
              main?.scrollTo(0, main.scrollTop + targetTop - rootTop + 220);
            }
            """);

        await page.WaitForFunctionAsync(
            """
            () => {
              const main = document.getElementById('main-content');
              const next = document.getElementById('generated-ui-design-contract');
              const active = document.querySelector("#docs-page-outline a[href='#hero-proof']");
              if (!main || !next || active?.getAttribute('aria-current') !== 'location') {
                return false;
              }

              const mainRect = main.getBoundingClientRect();
              const nextRect = next.getBoundingClientRect();
              return nextRect.top > mainRect.top + main.clientHeight;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Equal(
            "location",
            await page.GetAttributeAsync("#docs-page-outline a[href='#hero-proof']", "aria-current"));
        Assert.Null(await page.EvaluateAsync<string?>(
            """() => document.querySelector("#docs-page-outline a[href='#60-second-quickstart']")?.getAttribute("aria-current") ?? null"""));
    }

    [Fact]
    public async Task DesktopOutline_KeepsActiveItemVisible_InScrollableRightRail()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 900
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/Web/ForgeTrust.RazorWire/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        await page.EvaluateAsync(
            """
            () => {
              const main = document.getElementById('main-content');
              const examples = document.getElementById('examples');
              const rootTop = main?.getBoundingClientRect().top ?? 0;
              const targetTop = examples?.getBoundingClientRect().top ?? 0;
              main?.scrollTo(0, main.scrollTop + targetTop - rootTop + 220);
            }
            """);

        await page.WaitForFunctionAsync(
            """
            () => {
              const shell = document.getElementById('docs-page-outline');
              const active = document.querySelector("#docs-page-outline a[href='#examples']");
              if (!shell || active?.getAttribute('aria-current') !== 'location') {
                return false;
              }

              const shellRect = shell.getBoundingClientRect();
              const activeRect = active.getBoundingClientRect();
              return shell.scrollTop > 0
                && activeRect.top >= shellRect.top
                && activeRect.bottom <= shellRect.bottom;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.True(await page.Locator("#docs-page-outline").EvaluateAsync<bool>(
            """
            shell => {
              const panel = shell.querySelector('.docs-outline-panel');
              return Boolean(panel)
                && shell.scrollTop > 0
                && panel.getBoundingClientRect().height > shell.clientHeight;
            }
            """));
    }

    [Fact]
    public async Task DesktopOutline_DoesNotAddBlankScrollPastDetailsContent()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 900
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/Namespaces/ForgeTrust.AppSurface.Aspire.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        await page.ClickAsync("#docs-page-outline a[href='#ForgeTrust-AppSurface-Aspire-AspireProfile-string-PassThroughArgs-get']");
        await page.WaitForFunctionAsync(
            """
            () => document
              .querySelector("#docs-page-outline a[href='#ForgeTrust-AppSurface-Aspire-AspireProfile-string-PassThroughArgs-get']")
              ?.getAttribute("aria-current") === "location"
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        await page.EvaluateAsync(ScrollMainContentToBottomScript);
        var scrollState = await page.EvaluateAsync<DetailsScrollState>(
            """
            () => {
              const main = document.getElementById('main-content');
              if (!main) {
                return {
                  scrollTop: Number.MAX_SAFE_INTEGER,
                  maxScrollTop: 0,
                  documentScrollTop: Number.MAX_SAFE_INTEGER
                };
              }

              return {
                scrollTop: Math.round(main.scrollTop),
                maxScrollTop: Math.round(Math.max(0, main.scrollHeight - main.clientHeight)),
                documentScrollTop: Math.round(document.scrollingElement?.scrollTop ?? window.scrollY)
              };
            }
            """);

        Assert.InRange(scrollState.MaxScrollTop - scrollState.ScrollTop, 0, 8);
        Assert.InRange(scrollState.DocumentScrollTop, 0, 1);
    }

    [Fact]
    public async Task DetailsFrameNavigation_ResetMainScroll_WhenMovingFromLongPageToShortApiPage()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 900
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/examples/razorwire-mvc/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        var overflowAnchor = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.getElementById('main-content')).overflowAnchor");
        Assert.Equal("none", overflowAnchor);

        await page.EvaluateAsync(ScrollMainContentToBottomScript);
        await page.EvaluateAsync(
            """
            () => {
              window.setTimeout(() => {
                const main = document.getElementById('main-content');
                main?.scrollTo(0, main.scrollHeight);
              }, 60);
            }
            """);
        await page.EvaluateAsync("() => { window.__rwFrameNavigationSentinel = 'alive'; }");

        await page.EvaluateAsync(
            """
            () => document
              .querySelector("#docs-sidebar a[href='/docs/Namespaces/ForgeTrust.AppSurface.Aspire.html']")
              ?.click()
            """);
        await page.WaitForFunctionAsync(
            """
            () => window.__rwFrameNavigationSentinel === 'alive'
              && window.location.pathname === '/docs/Namespaces/ForgeTrust.AppSurface.Aspire.html'
              && document.querySelector('#doc-content h1')?.textContent?.trim() === 'Aspire'
              && (document.getElementById('main-content')?.scrollTop ?? Number.MAX_SAFE_INTEGER) <= 8
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var scrollTop = await page.EvaluateAsync<int>(
            "() => Math.round(document.getElementById('main-content')?.scrollTop ?? Number.MAX_SAFE_INTEGER)");
        Assert.InRange(scrollTop, 0, 8);

        await page.ClickAsync("#docs-page-outline a[href='#ForgeTrust-AppSurface-Aspire-AspireProfile-string-PassThroughArgs-get']");
        await page.WaitForFunctionAsync(
            """
            () => document
              .querySelector("#docs-page-outline a[href='#ForgeTrust-AppSurface-Aspire-AspireProfile-string-PassThroughArgs-get']")
              ?.getAttribute("aria-current") === "location"
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        await page.EvaluateAsync(ScrollMainContentToBottomScript);
        var scrollState = await page.EvaluateAsync<DetailsScrollState>(
            """
            () => {
              const main = document.getElementById('main-content');
              if (!main) {
                return {
                  scrollTop: Number.MAX_SAFE_INTEGER,
                  maxScrollTop: 0,
                  documentScrollTop: Number.MAX_SAFE_INTEGER
                };
              }

              return {
                scrollTop: Math.round(main.scrollTop),
                maxScrollTop: Math.round(Math.max(0, main.scrollHeight - main.clientHeight)),
                documentScrollTop: Math.round(document.scrollingElement?.scrollTop ?? window.scrollY)
              };
            }
            """);

        Assert.InRange(scrollState.MaxScrollTop - scrollState.ScrollTop, 0, 8);
        Assert.InRange(scrollState.DocumentScrollTop, 0, 1);
    }

    [Fact]
    public async Task OutlineClient_IgnoresStaleHeadingTargets_ForHashAndClickActiveState()
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

        await page.GotoAsync(_fixture.DocsUrl);
        await page.SetContentAsync(
            """
            <main id="main-content">
              <div class="docs-detail-layout docs-detail-layout--with-outline">
                <div class="docs-detail-primary">
                  <h2 id="real-section">Real Section</h2>
                  <p>Body</p>
                </div>
                <aside id="docs-page-outline" data-outline-expanded="true">
                  <button type="button" aria-expanded="true" data-doc-outline-toggle="true">
                    <span>On this page</span>
                    <span data-doc-outline-current></span>
                  </button>
                  <nav id="docs-page-outline-panel" aria-label="On this page">
                    <ol>
                      <li><a href="#missing-section" data-doc-outline-link="true">Missing Section</a></li>
                      <li><a href="#real-section" data-doc-outline-link="true">Real Section</a></li>
                    </ol>
                  </nav>
                </aside>
              </div>
            </main>
            """);
        await page.EvaluateAsync("() => history.replaceState(null, '', '#missing-section')");

        await page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Url = $"{_fixture.DocsUrl}/outline-client.js"
        });
        await page.WaitForFunctionAsync(
            "() => document.getElementById('docs-page-outline')?.dataset.outlineEnhanced === 'true'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Null(await page.GetAttributeAsync("#docs-page-outline a[href='#missing-section']", "aria-current"));
        Assert.Null(await page.GetAttributeAsync("#docs-page-outline a[href='#real-section']", "aria-current"));

        await page.ClickAsync("#docs-page-outline [data-doc-outline-toggle]");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#docs-page-outline [data-doc-outline-toggle]')?.getAttribute('aria-expanded') === 'true'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        await page.ClickAsync("#docs-page-outline a[href='#missing-section']");
        await page.WaitForFunctionAsync(
            """
            () => window.location.hash === '#missing-section'
              && !document.querySelector("#docs-page-outline a[href='#missing-section']")?.hasAttribute("aria-current")
              && !document.querySelector("#docs-page-outline a[href='#real-section']")?.hasAttribute("aria-current")
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Equal("true", await page.GetAttributeAsync("#docs-page-outline [data-doc-outline-toggle]", "aria-expanded"));

        await page.SetContentAsync(
            """
            <main id="main-content">
              <turbo-frame id="doc-content">
                <div class="docs-detail-layout docs-detail-layout--with-outline">
                  <div class="docs-detail-primary">
                    <p>Body without matching heading IDs.</p>
                  </div>
                  <aside id="docs-page-outline" data-outline-expanded="true">
                    <button type="button" aria-expanded="true" data-doc-outline-toggle="true">
                      <span>On this page</span>
                      <span data-doc-outline-current></span>
                    </button>
                    <nav id="docs-page-outline-panel" aria-label="On this page">
                      <ol>
                        <li><a href="#missing-section" data-doc-outline-link="true">Missing Section</a></li>
                      </ol>
                    </nav>
                  </aside>
                </div>
              </turbo-frame>
            </main>
            """);
        await page.EvaluateAsync(
            """
            () => document
              .getElementById('doc-content')
              ?.dispatchEvent(new Event('turbo:frame-load', { bubbles: true }))
            """);

        Assert.Null(await page.GetAttributeAsync("#docs-page-outline", "data-outline-enhanced"));
        Assert.Equal("true", await page.GetAttributeAsync("#docs-page-outline [data-doc-outline-toggle]", "aria-expanded"));
    }

    [Fact]
    public async Task MobileOutline_CollapsesByDefault_AndClosesAfterAnchorNavigation()
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

        await page.GotoAsync($"{_fixture.DocsUrl}/examples/razorwire-mvc/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Equal(1, await page.Locator("#docs-page-outline").CountAsync());
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#docs-page-outline [data-doc-outline-toggle]')?.getAttribute('aria-expanded') === 'false'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        Assert.False(await page.Locator("#docs-page-outline-panel").IsVisibleAsync());

        await page.ClickAsync("#docs-page-outline [data-doc-outline-toggle]");
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#docs-page-outline [data-doc-outline-toggle]')?.getAttribute('aria-expanded') === 'true'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        Assert.True(await page.Locator("#docs-page-outline-panel").IsVisibleAsync());

        await page.ClickAsync("#docs-page-outline a[href='#files-behind-the-hero-flow']");
        await page.WaitForFunctionAsync(
            """
            () => {
              const target = document.getElementById('files-behind-the-hero-flow');
              const toggle = document.querySelector('#docs-page-outline [data-doc-outline-toggle]');
              if (!target || window.location.hash !== '#files-behind-the-hero-flow') {
                return false;
              }

              const rect = target.getBoundingClientRect();
              return rect.top >= 0 && rect.top <= 220 && toggle?.getAttribute('aria-expanded') === 'false';
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        Assert.False(await page.Locator("#docs-page-outline-panel").IsVisibleAsync());
    }

    [Fact]
    public async Task MobileOutline_StickyContextTracksNeighborSections()
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

        await page.GotoAsync($"{_fixture.DocsUrl}/examples/razorwire-mvc/README.md.html");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        await page.WaitForFunctionAsync(
            "() => document.getElementById('docs-page-outline')?.dataset.outlineEnhanced === 'true'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        var initialState = await page.EvaluateAsync<MobileOutlineContextState>(
            """
            () => {
              const shell = document.getElementById('docs-page-outline');
              const context = shell?.querySelector('[data-doc-outline-context]');
              const links = Array.from(shell?.querySelectorAll("a[data-doc-outline-link='true']") ?? []);
              return {
                activeIndex: context?.dataset.outlineActiveIndex ?? '',
                current: shell?.querySelector('[data-doc-outline-current]')?.textContent?.trim() ?? '',
                toggleLabel: shell?.querySelector('.docs-outline-toggle-label')?.textContent?.trim() ?? '',
                pageTitle: document.querySelector('h1')?.textContent?.trim() ?? '',
                previous: shell?.querySelector('[data-doc-outline-previous] [data-doc-outline-context-title]')?.textContent?.trim() ?? '',
                next: shell?.querySelector('[data-doc-outline-next] [data-doc-outline-context-title]')?.textContent?.trim() ?? '',
                previousHidden: Boolean(shell?.querySelector('[data-doc-outline-previous]')?.hidden),
                nextHidden: Boolean(shell?.querySelector('[data-doc-outline-next]')?.hidden),
                previousEmpty: shell?.querySelector('[data-doc-outline-previous]')?.dataset.outlineEmpty ?? '',
                nextEmpty: shell?.querySelector('[data-doc-outline-next]')?.dataset.outlineEmpty ?? '',
                firstLink: links[0]?.textContent?.trim() ?? '',
                secondLink: links[1]?.textContent?.trim() ?? '',
                thirdLink: links[2]?.textContent?.trim() ?? '',
                shellHeight: shell?.getBoundingClientRect().height ?? 0,
                shellLeft: shell?.getBoundingClientRect().left ?? 0,
                shellRight: shell?.getBoundingClientRect().right ?? 0,
                viewportWidth: window.innerWidth,
                borderRadius: shell ? getComputedStyle(shell).borderRadius : '',
                position: shell ? getComputedStyle(shell).position : '',
                top: shell ? getComputedStyle(shell).top : ''
              };
            }
            """);

        Assert.Equal("sticky", initialState.Position);
        Assert.Equal("61px", initialState.Top);
        Assert.Equal(0, initialState.ShellLeft, precision: 1);
        Assert.Equal(initialState.ViewportWidth, initialState.ShellRight, precision: 1);
        Assert.Equal("0px", initialState.BorderRadius);
        Assert.True(initialState.ShellHeight is >= 44 and <= 76);
        Assert.Equal("0", initialState.ActiveIndex);
        Assert.Equal(initialState.FirstLink, initialState.Current);
        Assert.Equal(initialState.PageTitle, initialState.ToggleLabel);
        Assert.False(initialState.PreviousHidden);
        Assert.False(initialState.NextHidden);
        Assert.Equal("true", initialState.PreviousEmpty);
        Assert.Equal("false", initialState.NextEmpty);
        Assert.Equal(initialState.SecondLink, initialState.Next);

        await page.EvaluateAsync(
            """
            () => {
              const main = document.getElementById('main-content');
              const link = document.querySelectorAll("#docs-page-outline a[data-doc-outline-link='true']")[2];
              const targetId = link?.getAttribute('href')?.slice(1);
              const target = targetId ? document.getElementById(targetId) : null;
              if (!main || !target) {
                return;
              }

              const rootTop = main.getBoundingClientRect().top;
              const targetTop = target.getBoundingClientRect().top;
              main.scrollTo(0, main.scrollTop + targetTop - rootTop + 90);
            }
            """);

        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-doc-outline-context]')?.dataset.outlineActiveIndex === '2'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        var scrolledState = await page.EvaluateAsync<MobileOutlineContextState>(
            """
            () => {
              const shell = document.getElementById('docs-page-outline');
              const context = shell?.querySelector('[data-doc-outline-context]');
              const links = Array.from(shell?.querySelectorAll("a[data-doc-outline-link='true']") ?? []);
              return {
                activeIndex: context?.dataset.outlineActiveIndex ?? '',
                current: shell?.querySelector('[data-doc-outline-current]')?.textContent?.trim() ?? '',
                toggleLabel: shell?.querySelector('.docs-outline-toggle-label')?.textContent?.trim() ?? '',
                pageTitle: document.querySelector('h1')?.textContent?.trim() ?? '',
                previous: shell?.querySelector('[data-doc-outline-previous] [data-doc-outline-context-title]')?.textContent?.trim() ?? '',
                next: shell?.querySelector('[data-doc-outline-next] [data-doc-outline-context-title]')?.textContent?.trim() ?? '',
                previousHidden: Boolean(shell?.querySelector('[data-doc-outline-previous]')?.hidden),
                nextHidden: Boolean(shell?.querySelector('[data-doc-outline-next]')?.hidden),
                previousEmpty: shell?.querySelector('[data-doc-outline-previous]')?.dataset.outlineEmpty ?? '',
                nextEmpty: shell?.querySelector('[data-doc-outline-next]')?.dataset.outlineEmpty ?? '',
                firstLink: links[0]?.textContent?.trim() ?? '',
                secondLink: links[1]?.textContent?.trim() ?? '',
                thirdLink: links[2]?.textContent?.trim() ?? '',
                shellHeight: shell?.getBoundingClientRect().height ?? 0,
                shellLeft: shell?.getBoundingClientRect().left ?? 0,
                shellRight: shell?.getBoundingClientRect().right ?? 0,
                viewportWidth: window.innerWidth,
                borderRadius: shell ? getComputedStyle(shell).borderRadius : '',
                position: shell ? getComputedStyle(shell).position : '',
                top: shell ? getComputedStyle(shell).top : '',
                rollDirection: context?.dataset.outlineRollDirection ?? '',
                motion: context?.dataset.outlineMotion ?? ''
              };
            }
            """);

        Assert.Equal("2", scrolledState.ActiveIndex);
        Assert.Equal(scrolledState.SecondLink, scrolledState.Previous);
        Assert.Equal(scrolledState.ThirdLink, scrolledState.Current);
        Assert.Equal(initialState.PageTitle, scrolledState.ToggleLabel);
        Assert.Equal(initialState.ShellHeight, scrolledState.ShellHeight, precision: 1);
        Assert.False(scrolledState.NextHidden);
        Assert.Equal("61px", scrolledState.Top);
        Assert.Equal(0, scrolledState.ShellLeft, precision: 1);
        Assert.Equal(scrolledState.ViewportWidth, scrolledState.ShellRight, precision: 1);
        Assert.Equal("down", scrolledState.RollDirection);
        Assert.Equal("rolling", scrolledState.Motion);
    }

    [Fact]
    public async Task CompactOutline_FullBleedsInsideSidebarLayout()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1100,
                Height = 900
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/Web/ForgeTrust.RazorWire/README.md.html#generated-ui-design-contract");
        await page.WaitForSelectorAsync("#docs-page-outline", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        await page.WaitForFunctionAsync(
            "() => document.getElementById('docs-page-outline')?.dataset.outlineEnhanced === 'true'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        var state = await page.EvaluateAsync<CompactOutlineBandState>(
            """
            () => {
              const shell = document.getElementById('docs-page-outline');
              const main = document.getElementById('main-content');
              const toggle = shell?.querySelector('[data-doc-outline-toggle]');
              const panel = document.getElementById('docs-page-outline-panel');
              const shellRect = shell?.getBoundingClientRect();
              const mainRect = main?.getBoundingClientRect();
              return {
                shellLeft: shellRect?.left ?? 0,
                shellRight: shellRect?.right ?? 0,
                mainLeft: mainRect?.left ?? 0,
                mainRight: mainRect?.right ?? 0,
                top: shell ? getComputedStyle(shell).top : '',
                position: shell ? getComputedStyle(shell).position : '',
                borderRadius: shell ? getComputedStyle(shell).borderRadius : '',
                borderTopWidth: shell ? getComputedStyle(shell).borderTopWidth : '',
                toggleVisible: toggle ? getComputedStyle(toggle).display !== 'none' : false,
                panelVisible: panel ? getComputedStyle(panel).display !== 'none' : false
              };
            }
            """);

        Assert.Equal("sticky", state.Position);
        Assert.Equal("0px", state.Top);
        Assert.Equal("0px", state.BorderRadius);
        Assert.Equal("0px", state.BorderTopWidth);
        Assert.True(state.ToggleVisible);
        Assert.False(state.PanelVisible);
        Assert.Equal(state.MainLeft, state.ShellLeft, precision: 1);
        Assert.Equal(state.MainRight, state.ShellRight, precision: 1);
    }

    [Fact]
    public async Task MobileOutline_ReducedMotionUpdatesContextWithoutRollingAnimation()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ReducedMotion = ReducedMotion.Reduce,
            ViewportSize = new ViewportSize
            {
                Width = 390,
                Height = 844
            }
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/examples/razorwire-mvc/README.md.html");
        await page.WaitForFunctionAsync(
            "() => document.getElementById('docs-page-outline')?.dataset.outlineEnhanced === 'true'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              const main = document.getElementById('main-content');
              const link = document.querySelectorAll("#docs-page-outline a[data-doc-outline-link='true']")[2];
              const targetId = link?.getAttribute('href')?.slice(1);
              const target = targetId ? document.getElementById(targetId) : null;
              if (!main || !target) {
                return;
              }

              const rootTop = main.getBoundingClientRect().top;
              const targetTop = target.getBoundingClientRect().top;
              main.scrollTo(0, main.scrollTop + targetTop - rootTop + 90);
            }
            """);

        await page.WaitForFunctionAsync(
            "() => document.querySelector('[data-doc-outline-context]')?.dataset.outlineActiveIndex === '2'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Equal(
            "reduced",
            await page.GetAttributeAsync("[data-doc-outline-context]", "data-outline-motion"));
        Assert.False(await page.Locator("[data-doc-outline-context]").EvaluateAsync<bool>(
            "element => element.classList.contains('docs-outline-toggle-context--rolling')"));
    }

    [Fact]
    public async Task MobileSidebar_NavigatesToNeighborPage_AndRestoresOpenButtonFocusOnEscape()
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

        await page.GotoAsync($"{_fixture.DocsUrl}/examples/razorwire-mvc/README.md.html");
        await page.WaitForSelectorAsync("#docs-sidebar-open", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        await page.FocusAsync("#docs-sidebar-open");
        await page.ClickAsync("#docs-sidebar-open");
        await page.WaitForFunctionAsync(
            "() => document.getElementById('docs-sidebar')?.getAttribute('aria-hidden') === 'false'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await page.WaitForFunctionAsync(
            "() => { const sidebar = document.getElementById('docs-sidebar'); return Boolean(sidebar) && sidebar.contains(document.activeElement); }",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        const string neighborHref = "/docs/Web/ForgeTrust.RazorWire/Docs/antiforgery.md.html";
        var neighborSection = page.Locator("#docs-sidebar details").Filter(new LocatorFilterOptions
        {
            Has = page.Locator($"a[href='{neighborHref}']")
        }).First;
        if (!await neighborSection.EvaluateAsync<bool>("section => section.open"))
        {
            await neighborSection.Locator("summary span[aria-hidden='true']").ClickAsync();
        }

        var neighborLink = page.Locator($"#docs-sidebar a[href='{neighborHref}']").First;
        await neighborLink.WaitForAsync(new LocatorWaitForOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        await neighborLink.ClickAsync();

        await page.WaitForFunctionAsync(
            "() => window.location.pathname.endsWith('/docs/Web/ForgeTrust.RazorWire/Docs/antiforgery.md.html')",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await page.WaitForFunctionAsync(
            "() => document.getElementById('docs-sidebar-open')?.getAttribute('aria-expanded') === 'false'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        await page.ClickAsync("#docs-sidebar-open");
        await page.WaitForFunctionAsync(
            "() => { const sidebar = document.getElementById('docs-sidebar'); return Boolean(sidebar) && sidebar.contains(document.activeElement); }",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForFunctionAsync(
            "() => document.activeElement?.id === 'docs-sidebar-open'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private sealed class DetailsScrollState
    {
        public int ScrollTop { get; init; }

        public int MaxScrollTop { get; init; }

        public int DocumentScrollTop { get; init; }
    }

    private sealed class MobileOutlineContextState
    {
        public string ActiveIndex { get; init; } = string.Empty;

        public string Current { get; init; } = string.Empty;

        public string ToggleLabel { get; init; } = string.Empty;

        public string PageTitle { get; init; } = string.Empty;

        public string Previous { get; init; } = string.Empty;

        public string Next { get; init; } = string.Empty;

        public bool PreviousHidden { get; init; }

        public bool NextHidden { get; init; }

        public string PreviousEmpty { get; init; } = string.Empty;

        public string NextEmpty { get; init; } = string.Empty;

        public string FirstLink { get; init; } = string.Empty;

        public string SecondLink { get; init; } = string.Empty;

        public string ThirdLink { get; init; } = string.Empty;

        public double ShellHeight { get; init; }

        public double ShellLeft { get; init; }

        public double ShellRight { get; init; }

        public double ViewportWidth { get; init; }

        public string BorderRadius { get; init; } = string.Empty;

        public string Position { get; init; } = string.Empty;

        public string Top { get; init; } = string.Empty;

        public string RollDirection { get; init; } = string.Empty;

        public string Motion { get; init; } = string.Empty;
    }

    private sealed class CompactOutlineBandState
    {
        public double ShellLeft { get; init; }

        public double ShellRight { get; init; }

        public double MainLeft { get; init; }

        public double MainRight { get; init; }

        public string Top { get; init; } = string.Empty;

        public string Position { get; init; } = string.Empty;

        public string BorderRadius { get; init; } = string.Empty;

        public string BorderTopWidth { get; init; } = string.Empty;

        public bool ToggleVisible { get; init; }

        public bool PanelVisible { get; init; }
    }

    private const string ScrollMainContentToBottomScript =
        """
        () => {
          const main = document.getElementById('main-content');
          if (!main) {
            return;
          }

          main.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true }));
          main.scrollTo(0, main.scrollHeight);
        }
        """;
}
