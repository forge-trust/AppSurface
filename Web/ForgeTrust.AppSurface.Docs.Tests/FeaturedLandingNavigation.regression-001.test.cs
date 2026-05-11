using System.Diagnostics;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Tests;

public class FeaturedLandingNavigationRegressionTests
{
    [Fact]
    public async Task IndexView_ShouldAdvanceHistory_WhenFeaturedCardTargetsDocumentFrame()
    {
        // Regression: ISSUE-001 — featured landing cards replaced doc content without advancing browser history
        // Found by /qa on 2026-04-19
        // Report: .gstack/qa-reports/qa-report-localhost-5189-2026-04-19.md
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
                        new DocFeaturedPageGroupDefinition
                        {
                            Intent = "open",
                            Label = "Open",
                            Pages =
                            [
                                new DocFeaturedPageDefinition
                                {
                                    Question = "Open Web docs",
                                    Path = "Web/README.md"
                                }
                            ]
                        }
                    ]
                }),
            new(
                "Web",
                "Web/README.md",
                "<p>Web docs</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Web summary."
                })
        };

        using var services = CreateServiceProvider(docs);
        var html = await RenderIndexViewAsync(services);
        var document = new HtmlParser().ParseDocument(html);

        var featuredLink = document.QuerySelector("a[href='/docs/Web/README.md.html']");

        Assert.NotNull(featuredLink);
        Assert.Equal("doc-content", featuredLink!.GetAttribute("data-turbo-frame"));
        Assert.Equal("advance", featuredLink.GetAttribute("data-turbo-action"));
    }

    private static ServiceProvider CreateServiceProvider(IReadOnlyList<DocNode> docs)
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var webRoot = Path.Combine(repoRoot, "Web", "ForgeTrust.AppSurface.Docs");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RepositoryRoot"] = repoRoot
                })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        var diagnosticListener = new DiagnosticListener(nameof(FeaturedLandingNavigationRegressionTests));
        services.AddSingleton<DiagnosticSource>(diagnosticListener);
        services.AddSingleton(diagnosticListener);
        services.AddSingleton<IWebHostEnvironment>(_ => new TestWebHostEnvironment(webRoot));
        services.AddSingleton<IConfiguration>(_ => configuration);
        services.AddMemoryCache();
        services.AddSingleton<IMemo, Memo>();
        services.AddRazorDocs();
        services.RemoveAll<IDocHarvester>();
        services.AddSingleton<IDocHarvester>(_ => new StaticDocHarvester(docs));
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(DocsController).Assembly);

        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderIndexViewAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scopedServices
        };
        httpContext.Response.Body = new MemoryStream();

        var controller = ActivatorUtilities.CreateInstance<DocsController>(scopedServices);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData(),
            ActionDescriptor = new ControllerActionDescriptor
            {
                ControllerName = "Docs",
                ActionName = "Index"
            }
        };

        var result = await controller.Index();
        var viewResult = Assert.IsType<ViewResult>(result);
        viewResult.ViewName ??= "/Views/Docs/Index.cshtml";

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

    private sealed class StaticDocHarvester : IDocHarvester
    {
        private readonly IReadOnlyList<DocNode> _docs;

        public StaticDocHarvester(IReadOnlyList<DocNode> docs)
        {
            _docs = docs;
        }

        public Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_docs);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment, IDisposable
    {
        public TestWebHostEnvironment(string contentRootPath)
        {
            ApplicationName = typeof(DocsController).Assembly.GetName().Name ?? "RazorDocsRegressionTests";
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
