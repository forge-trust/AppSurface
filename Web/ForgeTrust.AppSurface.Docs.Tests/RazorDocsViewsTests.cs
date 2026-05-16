using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Docs.ViewComponents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Tests;

public class RazorDocsViewsTests
{
    private static readonly Regex RawCssColorLiteralRegex = new(
        @"#[0-9a-fA-F]{3,8}\b|rgba?\(",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    [Fact]
    public void Layout_ShouldContain_SearchShellMarkers()
    {
        var layout = ReadLayoutMarkup();
        Assert.Contains("id=\"docs-search-input\"", layout);
        Assert.Contains("id=\"docs-search-results\"", layout);
        Assert.Contains("Url.PathBaseAware(DocsUrlBuilder.BuildAssetUrl(\"search.css\"))", layout);
        Assert.Contains("docsSearchIndexUrl", layout);
        Assert.Contains("var isSearchPage = string.Equals(", layout);
        Assert.Contains("crossorigin=\"use-credentials\"", layout);
        Assert.Contains("data-rw-search-runtime=\"minisearch\"", layout);
        Assert.DoesNotContain("src=\"~/docs/outline-client.js\"", layout);
        Assert.Contains("window.__razorDocsConfig", layout);
        Assert.Contains("Url.PathBaseAware(DocsUrlBuilder.BuildAssetUrl(\"search-client.js\"))", layout);
        Assert.Contains("Url.PathBaseAware(DocsUrlBuilder.BuildAssetUrl(\"minisearch.min.js\"))", layout);
    }

    [Fact]
    public async Task Layout_ShouldRenderRootStylesheet_WhenRazorDocsIsTheApplication()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderDocsViewAsync(services, "Index", c => c.Index());

        Assert.Contains("href=\"/css/site.gen.css", html);
    }

    [Fact]
    public async Task Layout_ShouldRenderPackagedStylesheet_WhenRazorDocsIsEmbeddedInAnotherHost()
    {
        using var services = CreateServiceProvider(
            CreateDocs(),
            rootModuleAssembly: typeof(RazorDocsViewsTests).Assembly);

        var html = await RenderDocsViewAsync(services, "Index", c => c.Index());

        Assert.Contains("href=\"/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css", html);
    }

    [Fact]
    public async Task Layout_ShouldRenderPathBaseAwareDocsChromeUrls()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderDocsViewAsync(
            services,
            "Search",
            c => c.Search(),
            pathBase: "/some-base");

