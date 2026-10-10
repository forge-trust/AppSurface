using System.Diagnostics;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
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

public sealed class AppSurfaceDocsVersionArchiveUiTests
{
    [Fact]
    public async Task Versions_ShouldRenderEncodedEditorialAliasRowsInApprovedDomOrder()
    {
        using var services = CreateServiceProvider();
        var model = new AppSurfaceDocsVersionArchiveViewModel
        {
            Heading = "Documentation versions",
            Description = "Choose a release entry.",
            PreviewHref = "/docs/next",
            VersionsHref = "/docs/versions",
            Aliases =
            [
                new AppSurfaceDocsVersionAliasArchiveEntryViewModel
                {
                    Name = "stable",
                    Label = "<script>alert(1)</script>",
                    TargetVersion = "1.2.3<svg/onload=alert(2)>",
                    SupportStateLabel = "Maintained",
                    AdvisoryLabel = "Security risk",
                    Summary = "<img src=x onerror=alert(3)>",
                    Href = "/docs/a/stable",
                    IsAvailable = true
                },
                new AppSurfaceDocsVersionAliasArchiveEntryViewModel
                {
                    Name = "broken",
                    Label = string.Empty,
                    AvailabilityMessage = "This release entry has conflicting definitions."
                }
            ],
            Versions =
            [
                new AppSurfaceDocsVersionArchiveEntryViewModel
                {
                    Version = "1.2.3",
                    Label = "Release 1.2.3",
                    Href = "/docs/v/1.2.3",
                    IsAvailable = true,
                    IsRecommended = true,
                    SupportStateLabel = "Current"
                }
            ]
        };

        var html = await RenderViewAsync(services, model);
        var document = new HtmlParser().ParseDocument(html);

        Assert.Equal("Documentation versions", document.QuerySelector("h1")?.TextContent.Trim());
        Assert.Equal("Named release entries", document.QuerySelector("#docs-version-aliases-heading")?.TextContent.Trim());
        Assert.Contains(document.QuerySelectorAll("a"), anchor => anchor.TextContent.Contains("Open live source docs", StringComparison.Ordinal));

        var aliasRows = document.QuerySelectorAll(".docs-version-alias-row");
        Assert.Equal(2, aliasRows.Length);
        var healthyRow = aliasRows[0];
        Assert.Empty(healthyRow.QuerySelectorAll("script, img, svg"));
        Assert.Contains("<script>alert(1)</script>", healthyRow.QuerySelector(".docs-version-alias-row__label")?.TextContent, StringComparison.Ordinal);
        Assert.Equal("stable", healthyRow.QuerySelector(".docs-version-alias-row__name")?.TextContent.Trim());

        var details = Assert.Single(healthyRow.QuerySelectorAll(".docs-version-alias-row__details"));
        Assert.Collection(
            details.Children,
            target =>
            {
                Assert.Contains("1.2.3<svg/onload=alert(2)>", target.TextContent, StringComparison.Ordinal);
                Assert.Empty(target.QuerySelectorAll("svg"));
            },
            warning => Assert.Contains("Security risk", warning.TextContent, StringComparison.Ordinal),
            explanation =>
            {
                Assert.Contains("<img src=x onerror=alert(3)>", explanation.TextContent, StringComparison.Ordinal);
                Assert.Empty(explanation.QuerySelectorAll("img"));
            });

        var healthyAction = Assert.Single(healthyRow.QuerySelectorAll(".docs-version-alias-row__action a"));
        Assert.Equal("/docs/a/stable", healthyAction.GetAttribute("href"));
        Assert.Contains("1.2.3<svg/onload=alert(2)>", healthyAction.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Contains("1.2.3<svg/onload=alert(2)>", healthyAction.TextContent, StringComparison.Ordinal);

        var unavailableRow = aliasRows[1];
        Assert.Equal("broken", unavailableRow.QuerySelector(".docs-version-alias-row__label")?.TextContent.Trim());
        Assert.Contains("This release entry has conflicting definitions.", unavailableRow.TextContent, StringComparison.Ordinal);
        Assert.Empty(unavailableRow.QuerySelectorAll("a"));

        var exactSection = Assert.Single(document.QuerySelectorAll("#docs-exact-releases-heading"));
        Assert.Equal("Exact releases", exactSection.TextContent.Trim());
        Assert.Equal("Release 1.2.3", document.QuerySelector(".docs-version-card h2")?.TextContent.Trim());
        Assert.Equal("/docs/v/1.2.3", document.QuerySelector(".docs-version-card a")?.GetAttribute("href"));
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var repoRoot = FindRepoRoot();
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
        services.AddLogging();
        services.AddSingleton<DiagnosticListener>(_ => new DiagnosticListener("AppSurfaceDocsVersionArchiveUiTests"));
        services.AddSingleton<DiagnosticSource>(provider => provider.GetRequiredService<DiagnosticListener>());
        services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(repoRoot, webRoot));
        services.AddSingleton<IConfiguration>(_ => configuration);
        services.AddMemoryCache();
        services.AddSingleton<IMemo, Memo>();
        services.AddAppSurfaceDocs();
        services.AddSingleton(
            AppSurfaceDocsAssetPathResolver.CreateForRootModule(typeof(AppSurfaceDocsWebModule).Assembly));
        services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderViewAsync(
        ServiceProvider services,
        AppSurfaceDocsVersionArchiveViewModel model)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scopedServices
        };
        httpContext.Response.Body = new MemoryStream();

        var result = new ViewResult
        {
            ViewName = "/Views/Docs/Versions.cshtml",
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
            {
                Model = model
            }
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executor = scopedServices.GetRequiredService<IActionResultExecutor<ViewResult>>();
        await executor.ExecuteAsync(actionContext, result);

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the AppSurface repository root.");
    }

    private sealed class TestWebHostEnvironment(string contentRoot, string webRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = typeof(AppSurfaceDocsWebModule).Assembly.GetName().Name!;

        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(webRoot);

        public string WebRootPath { get; set; } = webRoot;

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRoot);
    }
}
