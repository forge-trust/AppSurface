using System.Diagnostics;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Docs.ViewComponents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RecommendedPagesViewTests
{
    private const string DetailsViewPath = "/Views/Docs/Details.cshtml";
    private static readonly HtmlParser HtmlParser = new();

    [Fact]
    public async Task DetailsView_ShouldRenderCombinedRecommendationsAsOneAccessibleList_AfterSequenceNavigation()
    {
        using var services = CreateServiceProvider();
        var authored = Enumerable.Range(1, 12)
            .Select(index => CreateRecommendation(
                $"Authored page {index:00}",
                $"/docs/authored-{index:00}",
                DocRecommendationSource.Authored,
                "authored"))
            .ToArray();
        var automatic = new[]
        {
            CreateRecommendation(
                "Automatic example",
                "/docs/automatic-example",
                DocRecommendationSource.Automatic,
                "try-example",
                summary: "Try the highlighted flow.",
                pageType: "example",
                reasonText: "",
                supportingReasonCodes: ["shared-component"]),
            CreateRecommendation(
                "Automatic guide",
                "/docs/automatic-guide",
                DocRecommendationSource.Automatic,
                "deepen-guide"),
            CreateRecommendation(
                "Automatic recovery",
                "/docs/automatic-recovery",
                DocRecommendationSource.Automatic,
                "troubleshoot")
        };
        var model = CreateDetailsViewModel(
            recommendedPages: [.. authored, .. automatic],
            relatedPages: [CreateLink("Legacy fallback must not render", "/docs/legacy-fallback")],
            previousPage: CreateLink("Previous sequence page", "/docs/previous"),
            nextPage: CreateLink("Next sequence page", "/docs/next"));

        var html = await RenderDetailsViewAsync(services, model);
        var document = HtmlParser.ParseDocument(html);
        var wayfinding = Assert.IsAssignableFrom<IElement>(document.QuerySelector("#docs-page-wayfinding"));
        var navigation = GetReadNextNavigation(wayfinding);
        var list = Assert.Single(navigation.QuerySelectorAll("ul, ol"));
        var rows = list.QuerySelectorAll("li").ToArray();
        var expectedRows = authored.Concat(automatic).ToArray();

        Assert.Equal(expectedRows.Length, rows.Length);
        Assert.Equal(
            expectedRows.Select(row => row.Page.Title),
            rows.Select(row => GetAccessibleTitle(document, Assert.Single(row.QuerySelectorAll("a")))));
        Assert.Empty(wayfinding.QuerySelectorAll("a[href='/docs/legacy-fallback']"));

        var previous = Assert.Single(wayfinding.QuerySelectorAll("a[data-doc-wayfinding='previous']"));
        var next = Assert.Single(wayfinding.QuerySelectorAll("a[data-doc-wayfinding='next']"));
        var firstRecommendation = Assert.Single(rows[0].QuerySelectorAll("a"));
        var wayfindingLinks = wayfinding.QuerySelectorAll("a").ToArray();
        Assert.True(Array.IndexOf(wayfindingLinks, previous) < Array.IndexOf(wayfindingLinks, next));
        Assert.True(Array.IndexOf(wayfindingLinks, next) < Array.IndexOf(wayfindingLinks, firstRecommendation));

        for (var index = 0; index < expectedRows.Length; index++)
        {
            var row = rows[index];
            var link = Assert.Single(row.QuerySelectorAll("a"));

            Assert.Contains(
                DocRecommendationPolicy.ReasonText(
                    expectedRows[index].PrimaryReasonCode,
                    expectedRows[index].ReasonCodes.Contains("shared-component", StringComparer.Ordinal)),
                NormalizeText(row.TextContent),
                StringComparison.Ordinal);
            Assert.Equal("true", link.GetAttribute("data-doc-related-link"));
            Assert.Equal("doc-content", link.GetAttribute("data-turbo-frame"));
            Assert.Equal("advance", link.GetAttribute("data-turbo-action"));
            Assert.Single(row.QuerySelectorAll(".docs-recommended-next__arrow[aria-hidden='true']"));
        }

        Assert.Empty(rows[0].QuerySelectorAll(".docs-page-badge"));
        Assert.DoesNotContain("Try the highlighted flow.", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Example", rows[12].QuerySelector(".docs-page-badge")?.TextContent, StringComparison.Ordinal);
        Assert.Contains("Try the highlighted flow.", rows[12].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailsView_ShouldRenderEveryLegacyAuthoredLinkOnce_WithAuthoredCaption()
    {
        using var services = CreateServiceProvider();
        var authoredPages = Enumerable.Range(1, 12)
            .Select(index => CreateLink($"Legacy authored page {index:00}", $"/docs/legacy-{index:00}"))
            .ToArray();
        var model = CreateDetailsViewModel(relatedPages: authoredPages);

        var html = await RenderDetailsViewAsync(services, model);
        var document = HtmlParser.ParseDocument(html);
        var wayfinding = Assert.IsAssignableFrom<IElement>(document.QuerySelector("#docs-page-wayfinding"));
        var navigation = GetReadNextNavigation(wayfinding);
        var list = Assert.Single(navigation.QuerySelectorAll("ul, ol"));
        var rows = list.QuerySelectorAll("li").ToArray();

        Assert.Equal(authoredPages.Length, rows.Length);
        Assert.Equal(
            authoredPages.Select(page => page.Title),
            rows.Select(row => GetAccessibleTitle(document, Assert.Single(row.QuerySelectorAll("a")))));
        Assert.Equal(authoredPages.Length, wayfinding.QuerySelectorAll("a").Length);

        foreach (var row in rows)
        {
            Assert.Contains(
                DocRecommendationPolicy.ReasonText("authored"),
                NormalizeText(row.TextContent),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DetailsView_ShouldKeepUnknownRecommendationSourceNavigable_WithGenericCaption()
    {
        using var services = CreateServiceProvider();
        const string href = "/docs/future-source";
        var recommendation = CreateRecommendation(
            "Future recommendation",
            href,
            (DocRecommendationSource)99,
            "try-example",
            reasonText: "");

        var html = await RenderDetailsViewAsync(
            services,
            CreateDetailsViewModel(recommendedPages: [recommendation]));
        var document = HtmlParser.ParseDocument(html);
        var wayfinding = Assert.IsAssignableFrom<IElement>(document.QuerySelector("#docs-page-wayfinding"));
        var navigation = GetReadNextNavigation(wayfinding);
        var row = Assert.Single(Assert.Single(navigation.QuerySelectorAll("ul, ol")).QuerySelectorAll("li"));
        var link = Assert.Single(row.QuerySelectorAll("a"));
        var reason = Assert.Single(row.QuerySelectorAll(".docs-recommended-next__reason"));

        Assert.Equal(href, link.GetAttribute("href"));
        Assert.Equal("Future recommendation", GetAccessibleTitle(document, link));
        Assert.Equal(DocRecommendationPolicy.ReasonText("future"), reason.TextContent.Trim());
        Assert.NotEqual(DocRecommendationPolicy.ReasonText("try-example"), reason.TextContent.Trim());
    }

    [Fact]
    public async Task DetailsView_ShouldEncodeRecommendationText_AndKeepTheResolvedHref()
    {
        using var services = CreateServiceProvider();
        const string title = "<script>alert('title')</script> & guide";
        const string summary = "<img src=x onerror='run()'> & details";
        const string caption = "<em>Read this</em> & continue";
        const string href = "/docs/encoded?first=one&second=two";
        var recommendation = CreateRecommendation(
            title,
            href,
            DocRecommendationSource.Automatic,
            "try-example",
            summary,
            reasonText: caption);

        var html = await RenderDetailsViewAsync(
            services,
            CreateDetailsViewModel(recommendedPages: [recommendation]));
        var document = HtmlParser.ParseDocument(html);
        var wayfinding = Assert.IsAssignableFrom<IElement>(document.QuerySelector("#docs-page-wayfinding"));
        var navigation = GetReadNextNavigation(wayfinding);
        var row = Assert.Single(Assert.Single(navigation.QuerySelectorAll("ul, ol")).QuerySelectorAll("li"));
        var link = Assert.Single(row.QuerySelectorAll("a"));

        Assert.Equal(href, link.GetAttribute("href"));
        Assert.Contains(title, row.TextContent, StringComparison.Ordinal);
        Assert.Contains(summary, row.TextContent, StringComparison.Ordinal);
        Assert.Contains(caption, row.TextContent, StringComparison.Ordinal);
        Assert.Empty(row.QuerySelectorAll("script, img, em"));
        Assert.Equal(title, GetAccessibleTitle(document, link));
    }

    [Fact]
    public async Task DetailsView_ShouldOmitEmptyWayfinding_AndKeepSequenceWithoutAnEmptyReadNextFooter()
    {
        using var services = CreateServiceProvider();

        var emptyHtml = await RenderDetailsViewAsync(services, CreateDetailsViewModel());
        var emptyDocument = HtmlParser.ParseDocument(emptyHtml);

        Assert.Null(emptyDocument.QuerySelector("#docs-page-wayfinding"));
        Assert.DoesNotContain(
            emptyDocument.QuerySelectorAll("h2"),
            heading => string.Equals(heading.TextContent.Trim(), "Read next", StringComparison.Ordinal));

        var sequenceOnlyHtml = await RenderDetailsViewAsync(
            services,
            CreateDetailsViewModel(
                previousPage: CreateLink("Previous only", "/docs/previous-only"),
                nextPage: CreateLink("Next only", "/docs/next-only")));
        var sequenceOnlyDocument = HtmlParser.ParseDocument(sequenceOnlyHtml);
        var wayfinding = Assert.IsAssignableFrom<IElement>(sequenceOnlyDocument.QuerySelector("#docs-page-wayfinding"));

        Assert.Single(wayfinding.QuerySelectorAll("a[data-doc-wayfinding='previous']"));
        Assert.Single(wayfinding.QuerySelectorAll("a[data-doc-wayfinding='next']"));
        Assert.DoesNotContain(
            wayfinding.QuerySelectorAll("h2"),
            heading => string.Equals(heading.TextContent.Trim(), "Read next", StringComparison.Ordinal));
        Assert.Empty(wayfinding.QuerySelectorAll("nav, ul, ol, hr"));
    }

    private static IElement GetReadNextNavigation(IParentNode wayfinding)
    {
        var heading = Assert.Single(wayfinding.QuerySelectorAll("h2"));
        Assert.Equal("Read next", heading.TextContent.Trim());
        var headingId = heading.GetAttribute("id");
        Assert.False(string.IsNullOrWhiteSpace(headingId));
        Assert.Same(heading, wayfinding.QuerySelectorAll("[id]").Single(element => element.Id == headingId));

        var navigation = Assert.Single(wayfinding.QuerySelectorAll("nav[aria-labelledby]"));
        Assert.Equal(headingId, navigation.GetAttribute("aria-labelledby"));
        return navigation;
    }

    private static string GetAccessibleTitle(IParentNode document, IElement link)
    {
        Assert.Equal("a", link.LocalName);
        Assert.False(string.IsNullOrWhiteSpace(link.GetAttribute("href")));
        Assert.Null(link.GetAttribute("role"));
        Assert.NotEqual("-1", link.GetAttribute("tabindex"));

        var labelledBy = link.GetAttribute("aria-labelledby");
        Assert.False(string.IsNullOrWhiteSpace(labelledBy));
        Assert.DoesNotContain(' ', labelledBy!);
        var title = Assert.IsAssignableFrom<IElement>(document.QuerySelectorAll("[id]")
            .SingleOrDefault(element => element.Id == labelledBy));
        Assert.True(title.ClassList.Contains("docs-recommended-next__title"));
        Assert.Empty(title.Children);

        return NormalizeText(title.TextContent);
    }

    private static string NormalizeText(string? value)
    {
        return string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static DocPageRecommendationViewModel CreateRecommendation(
        string title,
        string href,
        DocRecommendationSource source,
        string reasonCode,
        string? summary = null,
        string? pageType = null,
        string? reasonText = null,
        IReadOnlyList<string>? supportingReasonCodes = null)
    {
        return new DocPageRecommendationViewModel
        {
            Page = CreateLink(title, href, summary, pageType),
            Source = source,
            PrimaryReasonCode = reasonCode,
            ReasonCodes = new[] { reasonCode }.Concat(supportingReasonCodes ?? []).ToArray(),
            ReasonText = reasonText ?? DocRecommendationPolicy.ReasonText(
                reasonCode,
                (supportingReasonCodes ?? []).Contains("shared-component", StringComparer.Ordinal))
        };
    }

    private static DocPageLinkViewModel CreateLink(
        string title,
        string href,
        string? summary = null,
        string? pageType = null)
    {
        return new DocPageLinkViewModel
        {
            Title = title,
            Href = href,
            Summary = summary,
            PageTypeBadge = pageType is null ? null : DocMetadataPresentation.ResolvePageTypeBadge(pageType)
        };
    }

    private static DocDetailsViewModel CreateDetailsViewModel(
        IReadOnlyList<DocPageRecommendationViewModel>? recommendedPages = null,
        IReadOnlyList<DocPageLinkViewModel>? relatedPages = null,
        DocPageLinkViewModel? previousPage = null,
        DocPageLinkViewModel? nextPage = null)
    {
        var document = new DocNode("Current page", "current.md", "<p>Current page body</p>");
        return new DocDetailsViewModel
        {
            Document = document,
            Title = document.Title,
            IsApiSurfaceDoc = false,
            CSharpRenderKind = CSharpRenderKind.Legacy,
            PreviousPage = previousPage,
            NextPage = nextPage,
            RelatedPages = relatedPages ?? [],
            RecommendedPages = recommendedPages ?? []
        };
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var webRoot = Path.Join(repoRoot, "Web", "ForgeTrust.AppSurface.Docs");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AppSurfaceDocs:Source:RepositoryRoot"] = repoRoot,
                    ["AppSurfaceDocs:Harvest:StartupMode"] = nameof(AppSurfaceDocsHarvestStartupMode.Disabled)
                })
            .Build();

        var services = new ServiceCollection();
        var diagnosticListener = new DiagnosticListener(nameof(RecommendedPagesViewTests));
        services.AddLogging();
        services.AddSingleton<DiagnosticSource>(diagnosticListener);
        services.AddSingleton(diagnosticListener);
        services.AddSingleton<IWebHostEnvironment>(_ => new TestWebHostEnvironment(webRoot));
        services.AddSingleton<IConfiguration>(_ => configuration);
        services.AddMemoryCache();
        services.AddSingleton<IMemo, Memo>();
        services.AddAppSurfaceDocs();
        services.AddSingleton(
            AppSurfaceDocsAssetPathResolver.CreateForRootModule(typeof(AppSurfaceDocsWebModule).Assembly));
        services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);

        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderDetailsViewAsync(ServiceProvider services, DocDetailsViewModel model)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scopedServices
        };
        httpContext.Response.Body = new MemoryStream();

        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model
        };
        var viewResult = new ViewResult
        {
            ViewName = DetailsViewPath,
            ViewData = viewData
        };
        var executor = scopedServices.GetRequiredService<IActionResultExecutor<ViewResult>>();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        await executor.ExecuteAsync(actionContext, viewResult);

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment, IDisposable
    {
        public string ApplicationName { get; set; } = typeof(DocsController).Assembly.GetName().Name ?? "DocsTests";

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);

        public string ContentRootPath { get; set; } = contentRootPath;

        public string EnvironmentName { get; set; } = Environments.Development;

        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);

        public string WebRootPath { get; set; } = contentRootPath;

        public void Dispose()
        {
            (ContentRootFileProvider as IDisposable)?.Dispose();
            (WebRootFileProvider as IDisposable)?.Dispose();
        }
    }
}
