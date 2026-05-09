using FakeItEasy;
using ForgeTrust.Runnable.Caching;
using ForgeTrust.Runnable.Core;
using ForgeTrust.Runnable.Web.RazorDocs.Controllers;
using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using ForgeTrust.Runnable.Web.RazorWire;
using ForgeTrust.Runnable.Web.Tailwind;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public class RazorDocsWebModuleTests
{
    private readonly RazorDocsWebModule _module;

    public RazorDocsWebModuleTests()
    {
        _module = new RazorDocsWebModule();
    }

    [Fact]
    public void Properties_ShouldReturnDefaultValues()
    {
        Assert.True(_module.IncludeAsApplicationPart);
    }

    [Fact]
    public void RegisterDependentModules_ShouldAddRazorWireModule()
    {
        // Arrange
        var builder = new ModuleDependencyBuilder();

        // Act
        _module.RegisterDependentModules(builder);

        // Assert
        Assert.Contains(builder.Modules, m => m is RazorWireWebModule);
    }

    [Fact]
    public void RegisterDependentModules_ShouldAddCachingModule()
    {
        var builder = new ModuleDependencyBuilder();

        _module.RegisterDependentModules(builder);

        Assert.Contains(builder.Modules, m => m is RunnableCachingModule);
    }


    [Fact]
    public void ConfigureServices_ShouldRegisterRequiredServices()
    {
        // Arrange
        var rootModuleFake = A.Fake<IRunnableHostModule>();
        var envFake = A.Fake<IEnvironmentProvider>();
        var webHostEnvironment = A.Fake<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        A.CallTo(() => webHostEnvironment.ContentRootPath).Returns(Path.GetTempPath());
        var context = new StartupContext(Array.Empty<string>(), rootModuleFake, "TestApp", envFake);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(webHostEnvironment);
        services.AddLogging();

        // Act
        _module.ConfigureServices(context, services);

        // Assert
        Assert.Contains(
            services,
            s => s.ServiceType == typeof(IDocHarvester) && s.ImplementationType == typeof(MarkdownHarvester));
        Assert.Contains(
            services,
            s => s.ServiceType == typeof(IDocHarvester) && s.ImplementationType == typeof(CSharpDocHarvester));
        Assert.Contains(services, s => s.ServiceType == typeof(DocAggregator));
        Assert.Contains(
            services,
            s => s.ServiceType == typeof(IRazorDocsHtmlSanitizer) && s.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, s => s.ServiceType == typeof(RazorDocsAssetPathResolver));
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(TailwindCliManager));
        Assert.DoesNotContain(
            services,
            s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(TailwindWatchService));
        Assert.Contains(services, s => s.ServiceType == typeof(IMemoryCache));
        Assert.Contains(services, s => s.ServiceType == typeof(IMemo));

        using var serviceProvider = services.BuildServiceProvider();
        var sanitizer = Assert.IsType<RazorDocsHtmlSanitizer>(
            serviceProvider.GetRequiredService<IRazorDocsHtmlSanitizer>());
        var assetPathResolver = serviceProvider.GetRequiredService<RazorDocsAssetPathResolver>();
        Assert.NotNull(serviceProvider.GetService<IOptions<RazorDocsOptions>>());
        Assert.NotNull(serviceProvider.GetService<RazorDocsOptions>());
        Assert.NotNull(serviceProvider.GetRequiredService<IMemoryCache>());
        Assert.NotNull(serviceProvider.GetRequiredService<IMemo>());
        Assert.NotNull(serviceProvider.GetRequiredService<DocAggregator>());
        Assert.Contains(serviceProvider.GetServices<IDocHarvester>(), harvester => harvester is MarkdownHarvester);
        Assert.Equal(RazorDocsAssetPathResolver.PackagedStylesheetPath, assetPathResolver.StylesheetPath);
        Assert.Contains("section", sanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("article", sanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("header", sanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("details", sanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("summary", sanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("pre", sanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("code", sanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("span", sanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("class", sanitizer.InnerSanitizer.AllowedAttributes);
        Assert.Contains("id", sanitizer.InnerSanitizer.AllowedAttributes);
        Assert.Contains("open", sanitizer.InnerSanitizer.AllowedAttributes);
    }

    [Fact]
    public void ConfigureServices_ShouldUseDedicatedRazorDocsSanitizer_WhenAmbientSanitizerExists()
    {
        var rootModuleFake = A.Fake<IRunnableHostModule>();
        var envFake = A.Fake<IEnvironmentProvider>();
        var webHostEnvironment = A.Fake<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        var ambientSanitizer = A.Fake<Ganss.Xss.IHtmlSanitizer>();
        A.CallTo(() => webHostEnvironment.ContentRootPath).Returns(Path.GetTempPath());
        var context = new StartupContext(Array.Empty<string>(), rootModuleFake, "TestApp", envFake);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(webHostEnvironment);
        services.AddSingleton(ambientSanitizer);
        services.AddLogging();

        _module.ConfigureServices(context, services);

        using var serviceProvider = services.BuildServiceProvider();
        var razorDocsSanitizer = Assert.IsType<RazorDocsHtmlSanitizer>(
            serviceProvider.GetRequiredService<IRazorDocsHtmlSanitizer>());

        Assert.Same(ambientSanitizer, serviceProvider.GetRequiredService<Ganss.Xss.IHtmlSanitizer>());
        Assert.Contains("details", razorDocsSanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("summary", razorDocsSanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("open", razorDocsSanitizer.InnerSanitizer.AllowedAttributes);
    }

    [Fact]
    public void ConfigureServices_ShouldUseRootStylesheetPath_WhenRazorDocsIsTheRootModule()
    {
        var envFake = A.Fake<IEnvironmentProvider>();
        var webHostEnvironment = A.Fake<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        A.CallTo(() => webHostEnvironment.ContentRootPath).Returns(Path.GetTempPath());
        var context = new StartupContext(Array.Empty<string>(), _module, "CustomDocsHost", envFake);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(webHostEnvironment);
        services.AddLogging();

        _module.ConfigureServices(context, services);

        using var serviceProvider = services.BuildServiceProvider();
        var assetPathResolver = serviceProvider.GetRequiredService<RazorDocsAssetPathResolver>();

        Assert.Equal(RazorDocsAssetPathResolver.RootStylesheetPath, assetPathResolver.StylesheetPath);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldMapDefaultRoute()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var routePatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => !string.IsNullOrEmpty(pattern))
            .ToList();

        Assert.Contains("docs", routePatterns);
        Assert.Contains("docs/search", routePatterns);
        Assert.Contains("docs/search-index.json", routePatterns);
        Assert.Contains("docs/{*path}", routePatterns);
        Assert.Contains("{controller=Docs}/{action=Index}/{path?}", routePatterns);

        var prioritizedPatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .OrderBy(endpoint => endpoint.Order)
            .ThenBy(endpoint => endpoint.RoutePattern.InboundPrecedence)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => !string.IsNullOrEmpty(pattern))
            .ToList();

        var searchIndex = prioritizedPatterns.IndexOf("docs/search");
        var catchAllIndex = prioritizedPatterns.IndexOf("docs/{*path}");
        Assert.True(searchIndex >= 0, "Expected docs/search route declaration.");
        Assert.True(catchAllIndex >= 0, "Expected docs/{*path} route declaration.");
        Assert.True(searchIndex < catchAllIndex, "docs/search must be prioritized before docs/{*path}.");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldMapVersionedRoutes_WhenVersioningIsEnabled()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = "catalog.json"
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<RazorDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var routePatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => !string.IsNullOrEmpty(pattern))
            .ToList();

        Assert.Contains("docs", routePatterns);
        Assert.Contains("docs/versions", routePatterns);
        Assert.Contains("docs/next", routePatterns);
        Assert.Contains("docs/next/search", routePatterns);
        Assert.Contains("docs/next/search-index.json", routePatterns);
        Assert.Contains("docs/next/{*path}", routePatterns);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldTrimLeadingSlash_ForRootMountedSectionAndDetailsRoutes()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<RazorDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var routePatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern is not null)
            .ToList();

        Assert.Contains("search", routePatterns);
        Assert.Contains("search-index.json", routePatterns);
        Assert.Contains("sections/{sectionSlug}", routePatterns);
        Assert.Contains("{*path}", routePatterns);
        Assert.DoesNotContain("/sections/{sectionSlug}", routePatterns);
        Assert.DoesNotContain("/{*path}", routePatterns);
    }

    [Fact]
    public void ConfigureWebApplication_ShouldReturn_WhenVersioningIsEnabledButCatalogServiceIsMissing()
    {
        var context = CreateStartupContext();
        var services = new ServiceCollection();
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = "catalog.json"
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<RazorDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        services.AddSingleton(optionsMonitor);
        var recordingBuilder = new RecordingApplicationBuilder(services.BuildServiceProvider());

        _module.ConfigureWebApplication(context, recordingBuilder);

        Assert.Equal(0, recordingBuilder.UseCallCount);
    }

    [Fact]
    public async Task ConfigureWebApplication_ShouldFallbackToConstructedDocsUrlBuilder_WhenServiceIsMissing()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "razordocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var treePath = Path.Combine(tempDirectory, "1.2.3");
            Directory.CreateDirectory(treePath);
            File.WriteAllText(Path.Combine(treePath, "index.html"), "<html>ok</html>");
            Directory.CreateDirectory(Path.Combine(treePath, "next"));
            File.WriteAllText(Path.Combine(treePath, "next", "index.html"), "<html>published-collision</html>");
            File.WriteAllText(Path.Combine(treePath, "search.html"), "<html>search</html>");
            File.WriteAllText(Path.Combine(treePath, "search-index.json"), "{\"documents\":[]}");
            File.WriteAllText(Path.Combine(treePath, "search.css"), "body { color: #fff; }");
            File.WriteAllText(Path.Combine(treePath, "search-client.js"), "window.__searchClientLoaded = true;");
            File.WriteAllText(Path.Combine(treePath, "outline-client.js"), "window.__outlineClientLoaded = true;");
            File.WriteAllText(Path.Combine(treePath, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");

            var catalogPath = Path.Combine(tempDirectory, "catalog.json");
            File.WriteAllText(
                catalogPath,
                """
                {
                  "recommendedVersion": "1.2.3",
                  "versions": [
                    {
                      "version": "1.2.3",
                      "exactTreePath": "1.2.3",
                      "supportState": "Current",
                      "visibility": "Public",
                      "advisoryState": "None"
                    }
                  ]
                }
                """);

            var context = CreateStartupContext();
            var services = new ServiceCollection();
            var options = new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/docs/next"
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = catalogPath
                }
            };
            var optionsMonitor = A.Fake<IOptionsMonitor<RazorDocsOptions>>();
            A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
            services.AddSingleton(optionsMonitor);
            services.AddSingleton(
                new RazorDocsVersionCatalogService(
                    options,
                    new TestWebHostEnvironment { ContentRootPath = tempDirectory, WebRootPath = tempDirectory },
                    NullLogger<RazorDocsVersionCatalogService>.Instance));
            var recordingBuilder = new RecordingApplicationBuilder(services.BuildServiceProvider());

            _module.ConfigureWebApplication(context, recordingBuilder);

            Assert.Equal(1, recordingBuilder.UseCallCount);

            var pipeline = recordingBuilder.Build(
                async httpContext =>
                {
                    httpContext.Response.StatusCode = StatusCodes.Status200OK;
                    await httpContext.Response.WriteAsync("<html>preview-surface</html>");
                });
            var httpContext = new DefaultHttpContext
            {
                RequestServices = recordingBuilder.ApplicationServices
            };
            httpContext.Request.Path = "/docs/next";
            httpContext.Response.Body = new MemoryStream();

            await pipeline(httpContext);

            httpContext.Response.Body.Position = 0;
            using var reader = new StreamReader(httpContext.Response.Body);
            var responseBody = reader.ReadToEnd();
            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal("<html>preview-surface</html>", responseBody);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ConfigureWebApplication_ShouldSkipRecommendedMount_WhenCatalogMarksReleaseUnavailable()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "razordocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var treePath = Path.Combine(tempDirectory, "1.2.3");
            Directory.CreateDirectory(treePath);
            File.WriteAllText(Path.Combine(treePath, "index.html"), "<html>broken</html>");

            var catalogPath = Path.Combine(tempDirectory, "catalog.json");
            File.WriteAllText(
                catalogPath,
                """
                {
                  "recommendedVersion": "1.2.3",
                  "versions": [
                    {
                      "version": "1.2.3",
                      "exactTreePath": "1.2.3",
                      "supportState": "Current",
                      "visibility": "Public",
                      "advisoryState": "None"
                    }
                  ]
                }
                """);

            var context = CreateStartupContext();
            var services = new ServiceCollection();
            var options = new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/docs/next"
                },
                Versioning = new RazorDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = catalogPath
                }
            };
            services.AddSingleton(options);
            services.AddSingleton(
                new RazorDocsVersionCatalogService(
                    options,
                    new TestWebHostEnvironment { ContentRootPath = tempDirectory, WebRootPath = tempDirectory },
                    NullLogger<RazorDocsVersionCatalogService>.Instance));
            var recordingBuilder = new RecordingApplicationBuilder(services.BuildServiceProvider());

            _module.ConfigureWebApplication(context, recordingBuilder);

            Assert.Equal(0, recordingBuilder.UseCallCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildPublishedTreeMounts_ShouldReuseProvider_ForRecommendedAliasOfPublicVersion()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "razordocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var exactTreePath = Path.Combine(tempDirectory, "1.2.3");
            Directory.CreateDirectory(exactTreePath);
            var version = new RazorDocsResolvedVersion(
                Version: "1.2.3",
                Label: "1.2.3",
                Summary: null,
                ExactTreePath: exactTreePath,
                ExactRootUrl: "/docs/v/1.2.3",
                SupportState: RazorDocsVersionSupportState.Current,
                Visibility: RazorDocsVersionVisibility.Public,
                AdvisoryState: RazorDocsVersionAdvisoryState.None,
                IsAvailable: true,
                AvailabilityIssue: null);
            var catalog = new RazorDocsResolvedVersionCatalog(
                RazorDocsResolvedVersionCatalogStatus.Resolved,
                CatalogPath: Path.Combine(tempDirectory, "catalog.json"),
                Versions: [version],
                RecommendedVersion: version);

            var docsUrlBuilder = new DocsUrlBuilder(
                new RazorDocsOptions
                {
                    Routing = new RazorDocsRoutingOptions
                    {
                        RouteRootPath = "/docs",
                        DocsRootPath = "/docs/next"
                    },
                    Versioning = new RazorDocsVersioningOptions
                    {
                        Enabled = true,
                        CatalogPath = "catalog.json"
                    }
                });
            var (mounts, providers) = RazorDocsWebModule.BuildPublishedTreeMounts(catalog, docsUrlBuilder);

            Assert.Equal(2, mounts.Count);
            Assert.Single(providers);
            Assert.Equal("/docs/v/1.2.3", mounts[0].MountRootPath);
            Assert.Equal("/docs", mounts[1].MountRootPath);
            Assert.Same(mounts[0].FileProvider, mounts[1].FileProvider);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildPublishedTreeMounts_ShouldUseConfiguredRouteRoot_ForRecommendedAlias()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "razordocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var exactTreePath = Path.Combine(tempDirectory, "1.2.3");
            Directory.CreateDirectory(exactTreePath);
            var version = new RazorDocsResolvedVersion(
                Version: "1.2.3",
                Label: "1.2.3",
                Summary: null,
                ExactTreePath: exactTreePath,
                ExactRootUrl: "/foo/bar/v/1.2.3",
                SupportState: RazorDocsVersionSupportState.Current,
                Visibility: RazorDocsVersionVisibility.Public,
                AdvisoryState: RazorDocsVersionAdvisoryState.None,
                IsAvailable: true,
                AvailabilityIssue: null);
            var catalog = new RazorDocsResolvedVersionCatalog(
                RazorDocsResolvedVersionCatalogStatus.Resolved,
                CatalogPath: Path.Combine(tempDirectory, "catalog.json"),
                Versions: [version],
                RecommendedVersion: version);
            var docsUrlBuilder = new DocsUrlBuilder(
                new RazorDocsOptions
                {
                    Routing = new RazorDocsRoutingOptions
                    {
                        RouteRootPath = "/foo/bar",
                        DocsRootPath = "/foo/bar/next"
                    },
                    Versioning = new RazorDocsVersioningOptions
                    {
                        Enabled = true,
                        CatalogPath = "catalog.json"
                    }
                });

            var (mounts, providers) = RazorDocsWebModule.BuildPublishedTreeMounts(catalog, docsUrlBuilder);

            Assert.Equal(2, mounts.Count);
            Assert.Single(providers);
            Assert.Equal("/foo/bar/v/1.2.3", mounts[0].MountRootPath);
            Assert.Equal("/foo/bar", mounts[1].MountRootPath);
            Assert.Same(mounts[0].FileProvider, mounts[1].FileProvider);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ConfigureWebApplication_ShouldReturn_WhenVersioningOptionsAreMissing()
    {
        var context = CreateStartupContext();
        var services = new ServiceCollection();
        services.AddSingleton(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/docs"
                },
                Versioning = null!
            });
        var recordingBuilder = new RecordingApplicationBuilder(services.BuildServiceProvider());

        _module.ConfigureWebApplication(context, recordingBuilder);

        Assert.Equal(0, recordingBuilder.UseCallCount);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldTreatNullVersioningOptionsAsDisabled()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Versioning = null!
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<RazorDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var routePatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => !string.IsNullOrEmpty(pattern))
            .ToList();

        Assert.DoesNotContain("docs/versions", routePatterns);
    }

    [Fact]
    public void HostAndAppConfigureMethods_ShouldNotThrow()
    {
        var context = CreateStartupContext();
        var hostBuilder = A.Fake<IHostBuilder>();
        var appBuilder = A.Fake<Microsoft.AspNetCore.Builder.IApplicationBuilder>();

        _module.ConfigureHostBeforeServices(context, hostBuilder);
        _module.ConfigureHostAfterServices(context, hostBuilder);
        _module.ConfigureWebApplication(context, appBuilder);

        Assert.True(true);
    }

    private static StartupContext CreateStartupContext()
    {
        var rootModuleFake = A.Fake<IRunnableHostModule>();
        var envFake = A.Fake<IEnvironmentProvider>();
        return new StartupContext(Array.Empty<string>(), rootModuleFake, "TestApp", envFake);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "RazorDocsTests";

        public IFileProvider WebRootFileProvider { get; set; } = null!;

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class RecordingApplicationBuilder : IApplicationBuilder
    {
        private readonly IList<Func<RequestDelegate, RequestDelegate>> _components = [];

        public RecordingApplicationBuilder(IServiceProvider applicationServices)
        {
            ApplicationServices = applicationServices;
        }

        public int UseCallCount => _components.Count;

        public IServiceProvider ApplicationServices { get; set; }

        public IFeatureCollection ServerFeatures { get; } = new FeatureCollection();

        public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>();

        public RequestDelegate Build()
        {
            return Build(_ => Task.CompletedTask);
        }

        public RequestDelegate Build(RequestDelegate terminal)
        {
            RequestDelegate app = terminal;
            foreach (var component in _components.Reverse())
            {
                app = component(app);
            }

            return app;
        }

        public IApplicationBuilder New()
        {
            return new RecordingApplicationBuilder(ApplicationServices);
        }

        public IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
        {
            ArgumentNullException.ThrowIfNull(middleware);
            _components.Add(middleware);
            return this;
        }
    }
}
