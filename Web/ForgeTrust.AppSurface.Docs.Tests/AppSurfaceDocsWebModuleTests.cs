using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Docs.Standalone;
using ForgeTrust.AppSurface.Web.Tailwind;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs.Tests;

public class AppSurfaceDocsWebModuleTests
{
    private static readonly string HarvestProgressStreamPath =
        $"/_rw/streams/{AppSurfaceDocsStreamAuthorization.HarvestProgressChannel}?replay=1";

    private readonly AppSurfaceDocsWebModule _module;

    public AppSurfaceDocsWebModuleTests()
    {
        _module = new AppSurfaceDocsWebModule();
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

        Assert.Contains(builder.Modules, m => m is AppSurfaceCachingModule);
    }


    [Fact]
    public void ConfigureServices_ShouldRegisterRequiredServices()
    {
        // Arrange
        var rootModuleFake = A.Fake<IAppSurfaceHostModule>();
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
            s => s.ServiceType == typeof(IDocHarvester) && s.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, s => s.ServiceType == typeof(DocAggregator));
        Assert.Contains(services, s => s.ServiceType == typeof(AppSurfaceDocsHarvestProgressReporter));
        Assert.Contains(services, s => s.ServiceType == typeof(AppSurfaceDocsHarvestCoordinator));
        Assert.Contains(
            services,
            s => s.ServiceType == typeof(IRazorWireChannelAuthorizer)
                 && s.ImplementationFactory is not null);
        Assert.Contains(
            services,
            s => s.ServiceType == typeof(IRazorWireStreamAuthorizer)
                 && s.ImplementationFactory is not null);
        Assert.Contains(
            services,
            s => s.ServiceType == typeof(IRazorWireStreamAuthorizationFilter)
                 && s.ImplementationType == typeof(AppSurfaceDocsHarvestStreamAuthorizationFilter));
        Assert.Contains(services, s => s.ServiceType == typeof(AppSurfaceDocsHarvestPathPolicy));
        Assert.Contains(
            services,
            s => s.ServiceType == typeof(IAppSurfaceDocsHtmlSanitizer) && s.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, s => s.ServiceType == typeof(AppSurfaceDocsAssetPathResolver));
        Assert.Contains(
            services,
            s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(AppSurfaceDocsHarvestFailurePreflightService));
        Assert.Contains(
            services,
            s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(AppSurfaceDocsOperatorReadPolicyWarningService));
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(TailwindCliManager));
        Assert.DoesNotContain(
            services,
            s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(TailwindWatchService));
        Assert.Contains(services, s => s.ServiceType == typeof(IMemoryCache));
        Assert.Contains(services, s => s.ServiceType == typeof(IMemo));

        using var serviceProvider = services.BuildServiceProvider();
        var sanitizer = Assert.IsType<AppSurfaceDocsHtmlSanitizer>(
            serviceProvider.GetRequiredService<IAppSurfaceDocsHtmlSanitizer>());
        var assetPathResolver = serviceProvider.GetRequiredService<AppSurfaceDocsAssetPathResolver>();
        Assert.NotNull(serviceProvider.GetService<IOptions<AppSurfaceDocsOptions>>());
        Assert.NotNull(serviceProvider.GetService<AppSurfaceDocsOptions>());
        Assert.NotNull(serviceProvider.GetRequiredService<IMemoryCache>());
        Assert.NotNull(serviceProvider.GetRequiredService<IMemo>());
        Assert.NotNull(serviceProvider.GetRequiredService<RazorWireOptions>());
        Assert.NotNull(serviceProvider.GetRequiredService<IRazorWireStreamHub>());
        Assert.NotNull(serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>());
        Assert.NotNull(serviceProvider.GetRequiredService<IRazorWireStreamAuthorizer>());
        Assert.NotNull(serviceProvider.GetRequiredService<DocAggregator>());
        Assert.NotNull(serviceProvider.GetRequiredService<AppSurfaceDocsHarvestProgressReporter>());
        Assert.NotNull(serviceProvider.GetRequiredService<AppSurfaceDocsHarvestCoordinator>());
        Assert.NotNull(serviceProvider.GetRequiredService<AppSurfaceDocsHarvestPathPolicy>());
        Assert.Contains(serviceProvider.GetServices<IDocHarvester>(), harvester => harvester is MarkdownHarvester);
        Assert.Contains(serviceProvider.GetServices<IDocHarvester>(), harvester => harvester is JavaScriptDocHarvester);
        Assert.Contains(serviceProvider.GetServices<IDocHarvester>(), harvester => harvester is CSharpDocHarvester);
        Assert.Equal(AppSurfaceDocsAssetPathResolver.PackagedStylesheetPath, assetPathResolver.StylesheetPath);
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
    public async Task AddAppSurfaceDocs_ShouldWireConfiguredMarkdownResourceLimitsIntoBuiltInHarvester()
    {
        var root = Directory.CreateTempSubdirectory("appsurface-docs-di-markdown-limit-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "Small.md"), "# Ok");
            await File.WriteAllTextAsync(Path.Join(root, "Large.md"), "# Large\nThis file exceeds the configured test limit.");
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSurfaceDocs:Harvest:Markdown:MaxFileSizeBytes"] = "8"
                        })
                    .Build());
            services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment { ContentRootPath = root });
            services.AddSingleton<IHostEnvironment>(new TestWebHostEnvironment { ContentRootPath = root });
            services.AddLogging();
            services.AddAppSurfaceDocs();

            await using var serviceProvider = services.BuildServiceProvider();
            var markdownHarvester = serviceProvider.GetServices<IDocHarvester>().OfType<MarkdownHarvester>().Single();

            var docs = await markdownHarvester.HarvestAsync(root);
            var diagnostics = Assert.IsAssignableFrom<IDocHarvesterDiagnosticProvider>(markdownHarvester).GetHarvestDiagnostics();

            var doc = Assert.Single(docs);
            Assert.Equal("Small.md", doc.Path);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(DocHarvestDiagnosticCodes.MarkdownFileTooLarge, diagnostic.Code);
            Assert.Contains("8 bytes", diagnostic.Problem, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenProductionHostHasNoCustomChannelAuthorizer_DeniesHarvestChannel()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenRazorWireWasAlreadyRegisteredWithBuiltInDenyAll_DeniesHarvestChannel()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddLogging();
        services.AddRazorWire();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenBuiltInDenyAllAuthorizerWasRegisteredByType_DeniesHarvestChannel()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer, DenyAllRazorWireChannelAuthorizer>();
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenBuiltInDenyAllAuthorizerWasRegisteredByInstance_DeniesHarvestChannel()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer>(new DenyAllRazorWireChannelAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenBuiltInDenyAllAuthorizerWasRegisteredByFactory_DeniesHarvestChannel()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer>(_ => new DenyAllRazorWireChannelAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenRazorWireAllowAllModeWasAlreadyRegistered_DeniesHarvestAndDelegatesHostChannels()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddLogging();
        services.AddRazorWire(options => options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll);

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.True(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenBuiltInAllowAllAuthorizerWasRegisteredByType_DeniesHarvestAndDelegatesHostChannels()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer, AllowAllRazorWireChannelAuthorizer>();
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.True(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenBuiltInAllowAllAuthorizerWasRegisteredByInstance_DeniesHarvestAndDelegatesHostChannels()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer>(new AllowAllRazorWireChannelAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.True(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenBuiltInAllowAllAuthorizerWasRegisteredByFactory_DeniesHarvestAndDelegatesHostChannels()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer>(_ => new AllowAllRazorWireChannelAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.True(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenHostHasCustomChannelAuthorizer_StillEnforcesHarvestVisibility()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Never"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer, AllowAllChannelAuthorizer>();
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();

        Assert.False(await authorizer.CanSubscribeAsync(
            new DefaultHttpContext { RequestServices = serviceProvider },
            AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.True(await authorizer.CanSubscribeAsync(
            new DefaultHttpContext { RequestServices = serviceProvider },
            "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenHostChannelAuthorizerIsInstance_DelegatesHostChannels()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer>(new DenyAllChannelAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();

        Assert.False(await authorizer.CanSubscribeAsync(
            new DefaultHttpContext { RequestServices = serviceProvider },
            "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenHostChannelAuthorizerFactoryReturnsNull_DeniesProductionHarvestChannel()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer>(_ => null!);
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenHostChannelAuthorizerIsKeyed_DoesNotUseItAsUnkeyedInnerAuthorizer()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddKeyedSingleton<IRazorWireChannelAuthorizer>("host", new AllowAllChannelAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.False(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.False(await authorizer.CanSubscribeAsync(context, "host-channel"));
        Assert.IsType<AllowAllChannelAuthorizer>(
            serviceProvider.GetRequiredKeyedService<IRazorWireChannelAuthorizer>("host"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenHostStreamAuthorizerIsKeyed_DoesNotUseItAsUnkeyedInnerAuthorizer()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddKeyedSingleton<IRazorWireStreamAuthorizer>("host", new AllowAllStreamAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.Equal(
            AppSurfaceAuthOutcome.Forbid,
            (await authorizer.AuthorizeAsync(new RazorWireStreamAuthorizationContext(
                context,
                AppSurfaceDocsStreamAuthorization.HarvestProgressChannel,
                RazorWireStreamAuthorizationMode.DenyAll))).Outcome);
        Assert.Equal(
            AppSurfaceAuthOutcome.Forbid,
            (await authorizer.AuthorizeAsync(new RazorWireStreamAuthorizationContext(
                context,
                "host-channel",
                RazorWireStreamAuthorizationMode.DenyAll))).Outcome);
        Assert.IsType<AllowAllStreamAuthorizer>(
            serviceProvider.GetRequiredKeyedService<IRazorWireStreamAuthorizer>("host"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenCustomAuthorizerIsRegisteredBeforeDocs_AllowsVisibleHarvestChannel()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer, AllowAllChannelAuthorizer>();
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.True(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.True(await authorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenCustomAuthorizerIsRegisteredAfterDocs_DoesNotBypassHiddenHarvestStream()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Never"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddLogging();

        services.AddAppSurfaceDocs();
        services.AddSingleton<IRazorWireChannelAuthorizer, AllowAllChannelAuthorizer>();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var streamAuthorizer = serviceProvider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.IsType<AllowAllChannelAuthorizer>(authorizer);
        Assert.True(await authorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.Equal(AppSurfaceAuthOutcome.Forbid, (await streamAuthorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                AppSurfaceDocsStreamAuthorization.HarvestProgressChannel,
                RazorWireStreamAuthorizationMode.DenyAll))).Outcome);
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenCustomResultAuthorizerIsRegisteredBeforeDocs_AllowsVisibleHarvestChannel()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireStreamAuthorizer, AllowAllStreamAuthorizer>();
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var streamAuthorizer = serviceProvider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var channelAuthorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.True((await streamAuthorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                AppSurfaceDocsStreamAuthorization.HarvestProgressChannel,
                RazorWireStreamAuthorizationMode.DenyAll))).IsAllowed);
        Assert.True(await channelAuthorizer.CanSubscribeAsync(context, AppSurfaceDocsStreamAuthorization.HarvestProgressChannel));
        Assert.True(await channelAuthorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenCustomResultAuthorizerInstanceIsRegisteredBeforeDocs_AllowsVisibleHarvestChannel()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireStreamAuthorizer>(new AllowAllStreamAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var streamAuthorizer = serviceProvider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.True((await streamAuthorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                AppSurfaceDocsStreamAuthorization.HarvestProgressChannel,
                RazorWireStreamAuthorizationMode.DenyAll))).IsAllowed);
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenCustomResultAuthorizerFactoryIsRegisteredBeforeDocs_DelegatesToFactoryResult()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireStreamAuthorizer>(_ => new ForbiddenStreamAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var streamAuthorizer = serviceProvider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        var result = await streamAuthorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                AppSurfaceDocsStreamAuthorization.HarvestProgressChannel,
                RazorWireStreamAuthorizationMode.DenyAll));

        Assert.Equal(AppSurfaceAuthOutcome.Forbid, result.Outcome);
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenCustomResultAuthorizerIsRegisteredBeforeDocs_DoesNotResolveLegacyBoolAuthorizer()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                    })
                .Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireStreamAuthorizer, AllowAllStreamAuthorizer>();
        services.AddSingleton<IRazorWireChannelAuthorizer>(
            _ => throw new InvalidOperationException("The legacy bool authorizer should not be resolved."));
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var streamAuthorizer = serviceProvider.GetRequiredService<IRazorWireStreamAuthorizer>();
        var channelAuthorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        Assert.True((await streamAuthorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                AppSurfaceDocsStreamAuthorization.HarvestProgressChannel,
                RazorWireStreamAuthorizationMode.DenyAll))).IsAllowed);
        Assert.True(await channelAuthorizer.CanSubscribeAsync(context, "host-channel"));
    }

    [Fact]
    public async Task HarvestProgressStreamEndpoint_WhenPostDocsResultAuthorizerReplacesWrapper_DoesNotBypassHiddenRoutes()
    {
        await using var fixture = await AppSurfaceDocsRazorWireFixture.StartAsync(
            Environments.Production,
            configureServices: services =>
            {
                services.AddSingleton<IConfiguration>(
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Never"
                            })
                        .Build());
            },
            configureServicesAfterDocs: services =>
                services.AddSingleton<IRazorWireStreamAuthorizer, AllowAllStreamAuthorizer>());

        using var response = await fixture.Client.GetAsync(HarvestProgressStreamPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task HarvestProgressStreamEndpoint_WhenPostDocsResultAuthorizerReplacesWrapper_DoesNotBypassOperatorReadPolicy()
    {
        await using var fixture = await AppSurfaceDocsRazorWireFixture.StartAsync(
            Environments.Production,
            configureServices: services =>
            {
                services.AddSingleton<IConfiguration>(
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always",
                                ["AppSurfaceDocs:Diagnostics:OperatorReadPolicy"] = "DocsRead"
                            })
                        .Build());
                AddDocsReadPolicy(services);
            },
            configureServicesAfterDocs: services =>
                services.AddSingleton<IRazorWireStreamAuthorizer, AllowAllStreamAuthorizer>());

        using var anonymous = await fixture.Client.GetAsync(HarvestProgressStreamPath);
        using var forbiddenRequest = CreateDocsReadRequest("docs.other");
        using var forbidden = await fixture.Client.SendAsync(forbiddenRequest);
        using var authorizedRequest = CreateDocsReadRequest("docs.read");
        using var authorized = await fixture.Client.SendAsync(
            authorizedRequest,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal("text/event-stream", authorized.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task HarvestProgressStreamEndpoint_WhenProductionRoutesExposedWithoutCustomAuthorizer_ReturnsForbidden()
    {
        await using var fixture = await AppSurfaceDocsRazorWireFixture.StartAsync(
            Environments.Production,
            configureServices: services =>
            {
                services.AddSingleton<IConfiguration>(
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                            })
                        .Build());
            });

        using var response = await fixture.Client.GetAsync(HarvestProgressStreamPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task HarvestProgressStreamEndpoint_WhenProductionCustomAuthorizerAllows_ReturnsSseHeaders()
    {
        await using var fixture = await AppSurfaceDocsRazorWireFixture.StartAsync(
            Environments.Production,
            configureServices: services =>
            {
                services.AddSingleton<IConfiguration>(
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSurfaceDocs:Harvest:Health:ExposeRoutes"] = "Always"
                            })
                        .Build());
                services.AddSingleton<IRazorWireChannelAuthorizer, HarvestProgressAllowAuthorizer>();
            });

        using var response = await fixture.Client.GetAsync(
            HarvestProgressStreamPath,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AddAppSurfaceDocs_WhenHostChannelAuthorizerIsFactory_DelegatesHostChannels()
    {
        var environment = new TestWebHostEnvironment { EnvironmentName = Environments.Production };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IRazorWireChannelAuthorizer>(_ => new DenyAllChannelAuthorizer());
        services.AddLogging();

        services.AddAppSurfaceDocs();

        await using var serviceProvider = services.BuildServiceProvider();
        var authorizer = serviceProvider.GetRequiredService<IRazorWireChannelAuthorizer>();

        Assert.False(await authorizer.CanSubscribeAsync(
            new DefaultHttpContext { RequestServices = serviceProvider },
            "host-channel"));
    }

    [Fact]
    public void AddAppSurfaceDocs_WhenCalledTwiceShouldNotRegisterDuplicateDefaultHarvesters()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddAppSurfaceDocs();
        services.AddAppSurfaceDocs();

        Assert.Equal(3, services.Count(service => service.ServiceType == typeof(IDocHarvester)));
    }

    [Fact]
    public void ConfigureServices_ShouldUseDedicatedAppSurfaceDocsSanitizer_WhenAmbientSanitizerExists()
    {
        var rootModuleFake = A.Fake<IAppSurfaceHostModule>();
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
        var razorDocsSanitizer = Assert.IsType<AppSurfaceDocsHtmlSanitizer>(
            serviceProvider.GetRequiredService<IAppSurfaceDocsHtmlSanitizer>());

        Assert.Same(ambientSanitizer, serviceProvider.GetRequiredService<Ganss.Xss.IHtmlSanitizer>());
        Assert.Contains("details", razorDocsSanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("summary", razorDocsSanitizer.InnerSanitizer.AllowedTags);
        Assert.Contains("open", razorDocsSanitizer.InnerSanitizer.AllowedAttributes);
    }

    [Fact]
    public void ConfigureServices_ShouldUseRootStylesheetPath_WhenAppSurfaceDocsIsTheRootModule()
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
        var assetPathResolver = serviceProvider.GetRequiredService<AppSurfaceDocsAssetPathResolver>();

        Assert.Equal(AppSurfaceDocsAssetPathResolver.RootStylesheetPath, assetPathResolver.StylesheetPath);
    }

    [Fact]
    public void AssetPathResolver_ShouldUseRootStylesheetPath_WhenAssemblyMarksDocsRootHost()
    {
        var resolver = AppSurfaceDocsAssetPathResolver.CreateForRootModule(
            typeof(AppSurfaceDocsStandaloneHost).Assembly);

        Assert.True(AppSurfaceDocsAssetPathResolver.IsRootModuleAssembly(typeof(AppSurfaceDocsStandaloneHost).Assembly));
        Assert.Equal(AppSurfaceDocsAssetPathResolver.RootStylesheetPath, resolver.StylesheetPath);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldMapDefaultDocsRoutesWithoutAppWideFallback()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
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
        Assert.Contains("docs/_search-index/refresh", routePatterns);
        Assert.Contains("docs/_harvest", routePatterns);
        Assert.Contains("docs/_harvest/rebuild", routePatterns);
        Assert.Contains("docs/_health", routePatterns);
        Assert.Contains("docs/_health.json", routePatterns);
        Assert.Contains("docs/_routes", routePatterns);
        Assert.Contains("docs/_routes.json", routePatterns);
        Assert.Contains("docs/{*path}", routePatterns);
        Assert.DoesNotContain("docs/_metrics/collect", routePatterns);
        Assert.DoesNotContain("docs/_search-quality", routePatterns);
        Assert.DoesNotContain("{controller=Docs}/{action=Index}/{path?}", routePatterns);

        var prioritizedPatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .OrderBy(endpoint => endpoint.Order)
            .ThenBy(endpoint => endpoint.RoutePattern.InboundPrecedence)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => !string.IsNullOrEmpty(pattern))
            .ToList();

        var searchIndex = prioritizedPatterns.IndexOf("docs/search");
        var searchIndexRefresh = prioritizedPatterns.IndexOf("docs/_search-index/refresh");
        var harvestIndex = prioritizedPatterns.IndexOf("docs/_harvest");
        var harvestRebuildIndex = prioritizedPatterns.IndexOf("docs/_harvest/rebuild");
        var healthIndex = prioritizedPatterns.IndexOf("docs/_health");
        var healthJsonIndex = prioritizedPatterns.IndexOf("docs/_health.json");
        var routeInspectorIndex = prioritizedPatterns.IndexOf("docs/_routes");
        var routeInspectorJsonIndex = prioritizedPatterns.IndexOf("docs/_routes.json");
        var catchAllIndex = prioritizedPatterns.IndexOf("docs/{*path}");
        Assert.True(searchIndex >= 0, "Expected docs/search route declaration.");
        Assert.True(searchIndexRefresh >= 0, "Expected docs/_search-index/refresh route declaration.");
        Assert.True(harvestIndex >= 0, "Expected docs/_harvest route declaration.");
        Assert.True(harvestRebuildIndex >= 0, "Expected docs/_harvest/rebuild route declaration.");
        Assert.True(healthIndex >= 0, "Expected docs/_health route declaration.");
        Assert.True(healthJsonIndex >= 0, "Expected docs/_health.json route declaration.");
        Assert.True(routeInspectorIndex >= 0, "Expected docs/_routes route declaration.");
        Assert.True(routeInspectorJsonIndex >= 0, "Expected docs/_routes.json route declaration.");
        Assert.True(catchAllIndex >= 0, "Expected docs/{*path} route declaration.");
        Assert.True(searchIndex < catchAllIndex, "docs/search must be prioritized before docs/{*path}.");
        Assert.True(searchIndexRefresh < catchAllIndex, "docs/_search-index/refresh must be prioritized before docs/{*path}.");
        Assert.True(harvestIndex < catchAllIndex, "docs/_harvest must be prioritized before docs/{*path}.");
        Assert.True(harvestRebuildIndex < catchAllIndex, "docs/_harvest/rebuild must be prioritized before docs/{*path}.");
        Assert.True(healthIndex < catchAllIndex, "docs/_health must be prioritized before docs/{*path}.");
        Assert.True(healthJsonIndex < catchAllIndex, "docs/_health.json must be prioritized before docs/{*path}.");
        Assert.True(routeInspectorIndex < catchAllIndex, "docs/_routes must be prioritized before docs/{*path}.");
        Assert.True(routeInspectorJsonIndex < catchAllIndex, "docs/_routes.json must be prioritized before docs/{*path}.");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldMapHarvestRebuildWithPostOnlyOperatorRoute()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var rebuildEndpoints = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.TrimStart('/') == "docs/_harvest/rebuild")
            .ToList();

        Assert.NotEmpty(rebuildEndpoints);
        Assert.Contains(
            rebuildEndpoints,
            endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                return methods is not null
                    && methods.Count == 1
                    && methods.Contains(HttpMethods.Post);
            });

        Assert.Contains(
            rebuildEndpoints,
            endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                return methods is not null
                    && methods.Contains(HttpMethods.Get)
                    && methods.Contains(HttpMethods.Head)
                    && methods.Contains(HttpMethods.Put)
                    && methods.Contains(HttpMethods.Patch)
                    && methods.Contains(HttpMethods.Delete)
                    && methods.Contains(HttpMethods.Options)
                    && !methods.Contains(HttpMethods.Post);
            });
    }

    [Fact]
    public void ConfigureEndpoints_ShouldMapSearchIndexRefreshWithPostOnlyOperatorRoute()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var refreshEndpoints = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.TrimStart('/') == "docs/_search-index/refresh")
            .ToList();

        Assert.NotEmpty(refreshEndpoints);
        Assert.Contains(
            refreshEndpoints,
            endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                return methods is not null
                    && methods.Count == 1
                    && methods.Contains(HttpMethods.Post);
            });

        Assert.Contains(
            refreshEndpoints,
            endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                return methods is not null
                    && methods.Contains(HttpMethods.Get)
                    && methods.Contains(HttpMethods.Head)
                    && methods.Contains(HttpMethods.Put)
                    && methods.Contains(HttpMethods.Patch)
                    && methods.Contains(HttpMethods.Delete)
                    && methods.Contains(HttpMethods.Options)
                    && !methods.Contains(HttpMethods.Post);
            });
    }

    [Fact]
    public async Task HarvestRebuildUnsupportedMethodEndpoint_ShouldReturnMethodNotAllowedAndDisableStatusPages()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var endpoint = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(
                endpoint =>
                {
                    var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                    return endpoint.RoutePattern.RawText?.TrimStart('/') == "docs/_harvest/rebuild"
                        && methods is not null
                        && methods.Contains(HttpMethods.Get)
                        && !methods.Contains(HttpMethods.Post);
                });
        await using var responseBody = new MemoryStream();
        var statusCodePages = A.Fake<IStatusCodePagesFeature>();
        statusCodePages.Enabled = true;
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.Features.Set(statusCodePages);

        await endpoint.RequestDelegate!(httpContext);
        await httpContext.Response.StartAsync();

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, httpContext.Response.StatusCode);
        Assert.False(statusCodePages.Enabled);
        Assert.Equal(DocsUrlBuilder.HarvestRebuildMethod, httpContext.Response.Headers.Allow);
    }

    [Fact]
    public async Task HarvestRebuildUnsupportedMethodEndpoint_ShouldReturnMethodNotAllowed_WhenStatusPagesAreAbsent()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var endpoint = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(
                endpoint =>
                {
                    var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                    return endpoint.RoutePattern.RawText?.TrimStart('/') == "docs/_harvest/rebuild"
                        && methods is not null
                        && methods.Contains(HttpMethods.Get)
                        && !methods.Contains(HttpMethods.Post);
                });
        await using var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;

        await endpoint.RequestDelegate!(httpContext);
        await httpContext.Response.StartAsync();

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, httpContext.Response.StatusCode);
        Assert.Equal(DocsUrlBuilder.HarvestRebuildMethod, httpContext.Response.Headers.Allow);
    }

    [Fact]
    public async Task SearchIndexRefreshUnsupportedMethodEndpoint_ShouldReturnMethodNotAllowedAndDisableStatusPages()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var endpoint = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(
                endpoint =>
                {
                    var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                    return endpoint.RoutePattern.RawText?.TrimStart('/') == "docs/_search-index/refresh"
                        && methods is not null
                        && methods.Contains(HttpMethods.Get)
                        && !methods.Contains(HttpMethods.Post);
                });
        await using var responseBody = new MemoryStream();
        var statusCodePages = A.Fake<IStatusCodePagesFeature>();
        statusCodePages.Enabled = true;
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.Features.Set(statusCodePages);

        await endpoint.RequestDelegate!(httpContext);
        await httpContext.Response.StartAsync();

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, httpContext.Response.StatusCode);
        Assert.False(statusCodePages.Enabled);
        Assert.Equal(DocsUrlBuilder.SearchIndexRefreshMethod, httpContext.Response.Headers.Allow);
    }

    [Fact]
    public async Task MetricsCollectUnsupportedMethodEndpoint_ShouldReturnMethodNotAllowedAndDisableStatusPages()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Metrics = new AppSurfaceDocsMetricsOptions
                {
                    Enabled = true,
                    HostedCollection = new AppSurfaceDocsHostedMetricsCollectionOptions
                    {
                        Enabled = true
                    }
                }
            });
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var endpoint = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(
                endpoint =>
                {
                    var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                    return endpoint.RoutePattern.RawText?.TrimStart('/') == "docs/_metrics/collect"
                        && methods is not null
                        && methods.Contains(HttpMethods.Get)
                        && !methods.Contains(HttpMethods.Post);
                });
        await using var responseBody = new MemoryStream();
        var statusCodePages = A.Fake<IStatusCodePagesFeature>();
        statusCodePages.Enabled = true;
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.Features.Set(statusCodePages);

        await endpoint.RequestDelegate!(httpContext);
        await httpContext.Response.StartAsync();

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, httpContext.Response.StatusCode);
        Assert.False(statusCodePages.Enabled);
        Assert.Equal(HttpMethods.Post, httpContext.Response.Headers.Allow);
    }

    [Fact]
    public async Task MetricsCollectUnsupportedMethodEndpoint_ShouldReturnMethodNotAllowedWithoutStatusPagesFeature()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Metrics = new AppSurfaceDocsMetricsOptions
                {
                    Enabled = true,
                    HostedCollection = new AppSurfaceDocsHostedMetricsCollectionOptions
                    {
                        Enabled = true
                    }
                }
            });
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var endpoint = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(
                endpoint =>
                {
                    var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                    return endpoint.RoutePattern.RawText?.TrimStart('/') == "docs/_metrics/collect"
                        && methods is not null
                        && methods.Contains(HttpMethods.Get)
                        && !methods.Contains(HttpMethods.Post);
                });
        await using var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;

        await endpoint.RequestDelegate!(httpContext);
        await httpContext.Response.StartAsync();

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, httpContext.Response.StatusCode);
        Assert.Equal(HttpMethods.Post, httpContext.Response.Headers.Allow);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void ConfigureEndpoints_ShouldMapHostedMetricsRoutesWhenEnabled(
        bool metricsEnabled,
        bool hostedCollectionEnabled,
        bool hostedReviewEnabled,
        bool shouldMapSearchQuality)
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        builder.Services.AddSingleton(
            new AppSurfaceDocsOptions
            {
                Metrics = new AppSurfaceDocsMetricsOptions
                {
                    Enabled = metricsEnabled,
                    HostedCollection = new AppSurfaceDocsHostedMetricsCollectionOptions
                    {
                        Enabled = hostedCollectionEnabled
                    },
                    HostedReview = new AppSurfaceDocsHostedMetricsReviewOptions
                    {
                        Enabled = hostedReviewEnabled
                    }
                }
            });
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var routePatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText?.TrimStart('/'))
            .ToArray();

        Assert.Equal(metricsEnabled && hostedCollectionEnabled, routePatterns.Contains("docs/_metrics/collect"));
        Assert.Equal(shouldMapSearchQuality, routePatterns.Contains("docs/_search-quality"));
    }

    [Fact]
    public void ConfigureEndpoints_ShouldNotMapHostedMetricsRoutesWhenMetricsOptionsAreMissing()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        builder.Services.AddSingleton(new AppSurfaceDocsOptions { Metrics = null! });
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var routePatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText?.TrimStart('/'))
            .ToArray();

        Assert.DoesNotContain("docs/_metrics/collect", routePatterns);
        Assert.DoesNotContain("docs/_search-quality", routePatterns);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldMapBareRootRedirect_WhenAppSurfaceDocsIsTheRootModule()
    {
        var envFake = A.Fake<IEnvironmentProvider>();
        var context = new StartupContext(Array.Empty<string>(), _module, "AppSurfaceDocsStandalone", envFake);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var rootRedirect = Assert.Single(
            routeBuilder.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText is "/" or "");
        var methods = Assert.Single(rootRedirect.Metadata.OfType<HttpMethodMetadata>());
        Assert.Contains(HttpMethods.Get, methods.HttpMethods);
        Assert.Contains(HttpMethods.Head, methods.HttpMethods);
    }

    [Fact]
    public async Task ConfigureEndpoints_ShouldRedirectBareRootToDocsHome_WhenAppSurfaceDocsIsTheRootModule()
    {
        var envFake = A.Fake<IEnvironmentProvider>();
        var context = new StartupContext(Array.Empty<string>(), _module, "AppSurfaceDocsStandalone", envFake);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var rootRedirect = Assert.Single(
            routeBuilder.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText is "/" or "");
        var httpContext = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        httpContext.Response.Body = new MemoryStream();

        await rootRedirect.RequestDelegate!(httpContext);

        Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
        Assert.Equal("/docs", httpContext.Response.Headers.Location);
    }

    [Fact]
    public async Task ConfigureEndpoints_ShouldPreservePathBase_WhenBareRootRedirectRuns()
    {
        var envFake = A.Fake<IEnvironmentProvider>();
        var context = new StartupContext(Array.Empty<string>(), _module, "AppSurfaceDocsStandalone", envFake);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var rootRedirect = Assert.Single(
            routeBuilder.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText is "/" or "");
        var httpContext = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        httpContext.Request.PathBase = "/portal/";
        httpContext.Response.Body = new MemoryStream();

        await rootRedirect.RequestDelegate!(httpContext);

        Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
        Assert.Equal("/portal/docs", httpContext.Response.Headers.Location);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldNotMapBareRootRedirect_WhenRootModuleDocsAreRootMounted()
    {
        var envFake = A.Fake<IEnvironmentProvider>();
        var context = new StartupContext(Array.Empty<string>(), _module, "AppSurfaceDocsStandalone", envFake);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        Assert.DoesNotContain(
            routeBuilder.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText is "/" or ""
                        && endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not null);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldNotMapBareRootRedirect_WhenAppSurfaceDocsIsEmbedded()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        Assert.DoesNotContain(
            routeBuilder.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText is "/" or "");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldReserveHealthRoutes_WhenWebHostEnvironmentIsMissing()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        using var app = builder.Build();
        var routeBuilder = new RecordingEndpointRouteBuilder(
            new HiddenServiceProvider(app.Services, typeof(IWebHostEnvironment)));

        _module.ConfigureEndpoints(context, routeBuilder);

        var routePatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => !string.IsNullOrEmpty(pattern))
            .ToList();

        Assert.Contains("docs/_health", routePatterns);
        Assert.Contains("docs/_health.json", routePatterns);
        Assert.Contains("docs/_harvest", routePatterns);
        Assert.Contains("docs/_harvest/rebuild", routePatterns);
        Assert.Contains("docs/_routes", routePatterns);
        Assert.Contains("docs/_routes.json", routePatterns);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldReserveHealthRoutes_InProductionByDefault()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(
            new TestWebHostEnvironment
            {
                EnvironmentName = Environments.Production
            });
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        var routePatterns = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => !string.IsNullOrEmpty(pattern))
            .ToList();

        Assert.Contains("docs/_health", routePatterns);
        Assert.Contains("docs/_health.json", routePatterns);
        Assert.Contains("docs/_routes", routePatterns);
        Assert.Contains("docs/_routes.json", routePatterns);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldMapHealthRoutes_InProductionWhenExplicitlyEnabled()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(
            new TestWebHostEnvironment
            {
                EnvironmentName = Environments.Production
            });
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    ExposeRoutes = AppSurfaceDocsHarvestHealthExposure.Always
                }
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
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

        Assert.Contains("docs/_health", routePatterns);
        Assert.Contains("docs/_health.json", routePatterns);
        Assert.Contains("docs/_routes", routePatterns);
        Assert.Contains("docs/_routes.json", routePatterns);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldApplyConfiguredHealthAuthorizationPolicyOnlyToHealthRoutes()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    AuthorizationPolicy = "DocsHealthRead"
                }
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        AssertHealthAuthorizationPolicy(routeBuilder, "docs/_health", "DocsHealthRead");
        AssertHealthAuthorizationPolicy(routeBuilder, "docs/_health.json", "DocsHealthRead");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_harvest");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_harvest/rebuild");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_routes");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_routes.json");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_search-index/refresh");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldApplyOperatorReadPolicyToExposedDiagnosticsReadRoutes()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        var options = new AppSurfaceDocsOptions
        {
            Diagnostics = new AppSurfaceDocsDiagnosticsOptions
            {
                OperatorReadPolicy = "DocsRead",
                ExposeRouteInspector = AppSurfaceDocsHarvestHealthExposure.Always
            },
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    ExposeRoutes = AppSurfaceDocsHarvestHealthExposure.Always
                }
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        AssertAuthorizationPolicy(routeBuilder, "docs/_health", "DocsRead");
        AssertAuthorizationPolicy(routeBuilder, "docs/_health.json", "DocsRead");
        AssertAuthorizationPolicy(routeBuilder, "docs/_harvest", "DocsRead");
        AssertAuthorizationPolicy(routeBuilder, "docs/_routes", "DocsRead");
        AssertAuthorizationPolicy(routeBuilder, "docs/_routes.json", "DocsRead");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_harvest/rebuild");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_search-index/refresh");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldPreferHealthAuthorizationPolicyForHealthRoutes()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        var options = new AppSurfaceDocsOptions
        {
            Diagnostics = new AppSurfaceDocsDiagnosticsOptions
            {
                OperatorReadPolicy = "DocsRead",
                ExposeRouteInspector = AppSurfaceDocsHarvestHealthExposure.Always
            },
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    AuthorizationPolicy = "DocsHealthRead",
                    ExposeRoutes = AppSurfaceDocsHarvestHealthExposure.Always
                }
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        AssertAuthorizationPolicy(routeBuilder, "docs/_health", "DocsHealthRead");
        AssertAuthorizationPolicy(routeBuilder, "docs/_health.json", "DocsHealthRead");
        AssertAuthorizationPolicy(routeBuilder, "docs/_harvest", "DocsRead");
        AssertAuthorizationPolicy(routeBuilder, "docs/_routes", "DocsRead");
        AssertAuthorizationPolicy(routeBuilder, "docs/_routes.json", "DocsRead");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldNotApplyOperatorReadPolicyToHiddenDiagnosticsRoutes()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(
            new TestWebHostEnvironment
            {
                EnvironmentName = Environments.Production
            });
        var options = new AppSurfaceDocsOptions
        {
            Diagnostics = new AppSurfaceDocsDiagnosticsOptions
            {
                OperatorReadPolicy = "DocsRead"
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health.json");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_harvest");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_routes");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_routes.json");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldNotApplyHealthAuthorizationPolicy_WhenUnset()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health.json");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfigureEndpoints_ShouldNotApplyHealthAuthorizationPolicy_WhenPolicyIsBlank(string authorizationPolicy)
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    AuthorizationPolicy = authorizationPolicy
                }
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health.json");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldNotApplyHealthAuthorizationPolicy_WhenHarvestOptionsAreNull()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        var options = new AppSurfaceDocsOptions
        {
            Harvest = null!
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health.json");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldNotApplyHealthAuthorizationPolicy_WhenHealthOptionsAreNull()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = null!
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        builder.Services.AddSingleton(optionsMonitor);
        using var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        _module.ConfigureEndpoints(context, routeBuilder);

        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health");
        AssertNoAuthorizationPolicy(routeBuilder, "docs/_health.json");
    }

    [Fact]
    public void ConfigureEndpoints_ShouldNotApplyHealthAuthorizationPolicy_WhenHostEnvironmentIsUnavailable()
    {
        var services = new ServiceCollection();
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    AuthorizationPolicy = "DocsHealthRead"
                }
            }
        };
        using var provider = services.BuildServiceProvider();

        var policyName = AppSurfaceDocsWebModule.ResolveHealthAuthorizationPolicyName(options, provider);

        Assert.Null(policyName);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldReserveHealthRoutes_InDevelopmentWhenExplicitlyDisabled()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(
            new TestWebHostEnvironment
            {
                EnvironmentName = Environments.Development
            });
        var options = new AppSurfaceDocsOptions
        {
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = new AppSurfaceDocsHarvestHealthOptions
                {
                    ExposeRoutes = AppSurfaceDocsHarvestHealthExposure.Never
                }
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
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

        Assert.Contains("docs/_health", routePatterns);
        Assert.Contains("docs/_health.json", routePatterns);
        Assert.Contains("docs/_routes", routePatterns);
        Assert.Contains("docs/_routes.json", routePatterns);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldMapVersionedRoutes_WhenVersioningIsEnabled()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            },
            Versioning = new AppSurfaceDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = "catalog.json"
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
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
        Assert.Contains("docs/next/_search-index/refresh", routePatterns);
        Assert.Contains("docs/next/_harvest", routePatterns);
        Assert.Contains("docs/next/_harvest/rebuild", routePatterns);
        Assert.Contains("docs/next/_health", routePatterns);
        Assert.Contains("docs/next/_health.json", routePatterns);
        Assert.Contains("docs/next/{*path}", routePatterns);
    }

    [Fact]
    public void ConfigureEndpoints_ShouldTrimLeadingSlash_ForRootMountedSectionAndDetailsRoutes()
    {
        var context = CreateStartupContext();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(DocsController).Assembly);
        builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/"
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
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

        Assert.Contains(string.Empty, routePatterns);
        Assert.Contains("search", routePatterns);
        Assert.Contains("search-index.json", routePatterns);
        Assert.Contains("_search-index/refresh", routePatterns);
        Assert.Contains("_harvest", routePatterns);
        Assert.Contains("_harvest/rebuild", routePatterns);
        Assert.Contains("_health", routePatterns);
        Assert.Contains("_health.json", routePatterns);
        Assert.Contains("sections/{sectionSlug}", routePatterns);
        Assert.Contains("{*path}", routePatterns);
        Assert.DoesNotContain("{controller=Docs}/{action=Index}/{path?}", routePatterns);
        Assert.DoesNotContain("/sections/{sectionSlug}", routePatterns);
        Assert.DoesNotContain("/{*path}", routePatterns);
    }

    [Fact]
    public void ConfigureWebApplication_ShouldReturn_WhenVersioningIsEnabledButCatalogServiceIsMissing()
    {
        var context = CreateStartupContext();
        var services = new ServiceCollection();
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            },
            Versioning = new AppSurfaceDocsVersioningOptions
            {
                Enabled = true,
                CatalogPath = "catalog.json"
            }
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
        A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
        services.AddSingleton(optionsMonitor);
        var recordingBuilder = new RecordingApplicationBuilder(services.BuildServiceProvider());

        _module.ConfigureWebApplication(context, recordingBuilder);

        Assert.Equal(0, recordingBuilder.UseCallCount);
    }

    [Fact]
    public async Task ConfigureWebApplication_ShouldFallbackToConstructedDocsUrlBuilder_WhenServiceIsMissing()
    {
        var tempDirectory = Path.Join(
            Path.GetTempPath(),
            "appsurfacedocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var treePath = Path.Join(tempDirectory, "1.2.3");
            Directory.CreateDirectory(treePath);
            File.WriteAllText(Path.Join(treePath, "index.html"), "<html>ok</html>");
            Directory.CreateDirectory(Path.Join(treePath, "next"));
            File.WriteAllText(Path.Join(treePath, "next", "index.html"), "<html>published-collision</html>");
            File.WriteAllText(Path.Join(treePath, "search.html"), "<html>search</html>");
            File.WriteAllText(Path.Join(treePath, "search-index.json"), "{\"documents\":[]}");
            File.WriteAllText(Path.Join(treePath, "search.css"), "body { color: #fff; }");
            File.WriteAllText(Path.Join(treePath, "search-client.js"), "window.__searchClientLoaded = true;");
            File.WriteAllText(Path.Join(treePath, "outline-client.js"), "window.__outlineClientLoaded = true;");
            File.WriteAllText(Path.Join(treePath, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");
            var releaseManifestSha256 = WriteReleaseManifest(treePath);

            var catalogPath = Path.Join(tempDirectory, "catalog.json");
            File.WriteAllText(
                catalogPath,
                $$"""
                {
                  "recommendedVersion": "1.2.3",
                  "versions": [
                    {
                      "version": "1.2.3",
                      "exactTreePath": "1.2.3",
                      "releaseManifestSha256": "{{releaseManifestSha256}}",
                      "supportState": "Current",
                      "visibility": "Public",
                      "advisoryState": "None"
                    }
                  ]
                }
                """);

            var context = CreateStartupContext();
            var services = new ServiceCollection();
            var options = new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/docs/next"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = catalogPath
                }
            };
            var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
            A.CallTo(() => optionsMonitor.CurrentValue).Returns(options);
            services.AddSingleton(optionsMonitor);
            services.AddSingleton(
                new AppSurfaceDocsVersionCatalogService(
                    options,
                    new TestWebHostEnvironment { ContentRootPath = tempDirectory, WebRootPath = tempDirectory },
                    NullLogger<AppSurfaceDocsVersionCatalogService>.Instance));
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
        var tempDirectory = Path.Join(
            Path.GetTempPath(),
            "appsurfacedocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var treePath = Path.Join(tempDirectory, "1.2.3");
            Directory.CreateDirectory(treePath);
            File.WriteAllText(Path.Join(treePath, "index.html"), "<html>broken</html>");

            var catalogPath = Path.Join(tempDirectory, "catalog.json");
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
            var options = new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
                {
                    DocsRootPath = "/docs/next"
                },
                Versioning = new AppSurfaceDocsVersioningOptions
                {
                    Enabled = true,
                    CatalogPath = catalogPath
                }
            };
            services.AddSingleton(options);
            services.AddSingleton(
                new AppSurfaceDocsVersionCatalogService(
                    options,
                    new TestWebHostEnvironment { ContentRootPath = tempDirectory, WebRootPath = tempDirectory },
                    NullLogger<AppSurfaceDocsVersionCatalogService>.Instance));
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

    [Theory]
    [InlineData("docs", null, "/docs", "/docs")]
    [InlineData("/docs/", "/docs/v/1.2.3/", "/docs", "/docs/v/1.2.3")]
    [InlineData("/", null, "/", "/")]
    [InlineData("/", "/v/1.2.3/", "/", "/v/1.2.3")]
    public void AppSurfaceDocsPublishedTreeMount_ShouldNormalizeMountAndCanonicalRoots(
        string mountRootPath,
        string? canonicalRootPath,
        string expectedMountRootPath,
        string expectedCanonicalRootPath)
    {
        var mount = new AppSurfaceDocsPublishedTreeMount(
            mountRootPath,
            new NullFileProvider(),
            canonicalRootPath: canonicalRootPath);

        Assert.Equal(expectedMountRootPath, mount.MountRootPath);
        Assert.Equal(expectedCanonicalRootPath, mount.CanonicalRootPath);
    }

    [Fact]
    public void BuildPublishedTreeMounts_ShouldReuseProvider_ForRecommendedAliasOfPublicVersion()
    {
        var tempDirectory = Path.Join(
            Path.GetTempPath(),
            "appsurfacedocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var exactTreePath = Path.Join(tempDirectory, "1.2.3");
            Directory.CreateDirectory(exactTreePath);
            var version = new AppSurfaceDocsResolvedVersion(
                Version: "1.2.3",
                Label: "1.2.3",
                Summary: null,
                ExactTreePath: exactTreePath,
                ExactRootUrl: "/docs/v/1.2.3",
                SupportState: AppSurfaceDocsVersionSupportState.Current,
                Visibility: AppSurfaceDocsVersionVisibility.Public,
                AdvisoryState: AppSurfaceDocsVersionAdvisoryState.None,
                IsAvailable: true,
                AvailabilityIssue: null);
            var catalog = new AppSurfaceDocsResolvedVersionCatalog(
                AppSurfaceDocsResolvedVersionCatalogStatus.Resolved,
                CatalogPath: Path.Join(tempDirectory, "catalog.json"),
                Versions: [version],
                RecommendedVersion: version);

            var docsUrlBuilder = new DocsUrlBuilder(
                new AppSurfaceDocsOptions
                {
                    Routing = new AppSurfaceDocsRoutingOptions
                    {
                        RouteRootPath = "/docs",
                        DocsRootPath = "/docs/next"
                    },
                    Versioning = new AppSurfaceDocsVersioningOptions
                    {
                        Enabled = true,
                        CatalogPath = "catalog.json"
                    }
                });
            var (mounts, providers) = AppSurfaceDocsWebModule.BuildPublishedTreeMounts(catalog, docsUrlBuilder);

            Assert.Equal(2, mounts.Count);
            Assert.Single(providers);
            Assert.Equal("/docs/v/1.2.3", mounts[0].MountRootPath);
            Assert.Equal("/docs/v/1.2.3", mounts[0].CanonicalRootPath);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(exactTreePath)), mounts[0].ExactTreeRootPath);
            Assert.Equal("/docs", mounts[1].MountRootPath);
            Assert.Equal("/docs/v/1.2.3", mounts[1].CanonicalRootPath);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(exactTreePath)), mounts[1].ExactTreeRootPath);
            Assert.Same(mounts[0].FileProvider, mounts[1].FileProvider);
            Assert.NotNull(mounts[0].FrozenRouteManifest);
            Assert.NotNull(mounts[1].FrozenRouteManifest);
            Assert.Same(mounts[0].FrozenRouteManifest, mounts[1].FrozenRouteManifest);
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
    public void BuildPublishedTreeMounts_ShouldUseVerifiedFrozenManifestSnapshot_ForVerifiedArchives()
    {
        var tempDirectory = TestPathUtils.PathUnder(
            Path.GetTempPath(),
            "appsurfacedocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var exactTreePath = TestPathUtils.PathUnder(tempDirectory, "1.2.3");
            Directory.CreateDirectory(exactTreePath);
            var verifiedArchive = new AppSurfaceDocsVerifiedReleaseArchive(
                new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>(StringComparer.OrdinalIgnoreCase),
                AppSurfaceDocsFrozenRouteManifest.Empty);
            var version = new AppSurfaceDocsResolvedVersion(
                Version: "1.2.3",
                Label: "1.2.3",
                Summary: null,
                ExactTreePath: exactTreePath,
                ExactRootUrl: "/docs/v/1.2.3",
                SupportState: AppSurfaceDocsVersionSupportState.Current,
                Visibility: AppSurfaceDocsVersionVisibility.Public,
                AdvisoryState: AppSurfaceDocsVersionAdvisoryState.None,
                IsAvailable: true,
                AvailabilityIssue: null,
                ArchiveVerificationState: AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
                VerifiedReleaseArchive: verifiedArchive);
            var catalog = new AppSurfaceDocsResolvedVersionCatalog(
                AppSurfaceDocsResolvedVersionCatalogStatus.Resolved,
                CatalogPath: TestPathUtils.PathUnder(tempDirectory, "catalog.json"),
                Versions: [version],
                RecommendedVersion: version);
            var docsUrlBuilder = new DocsUrlBuilder(
                new AppSurfaceDocsOptions
                {
                    Routing = new AppSurfaceDocsRoutingOptions
                    {
                        RouteRootPath = "/docs",
                        DocsRootPath = "/docs/next"
                    }
                });

            var (mounts, providers) = AppSurfaceDocsWebModule.BuildPublishedTreeMounts(catalog, docsUrlBuilder);

            Assert.Equal(2, mounts.Count);
            Assert.Single(providers);
            Assert.NotNull(mounts[0].FrozenRouteManifest);
            Assert.NotNull(mounts[1].FrozenRouteManifest);
            Assert.Same(mounts[0].FrozenRouteManifest, mounts[1].FrozenRouteManifest);
            Assert.True(mounts[0].FrozenRouteManifest!.UsesVerifiedSnapshot);
            Assert.True(mounts[1].FrozenRouteManifest!.UsesVerifiedSnapshot);
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
    public void BuildPublishedTreeMounts_ShouldReplaceSharedManifestCache_WhenLaterMountUsesVerifiedArchive()
    {
        var tempDirectory = TestPathUtils.PathUnder(
            Path.GetTempPath(),
            "appsurfacedocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var exactTreePath = TestPathUtils.PathUnder(tempDirectory, "shared");
            Directory.CreateDirectory(exactTreePath);
            var verifiedArchive = new AppSurfaceDocsVerifiedReleaseArchive(
                new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>(StringComparer.OrdinalIgnoreCase),
                AppSurfaceDocsFrozenRouteManifest.Empty);
            var legacyVersion = new AppSurfaceDocsResolvedVersion(
                Version: "1.2.2",
                Label: "1.2.2",
                Summary: null,
                ExactTreePath: exactTreePath,
                ExactRootUrl: "/docs/v/1.2.2",
                SupportState: AppSurfaceDocsVersionSupportState.Current,
                Visibility: AppSurfaceDocsVersionVisibility.Public,
                AdvisoryState: AppSurfaceDocsVersionAdvisoryState.None,
                IsAvailable: true,
                AvailabilityIssue: null);
            var verifiedVersion = new AppSurfaceDocsResolvedVersion(
                Version: "1.2.3",
                Label: "1.2.3",
                Summary: null,
                ExactTreePath: exactTreePath,
                ExactRootUrl: "/docs/v/1.2.3",
                SupportState: AppSurfaceDocsVersionSupportState.Current,
                Visibility: AppSurfaceDocsVersionVisibility.Public,
                AdvisoryState: AppSurfaceDocsVersionAdvisoryState.None,
                IsAvailable: true,
                AvailabilityIssue: null,
                ArchiveVerificationState: AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
                VerifiedReleaseArchive: verifiedArchive);
            var catalog = new AppSurfaceDocsResolvedVersionCatalog(
                AppSurfaceDocsResolvedVersionCatalogStatus.Resolved,
                CatalogPath: TestPathUtils.PathUnder(tempDirectory, "catalog.json"),
                Versions: [legacyVersion, verifiedVersion],
                RecommendedVersion: null);
            var docsUrlBuilder = new DocsUrlBuilder(
                new AppSurfaceDocsOptions
                {
                    Routing = new AppSurfaceDocsRoutingOptions
                    {
                        RouteRootPath = "/docs",
                        DocsRootPath = "/docs/next"
                    }
                });

            var (mounts, providers) = AppSurfaceDocsWebModule.BuildPublishedTreeMounts(catalog, docsUrlBuilder);

            Assert.Equal(2, mounts.Count);
            Assert.Single(providers);
            Assert.False(mounts[0].FrozenRouteManifest!.UsesVerifiedSnapshot);
            Assert.True(mounts[1].FrozenRouteManifest!.UsesVerifiedSnapshot);
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
    public void BuildPublishedTreeMounts_ShouldNotReuseProvider_ForCaseNeighborTreesOnCaseSensitivePlatforms()
    {
        var tempDirectory = Path.Join(
            Path.GetTempPath(),
            "appsurfacedocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var firstTreePath = Path.Join(tempDirectory, "Release");
            var secondTreePath = Path.Join(tempDirectory, "release");
            Directory.CreateDirectory(firstTreePath);
            if (Directory.Exists(secondTreePath))
            {
                return;
            }

            Directory.CreateDirectory(secondTreePath);
            var firstVersion = new AppSurfaceDocsResolvedVersion(
                Version: "1.0.0",
                Label: "1.0.0",
                Summary: null,
                ExactTreePath: firstTreePath,
                ExactRootUrl: "/docs/v/1.0.0",
                SupportState: AppSurfaceDocsVersionSupportState.Current,
                Visibility: AppSurfaceDocsVersionVisibility.Public,
                AdvisoryState: AppSurfaceDocsVersionAdvisoryState.None,
                IsAvailable: true,
                AvailabilityIssue: null);
            var secondVersion = firstVersion with
            {
                Version = "1.0.1",
                Label = "1.0.1",
                ExactTreePath = secondTreePath,
                ExactRootUrl = "/docs/v/1.0.1"
            };
            var catalog = new AppSurfaceDocsResolvedVersionCatalog(
                AppSurfaceDocsResolvedVersionCatalogStatus.Resolved,
                CatalogPath: Path.Join(tempDirectory, "catalog.json"),
                Versions: [firstVersion, secondVersion],
                RecommendedVersion: null);
            var docsUrlBuilder = new DocsUrlBuilder(
                new AppSurfaceDocsOptions
                {
                    Versioning = new AppSurfaceDocsVersioningOptions
                    {
                        Enabled = true,
                        CatalogPath = "catalog.json"
                    }
                });

            var (mounts, providers) = AppSurfaceDocsWebModule.BuildPublishedTreeMounts(catalog, docsUrlBuilder);

            Assert.Equal(2, mounts.Count);
            Assert.Equal(2, providers.Count);
            Assert.NotSame(mounts[0].FileProvider, mounts[1].FileProvider);
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
        var tempDirectory = Path.Join(
            Path.GetTempPath(),
            "appsurfacedocs-web-module-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var exactTreePath = Path.Join(tempDirectory, "1.2.3");
            Directory.CreateDirectory(exactTreePath);
            var version = new AppSurfaceDocsResolvedVersion(
                Version: "1.2.3",
                Label: "1.2.3",
                Summary: null,
                ExactTreePath: exactTreePath,
                ExactRootUrl: "/foo/bar/v/1.2.3",
                SupportState: AppSurfaceDocsVersionSupportState.Current,
                Visibility: AppSurfaceDocsVersionVisibility.Public,
                AdvisoryState: AppSurfaceDocsVersionAdvisoryState.None,
                IsAvailable: true,
                AvailabilityIssue: null);
            var catalog = new AppSurfaceDocsResolvedVersionCatalog(
                AppSurfaceDocsResolvedVersionCatalogStatus.Resolved,
                CatalogPath: Path.Join(tempDirectory, "catalog.json"),
                Versions: [version],
                RecommendedVersion: version);
            var docsUrlBuilder = new DocsUrlBuilder(
                new AppSurfaceDocsOptions
                {
                    Routing = new AppSurfaceDocsRoutingOptions
                    {
                        RouteRootPath = "/foo/bar",
                        DocsRootPath = "/foo/bar/next"
                    },
                    Versioning = new AppSurfaceDocsVersioningOptions
                    {
                        Enabled = true,
                        CatalogPath = "catalog.json"
                    }
                });

            var (mounts, providers) = AppSurfaceDocsWebModule.BuildPublishedTreeMounts(catalog, docsUrlBuilder);

            Assert.Equal(2, mounts.Count);
            Assert.Single(providers);
            Assert.Equal("/foo/bar/v/1.2.3", mounts[0].MountRootPath);
            Assert.Equal("/foo/bar/v/1.2.3", mounts[0].CanonicalRootPath);
            Assert.Equal("/foo/bar", mounts[1].MountRootPath);
            Assert.Equal("/foo/bar/v/1.2.3", mounts[1].CanonicalRootPath);
            Assert.Same(mounts[0].FileProvider, mounts[1].FileProvider);
            Assert.NotNull(mounts[0].FrozenRouteManifest);
            Assert.NotNull(mounts[1].FrozenRouteManifest);
            Assert.Same(mounts[0].FrozenRouteManifest, mounts[1].FrozenRouteManifest);
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
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions
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
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                DocsRootPath = "/docs"
            },
            Versioning = null!
        };
        var optionsMonitor = A.Fake<IOptionsMonitor<AppSurfaceDocsOptions>>();
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
        var rootModuleFake = A.Fake<IAppSurfaceHostModule>();
        var envFake = A.Fake<IEnvironmentProvider>();
        return new StartupContext(Array.Empty<string>(), rootModuleFake, "TestApp", envFake);
    }

    private static void AssertHealthAuthorizationPolicy(
        IEndpointRouteBuilder routeBuilder,
        string routePattern,
        string expectedPolicy)
    {
        AssertAuthorizationPolicy(routeBuilder, routePattern, expectedPolicy);
    }

    private static void AssertAuthorizationPolicy(
        IEndpointRouteBuilder routeBuilder,
        string routePattern,
        string expectedPolicy)
    {
        var endpoints = GetRouteEndpoints(routeBuilder, routePattern);

        Assert.NotEmpty(endpoints);
        Assert.All(
            endpoints,
            endpoint =>
            {
                var policies = endpoint.Metadata
                    .GetOrderedMetadata<IAuthorizeData>()
                    .Select(metadata => metadata.Policy)
                    .ToArray();

                Assert.Equal(expectedPolicy, Assert.Single(policies));
            });
    }

    private static void AssertNoAuthorizationPolicy(IEndpointRouteBuilder routeBuilder, string routePattern)
    {
        var endpoints = GetRouteEndpoints(routeBuilder, routePattern);

        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        }
    }

    private static IReadOnlyList<RouteEndpoint> GetRouteEndpoints(IEndpointRouteBuilder routeBuilder, string routePattern)
    {
        return routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => string.Equals(endpoint.RoutePattern.RawText?.TrimStart('/'), routePattern, StringComparison.Ordinal))
            .ToArray();
    }

    private static string WriteReleaseManifest(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), AppSurfaceDocsReleaseArchiveVerifier.FileName, StringComparison.Ordinal))
            .Select(
                path => new
                {
                    path = Path.GetRelativePath(root, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/'),
                    length = new FileInfo(path).Length,
                    contentType = (string?)null,
                    hashAlgorithm = "sha256",
                    sha256 = ComputeFileSha256(path)
                })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        var manifestPath = TestPathUtils.PathUnder(root, AppSurfaceDocsReleaseArchiveVerifier.FileName);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new { schema = AppSurfaceDocsReleaseArchiveVerifier.Schema, files },
                new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return ComputeFileSha256(manifestPath);
    }

    private static string ComputeFileSha256(string path)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private static void AddDocsReadPolicy(IServiceCollection services)
    {
        services
            .AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                HeaderAuthenticationHandler.SchemeName,
                _ => { });
        services.AddAuthorization(
            options =>
            {
                options.AddPolicy(
                    "DocsRead",
                    policy => policy.AddAuthenticationSchemes(HeaderAuthenticationHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .RequireClaim("scope", "docs.read"));
            });
    }

    private static HttpRequestMessage CreateDocsReadRequest(string scope)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, HarvestProgressStreamPath);
        request.Headers.Add(HeaderAuthenticationHandler.UserHeaderName, "alice");
        request.Headers.Add(HeaderAuthenticationHandler.ScopeHeaderName, scope);

        return request;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppSurfaceDocsTests";

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

    private sealed class HiddenServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly Type _hiddenServiceType;

        public HiddenServiceProvider(IServiceProvider inner, Type hiddenServiceType)
        {
            _inner = inner;
            _hiddenServiceType = hiddenServiceType;
        }

        public object? GetService(Type serviceType)
        {
            return serviceType == _hiddenServiceType ? null : _inner.GetService(serviceType);
        }
    }

    private sealed class AllowAllChannelAuthorizer : IRazorWireChannelAuthorizer
    {
        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            return new ValueTask<bool>(true);
        }
    }

    private sealed class HarvestProgressAllowAuthorizer : IRazorWireChannelAuthorizer
    {
        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            return new ValueTask<bool>(AppSurfaceDocsStreamAuthorization.IsHarvestProgressChannel(channel));
        }
    }

    private sealed class DenyAllChannelAuthorizer : IRazorWireChannelAuthorizer
    {
        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            return new ValueTask<bool>(false);
        }
    }

    private sealed class AllowAllStreamAuthorizer : IRazorWireStreamAuthorizer
    {
        public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            return new ValueTask<AppSurfaceAuthResult>(AppSurfaceAuthResult.Allowed());
        }
    }

    private sealed class ForbiddenStreamAuthorizer : IRazorWireStreamAuthorizer
    {
        public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            return new ValueTask<AppSurfaceAuthResult>(AppSurfaceAuthResult.Forbidden());
        }
    }

    private sealed class AppSurfaceDocsRazorWireFixture : IAsyncDisposable
    {
        private AppSurfaceDocsRazorWireFixture(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        public HttpClient Client { get; }

        private WebApplication App { get; }

        public static async Task<AppSurfaceDocsRazorWireFixture> StartAsync(
            string environmentName,
            Action<IServiceCollection> configureServices,
            Action<IServiceCollection>? configureServicesAfterDocs = null)
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    EnvironmentName = environmentName
                });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            configureServices(builder.Services);
            builder.Services.AddLogging();
            builder.Services.AddControllersWithViews();
            builder.Services.AddAppSurfaceDocs();
            configureServicesAfterDocs?.Invoke(builder.Services);

            var app = builder.Build();
            app.MapRazorWire();
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var address = Assert.Single(addresses?.Addresses ?? []);
            var client = new HttpClient
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10)
            };

            return new AppSurfaceDocsRazorWireFixture(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class HeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "DocsHeaderTest";
        public const string UserHeaderName = "X-Test-User";
        public const string ScopeHeaderName = "X-Test-Scope";

        public HeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeaderName, out var userValues)
                || string.IsNullOrWhiteSpace(userValues[0]))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, userValues[0]!),
                new(ClaimTypes.NameIdentifier, userValues[0]!)
            };
            if (Request.Headers.TryGetValue(ScopeHeaderName, out var scopeValues))
            {
                claims.AddRange(
                    scopeValues
                        .Where(scope => !string.IsNullOrWhiteSpace(scope))
                        .Select(scope => new Claim("scope", scope!)));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class RecordingEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public RecordingEndpointRouteBuilder(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder()
        {
            return new ApplicationBuilder(ServiceProvider);
        }
    }
}
