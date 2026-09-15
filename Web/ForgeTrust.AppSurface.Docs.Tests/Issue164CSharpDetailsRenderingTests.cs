using System.Diagnostics;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Web;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

/// <summary>
/// Proves the Issue 164 source fixture renders through the typed C# Razor partials.
/// </summary>
public sealed class Issue164CSharpDetailsRenderingTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "AppSurfaceDocsIssue164Rendering",
        Guid.NewGuid().ToString("N"));

    public Issue164CSharpDetailsRenderingTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Fixture_ShouldRenderTypedCSharpDetailsThroughRazorPartials()
    {
        File.Copy(FixturePath("ApiFixtures.cs"), Path.Join(_root, "ApiFixtures.cs"));

        var harvester = new CSharpDocHarvester(NullLogger<CSharpDocHarvester>.Instance);
        var results = await harvester.HarvestAsync(CreateContext());
        var namespaceNode = Assert.Single(results, node => node.Path == "Namespaces/Issue164.Api");
        Assert.IsType<CSharpNamespaceDocument>(namespaceNode.CSharpNamespaceDocument);

        using var services = CreateServiceProvider(namespaceNode);
        var html = await RenderViewAsync(
            services,
            "/Views/Docs/Details.cshtml",
            CreateDetailsViewModel(namespaceNode));
        var document = new HtmlParser().ParseDocument(html);

        var shellHeadings = document.QuerySelectorAll("h1.docs-detail-title");
        Assert.Single(shellHeadings);
        Assert.Equal("Api", shellHeadings[0].TextContent.Trim());

        var type = Assert.Single(document.QuerySelectorAll("section.doc-type:not(.doc-enum)"));
        Assert.Equal("FixtureService<TItem>", type.QuerySelector("h2")?.TextContent.Trim());

        var typeParameters = Assert.Single(type.QuerySelectorAll(".doc-typeparams"));
        Assert.Equal("TItem", typeParameters.QuerySelector("code")?.TextContent.Trim());
        Assert.Contains("item accepted by the service", typeParameters.TextContent, StringComparison.Ordinal);

        var methodGroup = Assert.Single(
            type.QuerySelectorAll("section.doc-method-group"),
            section => section.QuerySelector("h3")?.TextContent.Trim() == "Process");
        Assert.Equal("Process", methodGroup.QuerySelector("h3")?.TextContent.Trim());
        var overloads = methodGroup.QuerySelectorAll("details.doc-overload");
        Assert.Equal(2, overloads.Length);
        Assert.All(overloads, overload => Assert.Equal("Process", overload.QuerySelector(".sig-method")?.TextContent.Trim()));
        Assert.Contains("TItem", overloads[0].QuerySelector(".sig-type")?.TextContent, StringComparison.Ordinal);
        Assert.Contains("item", overloads[0].QuerySelector(".sig-parameter")?.TextContent, StringComparison.Ordinal);
        Assert.Contains("string", overloads[1].QuerySelector(".sig-type")?.TextContent, StringComparison.Ordinal);
        Assert.Contains("item", overloads[1].QuerySelector(".sig-parameter")?.TextContent, StringComparison.Ordinal);
        Assert.Contains("1", overloads[0].QuerySelector(".sig-literal")?.TextContent, StringComparison.Ordinal);
        Assert.True(overloads[0].HasAttribute("open"));
        Assert.Equal(
            "Processes a item and returns a safe System.String value.",
            NormalizeReaderText(overloads[0].QuerySelector(".doc-summary")?.TextContent));

        var parameters = Assert.Single(overloads[0].QuerySelectorAll(".doc-params"));
        Assert.Equal(["item", "attempt"], parameters.QuerySelectorAll("li code").Select(code => code.TextContent.Trim()));
        Assert.Contains(
            overloads[0].QuerySelectorAll(".doc-summary code"),
            code => code.TextContent.Trim() == "item");
        Assert.NotNull(overloads[0].QuerySelector(".doc-returns"));
        Assert.Contains("A rendered result.", overloads[0].QuerySelector(".doc-returns")?.TextContent, StringComparison.Ordinal);
        var exception = Assert.Single(overloads[0].QuerySelectorAll(".doc-exceptions code[data-csharp-cref]"));
        Assert.Equal("T:System.InvalidOperationException", exception.GetAttribute("data-csharp-cref"));
        Assert.Equal("System.InvalidOperationException", exception.TextContent.Trim());
        var exampleCode = Assert.Single(overloads[0].QuerySelectorAll(".doc-example pre code"));
        Assert.Equal("var value = service.Process(item);", exampleCode.TextContent.Trim());

        var enumSection = Assert.Single(document.QuerySelectorAll("section.doc-enum"));
        Assert.Equal("FixtureState", enumSection.QuerySelector("h2")?.TextContent.Trim());

        var remarks = Assert.Single(document.QuerySelectorAll(".doc-remarks"));
        Assert.Contains("Hostile XML-like text: <script>must remain text</script>.", remarks.TextContent, StringComparison.Ordinal);
        Assert.Empty(remarks.QuerySelectorAll("script"));
        Assert.Equal(2, remarks.QuerySelectorAll("p").Length);
        Assert.Equal(2, remarks.QuerySelectorAll("ol li").Length);
        Assert.Single(remarks.QuerySelectorAll("ul li"));
        Assert.Contains(
            remarks.QuerySelectorAll("code[data-csharp-cref]"),
            code => code.GetAttribute("data-csharp-cref") == "T:System.String" && code.TextContent.Trim() == "System.String");
        Assert.Contains(
            remarks.QuerySelectorAll("code[data-csharp-cref]"),
            code => code.GetAttribute("data-csharp-cref") == "https://example.com/issue164"
                    && code.TextContent.Trim() == "external documentation");
        Assert.Contains("TItem", remarks.TextContent, StringComparison.Ordinal);
        Assert.Contains("Unknown markup must fall back to text.", remarks.TextContent, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DocHarvestContext CreateContext()
    {
        var configuredPolicy = AppSurfaceDocsHarvestPathPolicy.CreateDefault();
        var vcsIgnorePolicy = new AppSurfaceDocsHarvestVcsIgnorePolicy(
            _root,
            new AppSurfaceDocsHarvestVcsIgnoreOptions(),
            NullLogger.Instance);

        return new DocHarvestContext(
            _root,
            new AppSurfaceDocsHarvestPathPolicySnapshot(configuredPolicy, vcsIgnorePolicy));
    }

    private ServiceProvider CreateServiceProvider(DocNode doc)
    {
        var repoRoot = FindRepoRoot();
        var webRoot = Path.Join(repoRoot, "Web", "ForgeTrust.AppSurface.Docs");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AppSurfaceDocs:Source:RepositoryRoot"] = _root,
                    ["AppSurfaceDocs:Harvest:StartupMode"] = nameof(AppSurfaceDocsHarvestStartupMode.Disabled)
                })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<DiagnosticListener>(_ => new DiagnosticListener("Issue164CSharpDetailsRenderingTests"));
        services.AddSingleton<DiagnosticSource>(sp => sp.GetRequiredService<DiagnosticListener>());
        services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(webRoot));
        services.AddSingleton<IConfiguration>(_ => configuration);
        services.AddMemoryCache();
        services.AddSingleton<IMemo, Memo>();
        services.AddAppSurfaceDocs();
        services.AddSingleton(
            AppSurfaceDocsAssetPathResolver.CreateForRootModule(typeof(AppSurfaceDocsWebModule).Assembly));
        services.RemoveAll<IDocHarvester>();
        services.AddSingleton<IDocHarvester>(_ => new StaticDocHarvester(doc));
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(DocsController).Assembly);

        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderViewAsync(
        ServiceProvider services,
        string viewName,
        object model)
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

    private static DocDetailsViewModel CreateDetailsViewModel(DocNode doc)
    {
        var metadata = doc.Metadata;

        return new DocDetailsViewModel
        {
            Document = doc,
            Title = string.IsNullOrWhiteSpace(metadata?.Title) ? doc.Title : metadata!.Title!.Trim(),
            Summary = metadata?.Summary,
            ShowSummary = !string.IsNullOrWhiteSpace(metadata?.Summary) && metadata.SummaryIsDerived != true,
            IsCSharpApiDoc = doc.CSharpNamespaceDocument is not null,
            CSharpRenderKind = doc.CSharpNamespaceDocument is null
                ? CSharpRenderKind.Legacy
                : CSharpRenderKind.TypedNamespace,
            IsApiSurfaceDoc = true,
            PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge(metadata?.PageType),
            Component = metadata?.ComponentIsDerived == true || string.IsNullOrWhiteSpace(metadata?.Component)
                ? null
                : metadata!.Component!.Trim(),
            Audience = metadata?.AudienceIsDerived == true || string.IsNullOrWhiteSpace(metadata?.Audience)
                ? null
                : metadata!.Audience!.Trim(),
            Outline = doc.Outline ?? []
        };
    }

    private static string FixturePath(string name)
    {
        return Path.Join(AppContext.BaseDirectory, "TestData", "Issue164CSharpApi", name);
    }

    private static string NormalizeReaderText(string? value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }

    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string testSourcePath = "")
    {
        return TestPathUtils.FindRepoRoot(testSourcePath);
    }

    private sealed class StaticDocHarvester(DocNode doc) : IDocHarvester
    {
        public Task<IReadOnlyList<DocNode>> HarvestAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<DocNode>>([doc]);
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment, IDisposable
    {
        public string ApplicationName { get; set; } = typeof(DocsController).Assembly.GetName().Name ?? "AppSurfaceDocsTests";

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
