using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RazorDocsIntegrationCollection : ICollectionFixture<RazorDocsPlaywrightFixture>
{
    public const string Name = "RazorDocsIntegrationCollection";
}

[Collection(RazorDocsIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorDocsSearchPlaywrightTests
{
    private const string SearchIndexPath = "/docs/search-index.json";
    private const string MiniSearchRuntimePathPattern = "**/docs/minisearch.min.js*";
    private readonly RazorDocsPlaywrightFixture _fixture;

    public RazorDocsSearchPlaywrightTests(RazorDocsPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Search_RemainsFunctional_AfterTurboNavigationToAdvancedSearch()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.DocsUrl);
        await WaitForSidebarSearchReadyAsync(page);
        await RunSidebarSearchAndAssertResultsAsync(page, _fixture.SearchQuery);

        await page.ClickAsync("#docs-search-shell a[href='/docs/search']");
        await WaitForPathAsync(page, "/docs/search");
        await WaitForSearchPageSettledAsync(page);
        await RunAdvancedSearchAndAssertResultsAsync(page, _fixture.SearchQuery);
        await RunSidebarSearchAndAssertResultsAsync(page, _fixture.SearchQuery);
    }

    [Fact]
    public async Task SearchPage_RemainsFunctional_AfterSidebarCta_RevisitsCurrentPage()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await WaitForSearchPageSettledAsync(page);

        await page.EvaluateAsync(
            """
            () => {
              window.__rwSearchQa = {
                initialRoot: document.getElementById("docs-search-page"),
                turboLoads: 0
              };

              document.addEventListener("turbo:load", () => {
                window.__rwSearchQa.turboLoads += 1;
              });
            }
            """);

        await page.ClickAsync("#docs-search-shell a[href='/docs/search']");
        await page.WaitForFunctionAsync(
            """
            () => {
              const qa = window.__rwSearchQa;
              const currentRoot = document.getElementById("docs-search-page");
              return Boolean(qa)
                && qa.turboLoads > 0
                && currentRoot
                && currentRoot !== qa.initialRoot;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await WaitForSearchPageSettledAsync(page);
        Assert.True(await page.Locator("#docs-search-page-starter").IsVisibleAsync());

        await RunAdvancedSearchAndAssertResultsAsync(page, _fixture.SearchQuery);
    }

    [Fact]
    public async Task SlashShortcut_FocusesVisibleSearchInput_WithoutStealingEditableFocus()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.DocsUrl);
        await WaitForSidebarSearchReadyAsync(page);

        await page.Keyboard.PressAsync("/");
        await ExpectActiveElementIdAsync(page, "docs-search-input");

        await page.EvaluateAsync(
            """
            () => {
              const textarea = document.createElement('textarea');
              textarea.id = 'qa-editable';
              document.body.append(textarea);
              textarea.focus();
            }
            """);

        await page.Keyboard.PressAsync("/");
        await ExpectActiveElementIdAsync(page, "qa-editable");
    }

    [Fact]
    public async Task SlashShortcut_NavigatesToWorkspace_WhenSidebarSearchIsHidden_OnMobileDocsPage()
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
        await WaitForSidebarSearchReadyAsync(page);

        await page.Keyboard.PressAsync("/");
        await WaitForPathAsync(page, "/docs/search");
        await WaitForSearchPageSettledAsync(page);
        await ExpectActiveElementIdAsync(page, "docs-search-page-input");
    }

    [Fact]
    public async Task SearchShortcut_NavigatesToWorkspace_AndPreservesSidebarQuery()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.DocsUrl);
        await WaitForSidebarSearchReadyAsync(page);
        await RunSidebarSearchAndAssertResultsAsync(page, _fixture.SearchQuery);

        await page.Keyboard.PressAsync(GetSearchWorkspaceShortcut());
        await WaitForPathAsync(page, "/docs/search");
        await WaitForSearchPageSettledAsync(page);

        Assert.Equal(_fixture.SearchQuery, await page.InputValueAsync("#docs-search-page-input"));
        Assert.Contains($"q={Uri.EscapeDataString(_fixture.SearchQuery)}", page.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPage_SupportsFilterOnlyBrowse_FromSharedUrl()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var url = $"{_fixture.DocsUrl}/search?pageType={Uri.EscapeDataString(_fixture.BrowsePageType)}";
        await page.GotoAsync(url);
        await WaitForSearchPageSettledAsync(page);
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#docs-search-page-results .docs-search-result').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Equal(string.Empty, await page.InputValueAsync("#docs-search-page-input"));
        Assert.Contains("pageType=", page.Url, StringComparison.Ordinal);
        Assert.Contains(
            "page(s) for the current filters.",
            await page.TextContentAsync("#docs-search-page-results-meta"),
            StringComparison.Ordinal);
        Assert.False(await page.GetByText("Search is temporarily unavailable").IsVisibleAsync());
    }

    [Fact]
    public async Task SearchPage_KeepsFailureRegionEmpty_WhenSearchIsReady()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/search?q={Uri.EscapeDataString(_fixture.SearchQuery)}");
        await WaitForSearchPageSettledAsync(page);
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#docs-search-page-results .docs-search-result').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var failureText = (await page.TextContentAsync("#docs-search-page-failure"))?.Trim();

        Assert.False(await page.Locator("#docs-search-page-failure").IsVisibleAsync());
        Assert.Equal(string.Empty, failureText);
    }

    [Fact]
    public async Task SearchPage_BackAndForward_RestoreQueryFiltersAndResults()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/search?q={Uri.EscapeDataString(_fixture.SearchQuery)}");
        await WaitForSearchPageSettledAsync(page);
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#docs-search-page-results .docs-search-result').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        var initialResultCount = await page.Locator("#docs-search-page-results .docs-search-result").CountAsync();
        Assert.True(initialResultCount > 0);

        var filterValue = await page.Locator("[data-rw-facet-key='pageType']:not([disabled])")
            .First
            .EvaluateAsync<string>("element => element.getAttribute('data-rw-facet-value') || ''");

        Assert.False(string.IsNullOrWhiteSpace(filterValue));

        await page.ClickAsync($"[data-rw-facet-key='pageType'][data-rw-facet-value='{filterValue}']");
        await page.WaitForFunctionAsync(
            "(expected) => new URLSearchParams(window.location.search).get('pageType') === expected",
            filterValue,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#docs-search-page-results .docs-search-result').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        var filteredResultCount = await page.Locator("#docs-search-page-results .docs-search-result").CountAsync();
        Assert.True(filteredResultCount > 0);

        await page.GoBackAsync();
        await page.WaitForFunctionAsync(
            "(query) => { const params = new URLSearchParams(window.location.search); return params.get('q') === query && !params.get('pageType'); }",
            _fixture.SearchQuery,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await WaitForSearchPageSettledAsync(page);
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#docs-search-page-results .docs-search-result').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Equal(_fixture.SearchQuery, await page.InputValueAsync("#docs-search-page-input"));
        Assert.False(await page.Locator("#docs-search-page-active-filters").IsVisibleAsync());
        Assert.True(await page.Locator("#docs-search-page-results .docs-search-result").CountAsync() > 0);

        await page.GoForwardAsync();
        await page.WaitForFunctionAsync(
            "(args) => { const params = new URLSearchParams(window.location.search); return params.get('q') === args.query && params.get('pageType') === args.pageType; }",
            new { query = _fixture.SearchQuery, pageType = filterValue },
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await WaitForSearchPageSettledAsync(page);
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#docs-search-page-results .docs-search-result').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        Assert.Equal(_fixture.SearchQuery, await page.InputValueAsync("#docs-search-page-input"));
        Assert.True(await page.Locator("#docs-search-page-active-filters").IsVisibleAsync());
        Assert.True(await page.Locator("#docs-search-page-results .docs-search-result").CountAsync() > 0);
    }

    [Fact]
    public async Task SearchPage_StarterChips_PopulateQueryAndRunImmediately()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await WaitForSearchPageSettledAsync(page);
        await page.WaitForSelectorAsync("#docs-search-page-starter", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        var chip = page.Locator("[data-rw-search-suggestion]").Nth(1);
        var chipQuery = await chip.GetAttributeAsync("data-rw-search-suggestion");
        Assert.False(string.IsNullOrWhiteSpace(chipQuery));

        await chip.ClickAsync();
        await page.WaitForFunctionAsync(
            "(expected) => new URLSearchParams(window.location.search).get('q') === expected",
            chipQuery,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await WaitForSearchPageSettledAsync(page);

        Assert.Equal(chipQuery, await page.InputValueAsync("#docs-search-page-input"));
        Assert.False(await page.Locator("#docs-search-page-starter").IsVisibleAsync());
    }

    [Fact]
    public async Task SearchPage_StarterState_ShowsRepresentativeRows_ForAvailablePageTypes()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await RouteSearchPayloadAsync(
            page,
            BuildControlledSearchPayload(
                SearchDoc("guide/start", "Getting Started Dependencies", "guide", "Guide", "guide", "Learn how to manage dependencies.", breadcrumbs: ["Guides", "Getting Started"]),
                SearchDoc("api/packages", "Package API", "api-reference", "API Reference", "api-reference", "Reference for package APIs.", breadcrumbs: ["API", "Packages"]),
                SearchDoc("examples/dependency", "Dependency Example", "example", "Example", "example", "A working dependency example.", breadcrumbs: ["Samples", "Dependency"]),
                SearchDoc("support/dependencies", "Dependency Troubleshooting", "troubleshooting", "Troubleshooting", "troubleshooting", "Resolve dependency build issues.", breadcrumbs: ["Support", "Build"]),
                SearchDoc("releases/2-1-0", "Release Notes 2.1.0", "release-note", "Release", "release", "Dependency fixes in the latest release.", breadcrumbs: ["Release Notes", "2.1.0"])));

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await WaitForSearchPageSettledAsync(page);

        Assert.Equal(string.Empty, await page.InputValueAsync("#docs-search-page-input"));
        await ExpectVisibleTextAsync(page, "#docs-search-page-starter-docs", "Browse the index");
        await ExpectVisibleTextAsync(page, "#docs-search-page-starter-docs", "Getting Started Dependencies");
        await ExpectVisibleTextAsync(page, "#docs-search-page-starter-docs", "Package API");
        await ExpectVisibleTextAsync(page, "#docs-search-page-starter-docs", "Dependency Example");
        await ExpectVisibleTextAsync(page, "#docs-search-page-starter-docs", "Dependency Troubleshooting");
        await ExpectVisibleTextAsync(page, "#docs-search-page-starter-docs", "Release Notes 2.1.0");
        await ExpectVisibleTextAsync(page, "#docs-search-page-starter-docs", "Release");
        Assert.Equal(5, await page.Locator("#docs-search-page-starter-docs .docs-search-result-starter").CountAsync());
    }

    [Fact]
    public async Task SearchPage_StarterState_OmitsMissingRepresentativeTypes()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await RouteSearchPayloadAsync(
            page,
            BuildControlledSearchPayload(
                SearchDoc("guide/start", "Getting Started", "guide", "Guide", "guide", "Start here."),
                SearchDoc("api/packages", "Package API", "api-reference", "API Reference", "api-reference", "Reference docs.")));

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await WaitForSearchPageSettledAsync(page);

        Assert.Equal(2, await page.Locator("#docs-search-page-starter-docs .docs-search-result-starter").CountAsync());
        Assert.Equal(0, await page.Locator("[data-rw-starter-page-type='troubleshooting']").CountAsync());
        Assert.Equal(0, await page.Locator("[data-rw-starter-page-type='release']").CountAsync());
    }

    [Fact]
    public async Task SearchPage_QueryResults_RenderRichCards_AndOmitBlankSnippets()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await RouteSearchPayloadAsync(
            page,
            BuildControlledSearchPayload(
                SearchDoc(
                    "guides/dependencies",
                    "Dependency Guide",
                    "guide",
                    "Guide",
                    "guide",
                    "Manage dependencies across RazorDocs projects.",
                    component: "razordocs",
                    audience: "maintainer",
                    status: "stable",
                    breadcrumbs: ["Guides", "Dependencies"]),
                SearchDoc(
                    "guides/blank",
                    "Blank Snippet Dependency",
                    "guide",
                    "Guide",
                    "guide",
                    string.Empty,
                    bodyText: "blank dependency body",
                    breadcrumbs: ["Guides", "Blank"])));

        await page.GotoAsync($"{_fixture.DocsUrl}/search?q=dependency");
        await WaitForSearchPageSettledAsync(page);

        await ExpectVisibleTextAsync(page, "#docs-search-page-results", "Guides");
        await ExpectVisibleTextAsync(page, "#docs-search-page-results", "Dependency Guide");
        await ExpectVisibleTextAsync(page, "#docs-search-page-results", "Manage dependencies across RazorDocs projects.");
        await ExpectVisibleTextAsync(page, "#docs-search-page-results", "Guide");
        await ExpectVisibleTextAsync(page, "#docs-search-page-results", "Razordocs");
        await ExpectVisibleTextAsync(page, "#docs-search-page-results", "Maintainer");
        await ExpectVisibleTextAsync(page, "#docs-search-page-results", "Stable");

        var blankSnippet = page
            .Locator(".docs-search-result")
            .Filter(new LocatorFilterOptions { HasTextString = "Blank Snippet Dependency" })
            .Locator(".docs-search-result-snippet");
        Assert.Equal(0, await blankSnippet.CountAsync());
    }

    [Fact]
    public async Task SearchPage_NoResultsRecovery_ShowsSummaryAndClearsFilters()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await RouteSearchPayloadAsync(
            page,
            BuildControlledSearchPayload(
                SearchDoc("guide/start", "Getting Started", "guide", "Guide", "guide", "Start here.")));

        await page.GotoAsync($"{_fixture.DocsUrl}/search?q=no-such-query&pageType=guide");
        await WaitForSearchPageSettledAsync(page);

        await ExpectVisibleTextAsync(page, "#docs-search-page-results", "Current search: query \"no-such-query\", Page Type: Guide.");
        await ExpectVisibleTextAsync(page, "#docs-search-page-results", "Clear filters");
        Assert.False(await page.GetByText("Search is temporarily unavailable").IsVisibleAsync());

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Clear filters", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => !new URLSearchParams(window.location.search).get('pageType')",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Contains("q=no-such-query", page.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPage_MobileFilters_CollapseButExposeSelectedState()
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
        await RouteSearchPayloadAsync(
            page,
            BuildControlledSearchPayload(
                SearchDoc("guide/start", "Getting Started", "guide", "Guide", "guide", "Start here."),
                SearchDoc("api/packages", "Package API", "api-reference", "API Reference", "api-reference", "Reference docs.")));

        await page.GotoAsync($"{_fixture.DocsUrl}/search?pageType=guide");
        await WaitForSearchPageSettledAsync(page);

        Assert.True(await page.Locator("#docs-search-page-filters-toggle").IsVisibleAsync());
        Assert.Equal("false", await page.GetAttributeAsync("#docs-search-page-filters-toggle", "aria-expanded"));
        Assert.False(await page.Locator("#docs-search-page-filters-panel").IsVisibleAsync());
        await ExpectVisibleTextAsync(page, "#docs-search-page-active-filters", "Page Type: Guide");

        await page.ClickAsync("#docs-search-page-filters-toggle");
        Assert.Equal("true", await page.GetAttributeAsync("#docs-search-page-filters-toggle", "aria-expanded"));
        Assert.True(await page.Locator("#docs-search-page-filters-panel").IsVisibleAsync());
    }

    [Fact]
    public async Task SearchPage_UsesTopLevelNavigation_ForDocumentationIndexRecoveryLink()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var payload = JsonSerializer.Serialize(new
        {
            metadata = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                version = "1",
                engine = "minisearch"
            },
            documents = new[]
            {
                new
                {
                    id = "misc/overview",
                    path = "/docs/misc/overview",
                    title = "Misc Overview",
                    summary = "Misc summary",
                    headings = Array.Empty<string>(),
                    bodyText = "miscellaneous body",
                    snippet = "miscellaneous body",
                    pageType = "reference-note",
                    audience = string.Empty,
                    component = string.Empty,
                    aliases = Array.Empty<string>(),
                    keywords = Array.Empty<string>(),
                    status = string.Empty,
                    navGroup = "Misc",
                    order = 1,
                    relatedPages = Array.Empty<string>(),
                    breadcrumbs = Array.Empty<string>()
                }
            }
        });

        await page.RouteAsync(
            $"**{SearchIndexPath}",
            async route =>
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = payload
                });
            });

        await page.GotoAsync($"{_fixture.DocsUrl}/search?q={Uri.EscapeDataString("no-such-query")}");
        await WaitForSearchPageSettledAsync(page);

        var docsIndexLink = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions
        {
            Name = "Documentation index",
            Exact = true
        });

        await docsIndexLink.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });

        Assert.Equal("_top", await docsIndexLink.GetAttributeAsync("data-turbo-frame"));
    }

    [Fact]
    public async Task SearchPage_ShowsFailureState_AndRetryRecovers()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.RouteAsync(
            $"**{SearchIndexPath}",
            async route =>
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 503,
                    ContentType = "application/json",
                    Body = "{}"
                });
            });

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await page.WaitForSelectorAsync("#docs-search-page-failure", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.True(await page.Locator("#docs-search-page-retry").IsVisibleAsync());
        Assert.True(await page.Locator("#docs-search-page-failure a[href]").First.IsVisibleAsync());

        await page.EvaluateAsync(
            """
            () => {
              history.pushState({ rwDocsSearch: true }, "", `${window.location.pathname}?q=retry-test&pageType=guide`);
              history.back();
            }
            """);
        await page.WaitForSelectorAsync("#docs-search-page-failure", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });
        Assert.Equal("false", await page.GetAttributeAsync("#docs-search-page-results", "aria-busy"));

        await page.UnrouteAsync($"**{SearchIndexPath}");
        await page.ClickAsync("#docs-search-page-retry");
        await WaitForSearchPageSettledAsync(page);

        Assert.False(await page.Locator("#docs-search-page-failure").IsVisibleAsync());
        Assert.True(await page.Locator("#docs-search-page-starter").IsVisibleAsync());
    }

    [Fact]
    public async Task SearchPage_RetryRecovers_WhenMiniSearchRuntimeFirstLoadDoesNotInitialize()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var runtimeRequests = 0;

        await page.RouteAsync(
            MiniSearchRuntimePathPattern,
            async route =>
            {
                var attempt = System.Threading.Interlocked.Increment(ref runtimeRequests);
                if (attempt == 1)
                {
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "application/javascript",
                        Body = "window.__rwMiniSearchRuntimeIntercept = 1;"
                    });
                    return;
                }

                await route.ContinueAsync();
            });

        await page.GotoAsync($"{_fixture.DocsUrl}/search");
        await page.WaitForSelectorAsync("#docs-search-page-failure", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Visible
        });

        Assert.Equal(1, runtimeRequests);
        Assert.Equal(
            "true",
            await page.GetAttributeAsync("script[data-rw-search-runtime=\"minisearch\"]", "data-rw-search-failed"));

        await page.ClickAsync("#docs-search-page-retry");
        await WaitForSearchPageSettledAsync(page);

        Assert.Equal(2, runtimeRequests);
        Assert.False(await page.Locator("#docs-search-page-failure").IsVisibleAsync());
        Assert.True(await page.Locator("#docs-search-page-starter").IsVisibleAsync());
    }

    [Fact]
    public async Task SidebarSearch_LazyLoadsResources_OnOrdinaryDocsPages()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var searchIndexRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        page.Request += (_, request) =>
        {
            if (request.Url.Contains(SearchIndexPath, StringComparison.Ordinal))
            {
                searchIndexRequested.TrySetResult();
            }
        };

        await page.GotoAsync(_fixture.DocsUrl);
        await WaitForSidebarSearchReadyAsync(page);
        Assert.False(await page.EvaluateAsync<bool>("() => Boolean(document.querySelector('script[data-rw-search-runtime=\"minisearch\"]'))"));

        await page.WaitForTimeoutAsync(500);
        Assert.False(searchIndexRequested.Task.IsCompleted);

        await page.FocusAsync("#docs-search-input");
        await WaitForTaskAsync(searchIndexRequested.Task, TimeSpan.FromSeconds(15));

        Assert.True(await page.EvaluateAsync<bool>("() => Boolean(document.querySelector('script[data-rw-search-runtime=\"minisearch\"]'))"));
    }

    private static async Task WaitForSidebarSearchReadyAsync(IPage page)
    {
        await page.WaitForSelectorAsync("#docs-search-input", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Attached
        });
        await page.WaitForFunctionAsync(
            "() => document.documentElement.getAttribute('data-rw-search-shortcuts-bound') === '1'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task RunSidebarSearchAndAssertResultsAsync(IPage page, string query)
    {
        await page.FillAsync("#docs-search-input", query);
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#docs-search-results [role=\"option\"]').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task RunAdvancedSearchAndAssertResultsAsync(IPage page, string query)
    {
        await WaitForSearchPageSettledAsync(page);
        await page.FillAsync("#docs-search-page-input", query);
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#docs-search-page-results .docs-search-result').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task RouteSearchPayloadAsync(IPage page, string payload)
    {
        await page.RouteAsync(
            $"**{SearchIndexPath}",
            async route =>
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = payload
                });
            });
    }

    private static string BuildControlledSearchPayload(params object[] documents)
    {
        return JsonSerializer.Serialize(new
        {
            metadata = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                version = "1",
                engine = "minisearch"
            },
            documents
        });
    }

    private static object SearchDoc(
        string id,
        string title,
        string pageType,
        string pageTypeLabel,
        string pageTypeVariant,
        string summary,
        string component = "",
        string audience = "",
        string status = "",
        string bodyText = "",
        string[]? breadcrumbs = null)
    {
        var path = id.StartsWith("/docs/", StringComparison.Ordinal)
            ? id
            : $"/docs/{id}";

        return new
        {
            id,
            path,
            title,
            summary,
            headings = Array.Empty<string>(),
            bodyText = string.IsNullOrWhiteSpace(bodyText) ? $"{title} {summary}" : bodyText,
            snippet = summary,
            pageType,
            pageTypeLabel,
            pageTypeVariant,
            audience,
            component,
            aliases = Array.Empty<string>(),
            keywords = Array.Empty<string>(),
            status,
            navGroup = breadcrumbs?.FirstOrDefault() ?? string.Empty,
            order = 1,
            relatedPages = Array.Empty<string>(),
            breadcrumbs = breadcrumbs ?? []
        };
    }

    private static async Task ExpectVisibleTextAsync(IPage page, string selector, string text)
    {
        await Assertions.Expect(page.Locator(selector)).ToContainTextAsync(text, new LocatorAssertionsToContainTextOptions
        {
            Timeout = 30_000
        });
    }

    private static async Task WaitForSearchPageSettledAsync(IPage page)
    {
        await page.WaitForSelectorAsync("#docs-search-page-input", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Attached
        });
        await page.WaitForFunctionAsync(
            """
            () => {
              const results = document.getElementById('docs-search-page-results');
              return results && results.getAttribute('aria-busy') === 'false';
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task ExpectActiveElementIdAsync(IPage page, string expectedId)
    {
        await page.WaitForFunctionAsync(
            "(expectedId) => document.activeElement && document.activeElement.id === expectedId",
            expectedId,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task WaitForTaskAsync(Task task, TimeSpan timeout)
    {
        var completedTask = await Task.WhenAny(task, Task.Delay(timeout));
        Assert.True(
            ReferenceEquals(completedTask, task),
            $"Timed out after {timeout.TotalSeconds} seconds waiting for the expected browser event.");
        await task;
    }

    private static string GetSearchWorkspaceShortcut()
    {
        return OperatingSystem.IsMacOS() ? "Meta+K" : "Control+K";
    }

    private static async Task WaitForPathAsync(IPage page, string expectedPath)
    {
        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path",
            expectedPath,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }
}

public sealed class RazorDocsPlaywrightFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim PlaywrightInstallLock = new(1, 1);
    private static bool _playwrightInstalled;
    private static readonly Regex SearchTokenRegex = new(
        "[A-Za-z0-9]{4,}",
        RegexOptions.Compiled);

    private RazorDocsInProcessHost? _appHost;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;
    public string DocsUrl { get; private set; } = string.Empty;
    public string SearchQuery { get; private set; } = "Namespaces";
    public string BrowsePageType { get; private set; } = "guide";

    public async Task InitializeAsync()
    {
        await EnsurePlaywrightInstalledAsync();

        try
        {
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });

            _appHost = await RazorDocsInProcessHost.StartAsync("http://127.0.0.1:0");
            var baseUrl = _appHost.BaseUrl;
            DocsUrl = $"{baseUrl}/docs";

            await WaitForAppReadyAsync(DocsUrl, TimeSpan.FromSeconds(60));
            SearchQuery = await WarmSearchIndexAndResolveQueryAsync(baseUrl, TimeSpan.FromSeconds(90));
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

    private async Task WaitForAppReadyAsync(string docsUrl, TimeSpan timeout)
    {
        using var client = new HttpClient();

        await PollUntilAsync(
            timeout,
            timeoutMessage: "RazorDocs app did not become ready",
            appExitedMessage: "RazorDocs app exited before it became ready",
            async cancellationToken =>
            {
                using var response = await client.GetAsync(docsUrl, cancellationToken);
                return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect;
            });
    }

    private async Task<string> WarmSearchIndexAndResolveQueryAsync(string baseUrl, TimeSpan timeout)
    {
        using var client = new HttpClient();
        var searchIndexUrl = $"{baseUrl}/docs/search-index.json";
        string? resolvedQuery = null;

        await PollUntilAsync(
            timeout,
            timeoutMessage: "RazorDocs search index did not become ready",
            appExitedMessage: "RazorDocs app exited before search index warmed",
            async cancellationToken =>
            {
                using var response = await client.GetAsync(searchIndexUrl, cancellationToken);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                BrowsePageType = ResolveBrowsePageType(payload.RootElement);
                resolvedQuery = ResolveSearchQuery(payload.RootElement);
                return true;
            });

        return resolvedQuery ?? "Namespaces";
    }

    private async Task PollUntilAsync(
        TimeSpan timeout,
        string timeoutMessage,
        string appExitedMessage,
        Func<CancellationToken, Task<bool>> probeAsync)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            EnsureAppHostIsRunning(appExitedMessage);

            try
            {
                if (await probeAsync(timeoutCts.Token))
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
                throw new TimeoutException($"{timeoutMessage} within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
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
                throw new TimeoutException($"{timeoutMessage} within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
            }
        }

        throw new TimeoutException($"{timeoutMessage} within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
    }

    private void EnsureAppHostIsRunning(string appExitedMessage)
    {
        if (_appHost is not { IsStarted: true })
        {
            throw new InvalidOperationException($"{appExitedMessage}.{Environment.NewLine}{GetRecentLogs()}");
        }
    }

    private static string ResolveSearchQuery(JsonElement payload)
    {
        if (!payload.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array)
        {
            return "Namespaces";
        }

        foreach (var document in documents.EnumerateArray())
        {
            foreach (var candidate in EnumerateCandidateText(document))
            {
                var token = ExtractSearchToken(candidate);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
        }

        return "Namespaces";
    }

    private static string ResolveBrowsePageType(JsonElement payload)
    {
        if (!payload.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array)
        {
            return "guide";
        }

        foreach (var document in documents.EnumerateArray())
        {
            if (document.TryGetProperty("pageType", out var pageType)
                && pageType.ValueKind == JsonValueKind.String)
            {
                var value = pageType.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return "guide";
    }

    private static IEnumerable<string> EnumerateCandidateText(JsonElement document)
    {
        if (document.TryGetProperty("title", out var title)
            && title.ValueKind == JsonValueKind.String)
        {
            var titleText = title.GetString();
            if (!string.IsNullOrWhiteSpace(titleText))
            {
                yield return titleText!;
            }
        }

        if (document.TryGetProperty("headings", out var headings) && headings.ValueKind == JsonValueKind.Array)
        {
            foreach (var heading in headings.EnumerateArray())
            {
                if (heading.ValueKind == JsonValueKind.String)
                {
                    var headingText = heading.GetString();
                    if (!string.IsNullOrWhiteSpace(headingText))
                    {
                        yield return headingText!;
                    }
                }
            }
        }

        if (document.TryGetProperty("snippet", out var snippet)
            && snippet.ValueKind == JsonValueKind.String)
        {
            var snippetText = snippet.GetString();
            if (!string.IsNullOrWhiteSpace(snippetText))
            {
                yield return snippetText!;
            }
        }

        if (document.TryGetProperty("bodyText", out var bodyText)
            && bodyText.ValueKind == JsonValueKind.String)
        {
            var bodyTextValue = bodyText.GetString();
            if (!string.IsNullOrWhiteSpace(bodyTextValue))
            {
                yield return bodyTextValue!;
            }
        }
    }

    private static string? ExtractSearchToken(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var match = SearchTokenRegex.Match(input);
        return match.Success ? match.Value : null;
    }

    private string GetRecentLogs()
    {
        return _appHost?.Diagnostics ?? string.Empty;
    }
}
