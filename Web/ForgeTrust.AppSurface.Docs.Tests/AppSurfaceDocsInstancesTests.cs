using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsInstancesTests
{
    [Fact]
    public void NamedAndLegacyComposition_ShouldFailInEitherRegistrationOrder()
    {
        var namedFirst = new ServiceCollection();
        var namedConfiguration = BuildConfiguration(("Routing:DocsRootPath", "/docs"));

        namedFirst.AddAppSurfaceDocs("public", namedConfiguration.GetSection("Docs"));

        var namedThenLegacy = Assert.Throws<InvalidOperationException>(() => namedFirst.AddAppSurfaceDocs());
        Assert.Contains("cannot be mixed", namedThenLegacy.Message, StringComparison.OrdinalIgnoreCase);

        var legacyFirst = new ServiceCollection();
        legacyFirst.AddSingleton<IConfiguration>(BuildConfiguration());
        legacyFirst.AddAppSurfaceDocs();

        var legacyThenNamed = Assert.Throws<InvalidOperationException>(
            () => legacyFirst.AddAppSurfaceDocs("public", namedConfiguration.GetSection("Docs")));
        Assert.Contains("cannot be mixed", legacyThenNamed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstances_ShouldRejectDuplicateNamesCaseInsensitively()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(("Routing:DocsRootPath", "/docs"));

        services.AddAppSurfaceDocs("Public", configuration.GetSection("Docs"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddAppSurfaceDocs(" public ", configuration.GetSection("Docs")));

        Assert.Contains("conflicts", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("case-insensitive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstanceAndRequestRuntimeAccessor_ShouldRejectNullDependencies()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var registry = fixture.App.Services.GetRequiredService<AppSurfaceDocsInstanceRegistry>();

        Assert.Equal(
            "declaration",
            Assert.Throws<ArgumentNullException>(() => new AppSurfaceDocsInstance(null!)).ParamName);
        Assert.Equal(
            "httpContextAccessor",
            Assert.Throws<ArgumentNullException>(() => new AppSurfaceDocsRequestRuntimeAccessor(null!, registry)).ParamName);
        Assert.Equal(
            "registry",
            Assert.Throws<ArgumentNullException>(
                () => new AppSurfaceDocsRequestRuntimeAccessor(new HttpContextAccessor(), null!)).ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("public docs")]
    [InlineData("public!")]
    public void NamedInstanceDeclaration_ShouldRejectBlankOrUnsafeNames(string name)
    {
        var configuration = BuildConfiguration(("Routing:DocsRootPath", "/docs"));

        var exception = Assert.Throws<ArgumentException>(
            () => new AppSurfaceDocsInstanceDeclaration(name, configuration.GetSection("Docs")));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void NamedInstanceDeclaration_ShouldRejectNamesLongerThanSixtyFourCharacters()
    {
        var configuration = BuildConfiguration(("Routing:DocsRootPath", "/docs"));

        var exception = Assert.Throws<ArgumentException>(
            () => new AppSurfaceDocsInstanceDeclaration(new string('a', 65), configuration.GetSection("Docs")));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void NamedInstanceOptionsNormalizer_ShouldRehydrateEveryRequiredNestedOptionsBlock()
    {
        var options = new AppSurfaceDocsOptions
        {
            Identity = null!,
            Source = null!,
            Harvest = null!,
            MarkdownDownload = null!,
            Diagnostics = null!,
            Metrics = null!,
            Bundle = null!,
            Sidebar = null!,
            Contributor = null!,
            Routing = null!,
            Versioning = null!,
            Localization = null!
        };

        AppSurfaceDocsServiceCollectionExtensions.NormalizeOptions(options);

        Assert.NotNull(options.Identity);
        Assert.NotNull(options.Identity.Logo);
        Assert.NotNull(options.Identity.BrandingAssets);
        Assert.NotNull(options.Source);
        Assert.NotNull(options.Harvest);
        Assert.NotNull(options.Harvest.Paths);
        Assert.NotNull(options.Harvest.Markdown);
        Assert.NotNull(options.MarkdownDownload);
        Assert.NotNull(options.Diagnostics);
        Assert.NotNull(options.Metrics);
        Assert.NotNull(options.Metrics.HostedCollection);
        Assert.NotNull(options.Bundle);
        Assert.NotNull(options.Sidebar);
        Assert.NotNull(options.Contributor);
        Assert.NotNull(options.Routing);
        Assert.NotNull(options.Versioning);
        Assert.NotNull(options.Localization);
    }

    [Fact]
    public void NamedInstanceOptionsNormalizer_ShouldNormalizeConfiguredCollectionsAndStrings()
    {
        var options = new AppSurfaceDocsOptions
        {
            Source = new AppSurfaceDocsSourceOptions { RepositoryRoot = "  /source/public  " },
            Routing = new AppSurfaceDocsRoutingOptions { DocsRootPath = " docs " },
            Sidebar = new AppSurfaceDocsSidebarOptions
            {
                NamespacePrefixes = ["  ForgeTrust. ", "forgetrust.", " "]
            },
            Localization = new AppSurfaceDocsLocalizationOptions
            {
                DefaultLocale = " ",
                Locales =
                [
                    null!,
                    new AppSurfaceDocsLocaleOptions
                    {
                        Code = " en ",
                        Label = " English ",
                        Lang = " en-US ",
                        RoutePrefix = " en "
                    }
                ]
            },
            Identity = new AppSurfaceDocsIdentityOptions
            {
                DisplayName = "  Public Docs  ",
                BrandingAssets = new AppSurfaceDocsBrandingAssetsOptions { RequestPath = " assets " }
            }
        };

        AppSurfaceDocsServiceCollectionExtensions.NormalizeOptions(options);

        Assert.Equal("/source/public", options.Source.RepositoryRoot);
        Assert.Equal("/docs", options.Routing.DocsRootPath);
        Assert.Equal("Public Docs", options.Identity.DisplayName);
        Assert.Equal("assets", options.Identity.BrandingAssets.RequestPath);
        Assert.Equal(["ForgeTrust."], options.Sidebar.NamespacePrefixes);
        Assert.Equal("en", options.Localization.DefaultLocale);
        var locale = Assert.Single(options.Localization.Locales, locale => locale is not null)!;
        Assert.Equal("en", locale.Code);
        Assert.Equal("English", locale.Label);
        Assert.Equal("en-US", locale.Lang);
        Assert.Equal("en", locale.RoutePrefix);
    }

    [Fact]
    public void NamedInstanceOptionsNormalizer_ShouldRehydrateMissingNestedChildOptions()
    {
        var options = new AppSurfaceDocsOptions
        {
            Identity = new AppSurfaceDocsIdentityOptions
            {
                Logo = null!,
                Wordmark = null!,
                Favicon = null!,
                BrandingAssets = null!
            },
            Harvest = new AppSurfaceDocsHarvestOptions
            {
                Health = null!,
                Paths = new AppSurfaceDocsHarvestPathOptions
                {
                    DefaultExclusions = null!,
                    VcsIgnore = null!
                },
                Markdown = new AppSurfaceDocsMarkdownHarvestOptions { DefaultExclusions = null! },
                CSharp = new AppSurfaceDocsCSharpHarvestOptions { DefaultExclusions = null! },
                JavaScript = new AppSurfaceDocsJavaScriptHarvestOptions { DefaultExclusions = null! }
            },
            Metrics = new AppSurfaceDocsMetricsOptions
            {
                BrowserCollector = null!,
                HostedCollection = null!,
                HostedReview = null!
            },
            Sidebar = new AppSurfaceDocsSidebarOptions { NamespacePrefixes = null! },
            Localization = new AppSurfaceDocsLocalizationOptions { Locales = null! }
        };

        AppSurfaceDocsServiceCollectionExtensions.NormalizeOptions(options);

        Assert.NotNull(options.Identity.Logo);
        Assert.NotNull(options.Identity.Wordmark);
        Assert.NotNull(options.Identity.Favicon);
        Assert.NotNull(options.Identity.BrandingAssets);
        Assert.NotNull(options.Harvest.Health);
        Assert.NotNull(options.Harvest.Paths.DefaultExclusions);
        Assert.NotNull(options.Harvest.Paths.VcsIgnore);
        Assert.NotNull(options.Harvest.Markdown.DefaultExclusions);
        Assert.NotNull(options.Harvest.CSharp.DefaultExclusions);
        Assert.NotNull(options.Harvest.JavaScript.DefaultExclusions);
        Assert.NotNull(options.Metrics.BrowserCollector);
        Assert.NotNull(options.Metrics.HostedCollection);
        Assert.NotNull(options.Metrics.HostedReview);
        Assert.NotNull(options.Sidebar.NamespacePrefixes);
        Assert.NotNull(options.Localization.Locales);
    }

    [Fact]
    public void NamedInstance_ShouldRequireAnExplicitSourceRoot()
    {
        using var fixture = BuildAppWithoutSourceRoot(("Public", "/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);

        var exception = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("requires an explicit Source:RepositoryRoot", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstanceRegistry_ShouldRejectRuntimeAccessBeforeFinalizationAndAfterDisposal()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var registry = fixture.App.Services.GetRequiredService<AppSurfaceDocsInstanceRegistry>();

        var incompleteException = Assert.Throws<InvalidOperationException>(() => registry.GetRequiredRuntime("Public"));
        Assert.Contains("was not finalized", incompleteException.Message, StringComparison.OrdinalIgnoreCase);

        using var disposableRegistry = new AppSurfaceDocsInstanceRegistry([]);
        disposableRegistry.Dispose();
        disposableRegistry.Dispose();

        Assert.Throws<ObjectDisposedException>(() => disposableRegistry.GetRequiredRuntime("Public"));
        Assert.Throws<ObjectDisposedException>(disposableRegistry.GetFinalizedRuntimes);
    }

    [Fact]
    public void NamedInstance_ShouldMapOnceAndFinalizeExactlyOnce()
    {
        using var fixture = BuildApp(
            ("Public", "/docs"));

        var endpoints = (IEndpointRouteBuilder)fixture.App;
        var mapping = fixture.Instances[0].MapEndpoints(endpoints);

        var duplicateMapException = Assert.Throws<InvalidOperationException>(
            () => fixture.Instances[0].MapEndpoints(endpoints));
        Assert.Contains("more than once", duplicateMapException.Message, StringComparison.OrdinalIgnoreCase);

        endpoints.FinalizeAppSurfaceDocsInstances();

        var finalizeException = Assert.Throws<InvalidOperationException>(
            () => endpoints.FinalizeAppSurfaceDocsInstances());
        Assert.Contains("exactly once", finalizeException.Message, StringComparison.OrdinalIgnoreCase);

        var mapAfterFinalizationException = Assert.Throws<InvalidOperationException>(
            () => fixture.Instances[0].MapEndpoints(endpoints));
        Assert.Contains("after finalization", mapAfterFinalizationException.Message, StringComparison.OrdinalIgnoreCase);

        var conventionAfterFinalizationException = Assert.Throws<InvalidOperationException>(
            () => mapping.Add(static _ => { }));
        Assert.Contains("endpoint conventions after finalization", conventionAfterFinalizationException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstanceRegistry_ShouldBecomeTerminalAfterEndpointPublicationFails()
    {
        using var fixture = BuildApp(
            ("Public", "/docs"),
            ("Internal", "/internal/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;

        fixture.Instances[0].MapEndpoints(endpoints);
        var internalMapping = fixture.Instances[1].MapEndpoints(endpoints);
        internalMapping.Add(static _ => throw new InvalidOperationException("The internal endpoint convention failed."));

        var publicationException = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("internal endpoint convention failed", publicationException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            GetDocsRouteEndpoints(fixture.App),
            endpoint => endpoint.RoutePattern.RawText == "/docs");

        var retryException = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("previous AppSurface Docs endpoint finalization attempt failed", retryException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => internalMapping.Add(static _ => { }));
    }

    [Fact]
    public void NamedInstances_ShouldRequireEveryRegisteredHandleToBeMappedBeforeFinalization()
    {
        using var fixture = BuildApp(
            ("Public", "/docs"),
            ("Internal", "/internal/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);

        var exception = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("never mapped", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Internal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedInstance_ShouldRejectFinalizationOnADifferentEndpointRouteBuilder()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints.MapGroup("/host"));

        var exception = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("never mapped on this endpoint route builder", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstanceDeclaration_ShouldRejectValidationAfterItIsMarkedFinalized()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        var declaration = new AppSurfaceDocsInstanceDeclaration("Public", BuildConfiguration().GetSection("Docs"));
        declaration.Map(endpoints);
        declaration.MarkFinalized(endpoints);

        var exception = Assert.Throws<InvalidOperationException>(() => declaration.EnsureMappedTo(endpoints));

        Assert.Contains("already finalized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstanceRegistry_ShouldRejectDuplicateDeclarationsDuringFinalization()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        var configuration = BuildConfiguration(("Source:RepositoryRoot", "/source/public"));
        var publicDeclaration = new AppSurfaceDocsInstanceDeclaration("Public", configuration.GetSection("Docs"));
        var duplicateDeclaration = new AppSurfaceDocsInstanceDeclaration("public", configuration.GetSection("Docs"));
        publicDeclaration.Map(endpoints);
        duplicateDeclaration.Map(endpoints);
        using var registry = new AppSurfaceDocsInstanceRegistry([publicDeclaration, duplicateDeclaration]);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.FinalizeMappings(endpoints));

        Assert.Contains("Conflicting names", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyInstanceRegistry_ShouldResolveTheDefaultRuntimeAndRejectNamedFinalization()
    {
        await using var app = BuildLegacyApp();
        var registry = app.Services.GetRequiredService<AppSurfaceDocsInstanceRegistry>();

        Assert.Equal("default", registry.GetRequiredRuntime(null).Name);
        Assert.Empty(registry.GetFinalizedRuntimes());

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.FinalizeMappings((IEndpointRouteBuilder)app));
        Assert.Contains("legacy AddAppSurfaceDocs", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstanceRegistry_ShouldRejectFinalizationWithoutDeclarations()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        using var registry = new AppSurfaceDocsInstanceRegistry([]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.FinalizeMappings((IEndpointRouteBuilder)fixture.App));

        Assert.Contains("No named AppSurface Docs instances", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstances_ShouldRejectMoreThanEightDeclaredProducts()
    {
        var declarations = Enumerable.Range(1, 9)
            .Select(index => ($"Docs{index}", $"/docs-{index}"))
            .ToArray();
        using var fixture = BuildApp(declarations);
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        foreach (var instance in fixture.Instances)
        {
            instance.MapEndpoints(endpoints);
        }

        var exception = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("at most 8", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstance_ShouldRejectInvalidOptionsDuringFinalization()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Public",
                "/docs",
                "/source/public",
                new Dictionary<string, string>
                {
                    ["MarkdownDownload:Enabled"] = "true"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);

        var exception = Assert.Throws<OptionsValidationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("AuthorizationPolicy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedInstances_ShouldDisposePreviouslyConstructedRuntimesWhenALaterOptionsBlockIsInvalid()
    {
        using var fixture = BuildAppWithOptions(
            ("Public", "/docs", "/source/public", new Dictionary<string, string>()),
            (
                "Internal",
                "/internal/docs",
                "/source/internal",
                new Dictionary<string, string>
                {
                    ["MarkdownDownload:Enabled"] = "true"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints);

        var exception = Assert.Throws<OptionsValidationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("AuthorizationPolicy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedInstances_ShouldAttachDistinctRuntimeMetadataToTheirRouteFamilies()
    {
        using var fixture = BuildApp(
            ("Public", "/docs"),
            ("Internal", "/internal/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;

        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();

        var routeEndpoints = GetDocsRouteEndpoints(fixture.App);
        var publicEndpoints = routeEndpoints
            .Where(endpoint => endpoint.RoutePattern.RawText == "/docs")
            .ToArray();
        var internalEndpoints = routeEndpoints
            .Where(endpoint => endpoint.RoutePattern.RawText == "/internal/docs")
            .ToArray();

        Assert.NotEmpty(publicEndpoints);
        Assert.NotEmpty(internalEndpoints);
        Assert.All(
            publicEndpoints,
            endpoint => Assert.Equal("Public", endpoint.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>()?.Name));
        Assert.All(
            internalEndpoints,
            endpoint => Assert.Equal("Internal", endpoint.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>()?.Name));
    }

    [Fact]
    public void NamedInstanceConvention_ShouldAuthorizeOnlyTheInternalDocsEndpoint()
    {
        using var fixture = BuildApp(
            ("Public", "/docs"),
            ("Internal", "/internal/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;

        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints).RequireAuthorization("DocsContributors");
        endpoints.FinalizeAppSurfaceDocsInstances();

        var routeEndpoints = GetDocsRouteEndpoints(fixture.App);
        Assert.NotEmpty(routeEndpoints);
        Assert.All(
            routeEndpoints.Where(
                endpoint => endpoint.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>()?.Name == "Public"),
            endpoint => Assert.Empty(GetAuthorizationMetadata(endpoint)));
        Assert.All(
            routeEndpoints.Where(
                endpoint => endpoint.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>()?.Name == "Internal"),
            endpoint => Assert.Contains(
                GetAuthorizationMetadata(endpoint),
                authorization => authorization.Policy == "DocsContributors"));
    }

    [Fact]
    public void NamedInstances_ShouldRejectOverlappingRouteFamilies()
    {
        using var fixture = BuildApp(
            ("Public", "/docs", "/source/public"),
            ("Internal", "/docs/internal", "/source/public/internal"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints);

        var exception = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("overlapping route families", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstances_ShouldRejectOverlappingSourceRoots()
    {
        using var fixture = BuildApp(
            ("Public", "/docs", "/source/public"),
            ("Internal", "/internal/docs", "/source/public/internal"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints);

        var exception = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("overlapping source roots", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstances_ShouldAllowSiblingRouteAndSourceRoots()
    {
        using var fixture = BuildApp(
            ("Public", "/docs", "/source/public"),
            ("Internal", "/docs-internal", "/source/public-api"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints);

        endpoints.FinalizeAppSurfaceDocsInstances();

        var registry = fixture.App.Services.GetRequiredService<AppSurfaceDocsInstanceRegistry>();
        Assert.Equal(
            ["Internal", "Public"],
            registry.GetFinalizedRuntimes().Select(runtime => runtime.Name).OrderBy(name => name).ToArray());

        var runtime = registry.GetRequiredRuntime("Public");
        Assert.NotNull(runtime.HarvestPathPolicy);
        runtime.Dispose();
        runtime.Dispose();
    }

    [Fact]
    public void NamedInstanceRegistry_ShouldRejectPreflightBeforeFinalization()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var registry = fixture.App.Services.GetRequiredService<AppSurfaceDocsInstanceRegistry>();

        var exception = Assert.Throws<InvalidOperationException>(registry.GetFinalizedRuntimes);

        Assert.Contains("startup preflight requires finalized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstances_ShouldRejectBrandingPrefixThatOverlapsAnotherRouteFamily()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Public",
                "/docs",
                "/source/public",
                new Dictionary<string, string>
                {
                    ["Identity:BrandingAssets:DirectoryPath"] = Path.GetFullPath(
                        "../../../../ForgeTrust.AppSurface.Docs/wwwroot/docs",
                        AppContext.BaseDirectory),
                    ["Identity:BrandingAssets:RequestPath"] = "/assets",
                    ["Identity:BrandingAssets:AllowSvgAssets"] = "true"
                }),
            ("Internal", "/assets/internal", "/source/internal", new Dictionary<string, string>()));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints);

        var exception = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("branding request path", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("route family", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Public", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Internal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedInstances_ShouldRejectOverlappingBrandingPrefixes()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Public",
                "/docs",
                "/source/public",
                new Dictionary<string, string>
                {
                    ["Identity:BrandingAssets:DirectoryPath"] = "/branding/public",
                    ["Identity:BrandingAssets:RequestPath"] = "/assets"
                }),
            (
                "Internal",
                "/internal/docs",
                "/source/internal",
                new Dictionary<string, string>
                {
                    ["Identity:BrandingAssets:DirectoryPath"] = "/branding/internal",
                    ["Identity:BrandingAssets:RequestPath"] = "/assets/internal"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints);

        var exception = Assert.Throws<InvalidOperationException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains("overlapping branding request paths", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstances_ShouldAllowDistinctBrandingPrefixesAlongsideSiblingRouteFamilies()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Public",
                "/docs",
                "/source/public",
                new Dictionary<string, string>
                {
                    ["Identity:BrandingAssets:DirectoryPath"] = AppContext.BaseDirectory,
                    ["Identity:BrandingAssets:RequestPath"] = "/assets/public"
                }),
            (
                "Internal",
                "/internal/docs",
                "/source/internal",
                new Dictionary<string, string>
                {
                    ["Identity:BrandingAssets:DirectoryPath"] = AppContext.BaseDirectory,
                    ["Identity:BrandingAssets:RequestPath"] = "/assets/internal"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints);

        endpoints.FinalizeAppSurfaceDocsInstances();

        Assert.NotEmpty(GetDocsRouteEndpoints(fixture.App));
    }

    [Fact]
    public void NamedInstance_ShouldRejectMissingBrandingAssetDirectory()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var fixture = BuildAppWithOptions(
            (
                "Public",
                "/docs",
                "/source/public",
                new Dictionary<string, string>
                {
                    ["Identity:BrandingAssets:DirectoryPath"] = missingDirectory,
                    ["Identity:BrandingAssets:RequestPath"] = "/assets"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);

        var exception = Assert.Throws<DirectoryNotFoundException>(endpoints.FinalizeAppSurfaceDocsInstances);

        Assert.Contains(missingDirectory, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedBrandingAssetEndpoint_ShouldRejectUnsafeAndMissingPathsAndServeGetAndHead()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Public",
                "/docs",
                "/source/public",
                new Dictionary<string, string>
                {
                    ["Identity:BrandingAssets:DirectoryPath"] = Path.GetFullPath(
                        "../../../../ForgeTrust.AppSurface.Docs/wwwroot/docs",
                        AppContext.BaseDirectory),
                    ["Identity:BrandingAssets:RequestPath"] = "/assets",
                    ["Identity:BrandingAssets:AllowSvgAssets"] = "true"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();

        var endpoint = GetDocsRouteEndpoints(fixture.App)
            .Single(route => route.RoutePattern.RawText == "/assets/{*assetPath}");
        var requestDelegate = endpoint.RequestDelegate!;

        var unsafeContext = CreateBrandingAssetContext(HttpMethods.Get, "../secret.txt");
        await requestDelegate(unsafeContext);
        Assert.Equal(StatusCodes.Status404NotFound, unsafeContext.Response.StatusCode);

        var missingContext = CreateBrandingAssetContext(HttpMethods.Get, "missing.png");
        await requestDelegate(missingContext);
        Assert.Equal(StatusCodes.Status404NotFound, missingContext.Response.StatusCode);

        const string knownAsset = "appsurface-docs-icon.svg";
        var getContext = CreateBrandingAssetContext(HttpMethods.Get, knownAsset);
        await requestDelegate(getContext);
        Assert.Equal(StatusCodes.Status200OK, getContext.Response.StatusCode);
        Assert.True(getContext.Response.ContentLength > 0);
        Assert.True(getContext.Response.Body.Length > 0);

        var headContext = CreateBrandingAssetContext(HttpMethods.Head, knownAsset);
        await requestDelegate(headContext);
        Assert.Equal(StatusCodes.Status200OK, headContext.Response.StatusCode);
        Assert.True(headContext.Response.ContentLength > 0);
        Assert.Equal(0, headContext.Response.Body.Length);
    }

    [Fact]
    public async Task NamedLegacyAssetRedirect_ShouldPreservePathBaseAndQueryString()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();

        var endpoint = GetDocsRouteEndpoints(fixture.App)
            .Single(route => route.RoutePattern.RawText == "/docs/search.css");
        var context = new DefaultHttpContext { RequestServices = fixture.App.Services };
        context.Request.Method = HttpMethods.Get;
        context.Request.PathBase = "/host";
        context.Request.QueryString = new QueryString("?cache=abc");

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(
            "/host/_content/ForgeTrust.AppSurface.Docs/docs/search.css?cache=abc",
            context.Response.Headers.Location);
    }

    [Fact]
    public void NamedRequestRuntimeAccessor_ShouldResolveOnlyTheEndpointSelectedRuntime()
    {
        using var fixture = BuildApp(
            ("Public", "/docs"),
            ("Internal", "/internal/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();

        var internalEndpoint = GetDocsRouteEndpoints(fixture.App)
            .First(endpoint => endpoint.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>()?.Name == "Internal");
        var requestContext = new DefaultHttpContext
        {
            RequestServices = fixture.App.Services
        };
        requestContext.SetEndpoint(internalEndpoint);
        var httpContextAccessor = fixture.App.Services.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = requestContext;

        using var scope = fixture.App.Services.CreateScope();
        var runtimeAccessor = scope.ServiceProvider.GetRequiredService<IAppSurfaceDocsRequestRuntimeAccessor>();

        var runtime = runtimeAccessor.GetRequiredRuntime();

        Assert.Equal("Internal", runtime.Name);
        Assert.Equal("/internal/docs", runtime.DocsUrlBuilder.CurrentDocsRootPath);

        httpContextAccessor.HttpContext = new DefaultHttpContext { RequestServices = fixture.App.Services };
        var missingMetadataException = Assert.Throws<InvalidOperationException>(runtimeAccessor.GetRequiredRuntime);
        Assert.Contains("does not identify", missingMetadataException.Message, StringComparison.OrdinalIgnoreCase);

        var unknownEndpointBuilder = new RouteEndpointBuilder(
            static _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/unknown"),
            order: 0);
        unknownEndpointBuilder.Metadata.Add(new AppSurfaceDocsEndpointMetadata("Unknown"));
        httpContextAccessor.HttpContext = new DefaultHttpContext { RequestServices = fixture.App.Services };
        httpContextAccessor.HttpContext.SetEndpoint(unknownEndpointBuilder.Build());

        var unknownMetadataException = Assert.Throws<InvalidOperationException>(runtimeAccessor.GetRequiredRuntime);
        Assert.Contains("does not identify", unknownMetadataException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedInstance_ShouldMapVersionedAndHostedDiagnosticsRoutesWhenEnabled()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Public",
                "/docs/next",
                "/source/public",
                new Dictionary<string, string>
                {
                    ["Routing:RouteRootPath"] = "/docs",
                    ["Versioning:Enabled"] = "true",
                    ["Versioning:CatalogPath"] = "catalog.json",
                    ["Metrics:Enabled"] = "true",
                    ["Metrics:HostedCollection:Enabled"] = "true",
                    ["Metrics:HostedReview:Enabled"] = "true"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);

        endpoints.FinalizeAppSurfaceDocsInstances();

        var patterns = GetDocsRouteEndpoints(fixture.App)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        Assert.Contains("/docs", patterns);
        Assert.Contains("/docs/versions", patterns);
        Assert.Contains("/docs/next/_metrics/collect", patterns);
        Assert.Contains("/docs/next/_search-quality", patterns);
    }

    [Fact]
    public async Task NamedInstance_ShouldMountAndServePublishedExactVersionTreesWithoutShadowingMissingFiles()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "appsurface-docs-instance-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);

        try
        {
            var version = "1.2.3";
            var treeRoot = CreatePublishedExactTree(fixtureRoot, version);
            var catalogPath = WritePublishedTreeCatalog(fixtureRoot, version, treeRoot);
            using var fixture = BuildAppWithOptions(
                (
                    "Public",
                    "/docs/next",
                    fixtureRoot,
                    new Dictionary<string, string>
                    {
                        ["Routing:RouteRootPath"] = "/docs",
                        ["Versioning:Enabled"] = "true",
                        ["Versioning:CatalogPath"] = catalogPath
                    }));
            var endpoints = (IEndpointRouteBuilder)fixture.App;
            fixture.Instances[0].MapEndpoints(endpoints);
            endpoints.FinalizeAppSurfaceDocsInstances();

            var runtime = fixture.App.Services.GetRequiredService<AppSurfaceDocsInstanceRegistry>()
                .GetRequiredRuntime("Public");
            Assert.NotNull(runtime.PublishedTreeHandler);

            var exactRootEndpoint = GetDocsRouteEndpoints(fixture.App)
                .Single(route => route.RoutePattern.RawText == "/docs/v/{version}");
            var exactPathEndpoint = GetDocsRouteEndpoints(fixture.App)
                .Single(route => route.RoutePattern.RawText == "/docs/v/{version}/{**path}");

            var rootContext = CreatePublishedTreeContext(HttpMethods.Get, $"/docs/v/{version}");
            await exactRootEndpoint.RequestDelegate!(rootContext);
            Assert.Equal(StatusCodes.Status200OK, rootContext.Response.StatusCode);
            Assert.Contains("published-index", await ReadResponseBodyAsync(rootContext), StringComparison.Ordinal);

            var documentContext = CreatePublishedTreeContext(HttpMethods.Get, $"/docs/v/{version}/search.html");
            await exactPathEndpoint.RequestDelegate!(documentContext);
            Assert.Equal(StatusCodes.Status200OK, documentContext.Response.StatusCode);
            Assert.Contains("published-search", await ReadResponseBodyAsync(documentContext), StringComparison.Ordinal);

            var missingContext = CreatePublishedTreeContext(HttpMethods.Get, $"/docs/v/{version}/missing.html");
            await exactPathEndpoint.RequestDelegate!(missingContext);
            Assert.Equal(StatusCodes.Status404NotFound, missingContext.Response.StatusCode);

            var headContext = CreatePublishedTreeContext(HttpMethods.Head, $"/docs/v/{version}/search.html");
            await exactPathEndpoint.RequestDelegate!(headContext);
            Assert.Equal(StatusCodes.Status200OK, headContext.Response.StatusCode);
            Assert.Equal(0, headContext.Response.Body.Length);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void NamedInstance_ShouldApplyItsConfiguredOperatorAndMarkdownPolicies()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Internal",
                "/internal/docs",
                "/source/internal",
                new Dictionary<string, string>
                {
                    ["Diagnostics:OperatorReadPolicy"] = "DocsRead",
                    ["Harvest:Health:ExposeRoutes"] = "Always",
                    ["MarkdownDownload:Enabled"] = "true",
                    ["MarkdownDownload:AuthorizationPolicy"] = "DocsMarkdownReader"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints).RequireAuthorization("DocsContributors");
        endpoints.FinalizeAppSurfaceDocsInstances();

        var routeEndpoints = GetDocsRouteEndpoints(fixture.App);

        AssertEndpointPolicies(routeEndpoints, "/internal/docs/_harvest", "DocsContributors", "DocsRead");
        AssertEndpointPolicies(routeEndpoints, "/internal/docs/_health", "DocsContributors", "DocsRead");
        AssertEndpointPolicies(routeEndpoints, "/internal/docs/_routes", "DocsContributors", "DocsRead");
        AssertEndpointPolicies(routeEndpoints, "/internal/docs/_markdown/{*path}", "DocsContributors", "DocsMarkdownReader");
    }

    [Fact]
    public async Task NamedHarvestStreamFilter_ShouldRequireTheInternalHostPolicy()
    {
        using var fixture = BuildApp(
            ("Public", "/docs"),
            ("Internal", "/internal/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints).RequireAuthorization("DocsContributors");
        endpoints.FinalizeAppSurfaceDocsInstances();

        var requestContext = new DefaultHttpContext
        {
            RequestServices = fixture.App.Services
        };
        var filter = fixture.App.Services
            .GetServices<IRazorWireStreamAuthorizationFilter>()
            .OfType<AppSurfaceDocsNamedHarvestStreamAuthorizationFilter>()
            .Single();

        var result = await filter.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                requestContext,
                AppSurfaceDocsStreamAuthorization.GetHarvestProgressChannel("Internal"),
                RazorWireStreamAuthorizationMode.DenyAll));

        Assert.NotNull(result);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task NamedHarvestStreamFilter_ShouldRequireEveryInternalHostPolicy()
    {
        using var fixture = BuildApp(
            ("Public", "/docs"),
            ("Internal", "/internal/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        fixture.Instances[1].MapEndpoints(endpoints)
            .RequireAuthorization("DocsContributors")
            .RequireAuthorization("DocsOperators");
        endpoints.FinalizeAppSurfaceDocsInstances();

        var requestContext = new DefaultHttpContext
        {
            RequestServices = fixture.App.Services
        };
        var filter = fixture.App.Services
            .GetServices<IRazorWireStreamAuthorizationFilter>()
            .OfType<AppSurfaceDocsNamedHarvestStreamAuthorizationFilter>()
            .Single();

        var result = await filter.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                requestContext,
                AppSurfaceDocsStreamAuthorization.GetHarvestProgressChannel("Internal"),
                RazorWireStreamAuthorizationMode.DenyAll));

        Assert.NotNull(result);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task NamedHarvestStreamFilter_ShouldPassThroughPublicAndUnrelatedChannelsButForbidUnknownInstances()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();

        var requestContext = new DefaultHttpContext { RequestServices = fixture.App.Services };
        var filter = fixture.App.Services
            .GetServices<IRazorWireStreamAuthorizationFilter>()
            .OfType<AppSurfaceDocsNamedHarvestStreamAuthorizationFilter>()
            .Single();

        var publicResult = await filter.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                requestContext,
                AppSurfaceDocsStreamAuthorization.GetHarvestProgressChannel("Public"),
                RazorWireStreamAuthorizationMode.DenyAll));
        var unrelatedResult = await filter.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                requestContext,
                "host-channel",
                RazorWireStreamAuthorizationMode.DenyAll));
        var malformedResult = await filter.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                requestContext,
                "appsurfacedocs-harvest-$",
                RazorWireStreamAuthorizationMode.DenyAll));
        var unknownResult = await filter.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                requestContext,
                AppSurfaceDocsStreamAuthorization.GetHarvestProgressChannel("Unknown"),
                RazorWireStreamAuthorizationMode.DenyAll));

        Assert.Null(publicResult);
        Assert.Null(unrelatedResult);
        Assert.Null(malformedResult);
        Assert.Equal(AppSurfaceAuthOutcome.Forbid, unknownResult?.Outcome);
    }

    [Fact]
    public async Task NamedHarvestStreamFilter_ShouldForbidWhenTheInstanceHidesHarvestRoutes()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Internal",
                "/internal/docs",
                "/source/internal",
                new Dictionary<string, string>
                {
                    ["Harvest:Health:ExposeRoutes"] = "Never"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();

        var filter = fixture.App.Services
            .GetServices<IRazorWireStreamAuthorizationFilter>()
            .OfType<AppSurfaceDocsNamedHarvestStreamAuthorizationFilter>()
            .Single();
        var result = await filter.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                new DefaultHttpContext { RequestServices = fixture.App.Services },
                AppSurfaceDocsStreamAuthorization.GetHarvestProgressChannel("Internal"),
                RazorWireStreamAuthorizationMode.DenyAll));

        Assert.Equal(AppSurfaceAuthOutcome.Forbid, result?.Outcome);
    }

    [Fact]
    public async Task NamedHarvestStreamFilter_ShouldReturnTheConfiguredOperatorPolicyFailure()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Internal",
                "/internal/docs",
                "/source/internal",
                new Dictionary<string, string>
                {
                    ["Diagnostics:OperatorReadPolicy"] = "DocsRead",
                    ["Harvest:Health:ExposeRoutes"] = "Always"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();

        var filter = fixture.App.Services
            .GetServices<IRazorWireStreamAuthorizationFilter>()
            .OfType<AppSurfaceDocsNamedHarvestStreamAuthorizationFilter>()
            .Single();
        var result = await filter.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                new DefaultHttpContext { RequestServices = fixture.App.Services },
                AppSurfaceDocsStreamAuthorization.GetHarvestProgressChannel("Internal"),
                RazorWireStreamAuthorizationMode.DenyAll));

        Assert.NotNull(result);
        Assert.False(result.IsAllowed);
        Assert.Equal("missing_policy", result.Metadata["code"]);
    }

    [Fact]
    public async Task NamedInstancePreflight_ShouldValidateEachInstanceMarkdownPolicy()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Internal",
                "/internal/docs",
                "/source/internal",
                new Dictionary<string, string>
                {
                    ["MarkdownDownload:Enabled"] = "true",
                    ["MarkdownDownload:AuthorizationPolicy"] = "DocsMarkdownReader"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();

        var preflight = fixture.App.Services
            .GetServices<IHostedService>()
            .OfType<AppSurfaceDocsNamedInstancePreflightService>()
            .Single();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => preflight.StartAsync(CancellationToken.None));

        Assert.Contains("Internal", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DocsMarkdownReader", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedInstancePreflight_ShouldRunEveryStartupPreflightAndStopCleanly()
    {
        using var fixture = BuildAppWithOptions(
            (
                "Public",
                "/docs",
                "/source/public",
                new Dictionary<string, string>
                {
                    ["Harvest:StartupMode"] = "Disabled"
                }));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();

        var preflight = fixture.App.Services
            .GetServices<IHostedService>()
            .OfType<AppSurfaceDocsNamedInstancePreflightService>()
            .Single();

        await preflight.StartAsync(CancellationToken.None);
        await preflight.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void NamedInstancePreflight_ShouldRejectNullDependencies()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var registry = fixture.App.Services.GetRequiredService<AppSurfaceDocsInstanceRegistry>();
        var services = fixture.App.Services;
        var environment = services.GetRequiredService<IHostEnvironment>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        Assert.Equal(
            "registry",
            Assert.Throws<ArgumentNullException>(
                () => new AppSurfaceDocsNamedInstancePreflightService(null!, services, environment, loggerFactory)).ParamName);
        Assert.Equal(
            "services",
            Assert.Throws<ArgumentNullException>(
                () => new AppSurfaceDocsNamedInstancePreflightService(registry, null!, environment, loggerFactory)).ParamName);
        Assert.Equal(
            "environment",
            Assert.Throws<ArgumentNullException>(
                () => new AppSurfaceDocsNamedInstancePreflightService(registry, services, null!, loggerFactory)).ParamName);
        Assert.Equal(
            "loggerFactory",
            Assert.Throws<ArgumentNullException>(
                () => new AppSurfaceDocsNamedInstancePreflightService(registry, services, environment, null!)).ParamName);
    }

    [Fact]
    public void AppSurfaceDocsRuntimeOwnedResources_ShouldDisposeEachResourceOnlyOnce()
    {
        var resource = new CountingDisposable();
        var ownedResources = new AppSurfaceDocsRuntimeOwnedResources(resource, Array.Empty<IDisposable>());

        ownedResources.Dispose();
        ownedResources.Dispose();

        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public void AppSurfaceDocsRuntime_ShouldRejectEveryRequiredNullConstructorArgument()
    {
        using var fixture = BuildApp(("Public", "/docs"));
        var endpoints = (IEndpointRouteBuilder)fixture.App;
        fixture.Instances[0].MapEndpoints(endpoints);
        endpoints.FinalizeAppSurfaceDocsInstances();
        var runtime = fixture.App.Services.GetRequiredService<AppSurfaceDocsInstanceRegistry>().GetRequiredRuntime("Public");
        var arguments = RuntimeConstructorArguments.From(runtime);

        AssertRuntimeConstructorNullArgument("name", (arguments with { Name = null }).Create);
        AssertRuntimeConstructorNullArgument("options", (arguments with { Options = null }).Create);
        AssertRuntimeConstructorNullArgument("docsUrlBuilder", (arguments with { DocsUrlBuilder = null }).Create);
        AssertRuntimeConstructorNullArgument("recoveryLinkBuilder", (arguments with { RecoveryLinkBuilder = null }).Create);
        AssertRuntimeConstructorNullArgument("identityResolver", (arguments with { IdentityResolver = null }).Create);
        AssertRuntimeConstructorNullArgument("themeResolver", (arguments with { ThemeResolver = null }).Create);
        AssertRuntimeConstructorNullArgument("versionCatalogService", (arguments with { VersionCatalogService = null }).Create);
        AssertRuntimeConstructorNullArgument("searchQualityReadModel", (arguments with { SearchQualityReadModel = null }).Create);
        AssertRuntimeConstructorNullArgument("harvestPathPolicy", (arguments with { HarvestPathPolicy = null }).Create);
        AssertRuntimeConstructorNullArgument("featuredPageResolver", (arguments with { FeaturedPageResolver = null }).Create);
        AssertRuntimeConstructorNullArgument("harvestProgressReporter", (arguments with { HarvestProgressReporter = null }).Create);
        AssertRuntimeConstructorNullArgument("aggregator", (arguments with { Aggregator = null }).Create);
        AssertRuntimeConstructorNullArgument("harvestCoordinator", (arguments with { HarvestCoordinator = null }).Create);
        AssertRuntimeConstructorNullArgument("assetVersioner", (arguments with { AssetVersioner = null }).Create);
    }

    private static void AssertRuntimeConstructorNullArgument(string expectedParameterName, Func<AppSurfaceDocsRuntime> create)
    {
        var exception = Assert.Throws<ArgumentNullException>(create);
        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    private static DefaultHttpContext CreateBrandingAssetContext(string method, string assetPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.RouteValues["assetPath"] = assetPath;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext CreatePublishedTreeContext(string method, string requestPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = requestPath;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static IReadOnlyList<RouteEndpoint> GetDocsRouteEndpoints(IEndpointRouteBuilder endpoints)
    {
        return endpoints.DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>() is not null)
            .ToArray();
    }

    private static IReadOnlyList<IAuthorizeData> GetAuthorizationMetadata(RouteEndpoint endpoint)
    {
        return endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .ToArray();
    }

    private static void AssertEndpointPolicies(
        IReadOnlyList<RouteEndpoint> endpoints,
        string pattern,
        params string[] expectedPolicies)
    {
        var matchingEndpoints = endpoints.Where(endpoint => endpoint.RoutePattern.RawText == pattern).ToArray();
        Assert.NotEmpty(matchingEndpoints);
        Assert.Contains(
            matchingEndpoints,
            endpoint => GetPolicyNames(endpoint).OrderBy(policy => policy)
                .SequenceEqual(expectedPolicies.OrderBy(policy => policy), StringComparer.Ordinal));
    }

    private static IReadOnlyList<string> GetPolicyNames(RouteEndpoint endpoint)
    {
        return GetAuthorizationMetadata(endpoint)
            .Select(data => data.Policy)
            .Where(policy => !string.IsNullOrWhiteSpace(policy))
            .Select(policy => policy!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static NamedDocsApp BuildApp(params (string Name, string DocsRootPath)[] instances)
    {
        return BuildApp(
            instances.Select(
                instance =>
                    (instance.Name, instance.DocsRootPath, (string?)$"/source/{instance.Name.ToLowerInvariant()}")).ToArray());
    }

    private static NamedDocsApp BuildAppWithoutSourceRoot(params (string Name, string DocsRootPath)[] instances)
    {
        return BuildApp(instances.Select(instance => (instance.Name, instance.DocsRootPath, (string?)null)).ToArray());
    }

    private static NamedDocsApp BuildApp(params (string Name, string DocsRootPath, string? RepositoryRoot)[] instances)
    {
        return BuildAppWithOptions(
            instances.Select(
                instance => (
                    instance.Name,
                    instance.DocsRootPath,
                    instance.RepositoryRoot,
                    (IReadOnlyDictionary<string, string>)new Dictionary<string, string>())).ToArray());
    }

    private static NamedDocsApp BuildAppWithOptions(
        params (string Name, string DocsRootPath, string? RepositoryRoot, IReadOnlyDictionary<string, string> Options)[] instances)
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
        var handles = new List<AppSurfaceDocsInstance>();

        foreach (var (name, docsRootPath, repositoryRoot, options) in instances)
        {
            var configurationValues = new List<(string Key, string Value)>
            {
                ("Routing:DocsRootPath", docsRootPath)
            };
            if (!string.IsNullOrWhiteSpace(repositoryRoot))
            {
                configurationValues.Add(("Source:RepositoryRoot", repositoryRoot));
            }

            configurationValues.AddRange(options.Select(option => (option.Key, option.Value)));

            var configuration = BuildConfiguration(configurationValues.ToArray());
            handles.Add(builder.Services.AddAppSurfaceDocs(name, configuration.GetSection("Docs")));
        }

        builder.Services.AddLogging();
        return new NamedDocsApp(builder.Build(), handles);
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] values)
    {
        var settings = values.ToDictionary(
            value => $"Docs:{value.Key}",
            value => (string?)value.Value,
            StringComparer.OrdinalIgnoreCase);

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    private static WebApplication BuildLegacyApp()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{AppSurfaceDocsOptions.SectionName}:Source:RepositoryRoot"] = Path.GetTempPath(),
                [$"{AppSurfaceDocsOptions.SectionName}:Routing:DocsRootPath"] = "/docs"
            });
        builder.Services.AddControllersWithViews();
        builder.Services.AddAppSurfaceDocs();

        return builder.Build();
    }

    private static string CreatePublishedExactTree(string fixtureRoot, string version)
    {
        var treeRoot = Path.Combine(fixtureRoot, version);
        Directory.CreateDirectory(treeRoot);
        File.WriteAllText(Path.Combine(treeRoot, "index.html"), "<html>published-index</html>");
        File.WriteAllText(Path.Combine(treeRoot, "search.html"), "<html>published-search</html>");
        File.WriteAllText(Path.Combine(treeRoot, "search-index.json"), "{\"documents\":[]}");
        File.WriteAllText(Path.Combine(treeRoot, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Combine(treeRoot, "search-client.js"), "window.__searchClientLoaded = true;");
        File.WriteAllText(Path.Combine(treeRoot, "outline-client.js"), "window.__outlineClientLoaded = true;");
        File.WriteAllText(Path.Combine(treeRoot, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");
        return treeRoot;
    }

    private static string WritePublishedTreeCatalog(string fixtureRoot, string version, string treeRoot)
    {
        var manifestSha256 = WriteReleaseManifest(treeRoot);
        var catalog = new AppSurfaceDocsVersionCatalog
        {
            RecommendedVersion = version,
            Versions =
            [
                new AppSurfaceDocsPublishedVersion
                {
                    Version = version,
                    ExactTreePath = version,
                    ReleaseManifestSha256 = manifestSha256
                }
            ]
        };
        var catalogPath = Path.Combine(fixtureRoot, "catalog.json");
        File.WriteAllText(catalogPath, System.Text.Json.JsonSerializer.Serialize(catalog));
        return catalogPath;
    }

    private static string WriteReleaseManifest(string treeRoot)
    {
        var files = Directory.EnumerateFiles(treeRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), AppSurfaceDocsReleaseArchiveVerifier.FileName, StringComparison.Ordinal))
            .Select(
                path => new
                {
                    path = Path.GetRelativePath(treeRoot, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/'),
                    length = new FileInfo(path).Length,
                    contentType = (string?)null,
                    hashAlgorithm = "sha256",
                    sha256 = ComputeFileSha256(path)
                })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        var manifestPath = Path.Combine(treeRoot, AppSurfaceDocsReleaseArchiveVerifier.FileName);
        File.WriteAllText(
            manifestPath,
            System.Text.Json.JsonSerializer.Serialize(
                new { schema = AppSurfaceDocsReleaseArchiveVerifier.Schema, files },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        return ComputeFileSha256(manifestPath);
    }

    private static string ComputeFileSha256(string path)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private sealed record RuntimeConstructorArguments(
        string? Name,
        AppSurfaceDocsOptions? Options,
        DocsUrlBuilder? DocsUrlBuilder,
        DocsRecoveryLinkBuilder? RecoveryLinkBuilder,
        AppSurfaceDocsIdentityResolver? IdentityResolver,
        AppSurfaceDocsThemeResolver? ThemeResolver,
        AppSurfaceDocsVersionCatalogService? VersionCatalogService,
        AppSurfaceDocsSearchQualityReadModel? SearchQualityReadModel,
        AppSurfaceDocsHarvestPathPolicy? HarvestPathPolicy,
        DocFeaturedPageResolver? FeaturedPageResolver,
        AppSurfaceDocsHarvestProgressReporter? HarvestProgressReporter,
        DocAggregator? Aggregator,
        AppSurfaceDocsHarvestCoordinator? HarvestCoordinator,
        AppSurfaceDocsAssetVersioner? AssetVersioner)
    {
        public static RuntimeConstructorArguments From(AppSurfaceDocsRuntime runtime)
        {
            return new RuntimeConstructorArguments(
                runtime.Name,
                runtime.Options,
                runtime.DocsUrlBuilder,
                runtime.RecoveryLinkBuilder,
                runtime.IdentityResolver,
                runtime.ThemeResolver,
                runtime.VersionCatalogService,
                runtime.SearchQualityReadModel,
                runtime.HarvestPathPolicy,
                runtime.FeaturedPageResolver,
                runtime.HarvestProgressReporter,
                runtime.Aggregator,
                runtime.HarvestCoordinator,
                runtime.AssetVersioner);
        }

        public AppSurfaceDocsRuntime Create()
        {
            return new AppSurfaceDocsRuntime(
                Name!,
                Options!,
                DocsUrlBuilder!,
                RecoveryLinkBuilder!,
                IdentityResolver!,
                ThemeResolver!,
                VersionCatalogService!,
                SearchQualityReadModel!,
                HarvestPathPolicy!,
                FeaturedPageResolver!,
                HarvestProgressReporter!,
                Aggregator!,
                HarvestCoordinator!,
                AssetVersioner!);
        }
    }

    private sealed class NamedDocsApp : IDisposable
    {
        public NamedDocsApp(WebApplication app, IReadOnlyList<AppSurfaceDocsInstance> instances)
        {
            App = app;
            Instances = instances;
        }

        public WebApplication App { get; }

        public IReadOnlyList<AppSurfaceDocsInstance> Instances { get; }

        public void Dispose()
        {
            App.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