        Assert.Contains("href=\"/some-base/docs/search.css\"", html);
        Assert.Contains("src=\"/some-base/docs/minisearch.min.js\"", html);
        Assert.Contains("src=\"/some-base/docs/search-client.js\"", html);
        Assert.Contains("\"docsRootPath\":\"/some-base/docs\"", html);
        Assert.Contains("\"docsSearchUrl\":\"/some-base/docs/search\"", html);
        Assert.Contains("\"docsSearchIndexUrl\":\"/some-base/docs/search-index.json\"", html);
    }

    [Fact]
    public void SearchClient_ShouldPersistAndRenderPageTypeBadgeFields()
    {
        var searchClient = ReadSearchClientMarkup();

        Assert.Contains("'pageTypeLabel'", searchClient);
        Assert.Contains("'pageTypeVariant'", searchClient);
        Assert.Contains("function renderPageTypeBadge(item)", searchClient);
        Assert.Contains("docs-search-option-title-row", searchClient);
        Assert.Contains("docs-page-badge", searchClient);
        Assert.Contains("function normalizePageTypeAlias(value)", searchClient);
        Assert.Contains("function isPageTypeInGroup(doc, group)", searchClient);
        Assert.Contains("function getPageTypeDisplayLabel(doc)", searchClient);
        Assert.Contains("function createSearchResultArticle(doc, queryTokens, options = {})", searchClient);
        Assert.Contains("docs-search-result-badges", searchClient);
        Assert.Contains("function createSearchResultPageTypeBadge(doc)", searchClient);
        Assert.Contains("createSearchResultBadge(formatFacetValue(doc.component))", searchClient);
        Assert.Contains("createSearchResultBadge(formatFacetValue(doc.audience), true)", searchClient);
        Assert.Contains("docs-search-result-meta-line", searchClient);
        Assert.Contains("docs-search-page-starter-docs", searchClient);
    }

    [Fact]
    public void SearchClient_ShouldNormalizeDocsRootPathsWithLeadingSlash()
    {
        var searchClient = ReadSearchClientMarkup();

        Assert.Contains("const prefixed = value.startsWith('/') ? value : `/${value}`;", searchClient);
        Assert.Contains("return prefixed !== '/' && prefixed.endsWith('/') ? prefixed.slice(0, -1) : prefixed;", searchClient);
    }

    [Fact]
    public void SearchClient_ShouldHandleRootMountedDocsSurface()
    {
        var searchClient = ReadSearchClientMarkup();

        Assert.Contains("const docsSearchUrl = rawConfig.docsSearchUrl || joinDocsPath(docsRootPath, 'search');", searchClient);
        Assert.Contains("const indexUrl = rawConfig.docsSearchIndexUrl || joinDocsPath(docsRootPath, 'search-index.json');", searchClient);
        Assert.Contains("const miniSearchUrl = rawConfig.miniSearchUrl || joinDocsPath(docsRootPath, 'minisearch.min.js');", searchClient);
        Assert.Contains("return root === '/' ? `/${normalizedLeaf}` : `${root}/${normalizedLeaf}`;", searchClient);
        Assert.Contains("docsByPath: collectInitialDocsPaths()", searchClient);
        Assert.Contains("function collectInitialDocsPaths()", searchClient);
        Assert.Contains("a[data-turbo-frame=\"${docsFrameId}\"][href]", searchClient);
        Assert.Contains("function isKnownRootMountedDocsNavigationPath(path)", searchClient);
        Assert.Contains("function getRootMountedDocsPathCandidates(path)", searchClient);
        Assert.Contains("if (normalizedPath.endsWith('.md'))", searchClient);
        Assert.Contains("if (normalizedPath.endsWith('.md.html'))", searchClient);
        Assert.Contains("candidates.add(normalizedPath.slice(0, -'.html'.length));", searchClient);
        Assert.Contains("docs.flatMap((doc) => getRootMountedDocsPathCandidates(toUrl(doc.path)?.pathname ?? doc.path))", searchClient);
        Assert.Contains("candidates.some((candidate) => searchData.docsByPath.has(candidate)", searchClient);
        Assert.Contains("candidates.add(`${canonicalPath}.html`);", searchClient);
        Assert.Contains("...collectInitialDocsPaths()", searchClient);
        Assert.Contains("normalizeComparablePath(searchUrl?.pathname)", searchClient);
        Assert.Contains("return isKnownRootMountedDocsNavigationPath(normalizedPath);", searchClient);
        Assert.DoesNotContain("? path.startsWith('/')", searchClient);
        Assert.Contains("const namespacesPath = joinDocsPath(docsRootPath, 'Namespaces');", searchClient);
    }

    [Fact]
    public void Stylesheets_ShouldKeepSharedDocsPrimitivesOutOfSearchStylesheet()
    {
        var tailwindEntryStylesheet = ReadTailwindEntryStylesheetMarkup();
        var searchStylesheet = ReadSearchStylesheetMarkup();

        Assert.Contains(".docs-page-badge", tailwindEntryStylesheet);
        Assert.Contains(".docs-metadata-chip", tailwindEntryStylesheet);
        Assert.Contains(".docs-page-meta", tailwindEntryStylesheet);
        Assert.Contains(".docs-provenance-strip", tailwindEntryStylesheet);
        Assert.Contains(".docs-trust-bar", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-shell", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-link", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-toggle-context", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-toggle-label", tailwindEntryStylesheet);
        Assert.Contains("max-width: clamp(8rem, 36%, 18rem);", tailwindEntryStylesheet);
        Assert.Contains("flex: 0 1 clamp(8rem, 36%, 18rem);", tailwindEntryStylesheet);
        Assert.Contains("text-overflow: ellipsis;", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-context-row--previous", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-context-row[data-outline-empty=\"true\"]", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-toggle-context--rolling", tailwindEntryStylesheet);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", tailwindEntryStylesheet);
        Assert.Contains("#docs-page-outline .docs-section-copy", tailwindEntryStylesheet);
        Assert.Contains(".docs-section-copy", tailwindEntryStylesheet);
        Assert.Contains(".docs-section-copy-fallback", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-shell[data-outline-enhanced=\"true\"] .docs-outline-toggle", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-shell[data-outline-enhanced=\"true\"] .docs-outline-label", tailwindEntryStylesheet);
        Assert.Contains("@media (max-width: 79.999rem)", tailwindEntryStylesheet);
        Assert.Contains(".docs-outline-shell[data-outline-enhanced=\"true\"] {", tailwindEntryStylesheet);
        Assert.Contains("position: sticky;", tailwindEntryStylesheet);
        Assert.Contains("--docs-outline-compact-bleed: 1rem;", tailwindEntryStylesheet);
        Assert.Contains("width: calc(100% + (var(--docs-outline-compact-bleed) * 2));", tailwindEntryStylesheet);
        Assert.Contains("margin-right: calc(var(--docs-outline-compact-bleed) * -1);", tailwindEntryStylesheet);
        Assert.Contains("border-top: 0;", tailwindEntryStylesheet);
        Assert.Contains("border-radius: 0;", tailwindEntryStylesheet);
        Assert.Contains("@media (min-width: 40rem) and (max-width: 79.999rem)", tailwindEntryStylesheet);
        Assert.Contains("--docs-outline-compact-bleed: 1.5rem;", tailwindEntryStylesheet);
        Assert.Contains("@media (max-width: 47.999rem)", tailwindEntryStylesheet);
        Assert.Contains("top: 3.8125rem;", tailwindEntryStylesheet);
        Assert.Contains("@media (min-width: 64rem) and (max-width: 79.999rem)", tailwindEntryStylesheet);
        Assert.Contains("--docs-outline-compact-bleed: 2rem;", tailwindEntryStylesheet);
        Assert.DoesNotContain(".docs-outline-shell {\n    order: -1;\n    position: sticky;", tailwindEntryStylesheet);

        Assert.DoesNotContain(".docs-page-badge", searchStylesheet);
        Assert.DoesNotContain(".docs-metadata-chip", searchStylesheet);
        Assert.DoesNotContain(".docs-page-meta", searchStylesheet);
        Assert.DoesNotContain(".docs-provenance-strip", searchStylesheet);
        Assert.DoesNotContain(".docs-trust-bar", searchStylesheet);
        Assert.DoesNotContain(".docs-outline-shell", searchStylesheet);
        Assert.DoesNotContain(".docs-outline-link", searchStylesheet);
        Assert.DoesNotContain(".docs-outline-toggle-context", searchStylesheet);
        Assert.DoesNotContain(".docs-section-copy", searchStylesheet);
    }

    [Fact]
    public void Stylesheets_ShouldDeclareAndConsumeInternalDocsStyleTokens()
    {
        var tailwindEntryStylesheet = ReadTailwindEntryStylesheetMarkup();
        var searchStylesheet = ReadSearchStylesheetMarkup();

        Assert.Contains(":root {", tailwindEntryStylesheet);
        Assert.Contains("--docs-brand-navy: #0d182a;", tailwindEntryStylesheet);
        Assert.Contains("--docs-brand-blue: #2563eb;", tailwindEntryStylesheet);
        Assert.Contains("--docs-brand-wordmark-blue: #3b82f6;", tailwindEntryStylesheet);
        Assert.Contains("--docs-brand-teal: #14b8a6;", tailwindEntryStylesheet);
        Assert.Contains("--docs-brand-violet: #8b5cf6;", tailwindEntryStylesheet);
        Assert.Contains("--docs-color-surface-canvas: #050b17;", tailwindEntryStylesheet);
        Assert.Contains("--docs-color-border-default: #314461;", tailwindEntryStylesheet);
        Assert.Contains("--docs-color-text-default: #e5e7eb;", tailwindEntryStylesheet);
        Assert.Contains("--docs-color-accent-strong: #2563eb;", tailwindEntryStylesheet);
        Assert.Contains("--docs-color-wordmark-edge-shadow: rgba(0, 0, 0, 0.45);", tailwindEntryStylesheet);
        Assert.Contains("--docs-focus-ring-inset:", tailwindEntryStylesheet);

        Assert.Contains("border: 1px solid var(--docs-color-border-default);", tailwindEntryStylesheet);
        Assert.Contains("color: var(--docs-color-accent);", tailwindEntryStylesheet);
        Assert.Contains("color: var(--docs-brand-wordmark-blue);", tailwindEntryStylesheet);
        Assert.Contains("text-shadow: 0 1px 2px var(--docs-color-wordmark-edge-shadow);", tailwindEntryStylesheet);
        Assert.Contains("outline: var(--docs-focus-outline);", tailwindEntryStylesheet);

        Assert.Contains("--docs-search-color-surface-canvas: var(--docs-color-surface-canvas, #050b17);", searchStylesheet);
        Assert.Contains("--docs-search-focus-ring-inset: var(--docs-focus-ring-inset,", searchStylesheet);
        Assert.Contains("background: var(--docs-search-color-surface-canvas);", searchStylesheet);
        Assert.Contains("border: 1px solid var(--docs-search-color-border-default);", searchStylesheet);
        Assert.Contains("box-shadow: var(--docs-search-focus-ring-inset);", searchStylesheet);
        Assert.Contains(
            "background: linear-gradient(90deg, var(--docs-search-color-skeleton-edge), var(--docs-search-color-skeleton-mid), var(--docs-search-color-skeleton-edge));",
            searchStylesheet);
    }

    [Fact]
    public void Stylesheets_ShouldKeepRawColorLiteralsClassified()
    {
        var tailwindEntryStylesheet = RemoveRootTokenBlock(ReadTailwindEntryStylesheetMarkup());
        var searchStylesheet = RemoveRootTokenBlock(ReadSearchStylesheetMarkup());

        Assert.Empty(FindUnclassifiedRawColorLiterals(tailwindEntryStylesheet));
        Assert.Empty(FindUnclassifiedRawColorLiterals(searchStylesheet));
    }

    [Fact]
    public void OutlineClient_ShouldUseMainContentRoot_AndRebindAfterFrameNavigation()
    {
        var outlineClient = ReadOutlineClientMarkup();

        Assert.Contains("const outlineSelector = \"#docs-page-outline\"", outlineClient);
        Assert.Contains("const mainContent = document.getElementById(\"main-content\")", outlineClient);
        Assert.Contains("if (!mainContent)", outlineClient);
        Assert.Contains("if (entries.length === 0)", outlineClient);
        Assert.Contains("root: mainContent", outlineClient);
        Assert.Contains("document.addEventListener(\"turbo:frame-load\"", outlineClient);
        Assert.Contains("activeObserver?.disconnect()", outlineClient);
        Assert.Contains("aria-current", outlineClient);
        Assert.Contains("data-doc-outline-context", outlineClient);
        Assert.Contains("const clientVersion = \"rolling-context\";", outlineClient);
        Assert.Contains("existingClient?.version === clientVersion", outlineClient);
        Assert.Contains("existingClient?.destroy?.();", outlineClient);
        Assert.Contains("destroy: destroyClient", outlineClient);
        Assert.Contains("document.removeEventListener(\"turbo:load\"", outlineClient);
        Assert.Contains("document.removeEventListener(\"turbo:frame-load\"", outlineClient);
        Assert.Contains("function resetStaleOutlineShell", outlineClient);
        Assert.Contains("shell.dataset.outlineClientVersion = clientVersion;", outlineClient);
        Assert.Contains("version: clientVersion", outlineClient);
        Assert.Contains("function setOutlineContext", outlineClient);
        Assert.Contains("data-doc-outline-previous", outlineClient);
        Assert.Contains("data-doc-outline-next", outlineClient);
        Assert.Contains("row.dataset.outlineEmpty = text ? \"false\" : \"true\";", outlineClient);
        Assert.Contains("prefers-reduced-motion: reduce", outlineClient);
        Assert.Contains("outlineRollDirection", outlineClient);
        Assert.Contains("typeof AbortController === \"function\"", outlineClient);
        Assert.Contains("function addLifecycleEventListener", outlineClient);
        Assert.Contains("removeEventListener", outlineClient);
        Assert.Contains("addListener", outlineClient);
        Assert.Contains("navigator.clipboard.writeText", outlineClient);
        Assert.Contains("function enhanceContentCopyTargets", outlineClient);
        Assert.Contains("function clearCopyArtifacts", outlineClient);
        Assert.Contains("data-doc-section-copy-inserted", outlineClient);
        Assert.Contains("data-doc-section-copy-fallback", outlineClient);
    }

    [Fact]
    public void Stylesheets_ShouldDefineMarkdownProseReadabilityRules()
    {
        var tailwindEntryStylesheet = ReadTailwindEntryStylesheetMarkup();

        Assert.Contains(".docs-content--markdown", tailwindEntryStylesheet);
        Assert.Contains("max-width: 70ch;", tailwindEntryStylesheet);
        Assert.Contains(".docs-content--markdown p", tailwindEntryStylesheet);
        Assert.Contains("margin: 0 0 1.08rem;", tailwindEntryStylesheet);
        Assert.Contains(".docs-content--markdown ul,", tailwindEntryStylesheet);
        Assert.Contains("list-style: disc;", tailwindEntryStylesheet);
        Assert.Contains("list-style: decimal;", tailwindEntryStylesheet);
        Assert.Contains(".docs-content--markdown li", tailwindEntryStylesheet);
        Assert.Contains("min-width: 0;", tailwindEntryStylesheet);
        Assert.Contains(".docs-content pre", tailwindEntryStylesheet);
        Assert.Contains("max-width: 100%;", tailwindEntryStylesheet);
        Assert.Contains(".docs-content--markdown blockquote", tailwindEntryStylesheet);
        Assert.Contains(".docs-content--markdown :not(pre) > code", tailwindEntryStylesheet);
    }

    [Fact]
    public void Layout_ShouldKeepSidebarVisibleByDefault_ForNoScriptFallback()
    {
        var layout = ReadLayoutMarkup();

        var sidebarStart = layout.IndexOf("<aside id=\"docs-sidebar\"", StringComparison.Ordinal);
        Assert.NotEqual(-1, sidebarStart);

        var sidebarEnd = layout.IndexOf(">", sidebarStart, StringComparison.Ordinal);
        Assert.NotEqual(-1, sidebarEnd);

        var sidebarDeclaration = layout.Substring(sidebarStart, sidebarEnd - sidebarStart);
        Assert.DoesNotContain("-translate-x-full", sidebarDeclaration);
    }

    [Fact]
    public void Layout_ShouldClampDocumentShellAroundMainScroller_ForWebKit()
    {
        var layout = ReadLayoutMarkup();

        Assert.Contains("<div class=\"flex h-full min-h-0 overflow-hidden\">", layout);
        Assert.Contains("id=\"main-content\" role=\"main\" tabindex=\"-1\" class=\"h-full min-h-0 flex-grow min-w-0 overflow-y-auto bg-transparent\"", layout);
    }

    [Fact]
    public void Layout_ShouldContain_MobileSidebarAccessibilityBehaviorMarkers()
    {
        var layout = ReadLayoutMarkup();

        Assert.Contains("id=\"docs-sidebar-overlay\"", layout);
        Assert.Contains("id=\"docs-sidebar-open\"", layout);
        Assert.Contains("id=\"docs-sidebar-close\"", layout);
        Assert.Contains("id=\"main-content\"", layout);
        Assert.Contains("tabindex=\"-1\"", layout);
        Assert.Contains("function getSidebarFocusableElements()", layout);
        Assert.Contains("setAttribute(\"inert\"", layout);
        Assert.Contains("removeAttribute(\"inert\")", layout);
        Assert.Contains("lastFocusedBeforeSidebarOpen.focus", layout);
    }

    [Fact]
    public async Task IndexView_ShouldRenderSidebarNamespacesWithoutTypeLinks()
    {
        using var services = CreateServiceProvider(
            CreateDocs(),
            new Dictionary<string, string?>
            {
                ["RazorDocs:Sidebar:NamespacePrefixes:0"] = "ForgeTrust.AppSurface."
            });

        var html = await RenderDocsViewAsync(services, "Index", c => c.Index());

        Assert.Contains("Documentation Index", html);
        Assert.Contains("href=\"/docs/sections/api-reference\"", html);
        Assert.Contains("href=\"/docs/Namespaces.html\"", html);
        Assert.DoesNotContain("href=\"/docs/Namespaces/ForgeTrust.AppSurface.Web.html#ForgeTrust.AppSurface.Web.AspireApp\"", html);
        Assert.Contains("ForgeTrust", html);
    }

    [Fact]
    public async Task IndexView_ShouldRenderCuratedFeaturedCards()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    Title = "AppSurface",
                    Summary = "Proof before promises.",
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "How does composition work?",
                                Path = "guides/composition.md",
                                SupportingCopy = "Follow the composition model.",
                                Order = 10
                            })
                    ]
                }),
            new(
                "Composition",
                "guides/composition.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "guide",
                    Summary = "Destination summary."
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Index", c => c.Index());

        Assert.Contains("<h1 class=\"docs-wordmark mt-3 text-3xl sm:text-4xl\">", html);
        Assert.Contains("App<span class=\"docs-wordmark-highlight\">Surface</span></h1>", html);
        Assert.Contains("Proof before promises.", html);
        Assert.Contains(">Test</h3>", html);
        Assert.Contains("How does composition work?", html);
        Assert.Contains(">Composition</h4>", html);
        Assert.Contains("Follow the composition model.", html);
        Assert.Contains("Guide", html);
        Assert.Contains("docs-page-badge--guide", html);
        Assert.Contains("href=\"/docs/guides/composition\"", html);
    }

    [Fact]
    public async Task IndexView_ShouldPreservePathBase_ForFeaturedAndFallbackLinks()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "How does composition work?",
                                Path = "guides/composition.md"
                            })
                    ]
                }),
            new(
                "Composition",
                "guides/composition.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Summary = "Destination summary."
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(
            services,
            "Index",
            c => c.Index(),
            httpContext => httpContext.Request.PathBase = "/tenant");

        Assert.Contains("href=\"/tenant/docs/sections/start-here\"", html);
        Assert.Contains("href=\"/tenant/docs/guides/composition\"", html);
    }

    [Fact]
    public async Task IndexView_ShouldFallbackToDestinationSummary_WhenSupportingCopyIsMissing()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "Show me an example",
                                Path = "examples/hello.md"
                            })
                    ]
                }),
            new(
                "Hello Example",
                "examples/hello.md",
                "<p>Example body</p>",
                Metadata: new DocMetadata
                {
                    Summary = "This is the summary fallback.",
                    PageType = "example"
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Index", c => c.Index());

        Assert.Contains("Show me an example", html);
        Assert.Contains("This is the summary fallback.", html);
        Assert.Contains("Example", html);
    }

    [Fact]
    public async Task IndexView_ShouldFormatKnownAndUnknownPageTypes_AndHideCardBadgeWhenPageTypeIsMissing()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "API card",
                                Path = "guides/api.md"
                            },
                            new DocFeaturedPageDefinition
                            {
                                Question = "How-to card",
                                Path = "guides/how-to.md"
                            },
                            new DocFeaturedPageDefinition
                            {
                                Question = "Start card",
                                Path = "guides/start.md"
                            },
                            new DocFeaturedPageDefinition
                            {
                                Question = "Untyped card",
                                Path = "guides/plain.md"
                            },
                            new DocFeaturedPageDefinition
                            {
                                Question = "Custom card",
                                Path = "guides/custom.md"
                            })
                    ]
                }),
            new("API Page", "guides/api.md", "<p>API body</p>", Metadata: new DocMetadata { PageType = "api-reference" }),
            new("How-To Page", "guides/how-to.md", "<p>How-to body</p>", Metadata: new DocMetadata { PageType = "how-to" }),
            new("Start Page", "guides/start.md", "<p>Start body</p>", Metadata: new DocMetadata { PageType = "start-here" }),
            new("Plain Page", "guides/plain.md", "<p>Plain body</p>"),
            new("Custom Page", "guides/custom.md", "<p>Custom body</p>", Metadata: new DocMetadata { PageType = "custom_reference" })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Index", c => c.Index());
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        Assert.Equal(
            "API Reference",
            document.QuerySelector("a.group[href='/docs/guides/api'] span.docs-page-badge")?.TextContent.Trim());
        Assert.Equal(
            "How-To",
            document.QuerySelector("a.group[href='/docs/guides/how-to'] span.docs-page-badge")?.TextContent.Trim());
        Assert.Equal(
            "Start Here",
            document.QuerySelector("a.group[href='/docs/guides/start'] span.docs-page-badge")?.TextContent.Trim());
        Assert.Equal(
            "Custom Reference",
            document.QuerySelector("a.group[href='/docs/guides/custom'] span.docs-page-badge")?.TextContent.Trim());
        Assert.Contains(
            "docs-page-badge--neutral",
            document.QuerySelector("a.group[href='/docs/guides/custom'] span.docs-page-badge")?.ClassName ?? string.Empty);

        var untypedCard = document.QuerySelector("a.group[href='/docs/guides/plain']");
        Assert.NotNull(untypedCard);
        Assert.Null(untypedCard!.QuerySelector("span.docs-page-badge"));
        Assert.Null(untypedCard.QuerySelector("p.mt-3"));
    }

    [Fact]
    public async Task IndexView_ShouldRenderNeutralFallback_WhenFeaturedEntriesResolveToHiddenPages()
    {
        var docs = new List<DocNode>
        {
            new(
                "Home",
                "README.md",
                "<p>Home</p>",
                Metadata: new DocMetadata
                {
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            new DocFeaturedPageDefinition
                            {
                                Question = "Show me internals",
                                Path = "guides/hidden.md"
                            })
                    ]
                }),
            new(
                "Hidden Guide",
                "guides/hidden.md",
                "<p>Guide body</p>",
                Metadata: new DocMetadata
                {
                    HideFromPublicNav = true
                }),
            new("Guide", "guides/intro.md", "<p>Guide body</p>")
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Index", c => c.Index());

        Assert.Contains("Documentation", html);
        Assert.DoesNotContain("Show me internals", html);
        Assert.Contains("Follow the proof path first.", html);
        Assert.DoesNotContain("Open Start Here", html);
    }

    [Fact]
    public async Task IndexView_ShouldRenderStartHereCta_AndSecondarySectionRoutes()
    {
        var docs = new List<DocNode>
        {
            new("Home", "README.md", "<p>Home</p>"),
            new(
                "Quickstart",
                "guides/quickstart.md",
                "<p>Quickstart</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here"
                }),
            new(
                "Concept Landing",
                "concepts/landing.md",
                "<p>Concept landing</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    SectionLanding = true,
                    FeaturedPageGroups =
                    [
                        FeaturedGroup(
                            "Learn the mental model",
                            new DocFeaturedPageDefinition
                            {
                                Question = "Learn the mental model",
                                Path = "concepts/deep-dive.md"
                            })
                    ]
                }),
            new(
                "Deep Dive",
                "concepts/deep-dive.md",
                "<p>Deep dive</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    Summary = "The deeper summary.",
                    PageType = "guide"
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Index", c => c.Index());

        Assert.Contains("href=\"/docs/sections/start-here\"", html);
        Assert.Contains("Open Start Here", html);
        Assert.Contains("href=\"/docs/sections/concepts\"", html);
        Assert.Contains("Build the mental model before you choose an implementation path.", html);
        Assert.Contains("Learn the mental model", html);
        Assert.Contains("Guide", html);
        Assert.Contains("The deeper summary.", html);
    }

    [Fact]
    public async Task SectionView_ShouldHideStartHereCta_WhenStartHereSectionIsUnavailable()
    {
        var docs = new List<DocNode>
        {
            new(
                "Conceptual Overview",
                "concepts/overview.md",
                "<p>Concept body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    Summary = "Understand the concepts."
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Section", c => c.Section("concepts"));

        Assert.DoesNotContain("href=\"/docs/sections/start-here\"", html);
        Assert.DoesNotContain(">Start Here<", html);
        Assert.Contains("href=\"/docs\"", html);
    }

    [Fact]
    public async Task SectionView_ShouldRenderAvailabilityMessage_WhenSectionIsUnavailable()
    {
        var docs = new List<DocNode>
        {
            new(
                "Conceptual Overview",
                "concepts/overview.md",
                "<p>Concept body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts"
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Section", c => c.Section("start-here"));

        Assert.Contains("This section may be hidden from the public shell", html);
    }

    [Fact]
    public async Task Section_ShouldRedirectAliasSectionRequests_ToCanonicalSlug()
    {
        var docs = new List<DocNode>
        {
            new(
                "Quickstart",
                "guides/quickstart.md",
                "<p>Quickstart body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here"
                })
        };
        using var services = CreateServiceProvider(docs);

        var result = await InvokeDocsActionAsync(services, "Section", controller => controller.Section("quickstart"));

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/docs/sections/start-here", redirect.Url);
    }

    [Fact]
    public async Task Section_ShouldRedirectToLandingDoc_WhenSectionHasAuthoredLanding()
    {
        var docs = new List<DocNode>
        {
            new(
                "Concept Landing",
                "concepts/landing.md",
                "<p>Concept landing</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    SectionLanding = true
                })
        };
        using var services = CreateServiceProvider(docs);

        var result = await InvokeDocsActionAsync(services, "Section", controller => controller.Section("concepts"));

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/docs/concepts/landing", redirect.Url);
    }

    [Fact]
    public async Task SectionView_ShouldHideStartHereCta_WhenViewingStartHereSection()
    {
        var docs = new List<DocNode>
        {
            new(
                "Quickstart",
                "guides/quickstart.md",
                "<p>Quickstart body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Summary = "Start here."
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Section", c => c.Section("start-here"));
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var actionLinks = document.QuerySelectorAll("div.mt-6.flex.flex-wrap.gap-3 > a")
            .Select(link => link.GetAttribute("href"))
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .ToArray();

        Assert.DoesNotContain("/docs/sections/start-here", actionLinks);
        Assert.Contains("href=\"/docs\"", html);
    }

    [Fact]
    public async Task SectionView_ShouldShowStartHereCta_WhenStartHereSectionExistsAndNotViewingIt()
    {
        var docs = new List<DocNode>
        {
            new(
                "Quickstart",
                "guides/quickstart.md",
                "<p>Quickstart body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    Summary = "Start here."
                }),
            new(
                "Conceptual Overview",
                "concepts/overview.md",
                "<p>Concept body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    Summary = "Understand the concepts."
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Section", c => c.Section("concepts"));
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var actionLinks = document.QuerySelectorAll("div.mt-6.flex.flex-wrap.gap-3 > a").ToArray();

        Assert.Contains(
            actionLinks,
            link => string.Equals(link.GetAttribute("href"), "/docs/sections/start-here", StringComparison.Ordinal));
        Assert.Contains(actionLinks, link => link.TextContent.Contains("Start Here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SectionView_ShouldRenderSparseRoutes_WithBadgesAndSummaries()
    {
        var docs = new List<DocNode>
        {
            new(
                "Quickstart",
                "guides/quickstart.md",
                "<p>Quickstart body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here"
                }),
            new(
                "Conceptual Overview",
                "concepts/overview.md",
                "<p>Concept body</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Concepts",
                    Summary = "Understand the concepts.",
                    PageType = "guide"
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Section", c => c.Section("concepts"));

        Assert.Contains("This section is still growing", html);
        Assert.Contains("Understand the concepts.", html);
        Assert.Contains("docs-page-badge--guide", html);
    }

    [Fact]
    public async Task SectionView_ShouldRenderEditorialGroupedLinks_BadgesSummariesAndChildren()
    {
        var docs = new List<DocNode>
        {
            new(
                "Install",
                "guides/install.md",
                "<p>Install guide</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "How-to Guides",
                    PageType = "guide",
                    Summary = "Install guide summary."
                }),
            new(
                "Install steps",
                "guides/install.md#install-steps",
                string.Empty,
                ParentPath: "guides/install.md",
                Metadata: new DocMetadata
                {
                    NavGroup = "How-to Guides",
                    PageType = "guide"
                })
        };
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Section", c => c.Section("how-to-guides"));

        Assert.Contains("Browse the public pages here.", html);
        Assert.Contains("Install guide summary.", html);
        Assert.Contains("docs-page-badge--guide", html);
        Assert.Contains("Install steps", html);
    }

    [Fact]
    public async Task SectionChildrenPartial_ShouldRenderRecursiveApiNamespaceChildren()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var children = new[]
        {
            new DocSectionLinkViewModel
            {
                Title = "Baz",
                Href = "/docs/Namespaces/Foo.Bar.Baz.html",
                Children =
                [
                    new DocSectionLinkViewModel
                    {
                        Title = "Qux",
                        Href = "/docs/Namespaces/Foo.Bar.Baz.Qux.html"
                    }
                ]
            }
        };

        var html = await RenderViewAsync(
            services,
            "/Views/Shared/_DocSectionCardChildren.cshtml",
            children);

        Assert.Contains("href=\"/docs/Namespaces/Foo.Bar.Baz.html\"", html);
        Assert.Contains("href=\"/docs/Namespaces/Foo.Bar.Baz.Qux.html\"", html);
        Assert.Contains("Baz", html);
        Assert.Contains("Qux", html);
    }

    [Fact]
    public async Task SidebarView_ShouldRenderAriaCurrentAttributes_UsingConditionalAttributeValues()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var model = new DocSidebarViewModel
        {
            Sections =
            [
                new DocSidebarSectionViewModel
                {
                    Section = DocPublicSection.HowToGuides,
                    Label = "How-to Guides",
                    Slug = "how-to-guides",
                    Href = "/docs/sections/how-to-guides",
                    IsActive = true,
                    IsExpanded = true,
                    Groups =
                    [
                        new DocSectionGroupViewModel
                        {
                            Links =
                            [
                                new DocSectionLinkViewModel
                                {
                                    Title = "Guide",
                                    Href = "/docs/guides/guide",
                                    IsCurrent = true,
                                    Children =
                                    [
                                        new DocSectionLinkViewModel
                                        {
                                            Title = "Run",
                                            Href = "/docs/guides/guide#run",
                                            IsCurrent = true,
                                            Children =
                                            [
                                                new DocSectionLinkViewModel
                                                {
                                                    Title = "Verify",
                                                    Href = "/docs/guides/guide#verify"
                                                }
                                            ]
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var html = await RenderViewAsync(
            services,
            "/Views/Shared/Components/Sidebar/Default.cshtml",
            model);

        Assert.Contains("href=\"/docs/sections/how-to-guides\"", html);
        Assert.Contains("aria-current=\"location\"", html);
        Assert.Contains("href=\"/docs/guides/guide\"", html);
        Assert.Contains("href=\"/docs/guides/guide#verify\"", html);
        Assert.Contains("aria-current=\"page\"", html);
        Assert.DoesNotContain("aria-current=&quot;page&quot;", html);
        Assert.DoesNotContain("aria-current=&quot;location&quot;", html);
    }

    [Fact]
    public async Task SidebarView_ShouldRenderPathBaseAwareLinks()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var model = new DocSidebarViewModel
        {
            Sections =
            [
                new DocSidebarSectionViewModel
                {
                    Section = DocPublicSection.HowToGuides,
                    Label = "How-to Guides",
                    Slug = "how-to-guides",
                    Href = "/docs/sections/how-to-guides",
                    IsActive = true,
                    IsExpanded = true,
                    Groups =
                    [
                        new DocSectionGroupViewModel
                        {
                            Links =
                            [
                                new DocSectionLinkViewModel
                                {
                                    Title = "Guide",
                                    Href = "/docs/guides/guide",
                                    IsCurrent = true,
                                    Children =
                                    [
                                        new DocSectionLinkViewModel
                                        {
                                            Title = "Run",
                                            Href = "/docs/guides/guide#run",
                                            IsCurrent = true
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var html = await RenderViewAsync(
            services,
            "/Views/Shared/Components/Sidebar/Default.cshtml",
            model,
            pathBase: "/some-base");

        Assert.Contains("href=\"/some-base/docs/sections/how-to-guides\"", html);
        Assert.Contains("href=\"/some-base/docs/guides/guide\"", html);
        Assert.Contains("href=\"/some-base/docs/guides/guide#run\"", html);
    }

    [Fact]
    public async Task SidebarView_ShouldRenderHarvestHealthLink_WhenModelProvidesHealth()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var model = new DocSidebarViewModel
        {
            HarvestHealth = new DocSidebarHarvestHealthViewModel
            {
                Status = "Degraded",
                Ok = false,
                Href = "/docs/_health"
            }
        };

        var html = await RenderViewAsync(
            services,
            "/Views/Shared/Components/Sidebar/Default.cshtml",
            model,
            pathBase: "/some-base");

        Assert.Contains("href=\"/some-base/docs/_health\"", html);
        Assert.Contains("Harvest Degraded", html);
        Assert.Contains("Health", html);
        Assert.Contains("text-rose-200", html);
    }

    [Fact]
    public async Task SidebarView_ShouldRenderHarvestHealthStatusWithoutLink_WhenHealthHrefIsHidden()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var model = new DocSidebarViewModel
        {
            HarvestHealth = new DocSidebarHarvestHealthViewModel
            {
                Status = "StatusOnly",
                Ok = true,
                Href = null
            }
        };

        var html = await RenderViewAsync(
            services,
            "/Views/Shared/Components/Sidebar/Default.cshtml",
            model);

        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var statusElement = document.QuerySelectorAll("span")
            .FirstOrDefault(element => element.TextContent.Contains("Harvest StatusOnly", StringComparison.Ordinal));
        Assert.NotNull(statusElement);
        Assert.NotNull(FindAncestor(statusElement, "div"));
        Assert.Null(FindAncestor(statusElement, "a"));
        Assert.Contains("Harvest StatusOnly", html);
        Assert.Contains("Health", html);
        Assert.Contains("text-emerald-200", html);
    }

    [Fact]
    public async Task HarvestHealthView_ShouldRenderRedactedSummaryAndDiagnostics()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var model = new RazorDocsHarvestHealthResponse
        {
            Status = "Failed",
            GeneratedUtc = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero),
            Verification = new RazorDocsHarvestHealthVerification
            {
                Ok = false,
                HttpStatusCode = StatusCodes.Status503ServiceUnavailable
            },
            TotalHarvesters = 1,
            FailedHarvesters = 1,
            Harvesters =
            [
                new RazorDocsHarvesterHealthResponse
                {
                    HarvesterType = "MarkdownHarvester",
                    Status = "Failed",
                    DocCount = 0
                }
            ],
            Diagnostics =
            [
                new RazorDocsHarvestDiagnosticResponse
                {
                    Code = DocHarvestDiagnosticCodes.HarvesterFailed,
                    Severity = "Error",
                    HarvesterType = "MarkdownHarvester",
                    Problem = "A RazorDocs harvester failed.",
                    Fix = "Check the docs source."
                }
            ]
        };

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/HarvestHealth.cshtml",
            model);

        Assert.Contains("Harvest Health", html);
        Assert.Contains("Failed", html);
        Assert.Contains("failing", html);
        Assert.Contains("MarkdownHarvester", html);
        Assert.Contains(DocHarvestDiagnosticCodes.HarvesterFailed, html);
        Assert.DoesNotContain("RepositoryRoot", html);
        Assert.DoesNotContain("Cause", html);
    }

    [Fact]
    public async Task HarvestHealthView_ShouldHideDiagnosticsSection_WhenDiagnosticsAreEmpty()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var model = new RazorDocsHarvestHealthResponse
        {
            Status = "Healthy",
            GeneratedUtc = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero),
            Verification = new RazorDocsHarvestHealthVerification
            {
                Ok = true,
                HttpStatusCode = StatusCodes.Status200OK
            },
            TotalHarvesters = 1,
            SuccessfulHarvesters = 1,
            TotalDocs = 3,
            Harvesters =
            [
                new RazorDocsHarvesterHealthResponse
                {
                    HarvesterType = "MarkdownHarvester",
                    Status = "Healthy",
                    DocCount = 3
                }
            ],
            Diagnostics = []
        };

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/HarvestHealth.cshtml",
            model);

        Assert.Contains("Harvest Health", html);
        Assert.Contains("Healthy", html);
        Assert.Contains("passing", html);
        Assert.Contains("MarkdownHarvester", html);
        Assert.DoesNotContain("diagnostics-heading", html);
        Assert.DoesNotContain("<h2 id=\"diagnostics-heading\"", html);
        Assert.DoesNotContain(DocHarvestDiagnosticCodes.HarvesterFailed, html);
    }

    [Fact]
    public async Task SidebarView_ShouldRenderBlankAndRelativeLinks_WithoutPathBaseRewriting()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var html = await RenderViewAsync(
            services,
            "/Views/Shared/Components/Sidebar/Default.cshtml",
            new DocSidebarViewModel
            {
                Sections =
                [
                    new DocSidebarSectionViewModel
                    {
                        Section = DocPublicSection.HowToGuides,
                        Label = "How-to Guides",
                        Slug = "how-to-guides",
                        Href = " ",
                        IsActive = true,
                        IsExpanded = true,
                        Groups =
                        [
                            new DocSectionGroupViewModel
                            {
                                Links =
                                [
                                    new DocSectionLinkViewModel
                                    {
                                        Title = "Relative guide",
                                        Href = "guides/relative"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            pathBase: "/some-base");

        Assert.Contains("href=\"\"", html);
        Assert.Contains("href=\"guides/relative\"", html);
    }

    [Fact]
    public void BuildGroups_ShouldSetUseAnchorNavigation_OnTopLevelSectionLinks()
    {
        var snapshot = new DocSectionSnapshot
        {
            Section = DocPublicSection.HowToGuides,
            Label = "How-to Guides",
            Slug = "how-to-guides",
            VisiblePages =
            [
                new(
                    "Guide",
                    "guides/guide.md",
                    "<p>Guide body</p>",
                    Metadata: new DocMetadata
                    {
                        Summary = "Follow this guide."
                    })
            ]
        };

        var groups = DocSectionDisplayBuilder.BuildGroups(snapshot);
        var link = Assert.Single(Assert.Single(groups).Links);

        Assert.True(link.UseAnchorNavigation);
    }

    [Fact]
    public void BuildGroups_ShouldIncludeSlashPaddedNamespacePaths_InApiReferenceGroups()
    {
        var snapshot = new DocSectionSnapshot
        {
            Section = DocPublicSection.ApiReference,
            Label = "API Reference",
            Slug = "api-reference",
            VisiblePages =
            [
                new("Namespaces", "Namespaces", "<p>Namespaces</p>", CanonicalPath: "Namespaces.html"),
                new("Foo", "/Namespaces/Foo", "<p>Foo namespace</p>", CanonicalPath: "Namespaces/Foo.html")
            ]
        };

        var groups = DocSectionDisplayBuilder.BuildGroups(snapshot, namespacePrefixes: ["Foo"]);

        Assert.Contains(
            groups.SelectMany(group => group.Links),
            link => string.Equals(link.Href, "/docs/Namespaces/Foo.html", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildGroups_ShouldNormalizeSourcePaths_ForHrefFallbackAndEditorialChildren()
    {
        var snapshot = new DocSectionSnapshot
        {
            Section = DocPublicSection.HowToGuides,
            Label = "How-to Guides",
            Slug = "how-to-guides",
            VisiblePages =
            [
                new("Guide", "/docs/guide.md", "<p>Guide</p>"),
                new(
                    "Build",
                    "docs/guide.md#Build",
                    string.Empty,
                    ParentPath: "docs/guide.md")
            ]
        };

        var groups = DocSectionDisplayBuilder.BuildGroups(snapshot);
        var link = Assert.Single(groups.SelectMany(group => group.Links));
        var child = Assert.Single(link.Children);

        Assert.Equal("/docs/docs/guide.md", link.Href);
        Assert.DoesNotContain("//docs", link.Href, StringComparison.Ordinal);
        Assert.Equal("Build", child.Title);
    }

    [Fact]
    public void BuildGroups_ShouldMatchEditorialAnchorChildren_CaseInsensitively()
    {
        var snapshot = new DocSectionSnapshot
        {
            Section = DocPublicSection.HowToGuides,
            Label = "How-to Guides",
            Slug = "how-to-guides",
            VisiblePages =
            [
                new("Guide", "docs/guide.md", "<p>Guide</p>", CanonicalPath: "docs/guide"),
                new(
                    "Build",
                    "docs/guide.md#Build",
                    string.Empty,
                    ParentPath: "DOCS/GUIDE.MD",
                    CanonicalPath: "docs/guide#Build")
            ]
        };

        var groups = DocSectionDisplayBuilder.BuildGroups(snapshot);
        var link = Assert.Single(groups.SelectMany(group => group.Links));
        var child = Assert.Single(link.Children);

        Assert.Equal("Build", child.Title);
    }

    [Fact]
    public void BuildGroups_ShouldOmitTypeAnchorChildren_FromApiReferenceGroups()
    {
        var snapshot = new DocSectionSnapshot
        {
            Section = DocPublicSection.ApiReference,
            Label = "API Reference",
            Slug = "api-reference",
            VisiblePages =
            [
                new("Foo", "Namespaces/Foo", "<p>Foo namespace</p>", CanonicalPath: "Namespaces/Foo.html"),
                new(
                    "Widget",
                    "Namespaces/Foo#Widget",
                    string.Empty,
                    ParentPath: "Namespaces/Foo",
                    CanonicalPath: "Namespaces/Foo.html#Widget")
            ]
        };

        var groups = DocSectionDisplayBuilder.BuildGroups(snapshot);
        var link = Assert.Single(groups.SelectMany(group => group.Links));

        Assert.Empty(link.Children);
    }

    [Fact]
    public void BuildGroups_ShouldNestDeepApiNamespacesUnderNearestParent_WithLeafLabels()
    {
        var snapshot = new DocSectionSnapshot
        {
            Section = DocPublicSection.ApiReference,
            Label = "API Reference",
            Slug = "api-reference",
            VisiblePages =
            [
                new("Core", "Namespaces/ForgeTrust.AppSurface.Core", "<p>Core namespace</p>", CanonicalPath: "Namespaces/ForgeTrust.AppSurface.Core.html"),
                new("Defaults", "Namespaces/ForgeTrust.AppSurface.Core.Defaults", "<p>Defaults namespace</p>", CanonicalPath: "Namespaces/ForgeTrust.AppSurface.Core.Defaults.html"),
                new("Extensions", "Namespaces/ForgeTrust.AppSurface.Core.Extensions", "<p>Extensions namespace</p>", CanonicalPath: "Namespaces/ForgeTrust.AppSurface.Core.Extensions.html"),
                new("Aspire", "Namespaces/ForgeTrust.AppSurface.Aspire", "<p>Aspire namespace</p>", CanonicalPath: "Namespaces/ForgeTrust.AppSurface.Aspire.html"),
                new("Configuration", "Namespaces/Microsoft.Extensions.Configuration", "<p>Configuration namespace</p>", CanonicalPath: "Namespaces/Microsoft.Extensions.Configuration.html")
            ]
        };

        var groups = DocSectionDisplayBuilder.BuildGroups(snapshot);
        var links = groups.SelectMany(group => group.Links).ToList();
        var coreLink = Assert.Single(links, link => string.Equals(link.Title, "AppSurface.Core", StringComparison.Ordinal));

        Assert.Equal(["Defaults", "Extensions"], coreLink.Children.Select(child => child.Title).ToArray());
        Assert.Equal("AppSurface.Aspire", Assert.Single(links, link => string.Equals(link.Title, "AppSurface.Aspire", StringComparison.Ordinal)).Title);
        Assert.DoesNotContain(links, link => string.Equals(link.Title, "AppSurface.Core.Defaults", StringComparison.Ordinal));
        Assert.DoesNotContain(links, link => string.Equals(link.Title, "AppSurface.Core.Extensions", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildGroups_ShouldNestApiNamespacesUnderTwoSegmentParent_WhenParentPageExists()
    {
        var snapshot = new DocSectionSnapshot
        {
            Section = DocPublicSection.ApiReference,
            Label = "API Reference",
            Slug = "api-reference",
            VisiblePages =
            [
                new("Bar", "Namespaces/Foo.Bar", "<p>Bar namespace</p>", CanonicalPath: "Namespaces/Foo.Bar.html"),
                new("Baz", "Namespaces/Foo.Bar.Baz", "<p>Baz namespace</p>", CanonicalPath: "Namespaces/Foo.Bar.Baz.html")
            ]
        };

        var groups = DocSectionDisplayBuilder.BuildGroups(snapshot);
        var barLink = Assert.Single(groups.SelectMany(group => group.Links));
        var bazLink = Assert.Single(barLink.Children);

        Assert.Equal("Bar", barLink.Title);
        Assert.Equal("Baz", bazLink.Title);
        Assert.Equal("/docs/Namespaces/Foo.Bar.Baz.html", bazLink.Href);
    }

    [Fact]
    public void BuildGroups_ShouldKeepRecursiveNestedApiNamespacesReachable()
    {
        var snapshot = new DocSectionSnapshot
        {
            Section = DocPublicSection.ApiReference,
            Label = "API Reference",
            Slug = "api-reference",
            VisiblePages =
            [
                new("Core", "Namespaces/ForgeTrust.AppSurface.Core", "<p>Core namespace</p>", CanonicalPath: "Namespaces/ForgeTrust.AppSurface.Core.html"),
                new("Defaults", "Namespaces/ForgeTrust.AppSurface.Core.Defaults", "<p>Defaults namespace</p>", CanonicalPath: "Namespaces/ForgeTrust.AppSurface.Core.Defaults.html"),
                new("Internal", "Namespaces/ForgeTrust.AppSurface.Core.Defaults.Internal", "<p>Internal namespace</p>", CanonicalPath: "Namespaces/ForgeTrust.AppSurface.Core.Defaults.Internal.html")
            ]
        };

        var groups = DocSectionDisplayBuilder.BuildGroups(snapshot, namespacePrefixes: ["ForgeTrust."]);
        var coreLink = Assert.Single(groups.SelectMany(group => group.Links));
        var defaultsLink = Assert.Single(coreLink.Children);
        var internalLink = Assert.Single(defaultsLink.Children);

        Assert.Equal("Defaults", defaultsLink.Title);
        Assert.Equal("Internal", internalLink.Title);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderNamespaceBreadcrumbLinks()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderDocsViewAsync(
            services,
            "Details",
            c => c.Details("Namespaces/ForgeTrust.AppSurface.Web.html"));

        Assert.Contains("aria-label=\"Breadcrumb\"", html);
        Assert.Contains("href=\"/docs/Namespaces.html\"", html);
        Assert.Contains("href=\"/docs/Namespaces/ForgeTrust.html\"", html);
        Assert.Contains("href=\"/docs/Namespaces/ForgeTrust.AppSurface.html\"", html);
        Assert.Contains(">Web</h1>", html);
    }

    [Fact]
    public async Task IndexView_ShouldRenderPathBaseAwareLandingLinks()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderDocsViewAsync(
            services,
            "Index",
            c => c.Index(),
            pathBase: "/some-base");

        Assert.Contains("href=\"/some-base/docs/sections/api-reference\"", html);
        Assert.Contains("href=\"/some-base/docs/guides/intro\"", html);
    }

    [Fact]
    public async Task IndexView_ShouldLeaveRelativeLinksUnchanged_AndSuppressBlankStartHereHref()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Index.cshtml",
            new DocLandingViewModel
            {
                Heading = "Documentation",
                Description = "Choose a route.",
                StartHereHref = "   ",
                FeaturedPageGroups =
                [
                    new DocLandingFeaturedPageGroupViewModel
                    {
                        Label = "Relative routes",
                        Pages =
                        [
                            new DocLandingFeaturedPageViewModel
                            {
                                Question = "Blank",
                                Title = "No destination yet",
                                Href = " "
                            },
                            new DocLandingFeaturedPageViewModel
                            {
                                Question = "Start",
                                Title = "Relative guide",
                                Href = "guides/relative"
                            }
                        ]
                    }
                ],
                SecondarySections =
                [
                    new DocHomeSectionViewModel
                    {
                        Section = DocPublicSection.Concepts,
                        Label = "Concepts",
                        Slug = "concepts",
                        Href = "sections/concepts",
                        Purpose = "Learn the model.",
                        KeyRoutes =
                        [
                            new DocSectionLinkViewModel
                            {
                                Title = "Relative child",
                                Href = "guides/child"
                            }
                        ]
                    }
                ]
            },
            pathBase: "/some-base");

        Assert.DoesNotContain("Open Start Here", html);
        Assert.Contains("href=\"\"", html);
        Assert.Contains("href=\"guides/relative\"", html);
        Assert.Contains("href=\"sections/concepts\"", html);
        Assert.Contains("href=\"guides/child\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderSectionLandingChrome_WithFeaturedPagesAndSectionGroups()
    {
        var landingDoc = new DocNode(
            "Concept Landing",
            "concepts/landing.md",
            "<p>Landing body</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "Concepts",
                SectionLanding = true,
                Summary = "Landing summary.",
                FeaturedPageGroups =
                [
                    new DocFeaturedPageGroupDefinition
                    {
                        Intent = "test",
                        Label = "Test",
                        Summary = "Choose this path when you need section context.",
                        Pages =
                        [
                            new DocFeaturedPageDefinition
                            {
                                Question = "Go deeper",
                                Path = "concepts/deep-dive.md",
                                SupportingCopy = "Follow the next route."
                            }
                        ]
                    }
                ]
            });
        var deepDive = new DocNode(
            "Deep Dive",
            "concepts/deep-dive.md",
            "<p>Deep dive body</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "Concepts",
                Summary = "Deep dive summary.",
                PageType = "guide"
            });
        var anchor = new DocNode(
            "Jump to section",
            "concepts/deep-dive.md#jump",
            string.Empty,
            ParentPath: "concepts/deep-dive.md",
            Metadata: new DocMetadata
            {
                NavGroup = "Concepts"
            });

        var html = await RenderDetailsViewAsync(landingDoc, deepDive, anchor);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var sectionRoutes = document.QuerySelectorAll("a[href='/docs/sections/concepts']")
            .Where(link => (link.ClassName ?? string.Empty).Contains("rounded-full", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains("Section landing", html);
        Assert.Contains("Use this section as the entry point.", html);
        Assert.Equal(2, sectionRoutes.Length);
        Assert.All(sectionRoutes, sectionRoute =>
        {
            Assert.Equal("doc-content", sectionRoute.GetAttribute("data-turbo-frame"));
            Assert.Equal("advance", sectionRoute.GetAttribute("data-turbo-action"));
        });
        Assert.Contains("Next steps", html);
        Assert.Contains(">Test</h3>", html);
        Assert.Contains("Choose this path when you need section context.", html);
        Assert.Contains("Go deeper", html);
        Assert.Contains("Follow the next route.", html);
        Assert.Contains("In this section", html);
        Assert.Contains("Deep dive summary.", html);
        Assert.Contains("Jump to section", html);
    }

    [Fact]
    public async Task SectionView_ShouldRenderPathBaseAwareNavigationLinks()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderDocsViewAsync(
            services,
            "Section",
            c => c.Section("api-reference"),
            httpContext => httpContext.Request.PathBase = "/some-base");

        Assert.Contains("href=\"/some-base/docs\"", html);
        Assert.Contains("href=\"/some-base/docs/Namespaces/ForgeTrust.html\"", html);
    }

    [Fact]
    public async Task SectionView_ShouldLeaveRelativeLinksUnchanged_AndSuppressBlankStartHereHref()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Section.cshtml",
            new DocSectionPageViewModel
            {
                Section = DocPublicSection.Concepts,
                Heading = "Concepts",
                Description = "Relative links stay relative.",
                DocsHomeHref = " ",
                StartHereHref = " ",
                IsSparse = true,
                KeyRoutes =
                [
                    new DocSectionLinkViewModel
                    {
                        Title = "Relative route",
                        Href = "guides/relative"
                    }
                ]
            },
            configureHttpContext: httpContext => httpContext.Request.PathBase = "/some-base");

        Assert.DoesNotContain(">Start Here<", html);
        Assert.Contains("href=\"\"", html);
        Assert.Contains("href=\"guides/relative\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldPreservePathBase_ForSectionLandingFeaturedLinks()
    {
        var landingDoc = new DocNode(
            "Concept Landing",
            "concepts/landing.md",
            "<p>Landing body</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "Concepts",
                SectionLanding = true,
                FeaturedPageGroups =
                [
                    FeaturedGroup(
                        new DocFeaturedPageDefinition
                        {
                            Question = "Go deeper",
                            Path = "concepts/deep-dive.md"
                        })
                ]
            });
        var deepDive = new DocNode(
            "Deep Dive",
            "concepts/deep-dive.md",
            "<p>Deep dive body</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "Concepts"
            });
        using var services = CreateServiceProvider(CreateDocsWithOverrides([landingDoc, deepDive]));

        var html = await RenderDocsViewAsync(
            services,
            "Details",
            controller => controller.Details("concepts/landing"),
            httpContext => httpContext.Request.PathBase = "/tenant");

        Assert.Contains("href=\"/tenant/docs/sections/concepts\"", html);
        Assert.Contains("href=\"/tenant/docs/concepts/deep-dive\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldNotRenderNextStepsChrome_WhenFeaturedGroupsAreEmpty()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode(
            "Concept Landing",
            "concepts/landing.md",
            "<p>Landing body</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "Concepts",
                SectionLanding = true
            });
        var model = CreateDetailsViewModel(doc) with
        {
            IsSectionLanding = true,
            FeaturedPageGroups =
            [
                new DocLandingFeaturedPageGroupViewModel
                {
                    Label = "Empty",
                    Pages = []
                }
            ]
        };

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);

        Assert.DoesNotContain("Next steps", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderSectionLandingGroupTitles_WhenGroupsHaveTitles()
    {
        var landingDoc = new DocNode(
            "Namespaces",
            "Namespaces",
            "<p>Namespace root</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "API Reference",
                PageType = "api-reference",
                SectionLanding = true
            });
        var fooBar = new DocNode(
            "Foo.Bar",
            "Namespaces/Foo.Bar",
            "<p>Foo.Bar namespace</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "API Reference",
                PageType = "api-reference"
            });
        var fooBaz = new DocNode(
            "Foo.Baz",
            "Namespaces/Foo.Baz",
            "<p>Foo.Baz namespace</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "API Reference",
                PageType = "api-reference"
            });

        var html = await RenderDetailsViewAsync(landingDoc, fooBar, fooBaz);

        Assert.Contains(">Bar<", html);
        Assert.Contains(">Baz<", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderPathBaseAwareStructuredAndBodyLinks()
    {
        var doc = new DocNode(
            "Linked Guide",
            "guides/linked-guide.md",
            "<p><a href=\"/docs/guides/intro\">Intro guide</a></p>",
            Metadata: new DocMetadata
            {
                NavGroup = "How-to Guides"
            });

        var html = await RenderDetailsViewWithPathBaseAsync(doc, "/some-base");

        Assert.Contains("href=\"/some-base/docs/sections/how-to-guides\"", html);
        Assert.Contains("href=\"/some-base/docs/guides/intro\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderBlankAndRelativeStructuredLinks_WithoutPathBaseRewriting()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode(
            "Linked Guide",
            "guides/linked-guide.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "How-to Guides"
            });
        var model = CreateDetailsViewModel(
            doc,
            previousPage: new DocPageLinkViewModel
            {
                Title = "Relative previous",
                Href = "guides/relative"
            },
            relatedPages:
            [
                new DocPageLinkViewModel
                {
                    Title = "Blank related",
                    Href = " "
                }
            ]);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model,
            pathBase: "/some-base");

        Assert.Contains("href=\"guides/relative\"", html);
        Assert.Contains("href=\"\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldHideTopH1ForCSharpDocs()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderDocsViewAsync(
            services,
            "Details",
            c => c.Details("src/Example.cs.html"));

        Assert.DoesNotContain("text-3xl font-bold text-white tracking-tight", html);
        Assert.Contains("Example body", html);
    }

    [Fact]
    public async Task DetailsView_ShouldSuppressLeadingMarkdownH1_WhenShellOwnsPageH1()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<h1 id=\"quickstart\">Quickstart</h1>\n<p>Start here.</p>");

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var headings = document.QuerySelectorAll("h1").ToArray();

        var heading = Assert.Single(headings);
        Assert.Equal("Quickstart", heading.TextContent.Trim());
        Assert.Null(heading.Id);
        Assert.Contains("Start here.", html);
        Assert.DoesNotContain("id=\"quickstart\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldSuppressLeadingMarkdownH1AfterComment_WhenShellOwnsMetadataTitle()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<!-- docs:snippet start -->\n<h1 id=\"quickstart\">Quickstart</h1>\n<p>Start here.</p>",
            Metadata: new DocMetadata { Title = "Metadata Quickstart" });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var heading = Assert.Single(document.QuerySelectorAll("h1"));

        Assert.Equal("Metadata Quickstart", heading.TextContent.Trim());
        Assert.Null(document.QuerySelector(".docs-content h1"));
        Assert.Contains("Start here.", html);
        Assert.DoesNotContain("id=\"quickstart\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldKeepLeadingDocumentH1_WhenShellDoesNotOwnPageH1()
    {
        var doc = new DocNode(
            "Example",
            "src/Example.cs",
            "<h1 id=\"example-api\">Example API</h1>\n<p>Example body</p>",
            Metadata: new DocMetadata { NavGroup = "API Reference" });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var heading = Assert.Single(document.QuerySelectorAll("h1"));

        Assert.Equal("example-api", heading.Id);
        Assert.Equal("Example API", heading.TextContent.Trim());
        Assert.Contains("Example body", html);
    }

    [Fact]
    public async Task DetailsView_ShouldMarkMarkdownAndApiContent_ForSurfaceSpecificProseStyling()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var markdownHtml = await RenderDocsViewAsync(
            services,
            "Details",
            c => c.Details("guides/intro"));
        var apiHtml = await RenderDocsViewAsync(
            services,
            "Details",
            c => c.Details("src/Example.cs.html"));

        Assert.Contains("class=\"docs-content docs-content--markdown\"", markdownHtml);
        Assert.Contains("class=\"docs-content docs-content--api\"", apiHtml);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("api-reference")]
    public async Task DetailsView_ShouldUseApiContentSurface_ForApiPageTypeMarkdown(string pageType)
    {
        var doc = new DocNode(
            "API Guide",
            "guides/api-reference.md",
            "<p>Reference body</p>",
            Metadata: new DocMetadata
            {
                PageType = pageType
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains("class=\"docs-content docs-content--api\"", html);
        Assert.DoesNotContain("class=\"docs-content docs-content--markdown\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldUseApiContentSurface_ForGeneratedExtensionlessDocs()
    {
        var doc = new DocNode(
            "Namespaces",
            "Namespaces",
            "<p>Namespace body</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "API Reference"
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains("class=\"docs-content docs-content--api\"", html);
        Assert.DoesNotContain("class=\"docs-content docs-content--markdown\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldHandleNamespacesRootPath()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderDocsViewAsync(
            services,
            "Details",
            c => c.Details("Namespaces.html"));

        Assert.Contains("aria-label=\"Breadcrumb\"", html);
        Assert.Contains(">Namespaces</span>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldFallbackToModelTitle_WhenMetadataTitleIsWhitespace()
    {
        var doc = new DocNode(
            "Fallback Title",
            "guides/whitespace.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Title = "   "
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains(">Fallback Title</h1>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldFallbackToModelTitle_WhenMetadataTitleIsNull()
    {
        var doc = new DocNode(
            "Fallback Title",
            "guides/null-title.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Title = null
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains(">Fallback Title</h1>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderTrimmedMetadataTitle_WhenMetadataTitleIsPresent()
    {
        var doc = new DocNode(
            "Fallback Title",
            "guides/trimmed-title.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Title = "  Authored Title  "
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains(">Authored Title</h1>", html);
        Assert.DoesNotContain(">Fallback Title</h1>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderNonCSharpTitleWithMobileWrappingClasses()
    {
        var doc = new DocNode(
            "ForgeTrust.AppSurface.Docs",
            "Web/ForgeTrust.AppSurface.Docs/README.md",
            "<p>Guide body</p>");

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var title = document.QuerySelector("h1");

        Assert.NotNull(title);
        Assert.Equal("ForgeTrust.AppSurface.Docs", title!.TextContent.Trim());
        Assert.Contains("max-w-full", title.ClassList);
        Assert.Contains("break-words", title.ClassList);
        Assert.Contains("leading-tight", title.ClassList);
    }

    [Fact]
    public async Task DetailsView_ShouldFallbackToPathBreadcrumbLabels_WhenMetadataTargetsCannotBeVerified()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Breadcrumbs = ["Start Here", "Quickstart"]
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var breadcrumbTexts = document.QuerySelectorAll("nav[aria-label='Breadcrumb'] a, nav[aria-label='Breadcrumb'] span")
            .Select(node => node.TextContent.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text) && text != "/")
            .ToArray();

        Assert.Contains("guides", breadcrumbTexts);
        Assert.Contains(">Quickstart</h1>", html);
        Assert.Contains("quickstart.md", breadcrumbTexts);
        Assert.DoesNotContain("Start Here", html);
    }

    [Fact]
    public async Task DetailsView_ShouldFallbackToPathBreadcrumbLabels_WhenMetadataBreadcrumbCountDoesNotMatchPath()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Breadcrumbs = ["Start Here", "Quickstart", "Extra"],
                BreadcrumbsMatchPathTargets = true
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var breadcrumbTexts = document.QuerySelectorAll("nav[aria-label='Breadcrumb'] a, nav[aria-label='Breadcrumb'] span")
            .Select(node => node.TextContent.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text) && text != "/")
            .ToArray();

        Assert.Contains("guides", breadcrumbTexts);
        Assert.Contains("quickstart.md", breadcrumbTexts);
        Assert.DoesNotContain(">Start Here</a>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldFallbackToPathBreadcrumbLabels_WhenMetadataBreadcrumbsCollapseToEmpty()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Breadcrumbs = ["   ", "\t"],
                BreadcrumbsMatchPathTargets = true
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var breadcrumbTexts = document.QuerySelectorAll("nav[aria-label='Breadcrumb'] a, nav[aria-label='Breadcrumb'] span")
            .Select(node => node.TextContent.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text) && text != "/")
            .ToArray();

        Assert.Contains("guides", breadcrumbTexts);
        Assert.Contains("quickstart.md", breadcrumbTexts);
        Assert.DoesNotContain(">Start Here</a>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldUseMetadataBreadcrumbLabels_WhenTargetsAreKnownToMatch()
    {
        var doc = new DocNode(
            "Web",
            "Namespaces/ForgeTrust.AppSurface.Web",
            "<p>Namespace body</p>",
            Metadata: new DocMetadata
            {
                Breadcrumbs = ["API Reference", "ForgeTrust", "AppSurface", "Web"],
                BreadcrumbsMatchPathTargets = true
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var breadcrumbTexts = document.QuerySelectorAll("nav[aria-label='Breadcrumb'] a, nav[aria-label='Breadcrumb'] span")
            .Select(node => node.TextContent.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text) && text != "/")
            .ToArray();

        Assert.Equal(new[] { "API Reference", "ForgeTrust", "AppSurface", "Web" }, breadcrumbTexts);
        Assert.Contains("href=\"/docs/Namespaces.html\"", html);
        Assert.Contains("href=\"/docs/Namespaces/ForgeTrust.html\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldUseMetadataBreadcrumbLabels_ForEditorialDocs_WhenTargetsAreKnownToMatch()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: DocMetadataFactory.CreateMarkdownMetadata(
                "guides/quickstart.md",
                "Quickstart",
                new DocMetadata
                {
                    NavGroup = "How-to Guides",
                    Breadcrumbs = ["Get Started", "Quickstart"]
                },
                derivedSummary: null));

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var breadcrumbTexts = document.QuerySelectorAll("nav[aria-label='Breadcrumb'] a, nav[aria-label='Breadcrumb'] span")
            .Select(node => node.TextContent.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text) && text != "/")
            .ToArray();

        Assert.Equal(new[] { "Get Started", "Quickstart" }, breadcrumbTexts);
    }

    [Fact]
    public async Task DetailsView_ShouldCollapseNestedReadmePathBreadcrumbs()
    {
        var doc = new DocNode(
            "Releases",
            "releases/README.md",
            "<p>Guide body</p>");

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains("aria-label=\"Breadcrumb\"", html);
        Assert.DoesNotContain(">README.md</span>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldUseMetadataBreadcrumbLabels_ForNestedReadmeLandings()
    {
        var doc = new DocNode(
            "Releases",
            "releases/README.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Breadcrumbs = ["Releases"],
                BreadcrumbsMatchPathTargets = true
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains(">Releases</span>", html);
        Assert.DoesNotContain(">releases</span>", html);
        Assert.DoesNotContain(">README.md</span>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldUseMetadataBreadcrumbLabels_WhenRootDocHasNavGroupParent()
    {
        var doc = new DocNode(
            "Changelog",
            "CHANGELOG.md",
            "<p>Release ledger</p>",
            Metadata: new DocMetadata
            {
                NavGroup = "Releases",
                Breadcrumbs = ["Releases", "Changelog"],
                BreadcrumbsMatchPathTargets = true
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains(">Releases</span>", html);
        Assert.Contains(">Changelog</span>", html);
        Assert.DoesNotContain(">CHANGELOG.md</span>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderSingleSegmentBreadcrumbWithoutParentLinks()
    {
        var doc = new DocNode(
            "Quickstart",
            "quickstart.md",
            "<p>Guide body</p>");

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains(">quickstart.md</span>", html);
        Assert.DoesNotContain("href=\"/docs/quickstart\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldNotRenderDerivedSummaryBlurb()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>This is the first paragraph.</p>",
            Metadata: new DocMetadata
            {
                Summary = "This is the first paragraph.",
                SummaryIsDerived = true
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.DoesNotContain("<p class=\"mt-3 max-w-3xl text-base text-slate-400\">This is the first paragraph.</p>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderExplicitSummaryBlurb()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Summary = "This is the summary paragraph.",
                SummaryIsDerived = false
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains("<p class=\"mt-3 max-w-3xl text-base text-slate-400\">This is the summary paragraph.</p>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderSummaryBlurb_WhenDerivedFlagIsUnset()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Summary = "This is the summary paragraph."
            });

        var html = await RenderDetailsViewAsync(doc);

        Assert.Contains("<p class=\"mt-3 max-w-3xl text-base text-slate-400\">This is the summary paragraph.</p>", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderPageTypeBadge_AndMetadataContextChips()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                PageType = "api-reference",
                Component = "RazorDocs",
                Audience = "Evaluators"
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        Assert.Equal("API Reference", document.QuerySelector(".docs-page-meta .docs-page-badge")?.TextContent.Trim());
        Assert.Contains(
            "docs-page-badge--api-reference",
            document.QuerySelector(".docs-page-meta .docs-page-badge")?.ClassName ?? string.Empty);
        Assert.Contains("Component: RazorDocs", html);
        Assert.Contains("Audience: Evaluators", html);
    }

    [Fact]
    public async Task DetailsView_ShouldSuppressDerivedAudienceAndComponentChips()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                PageType = "guide",
                Component = "AppSurface",
                ComponentIsDerived = true,
                Audience = "implementer",
                AudienceIsDerived = true
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        Assert.Equal("Guide", document.QuerySelector(".docs-page-meta .docs-page-badge")?.TextContent.Trim());
        Assert.DoesNotContain("Component: AppSurface", html);
        Assert.DoesNotContain("Audience: implementer", html);
        Assert.Null(document.QuerySelector(".docs-page-meta .docs-metadata-chip"));
    }

    [Fact]
    public async Task DetailsView_ShouldNotRenderMetaContainer_WhenBadgeAndChipsAreUnavailable()
    {
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                PageType = "   ",
                Component = "AppSurface",
                ComponentIsDerived = true,
                Audience = "implementer",
                AudienceIsDerived = true
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        Assert.Null(document.QuerySelector(".docs-page-meta"));
    }

    [Fact]
    public async Task DetailsView_ShouldRenderOutlineSection_WhenOutlineEntriesExist()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<h2 id='install'>Install</h2><h3 id='verify'>Verify</h3>");
        var model = CreateDetailsViewModel(
            doc,
            outline:
            [
                new DocOutlineItem
                {
                    Title = " Install ",
                    Id = " install ",
                    Level = 2
                },
                new DocOutlineItem
                {
                    Title = "Verify",
                    Id = "verify",
                    Level = 3
                },
                null!,
                new DocOutlineItem
                {
                    Title = "Missing fragment",
                    Id = " ",
                    Level = 2
                },
                new DocOutlineItem
                {
                    Title = " ",
                    Id = "missing-title",
                    Level = 2
                }
            ]);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        Assert.Contains("id=\"docs-page-outline\"", html);
        Assert.Contains("href=\"#install\"", html);
        Assert.Contains("data-doc-outline-link=\"true\"", html);
        Assert.Contains("href=\"#verify\"", html);
        Assert.Contains("docs-detail-layout--with-outline", html);
        Assert.NotNull(document.QuerySelector("#docs-page-outline.docs-outline-shell"));
        Assert.NotNull(document.QuerySelector(".docs-outline-toggle[aria-controls='docs-page-outline-panel']"));
        Assert.Equal("On this page: Quickstart", document.QuerySelector(".docs-outline-toggle")?.GetAttribute("aria-label"));
        Assert.Equal("Quickstart", document.QuerySelector(".docs-outline-toggle-label")?.TextContent.Trim());
        Assert.NotNull(document.QuerySelector(".docs-outline-toggle-context[data-doc-outline-context][aria-hidden='true']"));
        Assert.NotNull(document.QuerySelector(".docs-outline-context-row--previous[data-doc-outline-previous][hidden]"));
        Assert.NotNull(document.QuerySelector(".docs-outline-context-row--current[data-doc-outline-current]"));
        Assert.NotNull(document.QuerySelector(".docs-outline-context-row--next[data-doc-outline-next][hidden]"));
        Assert.Equal("Prev", document.QuerySelector("[data-doc-outline-previous] .docs-outline-context-kicker")?.TextContent.Trim());
        Assert.Equal("Next", document.QuerySelector("[data-doc-outline-next] .docs-outline-context-kicker")?.TextContent.Trim());
        Assert.NotNull(document.QuerySelector("#docs-page-outline-panel[aria-label='On this page']"));
        Assert.NotNull(document.QuerySelector("a.docs-outline-link[href='#install']"));
        Assert.NotNull(document.QuerySelector("a.docs-outline-link--level-3[href='#verify']"));
        Assert.NotNull(document.QuerySelector(".docs-section-copy-status[data-doc-section-copy-status][aria-live='polite']"));
        Assert.NotNull(document.QuerySelector(".docs-outline-item > button.docs-outline-copy[data-doc-section-copy='install'][data-doc-section-copy-title='Install'][aria-label='Copy link to Install']"));
        Assert.NotNull(document.QuerySelector(".docs-outline-item > button.docs-outline-copy[data-doc-section-copy='verify'][data-doc-section-copy-title='Verify'][aria-label='Copy link to Verify']"));
        Assert.Equal(2, document.QuerySelectorAll("#docs-page-outline .docs-section-copy-icon[aria-hidden='true'][viewBox='0 0 24 24']").Length);
        Assert.DoesNotContain(
            "#",
            document.QuerySelector(".docs-outline-item > button.docs-outline-copy[data-doc-section-copy='install']")!.TextContent);
        Assert.Equal(2, document.QuerySelectorAll("#docs-page-outline button[data-doc-section-copy]").Length);
        Assert.Null(document.QuerySelector("a.docs-outline-link[href='#missing-title']"));
        Assert.Null(document.QuerySelector("button[data-doc-section-copy='missing-title']"));
        Assert.DoesNotContain("Missing fragment", html);
        Assert.Single(document.QuerySelectorAll("#docs-page-outline nav"));
        Assert.True(
            document.QuerySelector(".docs-detail-primary")!.CompareDocumentPosition(document.QuerySelector("#docs-page-outline")!)
                .HasFlag(DocumentPositions.Following));
        Assert.NotNull(document.QuerySelector("script[src='/docs/outline-client.js?v=rolling-context'][data-doc-outline-client='true']"));
        Assert.DoesNotContain("data-doc-outline-client-loader=\"true\"", html);
        Assert.DoesNotContain("rounded-2xl border border-slate-800 bg-slate-900/60", html);

        var tenantHtml = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model,
            pathBase: "/tenant");
        var tenantDocument = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(tenantHtml);

        Assert.NotNull(tenantDocument.QuerySelector("script[src='/tenant/docs/outline-client.js?v=rolling-context'][data-doc-outline-client='true']"));
        Assert.DoesNotContain("data-doc-outline-client-loader=\"true\"", tenantHtml);
    }

    [Fact]
    public async Task DetailsView_ShouldHideOutlineRail_WhenOnlyMalformedOutlineEntriesExist()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<h2 id='install'>Install</h2>");
        var model = CreateDetailsViewModel(
            doc,
            outline:
            [
                null!,
                new DocOutlineItem
                {
                    Title = "Missing fragment",
                    Id = " ",
                    Level = 2
                },
                new DocOutlineItem
                {
                    Title = " ",
                    Id = "missing-title",
                    Level = 2
                }
            ]);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        Assert.Null(document.QuerySelector("#docs-page-outline"));
        Assert.Null(document.QuerySelector("script[data-doc-outline-client='true']"));
        Assert.DoesNotContain("docs-detail-layout--with-outline", html);
        Assert.NotNull(document.QuerySelector(".docs-detail-primary"));
        Assert.Contains("<h2 id='install'>Install</h2>", html);
    }

    [Fact]
    public async Task DetailsFrame_ShouldUseWideContainer_ForOutlineRail()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<h2 id='install'>Install</h2>");
        var model = CreateDetailsViewModel(
            doc,
            outline:
            [
                new DocOutlineItem
                {
                    Title = "Install",
                    Id = "install",
                    Level = 2
                }
            ]);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/DetailsFrame.cshtml",
            model);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var frameShell = document.QuerySelector("div.max-w-6xl");
        Assert.NotNull(frameShell);
        Assert.Contains("max-w-6xl", frameShell!.ClassList);
        Assert.DoesNotContain("max-w-4xl", frameShell.ClassList);
        Assert.NotNull(document.QuerySelector(".docs-detail-layout--with-outline #docs-page-outline"));
    }

    [Fact]
    public async Task DetailsView_ShouldRenderWayfindingSections_WhenLinksExist()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide body</p>");
        var model = CreateDetailsViewModel(
            doc,
            previousPage: new DocPageLinkViewModel
            {
                Title = "Intro",
                Href = "/docs/guides/intro",
                Summary = "Start here."
            },
            nextPage: new DocPageLinkViewModel
            {
                Title = "Troubleshooting",
                Href = "/docs/guides/troubleshooting",
                Summary = "Recover quickly."
            },
            relatedPages:
            [
                new DocPageLinkViewModel
                {
                    Title = "Reference",
                    Href = "/docs/guides/reference"
                }
            ]);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);

        Assert.Contains("id=\"docs-page-wayfinding\"", html);
        Assert.Contains("data-doc-wayfinding=\"previous\"", html);
        Assert.Contains("data-doc-wayfinding=\"next\"", html);
        Assert.Contains("data-doc-related-link=\"true\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderOptionalWayfindingBadgesAndSummaries()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide body</p>");
        var model = CreateDetailsViewModel(
            doc,
            previousPage: new DocPageLinkViewModel
            {
                Title = "Intro",
                Href = "/docs/guides/intro",
                PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge("guide")
            },
            nextPage: new DocPageLinkViewModel
            {
                Title = "Troubleshooting",
                Href = "/docs/guides/troubleshooting",
                PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge("troubleshooting")
            },
            relatedPages:
            [
                new DocPageLinkViewModel
                {
                    Title = "Reference",
                    Href = "/docs/guides/reference",
                    Summary = "Read the reference.",
                    PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge("api-reference")
                }
            ]);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);

        Assert.Contains("docs-page-badge--guide", html);
        Assert.Contains("docs-page-badge--troubleshooting", html);
        Assert.Contains("docs-page-badge--api-reference", html);
        Assert.Contains("Read the reference.", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderRelatedOnlyWayfinding_WithoutSequenceCards()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide body</p>");
        var model = CreateDetailsViewModel(
            doc,
            relatedPages:
            [
                new DocPageLinkViewModel
                {
                    Title = "Reference",
                    Href = "/docs/guides/reference",
                    Summary = "Read the reference.",
                    PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge("api-reference")
                }
            ]);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);

        Assert.Contains("id=\"docs-page-wayfinding\"", html);
        Assert.Contains("data-doc-related-link=\"true\"", html);
        Assert.DoesNotContain("data-doc-wayfinding=\"previous\"", html);
        Assert.DoesNotContain("data-doc-wayfinding=\"next\"", html);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderTrustBar_WhenTrustMetadataIsPresent()
    {
        var doc = new DocNode(
            "Unreleased",
            "releases/unreleased.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Trust = new DocTrustMetadata
                {
                    Status = "Unreleased",
                    Summary = "This page is provisional until the tag is cut.",
                    Freshness = "Updated on main.",
                    ChangeScope = "Repository-wide.",
                    Archive = "Tagged release notes keep the durable record.",
                    Sources = ["CHANGELOG.md", "releases/unreleased.md"],
                    Migration = new DocTrustLink
                    {
                        Label = "Read the upgrade policy",
                        Href = "/docs/releases/upgrade-policy"
                    }
                }
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var trustBar = document.QuerySelector(".docs-trust-bar");
        Assert.NotNull(trustBar);
        Assert.Equal("Unreleased", trustBar!.QuerySelector(".docs-trust-bar-status-badge")?.TextContent.Trim());
        Assert.Contains("Repository-wide.", trustBar.TextContent);
        Assert.Contains("CHANGELOG.md", trustBar.TextContent);

        var migrationLink = trustBar.QuerySelector("a.docs-trust-bar-link[href='/docs/releases/upgrade-policy']");
        Assert.NotNull(migrationLink);
        Assert.Equal("doc-content", migrationLink!.GetAttribute("data-turbo-frame"));
        Assert.Equal("advance", migrationLink.GetAttribute("data-turbo-action"));
    }

    [Fact]
    public async Task DetailsView_ShouldRenderContributorProvenanceStrip_AboveTrustBar()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode(
            "Unreleased",
            "releases/unreleased.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Trust = new DocTrustMetadata
                {
                    Status = "Unreleased",
                    Summary = "This page is provisional until the tag is cut."
                }
            });
        var model = CreateDetailsViewModel(
            doc,
            contributorProvenance: new DocContributorProvenanceViewModel
            {
                SourceHref = "https://example.com/blob/main/releases/unreleased.md",
                EditHref = "https://example.com/edit/main/releases/unreleased.md",
                LastUpdatedUtc = new DateTimeOffset(2026, 4, 22, 23, 19, 0, TimeSpan.Zero)
            });

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var provenanceStrip = document.QuerySelector(".docs-provenance-strip");
        var trustBar = document.QuerySelector(".docs-trust-bar");

        Assert.NotNull(provenanceStrip);
        Assert.NotNull(trustBar);
        Assert.Equal("Source of truth", provenanceStrip!.QuerySelector(".docs-provenance-label")?.TextContent.Trim());
        Assert.NotNull(provenanceStrip.QuerySelector("a.docs-provenance-link--primary[href='https://example.com/blob/main/releases/unreleased.md']"));
        Assert.NotNull(provenanceStrip.QuerySelector("a.docs-provenance-link--secondary[href='https://example.com/edit/main/releases/unreleased.md']"));

        var timestamp = provenanceStrip.QuerySelector("time.docs-provenance-time");
        Assert.NotNull(timestamp);
        Assert.Equal("relative", timestamp!.GetAttribute("data-rw-time-display"));
        Assert.Equal("2026-04-22T23:19:00.0000000+00:00", timestamp.GetAttribute("datetime"));

        Assert.True(provenanceStrip.CompareDocumentPosition(trustBar!).HasFlag(DocumentPositions.Following));
    }

    [Fact]
    public async Task DetailsView_ShouldRenderCustomContributorProvenanceLabel()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Web", "Namespaces/ForgeTrust.Web", "<p>Namespace body</p>");
        var model = CreateDetailsViewModel(
            doc,
            contributorProvenance: new DocContributorProvenanceViewModel
            {
                Label = "Namespace intro source",
                SourceHref = "https://example.com/blob/main/docs/ForgeTrust.Web/README.md"
            });

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var provenanceStrip = document.QuerySelector(".docs-provenance-strip");
        Assert.NotNull(provenanceStrip);
        Assert.Equal("Namespace intro source", provenanceStrip!.GetAttribute("aria-label"));
        Assert.Equal("Namespace intro source", provenanceStrip.QuerySelector(".docs-provenance-label")?.TextContent.Trim());
    }

    [Fact]
    public async Task DetailsView_ShouldRenderPartialContributorProvenance_WhenOnlyOneEvidenceItemExists()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide body</p>");
        var model = CreateDetailsViewModel(
            doc,
            contributorProvenance: new DocContributorProvenanceViewModel
            {
                EditHref = "https://example.com/edit/main/guides/quickstart.md"
            });

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var provenanceStrip = document.QuerySelector(".docs-provenance-strip");
        Assert.NotNull(provenanceStrip);
        Assert.NotNull(provenanceStrip!.QuerySelector("a.docs-provenance-link--secondary[href='https://example.com/edit/main/guides/quickstart.md']"));
        Assert.Null(provenanceStrip.QuerySelector(".docs-provenance-link--primary"));
        Assert.Null(provenanceStrip.QuerySelector("time.docs-provenance-time"));
    }

    [Fact]
    public async Task DetailsView_ShouldRenderPartialContributorProvenance_WhenOnlySourceEvidenceExists()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide body</p>");
        var model = CreateDetailsViewModel(
            doc,
            contributorProvenance: new DocContributorProvenanceViewModel
            {
                SourceHref = "https://example.com/blob/main/guides/quickstart.md"
            });

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var provenanceStrip = document.QuerySelector(".docs-provenance-strip");
        Assert.NotNull(provenanceStrip);
        Assert.NotNull(provenanceStrip!.QuerySelector("a.docs-provenance-link--primary[href='https://example.com/blob/main/guides/quickstart.md']"));
        Assert.Null(provenanceStrip.QuerySelector(".docs-provenance-link--secondary"));
        Assert.Null(provenanceStrip.QuerySelector("time.docs-provenance-time"));
    }

    [Fact]
    public async Task DetailsView_ShouldUseTurboNavigation_ForLocalContributorProvenanceLinks()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide body</p>");
        var model = CreateDetailsViewModel(
            doc,
            contributorProvenance: new DocContributorProvenanceViewModel
            {
                SourceHref = "/docs/guides/quickstart",
                EditHref = "/docs/guides/quickstart.edit"
            },
            contributorSourceUsesTurbo: true,
            contributorEditUsesTurbo: true);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var sourceLink = document.QuerySelector("a.docs-provenance-link--primary[href='/docs/guides/quickstart']");
        var editLink = document.QuerySelector("a.docs-provenance-link--secondary[href='/docs/guides/quickstart.edit']");

        Assert.NotNull(sourceLink);
        Assert.NotNull(editLink);
        Assert.Equal("doc-content", sourceLink!.GetAttribute("data-turbo-frame"));
        Assert.Equal("advance", sourceLink.GetAttribute("data-turbo-action"));
        Assert.Equal("doc-content", editLink!.GetAttribute("data-turbo-frame"));
        Assert.Equal("advance", editLink.GetAttribute("data-turbo-action"));
    }

    [Fact]
    public async Task DetailsView_ShouldPreservePathBase_ForLocalProvenanceAndMigrationLinks()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode(
            "Quickstart",
            "guides/quickstart.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Trust = new DocTrustMetadata
                {
                    Migration = new DocTrustLink
                    {
                        Href = "/docs/releases/unreleased",
                        Label = "Migration notes"
                    }
                }
            });
        var model = CreateDetailsViewModel(
            doc,
            contributorProvenance: new DocContributorProvenanceViewModel
            {
                SourceHref = "/docs/guides/quickstart",
                EditHref = "/docs/guides/quickstart.edit"
            },
            contributorSourceUsesTurbo: true,
            contributorEditUsesTurbo: true) with
        {
            TrustMigrationUsesTurbo = true
        };

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model,
            configureHttpContext: httpContext => httpContext.Request.PathBase = "/tenant");
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var sourceLink = document.QuerySelector("a.docs-provenance-link--primary[href='/tenant/docs/guides/quickstart']");
        var editLink = document.QuerySelector("a.docs-provenance-link--secondary[href='/tenant/docs/guides/quickstart.edit']");

        Assert.NotNull(sourceLink);
        Assert.NotNull(editLink);
        Assert.Equal("doc-content", sourceLink!.GetAttribute("data-turbo-frame"));
        Assert.Equal("advance", sourceLink.GetAttribute("data-turbo-action"));
        Assert.Equal("doc-content", editLink!.GetAttribute("data-turbo-frame"));
        Assert.Equal("advance", editLink.GetAttribute("data-turbo-action"));
        Assert.NotNull(document.QuerySelector("a.docs-trust-bar-link[href='/tenant/docs/releases/unreleased']"));
    }

    [Fact]
    public async Task DetailsView_ShouldPreservePathBase_ForGeneratedLocalSymbolSourceLinks()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode(
            "Calculator",
            "Namespaces/Test",
            """
            <p>
                <a aria-label="View source" class="chip doc-symbol-source-link" href="/repo/blob/src/Calculator.cs#L12">Source</a>
            </p>
            """);
        var model = CreateDetailsViewModel(doc);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model,
            configureHttpContext: httpContext => httpContext.Request.PathBase = "/tenant");
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var sourceLink = document.QuerySelector("a.doc-symbol-source-link");
        Assert.NotNull(sourceLink);
        Assert.Equal("/tenant/repo/blob/src/Calculator.cs#L12", sourceLink!.GetAttribute("href"));
        Assert.Equal("View source", sourceLink.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task DetailsView_ShouldLeaveProtocolRelativeSymbolSourceLinksUnchanged_WhenPathBaseExists()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode(
            "Calculator",
            "Namespaces/Test",
            """
            <p>
                <a aria-label="View source" class="chip doc-symbol-source-link" href="//example.com/repo/blob/src/Calculator.cs#L12">Source</a>
            </p>
            """);
        var model = CreateDetailsViewModel(doc);

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            model,
            configureHttpContext: httpContext => httpContext.Request.PathBase = "/tenant");
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var sourceLink = document.QuerySelector("a.doc-symbol-source-link");
        Assert.NotNull(sourceLink);
        Assert.Equal("//example.com/repo/blob/src/Calculator.cs#L12", sourceLink!.GetAttribute("href"));
        Assert.Equal("View source", sourceLink.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task DetailsView_ShouldNotRenderContributorProvenance_WhenNoEvidenceExists()
    {
        using var services = CreateServiceProvider(CreateDocs());
        var doc = new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide body</p>");

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            CreateDetailsViewModel(doc));
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        Assert.Null(document.QuerySelector(".docs-provenance-strip"));
    }

    [Fact]
    public async Task DetailsView_ShouldRenderExternalMigrationAndFilteredSources()
    {
        var doc = new DocNode(
            "Unreleased",
            "releases/unreleased.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Trust = new DocTrustMetadata
                {
                    Summary = "This page is still settling.",
                    Sources = ["  ", "CHANGELOG.md", "\t"],
                    Migration = new DocTrustLink
                    {
                        Label = "   ",
                        Href = "https://example.com/upgrade"
                    }
                }
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var trustBar = document.QuerySelector(".docs-trust-bar");
        Assert.NotNull(trustBar);
        Assert.Contains("This page is still settling.", trustBar!.TextContent);
        Assert.Null(trustBar.QuerySelector(".docs-trust-bar-status-badge"));

        var migrationLink = trustBar.QuerySelector("a.docs-trust-bar-link[href='https://example.com/upgrade']");
        Assert.NotNull(migrationLink);
        Assert.Equal("Migration guidance", migrationLink!.TextContent.Trim());
        Assert.Null(migrationLink.GetAttribute("data-turbo-frame"));
        Assert.Null(migrationLink.GetAttribute("data-turbo-action"));

        var trustSources = trustBar.QuerySelectorAll(".docs-trust-bar-list li")
            .Select(item => item.TextContent.Trim())
            .ToArray();
        Assert.Equal(["CHANGELOG.md"], trustSources);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderArchiveWithoutSourcesList_WhenSourcesAreMissing()
    {
        var doc = new DocNode(
            "Unreleased",
            "releases/unreleased.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Trust = new DocTrustMetadata
                {
                    Archive = "Tagged release notes keep the durable record."
                }
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var trustBar = document.QuerySelector(".docs-trust-bar");
        Assert.NotNull(trustBar);
        Assert.Contains("Tagged release notes keep the durable record.", trustBar!.TextContent);
        Assert.Null(trustBar.QuerySelector(".docs-trust-bar-list"));
    }

    [Fact]
    public async Task DetailsView_ShouldNotRenderTrustBar_WhenTrustMetadataHasNoDisplayableValues()
    {
        var doc = new DocNode(
            "Unreleased",
            "releases/unreleased.md",
            "<p>Guide body</p>",
            Metadata: new DocMetadata
            {
                Trust = new DocTrustMetadata
                {
                    Sources = Array.Empty<string>()
                }
            });

        var html = await RenderDetailsViewAsync(doc);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        Assert.Null(document.QuerySelector(".docs-trust-bar"));
    }

    [Fact]
    public async Task SearchView_ShouldRenderSearchPageShell()
    {
        var docs = CreateDocs();
        docs.Add(
            new(
                "Quick Example",
                "examples/quick-start",
                "<p>Example body</p>",
                Metadata: new DocMetadata
                {
                    PageType = "example"
                }));

        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(
            services,
            "Search",
            c => c.Search());

        Assert.Contains("id=\"docs-search-page-input\"", html);
        Assert.Contains("id=\"docs-search-page-status\"", html);
        Assert.Contains("id=\"docs-search-page-filters-toggle\"", html);
        Assert.Contains("id=\"docs-search-page-filters-panel\"", html);
        Assert.Contains("id=\"docs-search-page-starter\"", html);
        Assert.Contains("id=\"docs-search-page-starter-docs\"", html);
        Assert.Contains("data-rw-search-suggestion=\"getting started\"", html);
        Assert.Contains("data-rw-search-suggestion=\"API reference\"", html);
        Assert.Contains("data-rw-search-suggestion=\"release notes\"", html);
        Assert.Contains("data-rw-search-suggestion=\"troubleshooting\"", html);
        Assert.Contains("id=\"docs-search-page-failure\"", html);
        Assert.Contains("id=\"docs-search-page-failure-template\"", html);
        Assert.Contains("id=\"docs-search-page-retry\"", html);
        Assert.Contains("href=\"/docs/search-index.json\"", html);
        Assert.Contains("data-rw-search-runtime=\"minisearch\"", html);
        Assert.Contains("data-turbo-frame=\"doc-content\"", html);
        Assert.Contains("data-turbo-action=\"advance\"", html);
        Assert.Contains("id=\"docs-search-page-results\"", html);
        Assert.Contains("Search Documentation", html);
        Assert.Contains("id=\"docs-search-input\"", html);

        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        Assert.Equal(string.Empty, document.QuerySelector("#docs-search-page-failure")?.TextContent.Trim());
    }

    [Fact]
    public async Task SearchView_ShouldRenderTopLevelFailureFallbackLink_ForDocsIndexRecovery()
    {
        using var services = CreateServiceProvider([]);

        var html = await RenderDocsViewAsync(
            services,
            "Search",
            c => c.Search());

        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        Assert.Matches("<a[^>]*href=\"/docs\"[^>]*data-turbo-frame=\"_top\"", html);
        Assert.Equal(string.Empty, document.QuerySelector("#docs-search-page-failure")?.TextContent.Trim());
    }

    [Fact]
    public async Task SearchView_ShouldUseTopLevelNavigation_ForNonCurrentDocsFallbackLinks()
    {
        using var services = CreateServiceProvider(
            CreateDocs(),
            new Dictionary<string, string?>
            {
                ["RazorDocs:Routing:DocsRootPath"] = "/docs/next",
                ["RazorDocs:Versioning:Enabled"] = "true",
                ["RazorDocs:Versioning:CatalogPath"] = "catalog.json"
            });

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Search.cshtml",
            new SearchPageViewModel(
                Title: "Search Documentation",
                Orientation: "Search across the docs set.",
                StarterHint: "Try a starter query.",
                SearchPlaceholder: "Search docs",
                SuggestedQueries: ["getting started"],
                FailureFallbackLinks:
                [
                    new SearchPageFallbackLink("Open preview guide", "/docs/next/guides/quickstart", "Stay in preview.", UsesDocsFrame: true),
                    new SearchPageFallbackLink("Open exact release", "/docs/v/1.2.3/guides/quickstart", "Leave the live preview surface.")
                ]));

        Assert.Matches(
            "href=\"/docs/next/guides/quickstart\"[\\s\\S]*data-turbo-frame=\"doc-content\"",
            html);
        Assert.Matches(
            "href=\"/docs/v/1.2.3/guides/quickstart\"[\\s\\S]*data-turbo-frame=\"_top\"",
            html);
    }

    [Fact]
    public async Task SearchView_ShouldRenderPathBaseAwareFallbackLinks()
    {
        using var services = CreateServiceProvider(
            CreateDocs(),
            new Dictionary<string, string?>
            {
                ["RazorDocs:Routing:DocsRootPath"] = "/docs/next",
                ["RazorDocs:Versioning:Enabled"] = "true",
                ["RazorDocs:Versioning:CatalogPath"] = "catalog.json"
            });

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Search.cshtml",
            new SearchPageViewModel(
                Title: "Search Documentation",
                Orientation: "Search across the docs set.",
                StarterHint: "Try a starter query.",
                SearchPlaceholder: "Search docs",
                SuggestedQueries: ["getting started"],
                FailureFallbackLinks:
                [
                    new SearchPageFallbackLink("Open preview guide", "/docs/next/guides/quickstart", "Stay in preview.", UsesDocsFrame: true),
                    new SearchPageFallbackLink("Open exact release", "/docs/v/1.2.3/guides/quickstart", "Leave the live preview surface.")
                ]),
            pathBase: "/some-base");

        Assert.Matches(
            "href=\"/some-base/docs/next/guides/quickstart\"[\\s\\S]*data-turbo-frame=\"doc-content\"",
            html);
        Assert.Matches(
            "href=\"/some-base/docs/v/1.2.3/guides/quickstart\"[\\s\\S]*data-turbo-frame=\"_top\"",
            html);
    }

    [Fact]
    public async Task SearchView_ShouldLeaveRelativeFallbackLinksUnchanged_AndRenderBlankHrefAsEmpty()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Search.cshtml",
            new SearchPageViewModel(
                Title: "Search Documentation",
                Orientation: "Search across the docs set.",
                StarterHint: "Try a starter query.",
                SearchPlaceholder: "Search docs",
                SuggestedQueries: ["getting started"],
                FailureFallbackLinks:
                [
                    new SearchPageFallbackLink("Relative", "guides/relative", "Stay in the docs.", UsesDocsFrame: true),
                    new SearchPageFallbackLink("Blank", "   ", "No destination yet.")
                ]),
            pathBase: "/some-base");

        Assert.Contains("href=\"guides/relative\"", html);
        Assert.Contains("href=\"\"", html);
    }

    [Fact]
    public async Task VersionsView_ShouldUseTopLevelNavigation_ForCrossSurfaceLinks()
    {
        using var services = CreateServiceProvider(
            CreateDocs(),
            new Dictionary<string, string?>
            {
                ["RazorDocs:Routing:DocsRootPath"] = "/docs/next",
                ["RazorDocs:Versioning:Enabled"] = "true",
                ["RazorDocs:Versioning:CatalogPath"] = "catalog.json"
            });

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Versions.cshtml",
            new RazorDocsVersionArchiveViewModel
            {
                Heading = "Documentation versions",
                Description = "Choose the exact release you want to read, or keep using the preview surface.",
                PreviewHref = "/docs/next",
                VersionsHref = "/docs/versions",
                Versions =
                [
                    new RazorDocsVersionArchiveEntryViewModel
                    {
                        Version = "1.2.3",
                        Label = "1.2.3",
                        Href = "/docs/v/1.2.3",
                        IsAvailable = true,
                        SupportStateLabel = "Current"
                    }
                ]
            });

        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var previewLink = document.QuerySelectorAll("a[href='/docs/next']")
            .Single(link => link.TextContent.Contains("Open preview docs", StringComparison.Ordinal));
        var archiveLink = document.QuerySelectorAll("a[href='/docs/versions']")
            .Single(link => link.TextContent.Contains("Refresh archive", StringComparison.Ordinal));
        var exactVersionLink = document.QuerySelectorAll("a[href='/docs/v/1.2.3']")
            .Single(link => link.TextContent.Contains("Open 1.2.3", StringComparison.Ordinal));

        Assert.Equal("_top", previewLink.GetAttribute("data-turbo-frame"));
        Assert.Equal("_top", archiveLink.GetAttribute("data-turbo-frame"));
        Assert.Equal("_top", exactVersionLink.GetAttribute("data-turbo-frame"));
    }

    [Fact]
    public async Task VersionsView_ShouldRenderPathBaseAwareArchiveLinks()
    {
        using var services = CreateServiceProvider(
            CreateDocs(),
            new Dictionary<string, string?>
            {
                ["RazorDocs:Routing:DocsRootPath"] = "/docs/next",
                ["RazorDocs:Versioning:Enabled"] = "true",
                ["RazorDocs:Versioning:CatalogPath"] = "catalog.json"
            });

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Versions.cshtml",
            new RazorDocsVersionArchiveViewModel
            {
                Heading = "Documentation versions",
                Description = "Choose the exact release you want to read, or keep using the preview surface.",
                PreviewHref = "/docs/next",
                VersionsHref = "/docs/versions",
                Versions =
                [
                    new RazorDocsVersionArchiveEntryViewModel
                    {
                        Version = "1.2.3",
                        Label = "1.2.3",
                        Href = "/docs/v/1.2.3",
                        IsAvailable = true,
                        SupportStateLabel = "Current"
                    }
                ]
            },
            pathBase: "/some-base");

        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        Assert.NotNull(document.QuerySelector("a[href='/some-base/docs/next']"));
        Assert.NotNull(document.QuerySelector("a[href='/some-base/docs/versions']"));
        Assert.NotNull(document.QuerySelector("a[href='/some-base/docs/v/1.2.3']"));
    }

    [Fact]
    public async Task VersionsView_ShouldLeaveRelativeLinksUnchanged_AndRenderBlankPreviewHrefAsEmpty()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Versions.cshtml",
            new RazorDocsVersionArchiveViewModel
            {
                Heading = "Documentation versions",
                Description = "Choose the release you want.",
                PreviewHref = " ",
                VersionsHref = "versions-relative",
                Versions =
                [
                    new RazorDocsVersionArchiveEntryViewModel
                    {
                        Version = "1.2.3",
                        Label = "1.2.3",
                        Href = "release-relative",
                        IsAvailable = true,
                        SupportStateLabel = "Current"
                    }
                ]
            },
            pathBase: "/some-base");

        Assert.Contains("href=\"\"", html);
        Assert.Contains("href=\"versions-relative\"", html);
        Assert.Contains("href=\"release-relative\"", html);
    }

    [Fact]
    public async Task VersionsView_ShouldRenderEmptyArchiveState()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Versions.cshtml",
            new RazorDocsVersionArchiveViewModel
            {
                Heading = "Documentation versions",
                Description = "Choose the exact release you want to read.",
                PreviewHref = "/docs/next",
                VersionsHref = "/docs/versions",
                Versions = []
            });

        Assert.Contains("No published versions are currently listed", html);
    }

    [Fact]
    public async Task VersionsView_ShouldRenderAdvisoryBadgeAndSummaryCopy()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Versions.cshtml",
            new RazorDocsVersionArchiveViewModel
            {
                Heading = "Documentation versions",
                Description = "Choose the exact release you want to read.",
                PreviewHref = "/docs/next",
                VersionsHref = "/docs/versions",
                Versions =
                [
                    new RazorDocsVersionArchiveEntryViewModel
                    {
                        Version = "1.2.3",
                        Label = "1.2.3",
                        Summary = "Use this release if you need the supported API surface.",
                        Href = "/docs/v/1.2.3",
                        IsAvailable = true,
                        SupportStateLabel = "Current",
                        AdvisoryLabel = "Security risk"
                    }
                ]
            });

        Assert.Contains("Security risk", html);
        Assert.Contains("Use this release if you need the supported API surface.", html);
    }

    [Fact]
    public async Task VersionsView_ShouldRenderUnavailableVersionEntry()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Versions.cshtml",
            new RazorDocsVersionArchiveViewModel
            {
                Heading = "Documentation versions",
                Description = "Choose the exact release you want to read.",
                PreviewHref = "/docs/next",
                VersionsHref = "/docs/versions",
                Versions =
                [
                    new RazorDocsVersionArchiveEntryViewModel
                    {
                        Version = "1.2.3",
                        Label = "1.2.3",
                        Summary = "This release is temporarily unavailable.",
                        Href = "/docs/v/1.2.3",
                        IsAvailable = false,
                        SupportStateLabel = "Current",
                        AdvisoryLabel = "Security risk",
                        AvailabilityMessage = "Published release tree is missing search-index.json."
                    }
                ]
            });

        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        Assert.NotNull(document.QuerySelector("a[href='/docs/versions'][data-turbo-frame='_top']"));
        Assert.Null(document.QuerySelector("a[href='/docs/v/1.2.3']"));
        var unavailableMessage = document.QuerySelector("p.text-amber-200");
        Assert.NotNull(unavailableMessage);
        Assert.Contains("missing search-index.json", unavailableMessage!.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndexView_ShouldNotRenderSearchWorkspaceOrOutlineOnlyAssets()
    {
        using var services = CreateServiceProvider(CreateDocs());

        var html = await RenderDocsViewAsync(
            services,
            "Index",
            c => c.Index());

        Assert.DoesNotContain("href=\"/docs/search-index.json\"", html);
        Assert.DoesNotContain("data-rw-search-runtime=\"minisearch\"", html);
        Assert.Contains("src=\"/docs/search-client.js\"", html);
        Assert.DoesNotContain("src=\"/docs/outline-client.js\"", html);
        Assert.Contains("id=\"docs-search-input\"", html);
    }

    [Fact]
    public async Task IndexView_ShouldRenderNamespacesWithoutNamespaceRootLink_WhenRootIsMissing()
    {
        var docs = CreateDocs().Where(d => !string.Equals(d.Path, "Namespaces", StringComparison.OrdinalIgnoreCase)).ToList();
        using var services = CreateServiceProvider(docs);

        var html = await RenderDocsViewAsync(services, "Index", c => c.Index());

        Assert.DoesNotContain("href=\"/docs/Namespaces.html\"", html);
        Assert.Contains("ForgeTrust", html);
        Assert.Contains("AppSurface.Web", html);
    }

    [Fact]
    public async Task SidebarView_ShouldHonorMetadataOrder_ForNamespaceEntries()
    {
        var docs = new List<DocNode>
        {
            new("Namespaces", "Namespaces", "<p>root</p>"),
            new(
                "One",
                "Namespaces/Contoso.Product.Feature.One",
                "<p>one</p>",
                Metadata: new DocMetadata
                {
                    Order = 2
                }),
            new(
                "Two",
                "Namespaces/Contoso.Product.Feature.Two",
                "<p>two</p>",
                Metadata: new DocMetadata
                {
                    Order = 1
                })
        };
        using var services = CreateServiceProvider(docs);

        var model = CreateSidebarViewModel(
            ["Contoso.Product."],
            ("Namespaces", docs[0]),
            ("Namespaces", docs[1]),
            ("Namespaces", docs[2]));

        var html = await RenderViewAsync(
            services,
            "/Views/Shared/Components/Sidebar/Default.cshtml",
            model);

        var twoIndex = html.IndexOf("href=\"/docs/Namespaces/Contoso.Product.Feature.Two\"", StringComparison.Ordinal);
        var oneIndex = html.IndexOf("href=\"/docs/Namespaces/Contoso.Product.Feature.One\"", StringComparison.Ordinal);

        Assert.NotEqual(-1, twoIndex);
        Assert.NotEqual(-1, oneIndex);
        Assert.True(twoIndex < oneIndex);
    }

    [Fact]
    public async Task SidebarView_ShouldPreferCanonicalPaths_AndSupportMissingPrefixViewData()
    {
        var docs = new List<DocNode>
        {
            new("Namespaces", "Namespaces", "<p>root</p>", null, false, "Namespaces.html"),
            new("Web", "Namespaces/ForgeTrust.AppSurface.Web", "<p>web</p>", null, false, "Namespaces/ForgeTrust.AppSurface.Web.html"),
            new("AspireApp", "Namespaces/ForgeTrust.AppSurface.Web#AspireApp", string.Empty, "Namespaces/ForgeTrust.AppSurface.Web", false, "Namespaces/ForgeTrust.AppSurface.Web.html#AspireApp"),
            new("Guide", "docs/guide.md", "<p>guide</p>", null, false, "docs/guide"),
            new("Build", "docs/guide.md#Build", string.Empty, "docs/guide.md", false, "docs/guide#Build"),
            new("Run", "docs/guide.md#Run", string.Empty, "docs/guide.md", false, "docs/guide#Run")
        };
        using var services = CreateServiceProvider(docs);

        var model = CreateSidebarViewModel(
            ["ForgeTrust.AppSurface."],
            ("Namespaces", docs[0]),
            ("Namespaces", docs[1]),
            ("Namespaces", docs[2]),
            ("docs", docs[3]),
            ("docs", docs[4]),
            ("docs", docs[5]));

        var canonicalHtml = await RenderViewAsync(
            services,
            "/Views/Shared/Components/Sidebar/Default.cshtml",
            model);

        Assert.Contains("href=\"/docs/Namespaces.html\"", canonicalHtml);
        Assert.Contains("href=\"/docs/Namespaces/ForgeTrust.AppSurface.Web.html\"", canonicalHtml);
        Assert.DoesNotContain("href=\"/docs/Namespaces/ForgeTrust.AppSurface.Web.html#AspireApp\"", canonicalHtml);
        Assert.Contains("href=\"/docs/docs/guide\"", canonicalHtml);
        Assert.Contains("href=\"/docs/docs/guide#Build\"", canonicalHtml);
        Assert.Contains("href=\"/docs/docs/guide#Run\"", canonicalHtml);

        var nullPrefixHtml = await RenderViewAsync(
            services,
            "/Views/Shared/Components/Sidebar/Default.cshtml",
            CreateSidebarViewModel([], ("Namespaces", docs[0]), ("Namespaces", docs[1]), ("Namespaces", docs[2]), ("docs", docs[3]), ("docs", docs[4]), ("docs", docs[5])));
        Assert.Contains("href=\"/docs/Namespaces.html\"", nullPrefixHtml);
    }

    [Fact]
    public async Task SidebarView_ShouldFallbackToSourcePaths_WhenCanonicalPathsMissing()
    {
        var docs = new List<DocNode>
        {
            new("Namespaces", "Namespaces", "<p>root</p>"),
            new("Web", "Namespaces/ForgeTrust.AppSurface.Web", "<p>web</p>"),
            new("AspireApp", "Namespaces/ForgeTrust.AppSurface.Web#AspireApp", string.Empty, "Namespaces/ForgeTrust.AppSurface.Web"),
            new("Guide", "docs/guide.md", "<p>guide</p>"),
            new("Build", "docs/guide.md#Build", string.Empty, "docs/guide.md"),
            new("Run", "docs/guide.md#Run", string.Empty, "docs/guide.md")
        };
        // This test renders the sidebar with a direct model, so CreateServiceProvider docs are intentionally irrelevant.
        using var services = CreateServiceProvider(CreateDocs());

        var model = CreateSidebarViewModel(
            ["ForgeTrust.AppSurface."],
            ("Namespaces", docs[0]),
            ("Namespaces", docs[1]),
            ("Namespaces", docs[2]),
            ("docs", docs[3]),
            ("docs", docs[4]),
            ("docs", docs[5]));

        var html = await RenderViewAsync(
            services,
            "/Views/Shared/Components/Sidebar/Default.cshtml",
            model);

        Assert.Contains("href=\"/docs/Namespaces\"", html);
        Assert.Contains("href=\"/docs/Namespaces/ForgeTrust.AppSurface.Web\"", html);
        Assert.DoesNotContain("href=\"/docs/Namespaces/ForgeTrust.AppSurface.Web#AspireApp\"", html);
        Assert.Contains("href=\"/docs/docs/guide.md\"", html);
        Assert.Contains("href=\"/docs/docs/guide.md#Build\"", html);
        Assert.Contains("href=\"/docs/docs/guide.md#Run\"", html);
    }

    [Fact]
    public void SidebarDisplayHelper_IsTypeAnchorNode_ShouldHandleEdgeBranches()
    {
        var typeAnchorTrue = SidebarDisplayHelper.IsTypeAnchorNode(
            new DocNode("Anchor", "Namespaces/Foo#Anchor", string.Empty, "Namespaces/Foo"));
        var typeAnchorFalseMissingParent = SidebarDisplayHelper.IsTypeAnchorNode(
            new DocNode("NoParent", "Namespaces/Foo#Anchor", string.Empty, string.Empty));
        var typeAnchorFalseWithContent = SidebarDisplayHelper.IsTypeAnchorNode(
            new DocNode("HasContent", "Namespaces/Foo#Anchor", "<p>not empty</p>", "Namespaces/Foo"));
        var typeAnchorFalseNoHash = SidebarDisplayHelper.IsTypeAnchorNode(
            new DocNode("NoHash", "Namespaces/FooAnchor", string.Empty, "Namespaces/Foo"));

        Assert.True(typeAnchorTrue);
        Assert.False(typeAnchorFalseMissingParent);
        Assert.False(typeAnchorFalseWithContent);
        Assert.False(typeAnchorFalseNoHash);
    }

    [Fact]
    public void SidebarDisplayHelper_GetGroupName_ShouldRecognizeBackslashSeparatedNamespacePaths()
    {
        var groupName = SidebarDisplayHelper.GetGroupName(@"Namespaces\ForgeTrust.AppSurface");

        Assert.Equal("Namespaces", groupName);
    }

    [Fact]
    public void SidebarDisplayHelper_GetFullNamespaceName_ShouldHandleEdgeBranches()
    {
        var fullNamespaceRoot = SidebarDisplayHelper.GetFullNamespaceName(
            new DocNode("Root", "Namespaces/", string.Empty));
        var fullNamespaceNormal = SidebarDisplayHelper.GetFullNamespaceName(
            new DocNode("Foo", "Namespaces/Foo.Bar", string.Empty));
        var fullNamespaceFallbackTitle = SidebarDisplayHelper.GetFullNamespaceName(
            new DocNode("TitleFallback", "Other.Path", string.Empty));

        Assert.Equal(string.Empty, fullNamespaceRoot);
        Assert.Equal("Foo.Bar", fullNamespaceNormal);
        Assert.Equal("TitleFallback", fullNamespaceFallbackTitle);
    }

    [Fact]
    public void SidebarDisplayHelper_SimplifyNamespace_ShouldHandleEdgeBranches()
    {
        var simplifiedBlank = SidebarDisplayHelper.SimplifyNamespace(
            string.Empty,
            new List<string> { "unused" });
        var simplifiedDottedPrefix = SidebarDisplayHelper.SimplifyNamespace(
            "Foo.Bar",
            new List<string> { "   ", "Foo.." });
        var simplifiedDottedPrefixEmptyRemainder = SidebarDisplayHelper.SimplifyNamespace(
            "Foo.",
            new List<string> { "Foo.." });
        var simplifiedNormalizedPrefixWithRemainder = SidebarDisplayHelper.SimplifyNamespace(
            "ForgeTrust.AppSurface.Web",
            new List<string> { "ForgeTrust.AppSurface." });
        var simplifiedNormalizedPrefixNoRemainder = SidebarDisplayHelper.SimplifyNamespace(
            "ForgeTrust.AppSurface.",
            new List<string> { "ForgeTrust.AppSurface." });

        Assert.Equal("Namespaces", simplifiedBlank);
        Assert.Equal("Bar", simplifiedDottedPrefix);
        Assert.Equal("Foo", simplifiedDottedPrefixEmptyRemainder);
        Assert.Equal("Web", simplifiedNormalizedPrefixWithRemainder);
        Assert.Equal("AppSurface", simplifiedNormalizedPrefixNoRemainder);
    }

    [Fact]
    public void SidebarDisplayHelper_GetNamespaceDisplayName_ShouldHandleEdgeBranches()
    {
        var displayWithTrailingDot = SidebarDisplayHelper.GetNamespaceDisplayName(
            "Foo.",
            new List<string>());
        var displayWithSegment = SidebarDisplayHelper.GetNamespaceDisplayName(
            "Foo.Bar",
            new List<string>());

        Assert.Equal("Foo.", displayWithTrailingDot);
        Assert.Equal("Bar", displayWithSegment);
    }

    private static ServiceProvider CreateServiceProvider(
        IReadOnlyList<DocNode> docs,
        IDictionary<string, string?>? overrides = null,
        Assembly? rootModuleAssembly = null)
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var webRoot = Path.Combine(repoRoot, "Web", "ForgeTrust.AppSurface.Docs");

        var configValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["RepositoryRoot"] = repoRoot
        };

        if (overrides != null)
        {
            foreach (var pair in overrides)
            {
                configValues[pair.Key] = pair.Value;
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        var diagnosticListener = new DiagnosticListener("RazorDocsViewsTests");
        services.AddSingleton<DiagnosticSource>(diagnosticListener);
        services.AddSingleton(diagnosticListener);
        services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(webRoot));
        services.AddSingleton<IConfiguration>(_ => configuration);
        services.AddMemoryCache();
        services.AddSingleton<IMemo, Memo>();
        services.AddRazorDocs();
        services.AddSingleton(
            RazorDocsAssetPathResolver.CreateForRootModule(
                rootModuleAssembly ?? typeof(RazorDocsWebModule).Assembly));
        services.RemoveAll<IDocHarvester>();
        services.AddSingleton<IDocHarvester>(_ => new StaticDocHarvester(docs));
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(DocsController).Assembly);

        return services.BuildServiceProvider();
    }

    private static string ReadLayoutMarkup()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var layoutPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs",
            "Views",
            "Shared",
            "_Layout.cshtml");

        return File.ReadAllText(layoutPath);
    }

    private static string ReadSearchClientMarkup()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var searchClientPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs",
            "wwwroot",
            "docs",
            "search-client.js");

        return File.ReadAllText(searchClientPath);
    }

    private static string ReadOutlineClientMarkup()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var outlineClientPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs",
            "wwwroot",
            "docs",
            "outline-client.js");

        return File.ReadAllText(outlineClientPath);
    }

    private static string ReadTailwindEntryStylesheetMarkup()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var stylesheetPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs",
            "wwwroot",
            "css",
            "app.css");

        return File.ReadAllText(stylesheetPath);
    }

    private static string ReadSearchStylesheetMarkup()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var stylesheetPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs",
            "wwwroot",
            "docs",
            "search.css");

        return File.ReadAllText(stylesheetPath);
    }

    private static string RemoveRootTokenBlock(string stylesheet)
    {
        var rootStart = stylesheet.IndexOf(":root {", StringComparison.Ordinal);
        if (rootStart < 0)
        {
            return stylesheet;
        }

        var depth = 0;
        for (var index = rootStart; index < stylesheet.Length; index++)
        {
            if (stylesheet[index] == '{')
            {
                depth++;
            }
            else if (stylesheet[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return stylesheet.Remove(rootStart, index - rootStart + 1);
                }
            }
        }

        return stylesheet;
    }

    private static IReadOnlyList<string> FindUnclassifiedRawColorLiterals(string stylesheet)
    {
        var unclassified = new List<string>();

        foreach (Match match in RawCssColorLiteralRegex.Matches(stylesheet))
        {
            var selector = FindSelectorForCssDeclaration(stylesheet, match.Index);
            if (IsClassifiedRawColorSelector(selector))
            {
                continue;
            }

            var line = stylesheet[..match.Index].Count(c => c == '\n') + 1;
            unclassified.Add($"{line}: {selector} uses {match.Value}");
        }

        return unclassified;
    }

    private static string FindSelectorForCssDeclaration(string stylesheet, int declarationIndex)
    {
        var blockStart = stylesheet.LastIndexOf('{', declarationIndex);
        if (blockStart < 0)
        {
            return string.Empty;
        }

        var previousBlockEnd = stylesheet.LastIndexOf('}', Math.Max(0, blockStart - 1));
        var selectorStart = previousBlockEnd < 0 ? 0 : previousBlockEnd + 1;
        return stylesheet[selectorStart..blockStart].Trim();
    }

    private static bool IsClassifiedRawColorSelector(string selector)
    {
        return selector.Contains(".docs-page-badge--example", StringComparison.Ordinal)
               || selector.Contains(".docs-page-badge--api-reference", StringComparison.Ordinal)
               || selector.Contains(".docs-page-badge--glossary", StringComparison.Ordinal)
               || selector.Contains(".docs-page-badge--faq", StringComparison.Ordinal)
               || selector.Contains(".docs-page-badge--internals", StringComparison.Ordinal)
               || selector.Contains(".docs-page-badge--troubleshooting", StringComparison.Ordinal)
               || selector.Contains(".docs-content .doc-signature", StringComparison.Ordinal)
               || selector.Contains(".docs-content .doc-token", StringComparison.Ordinal);
    }

    private static async Task<string> RenderDocsViewAsync(
        ServiceProvider services,
        string actionName,
        Func<DocsController, Task<IActionResult>> action,
        string? pathBase)
    {
        return await RenderDocsViewAsync(
            services,
            actionName,
            action,
            string.IsNullOrWhiteSpace(pathBase)
                ? null
                : httpContext => httpContext.Request.PathBase = new PathString(pathBase));
    }

    private static async Task<string> RenderDocsViewAsync(
        ServiceProvider services,
        string actionName,
        Func<DocsController, Task<IActionResult>> action,
        Action<DefaultHttpContext>? configureHttpContext = null)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scopedServices
        };
        configureHttpContext?.Invoke(httpContext);
        httpContext.Response.Body = new MemoryStream();

        var controller = ActivatorUtilities.CreateInstance<DocsController>(scopedServices);
        var routeData = new RouteData();
        routeData.Values["controller"] = "Docs";
        routeData.Values["action"] = actionName;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            RouteData = routeData,
            ActionDescriptor = new ControllerActionDescriptor
            {
                ControllerName = "Docs",
                ActionName = actionName
            }
        };

        var result = await action(controller);
        var viewResult = Assert.IsType<ViewResult>(result);
        viewResult.ViewName ??= $"/Views/Docs/{actionName}.cshtml";

        var executor = scopedServices.GetRequiredService<IActionResultExecutor<ViewResult>>();
        var actionContext = new ActionContext(
            controller.HttpContext,
            controller.RouteData,
            controller.ControllerContext.ActionDescriptor);

        await executor.ExecuteAsync(actionContext, viewResult);

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static async Task<IActionResult> InvokeDocsActionAsync(
        ServiceProvider services,
        string actionName,
        Func<DocsController, Task<IActionResult>> action)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var controller = ActivatorUtilities.CreateInstance<DocsController>(scopedServices);
        var routeData = new RouteData();
        routeData.Values["controller"] = "Docs";
        routeData.Values["action"] = actionName;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = scopedServices
            },
            RouteData = routeData,
            ActionDescriptor = new ControllerActionDescriptor
            {
                ControllerName = "Docs",
                ActionName = actionName
            }
        };

        return await action(controller);
    }

    private static async Task<string> RenderViewAsync(
        ServiceProvider services,
        string viewName,
        object model,
        string? pathBase)
    {
        return await RenderViewAsync(
            services,
            viewName,
            model,
            null,
            string.IsNullOrWhiteSpace(pathBase)
                ? null
                : httpContext => httpContext.Request.PathBase = new PathString(pathBase));
    }

    private static async Task<string> RenderViewAsync(
        ServiceProvider services,
        string viewName,
        object model,
        Action<ViewDataDictionary>? configureViewData = null,
        Action<HttpContext>? configureHttpContext = null)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scopedServices
        };
        httpContext.Response.Body = new MemoryStream();
        configureHttpContext?.Invoke(httpContext);

        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = null
        };
        configureViewData?.Invoke(viewData);
        viewData.Model = AdaptViewModel(viewName, model, viewData);

        var result = new ViewResult
        {
            ViewName = viewName,
            ViewData = viewData
        };

        var executor = scopedServices.GetRequiredService<IActionResultExecutor<ViewResult>>();
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        await executor.ExecuteAsync(actionContext, result);

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> RenderDetailsViewAsync(DocNode doc, params DocNode[] additionalDocs)
    {
        using var services = CreateServiceProvider(CreateDocsWithOverrides([doc, .. additionalDocs]));

        return await RenderDocsViewAsync(
            services,
            "Details",
            controller => controller.Details(ToTestPublicRoute(doc, additionalDocs)));
    }

    private static async Task<string> RenderDetailsViewWithPathBaseAsync(DocNode doc, string pathBase, params DocNode[] additionalDocs)
    {
        using var services = CreateServiceProvider(CreateDocsWithOverrides([doc, .. additionalDocs]));

        return await RenderDocsViewAsync(
            services,
            "Details",
            controller => controller.Details(ToTestPublicRoute(doc, additionalDocs)),
            httpContext => httpContext.Request.PathBase = pathBase);
    }

    private static string ToTestPublicRoute(DocNode doc, IReadOnlyList<DocNode> additionalDocs)
    {
        var catalog = DocRouteIdentityCatalog.Create(
            [doc, .. additionalDocs],
            new DocsUrlBuilder(new RazorDocsOptions()));
        if (catalog.TryGetPublicRoutePath(doc.Path, out var publicRoutePath))
        {
            return publicRoutePath;
        }

        return doc.Path.Trim().Replace('\\', '/').Trim('/');
    }

    private static object AdaptViewModel(string viewName, object model, ViewDataDictionary viewData)
    {
        if (viewName.EndsWith("/Views/Shared/Components/Sidebar/Default.cshtml", StringComparison.OrdinalIgnoreCase)
            && model is IEnumerable<IGrouping<string, DocNode>> groupedDocs)
        {
            return CreateSidebarViewModel(groupedDocs, viewData);
        }

        return model;
    }

    private static DocSidebarViewModel CreateSidebarViewModel(
        IEnumerable<IGrouping<string, DocNode>> groupedDocs,
        ViewDataDictionary viewData)
    {
        var namespacePrefixes = viewData["NamespacePrefixes"] as IReadOnlyList<string>;
        var sections = groupedDocs
            .Select(
                group =>
                {
                    var section = ResolveSidebarSection(group.Key, group);
                    var snapshot = new DocSectionSnapshot
                    {
                        Section = section,
                        Label = DocPublicSectionCatalog.GetLabel(section),
                        Slug = DocPublicSectionCatalog.GetSlug(section),
                        VisiblePages = group.ToList()
                    };

                    return new DocSidebarSectionViewModel
                    {
                        Section = section,
                        Label = DocPublicSectionCatalog.GetLabel(section),
                        Slug = DocPublicSectionCatalog.GetSlug(section),
                        Href = DocPublicSectionCatalog.GetHref(section),
                        Groups = DocSectionDisplayBuilder.BuildGroups(snapshot, namespacePrefixes: namespacePrefixes)
                    };
                })
            .ToList();

        return new DocSidebarViewModel { Sections = sections };
    }

    private static DocPublicSection ResolveSidebarSection(string groupKey, IEnumerable<DocNode> docs)
    {
        if (DocPublicSectionCatalog.TryResolve(groupKey, out var section))
        {
            return section;
        }

        return groupKey.Equals("Namespaces", StringComparison.OrdinalIgnoreCase)
               || docs.Any(doc => NormalizeSidebarPath(doc.Path).StartsWith("Namespaces", StringComparison.OrdinalIgnoreCase))
            ? DocPublicSection.ApiReference
            : DocPublicSection.HowToGuides;
    }

    private static string NormalizeSidebarPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Trim('/', '\\');
    }

    [Fact]
    public void ResolveSidebarSection_ShouldFallbackToHowToGuides_ForUnknownGroups()
    {
        var docs = new[]
        {
            new DocNode("Guide", "guides/guide.md", "<p>Guide</p>")
        };

        var section = ResolveSidebarSection("Custom Group", docs);

        Assert.Equal(DocPublicSection.HowToGuides, section);
    }

    [Fact]
    public void ResolveSidebarSection_ShouldFallbackToApiReference_WhenNamespaceDocsArePresent()
    {
        var docs = new[]
        {
            new DocNode("Foo", "/Namespaces/Foo", "<p>Namespace</p>")
        };

        var section = ResolveSidebarSection("Custom Group", docs);

        Assert.Equal(DocPublicSection.ApiReference, section);
    }

    private static List<IGrouping<string, DocNode>> CreateGroupedSidebarModel(params (string Group, DocNode Node)[] items)
    {
        return items
            .GroupBy(item => item.Group, item => item.Node)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DocDetailsViewModel CreateDetailsViewModel(
        DocNode doc,
        IReadOnlyList<DocOutlineItem>? outline = null,
        DocPageLinkViewModel? previousPage = null,
        DocPageLinkViewModel? nextPage = null,
        IReadOnlyList<DocPageLinkViewModel>? relatedPages = null,
        DocContributorProvenanceViewModel? contributorProvenance = null,
        bool contributorSourceUsesTurbo = false,
        bool contributorEditUsesTurbo = false)
    {
        var metadata = doc.Metadata;

        return new DocDetailsViewModel
        {
            Document = doc,
            Title = string.IsNullOrWhiteSpace(metadata?.Title) ? doc.Title : metadata!.Title!.Trim(),
            Summary = metadata?.Summary,
            ShowSummary = !string.IsNullOrWhiteSpace(metadata?.Summary) && metadata?.SummaryIsDerived != true,
            IsCSharpApiDoc = doc.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
            IsApiSurfaceDoc = !IsMarkdownDoc(doc.Path)
                              || IsApiSurfacePageType(metadata?.PageType),
            PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge(metadata?.PageType),
            Component = metadata?.ComponentIsDerived == true || string.IsNullOrWhiteSpace(metadata?.Component)
                ? null
                : metadata!.Component!.Trim(),
            Audience = metadata?.AudienceIsDerived == true || string.IsNullOrWhiteSpace(metadata?.Audience)
                ? null
                : metadata!.Audience!.Trim(),
            Outline = outline ?? doc.Outline ?? [],
            PreviousPage = previousPage,
            NextPage = nextPage,
            RelatedPages = relatedPages ?? [],
            ContributorProvenance = contributorProvenance,
            ContributorSourceUsesTurbo = contributorSourceUsesTurbo,
            ContributorEditUsesTurbo = contributorEditUsesTurbo
        };
    }

    private static bool IsApiSurfacePageType(string? pageType)
    {
        var normalizedPageType = DocMetadataPresentation.NormalizeToken(pageType);

        return normalizedPageType is "api" or "api-reference";
    }

    private static bool IsMarkdownDoc(string path)
    {
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static DocSidebarViewModel CreateSidebarViewModel(
        IReadOnlyList<string> namespacePrefixes,
        params (string Group, DocNode Node)[] items)
    {
        var grouped = items
            .GroupBy(item => item.Group, item => item.Node)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            ["NamespacePrefixes"] = namespacePrefixes
        };

        return CreateSidebarViewModel(grouped, viewData);
    }

    private static List<DocNode> CreateDocs()
    {
        return
        [
            new("Namespaces", "Namespaces", "<p>Namespace root</p>", Metadata: new DocMetadata { NavGroup = "API Reference" }),
            new("ForgeTrust", "Namespaces/ForgeTrust", "<p>ForgeTrust namespace</p>", Metadata: new DocMetadata { NavGroup = "API Reference" }),
            new("AppSurface", "Namespaces/ForgeTrust.AppSurface", "<p>AppSurface namespace</p>", Metadata: new DocMetadata { NavGroup = "API Reference" }),
            new("Web", "Namespaces/ForgeTrust.AppSurface.Web", "<p>Web namespace</p>", Metadata: new DocMetadata { NavGroup = "API Reference" }),
            new("Api", "Namespaces/ForgeTrust.AppSurface.Web.Api", "<p>Api namespace</p>", Metadata: new DocMetadata { NavGroup = "API Reference" }),
            new(
                "AspireApp",
                "Namespaces/ForgeTrust.AppSurface.Web#ForgeTrust.AppSurface.Web.AspireApp",
                string.Empty,
                "Namespaces/ForgeTrust.AppSurface.Web",
                Metadata: new DocMetadata { NavGroup = "API Reference" }),
            new("RunAsync", "Namespaces/ForgeTrust.AppSurface.Web#ForgeTrust.AppSurface.Web.AspireApp.RunAsync(System.String[])", string.Empty, "Namespaces/ForgeTrust.AppSurface.Web"),
            new(
                "Example",
                "src/Example.cs",
                "<section id='example' class='doc-type'><header class='doc-type-header'><span class='doc-kind'>Type</span><h2>Example</h2></header><div class='doc-body'><p>Example body</p></div></section>",
                Metadata: new DocMetadata { NavGroup = "API Reference" }),
            new("Run", "src/Example.cs#Example.Run", string.Empty, "src/Example.cs", Metadata: new DocMetadata { NavGroup = "API Reference" }),
            new("Guide", "guides/intro.md", "<p>Guide body</p>", Metadata: new DocMetadata { NavGroup = "How-to Guides" })
        ];
    }

    private static List<DocNode> CreateDocsWithOverrides(IEnumerable<DocNode> overrides)
    {
        var docs = CreateDocs();
        foreach (var doc in overrides)
        {
            docs.RemoveAll(existing => string.Equals(existing.Path, doc.Path, StringComparison.OrdinalIgnoreCase));
            docs.Add(doc);
        }

        return docs;
    }

    private static DocFeaturedPageGroupDefinition FeaturedGroup(params DocFeaturedPageDefinition[] pages)
    {
        return FeaturedGroup("Test", pages);
    }

    private static DocFeaturedPageGroupDefinition FeaturedGroup(string label, params DocFeaturedPageDefinition[] pages)
    {
        return new DocFeaturedPageGroupDefinition
        {
            Intent = label.ToLowerInvariant().Replace(' ', '-'),
            Label = label,
            Pages = pages
        };
    }

    private static IElement? FindAncestor(IElement element, string tagName)
    {
        var current = element.ParentElement;
        while (current is not null)
        {
            if (string.Equals(current.LocalName, tagName, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            current = current.ParentElement;
        }

        return null;
    }

    private sealed class StaticDocHarvester : IDocHarvester
    {
        private readonly IReadOnlyList<DocNode> _docs;

        public StaticDocHarvester(IReadOnlyList<DocNode> docs)
        {
            _docs = docs;
        }

        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<DocNode>>(_docs);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment, IDisposable
    {
        public TestWebHostEnvironment(string contentRootPath)
        {
            ApplicationName = typeof(DocsController).Assembly.GetName().Name ?? "RazorDocsTests";
            EnvironmentName = Environments.Development;
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
            WebRootPath = contentRootPath;
            WebRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string ApplicationName { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }

        public string ContentRootPath { get; set; }

        public string EnvironmentName { get; set; }

        public IFileProvider WebRootFileProvider { get; set; }

        public string WebRootPath { get; set; }

        public void Dispose()
        {
            (ContentRootFileProvider as IDisposable)?.Dispose();
            (WebRootFileProvider as IDisposable)?.Dispose();
        }
    }
}
