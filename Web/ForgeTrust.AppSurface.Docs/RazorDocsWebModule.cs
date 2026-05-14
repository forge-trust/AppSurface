using System.Globalization;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Web module configuration for the RazorDocs documentation system.
/// </summary>
/// <remarks>
/// This module owns both the live source-backed RazorDocs surface and the optional published-version overlay used when
/// <see cref="RazorDocsVersioningOptions.Enabled" /> is turned on. Service registration wires up harvesting,
/// aggregation, sanitization, URL generation, and version-catalog resolution through <c>services.AddRazorDocs()</c>,
/// while endpoint and middleware hooks decide whether the host behaves like a plain live docs site or a mixed
/// live-plus-archive experience.
/// </remarks>
/// <remarks>
/// When versioning is enabled and the resolved catalog exposes available published trees, the module can mount those
/// exact-version exports into the request pipeline, reserve the configured route-family root for the recommended
/// release alias, short-circuit matching requests through <see cref="RazorDocsPublishedTreeHandler" />, and register
/// disposal for any mounted <see cref="PhysicalFileProvider" /> instances when the host stops. Hosts that leave
/// versioning disabled, omit the catalog service, or resolve an empty/unavailable catalog skip those mounts and
/// continue serving only the live source-backed preview surface.
/// </remarks>
public class RazorDocsWebModule : IAppSurfaceWebModule
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private const string RazorDocsStaticAssetBasePath = "/_content/ForgeTrust.AppSurface.Docs/docs";
    private const string RazorDocsPackagedStylesheetPath = "/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css";
    private const string RazorDocsRootStylesheetPath = "/css/site.gen.css";

    /// <inheritdoc />
    public bool IncludeAsApplicationPart => true;

    /// <inheritdoc />
    public void ConfigureWebOptions(StartupContext context, WebOptions options)
    {
        options.StaticFiles.EnableStaticWebAssets = true;
    }

    /// <summary>
    /// Registers services required by the RazorDocs module into the provided service collection.
    /// </summary>
    /// <remarks>
    /// Adds the RazorDocs harvesting, aggregation, and sanitization services via <c>services.AddRazorDocs()</c>.
    /// RazorDocs styling is compiled into the package during the RazorDocs build and the layout resolves the correct
    /// static asset path for root-module versus embedded consumer hosts, so hosts do not register
    /// <c>services.AddTailwind()</c> just to light up the embedded docs UI.
    /// </remarks>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddRazorDocs();
        services.Replace(ServiceDescriptor.Singleton(RazorDocsAssetPathResolver.CreateForRootModule(context.RootModuleAssembly)));
    }

    /// <summary>
    /// Registers runtime module dependencies for this web module, including RazorWireWebModule.
    /// </summary>
    /// <param name="builder">The dependency builder used to register required modules.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceCachingModule>();
        builder.AddModule<RazorWireWebModule>();
    }

    /// <summary>
    /// Performs host-level configuration steps before application services are registered.
    /// </summary>
    /// <param name="context">The startup context providing module and environment information.</param>
    /// <param name="builder">The host builder to configure prior to service registration.</param>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Performs host-level configuration steps after application services have been registered.
    /// </summary>
    /// <param name="context">The startup context providing module and environment information.</param>
    /// <param name="builder">The host builder to modify or extend after services are configured.</param>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Configures the application's request pipeline and middleware for this module.
    /// </summary>
    /// <remarks>
    /// This hook only mutates the pipeline when versioning is enabled and the resolved
    /// <see cref="RazorDocsVersionCatalogService" /> yields at least one available published tree. In that case the
    /// module mounts exact-version exports, optionally adds the configured route-family root alias for the recommended
    /// release, and inserts a short-circuiting middleware branch that lets <see cref="RazorDocsPublishedTreeHandler" /> serve
    /// matching requests before the live preview surface sees them.
    /// </remarks>
    /// <remarks>
    /// The middleware registration is intentionally skipped when versioning is disabled, when the catalog service is
    /// absent, when the configured catalog resolves no healthy trees, or when the recommended release is unavailable.
    /// Mounted <see cref="PhysicalFileProvider" /> instances are shared across mounts that point at the same exact tree
    /// path and are disposed on <see cref="IHostApplicationLifetime.ApplicationStopping" />. Call this hook before
    /// terminal middleware so mounted published-tree requests can short-circuit correctly.
    /// </remarks>
    /// <param name="context">The startup context for the module invocation.</param>
    /// <param name="app">The application builder used to configure middleware and endpoints.</param>
    public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
    {
        var options = ResolveOptions(app.ApplicationServices);
        if (options.Versioning?.Enabled != true)
        {
            return;
        }

        var catalogService = app.ApplicationServices.GetService(typeof(RazorDocsVersionCatalogService)) as RazorDocsVersionCatalogService;
        if (catalogService is null)
        {
            return;
        }

        var docsUrlBuilder = app.ApplicationServices.GetService(typeof(DocsUrlBuilder)) as DocsUrlBuilder
                             ?? new DocsUrlBuilder(options);
        var catalog = catalogService.GetCatalog();
        var (mounts, mountedProviders) = BuildPublishedTreeMounts(catalog, docsUrlBuilder);

        if (mounts.Count == 0)
        {
            return;
        }

        RegisterMountedProviderDisposal(app.ApplicationServices, mountedProviders);

        var publishedTreeHandler = new RazorDocsPublishedTreeHandler(
            mounts,
            docsUrlBuilder.CurrentDocsRootPath,
            docsUrlBuilder.RouteRootPath);
        app.Use(
            async (httpContext, next) =>
            {
                if (await publishedTreeHandler.TryHandleAsync(httpContext))
                {
                    return;
                }

                await next();
            });
    }

    /// <summary>
    /// Builds the published-tree mount table for the current resolved version catalog.
    /// </summary>
    /// <remarks>
    /// Public exact-version mounts always preserve the authored catalog order. When the recommended release points at a
    /// public exact tree, this helper adds the configured route-family root alias as an extra mount root that reuses the
    /// same <see cref="PhysicalFileProvider" /> instance instead of duplicating file watchers for the same export path.
    /// </remarks>
    /// <param name="catalog">The resolved version catalog that describes available published trees.</param>
    /// <param name="docsUrlBuilder">The configured URL builder that supplies the route-family alias root.</param>
    /// <returns>
    /// The ordered mount list plus the unique provider instances that should be disposed with the host lifetime.
    /// </returns>
    internal static (IReadOnlyList<RazorDocsPublishedTreeMount> Mounts, IReadOnlyList<PhysicalFileProvider> Providers) BuildPublishedTreeMounts(
        RazorDocsResolvedVersionCatalog catalog,
        DocsUrlBuilder docsUrlBuilder)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(docsUrlBuilder);

        var providersByPath = new Dictionary<string, PhysicalFileProvider>(StringComparer.OrdinalIgnoreCase);
        var mounts = new List<RazorDocsPublishedTreeMount>();

        foreach (var version in catalog.PublicVersions.Where(version => version.IsAvailable && version.ExactTreePath is not null))
        {
            var provider = GetOrCreateProvider(version.ExactTreePath!, providersByPath);
            mounts.Add(new RazorDocsPublishedTreeMount(version.ExactRootUrl, provider));
        }

        if (catalog.RecommendedVersion is { IsAvailable: true, ExactTreePath: not null } recommendedVersion)
        {
            var provider = GetOrCreateProvider(recommendedVersion.ExactTreePath, providersByPath);
            mounts.Add(new RazorDocsPublishedTreeMount(docsUrlBuilder.DocsEntryRootPath, provider));
        }

        return (mounts, providersByPath.Values.ToList());
    }

    private static PhysicalFileProvider GetOrCreateProvider(
        string exactTreePath,
        IDictionary<string, PhysicalFileProvider> providersByPath)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exactTreePath));
        if (providersByPath.TryGetValue(normalizedPath, out var provider))
        {
            return provider;
        }

        provider = new PhysicalFileProvider(normalizedPath);
        providersByPath[normalizedPath] = provider;
        return provider;
    }

    private static void RegisterMountedProviderDisposal(IServiceProvider services, IReadOnlyList<PhysicalFileProvider> providers)
    {
        if (providers.Count == 0)
        {
            return;
        }

        if (services.GetService(typeof(IHostApplicationLifetime)) is not IHostApplicationLifetime lifetime)
        {
            return;
        }

        lifetime.ApplicationStopping.Register(
            static state =>
            {
                foreach (var provider in (IReadOnlyList<PhysicalFileProvider>)state!)
                {
                    provider.Dispose();
                }
            },
            providers);
    }

    /// <summary>
    /// Adds the module's default controller routes and supporting asset routes for documentation endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When RazorDocs is the root module assembly, standalone and static-export hosts preserve the historical
    /// <c>/css/site.gen.css</c> URL by redirecting it to the packaged Razor Class Library stylesheet at
    /// <c>/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css</c>. Embedded hosts do not register that
    /// redirect because they already link to the packaged asset directly. Redirects preserve the request
    /// <see cref="HttpRequest.PathBase"/> and query string so legacy links continue to work behind a virtual path.
    /// </para>
    /// <para>
    /// When versioning is enabled, this hook also reserves the stable version entry route at the configured route-family
    /// root, adds the archive surface below that route root, and serves preview-surface assets from either the live web
    /// root or the packaged Razor Class Library depending on whether published-tree mounts can shadow the stable docs root.
    /// Asset routes are built with <see cref="DocsUrlBuilder.BuildAssetUrl(string)"/> for <c>search.css</c>,
    /// <c>minisearch.min.js</c>, <c>search-client.js</c>, and the page-local <c>outline-client.js</c>. Preview hosts can
    /// serve those files directly from the web root; otherwise the current-surface URLs redirect through
    /// <see cref="ResolveLegacySearchAssetBasePath"/> to the packaged RazorDocs assets.
    /// Route ordering matters: index, search, search-index, section, and catch-all routes are registered from most to
    /// least specific so the live preview root continues to behave correctly even when the current docs root is
    /// root-mounted or overlaps published exact-version aliases.
    /// </para>
    /// <para>
    /// The operator-facing harvest health route patterns are always registered before the catch-all docs route so
    /// <c>{DocsRootPath}/_health</c> and <c>{DocsRootPath}/_health.json</c> remain reserved operator paths rather than
    /// falling through to document lookup. The route named <c>razordocs_harvest_health</c> maps the current docs root
    /// health pattern from <see cref="DocsUrlBuilder.BuildHealthUrl"/> to <c>DocsController.HarvestHealth</c>, and
    /// <c>razordocs_harvest_health_json</c> maps <see cref="DocsUrlBuilder.BuildHealthJsonUrl"/> to
    /// <c>DocsController.HarvestHealthJson</c>. The controller actions still gate responses with
    /// <see cref="RazorDocsHarvestHealthVisibility.AreRoutesExposed(RazorDocsOptions, IHostEnvironment)"/>: by default
    /// they return health only in Development, while production hosts must opt in with
    /// <see cref="RazorDocsHarvestHealthOptions.ExposeRoutes"/>. These routes are intended for local and operator
    /// verification, not as unauthenticated public reader navigation.
    /// </para>
    /// </remarks>
    /// <param name="context">Startup context for the application and environment.</param>
    /// <param name="endpoints">Endpoint route builder used to map the module's routes.</param>
    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        var docsOptions = ResolveOptions(endpoints.ServiceProvider);
        var docsUrlBuilder = endpoints.ServiceProvider.GetService(typeof(DocsUrlBuilder)) as DocsUrlBuilder
                             ?? new DocsUrlBuilder(docsOptions);
        if (ShouldPreserveRootStylesheetPath(context))
        {
            // Published/exported standalone hosts can resolve the packaged stylesheet only under /_content.
            // Preserve the historical root stylesheet URL so docs HTML and static exports stay portable.
            MapLegacyAssetRedirect(endpoints, RazorDocsRootStylesheetPath, RazorDocsPackagedStylesheetPath);
        }

        if (ShouldServePreviewAssetsDirectlyFromWebRoot(context, docsOptions, docsUrlBuilder))
        {
            // Versioned root-module hosts mount published release trees at /docs. Serve preview-root assets directly
            // from the live web root so /docs/next or other preview surfaces do not inherit release-exported JS/CSS.
            MapWebRootAsset(endpoints, docsUrlBuilder.BuildAssetUrl("search.css"), "docs/search.css");
            MapWebRootAsset(endpoints, docsUrlBuilder.BuildAssetUrl("minisearch.min.js"), "docs/minisearch.min.js");
            MapWebRootAsset(endpoints, docsUrlBuilder.BuildAssetUrl("search-client.js"), "docs/search-client.js");
            MapWebRootAsset(endpoints, docsUrlBuilder.BuildAssetUrl("outline-client.js"), "docs/outline-client.js");
        }
        else
        {
            var searchAssetBasePath = ResolveLegacySearchAssetBasePath(context);

            // Preserve the active live-surface asset URLs even though the assets now live in the RazorDocs RCL package.
            MapLegacyAssetRedirect(endpoints, docsUrlBuilder.BuildAssetUrl("search.css"), $"{searchAssetBasePath}/search.css");
            MapLegacyAssetRedirect(endpoints, docsUrlBuilder.BuildAssetUrl("minisearch.min.js"), $"{searchAssetBasePath}/minisearch.min.js");
            MapLegacyAssetRedirect(endpoints, docsUrlBuilder.BuildAssetUrl("search-client.js"), $"{searchAssetBasePath}/search-client.js");
            MapLegacyAssetRedirect(endpoints, docsUrlBuilder.BuildAssetUrl("outline-client.js"), $"{searchAssetBasePath}/outline-client.js");
        }

        if (docsOptions.Versioning?.Enabled == true)
        {
            endpoints.MapControllerRoute(
                name: "razordocs_version_entry",
                pattern: TrimLeadingSlash(docsUrlBuilder.DocsEntryRootPath),
                defaults: new
                {
                    controller = "Docs",
                    action = "VersionEntry"
                });

            endpoints.MapControllerRoute(
                name: "razordocs_versions",
                pattern: TrimLeadingSlash(docsUrlBuilder.BuildVersionsUrl()),
                defaults: new
                {
                    controller = "Docs",
                    action = "Versions"
                });
        }

        var currentRootPattern = docsUrlBuilder.CurrentDocsRootPath.TrimStart('/');
        var currentSearchPattern = TrimLeadingSlash(docsUrlBuilder.BuildSearchUrl());
        var currentSearchIndexPattern = TrimLeadingSlash(docsUrlBuilder.BuildSearchIndexUrl());
        var currentHealthPattern = TrimLeadingSlash(docsUrlBuilder.BuildHealthUrl());
        var currentHealthJsonPattern = TrimLeadingSlash(docsUrlBuilder.BuildHealthJsonUrl());
        var currentSectionPattern = TrimLeadingSlash(DocsUrlBuilder.JoinPath(docsUrlBuilder.CurrentDocsRootPath, "sections/{sectionSlug}"));
        var currentDetailsPattern = TrimLeadingSlash(DocsUrlBuilder.JoinPath(docsUrlBuilder.CurrentDocsRootPath, "{*path}"));

        // Index route MUST come before catch-all to be matched first
        endpoints.MapControllerRoute(
            name: "razordocs_index",
            pattern: currentRootPattern,
            defaults: new
            {
                controller = "Docs",
                action = "Index"
            });

        endpoints.MapControllerRoute(
            name: "razordocs_search",
            pattern: currentSearchPattern,
            defaults: new
            {
                controller = "Docs",
                action = "Search"
            });

        endpoints.MapControllerRoute(
            name: "razordocs_search_index",
            pattern: currentSearchIndexPattern,
            defaults: new
            {
                controller = "Docs",
                action = "SearchIndex"
            });

        endpoints.MapControllerRoute(
            name: "razordocs_harvest_health",
            pattern: currentHealthPattern,
            defaults: new
            {
                controller = "Docs",
                action = "HarvestHealth"
            });

        endpoints.MapControllerRoute(
            name: "razordocs_harvest_health_json",
            pattern: currentHealthJsonPattern,
            defaults: new
            {
                controller = "Docs",
                action = "HarvestHealthJson"
            });

        endpoints.MapControllerRoute(
            name: "razordocs_section",
            pattern: currentSectionPattern,
            defaults: new
            {
                controller = "Docs",
                action = "Section"
            });

        endpoints.MapControllerRoute(
            name: "razordocs_doc",
            pattern: currentDetailsPattern,
            defaults: new
            {
                controller = "Docs",
                action = "Details"
            });
    }

    private static void MapLegacyAssetRedirect(IEndpointRouteBuilder endpoints, string route, string targetPath)
    {
        endpoints.MapMethods(
            route,
            [HttpMethods.Get, HttpMethods.Head],
            context =>
            {
                var redirectPath = $"{context.Request.PathBase}{targetPath}{context.Request.QueryString}";

                context.Response.Redirect(redirectPath, permanent: false);
                return Task.CompletedTask;
            });
    }

    private static RazorDocsOptions ResolveOptions(IServiceProvider? services)
    {
        return services?.GetService(typeof(RazorDocsOptions)) as RazorDocsOptions
               ?? (services?.GetService(typeof(IOptionsMonitor<RazorDocsOptions>)) as IOptionsMonitor<RazorDocsOptions>)?.CurrentValue
               ?? new RazorDocsOptions();
    }

    private static void MapWebRootAsset(IEndpointRouteBuilder endpoints, string route, string webRootSubPath)
    {
        endpoints.MapMethods(
            route,
            [HttpMethods.Get, HttpMethods.Head],
            async context =>
            {
                var environment = context.RequestServices.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
                var fileProvider = environment?.WebRootFileProvider;
                if (fileProvider is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var fileInfo = fileProvider.GetFileInfo(webRootSubPath);
                if (!fileInfo.Exists)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = ResolveContentType(webRootSubPath);
                context.Response.ContentLength = fileInfo.Length;
                context.Response.Headers.LastModified = fileInfo.LastModified
                    .ToUniversalTime()
                    .ToString("R", CultureInfo.InvariantCulture);
                if (HttpMethods.IsHead(context.Request.Method))
                {
                    return;
                }

                await context.Response.SendFileAsync(fileInfo, context.RequestAborted);
            });
    }

    private static bool ShouldPreserveRootStylesheetPath(StartupContext context)
    {
        return RazorDocsAssetPathResolver.IsRootModuleAssembly(context.RootModuleAssembly);
    }

    private static bool ShouldServePreviewAssetsDirectlyFromWebRoot(
        StartupContext context,
        RazorDocsOptions options,
        DocsUrlBuilder docsUrlBuilder)
    {
        return ShouldPreserveRootStylesheetPath(context)
               && options.Versioning?.Enabled == true
               && !string.Equals(
                   docsUrlBuilder.CurrentDocsRootPath,
                   DocsUrlBuilder.DocsEntryPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLegacySearchAssetBasePath(StartupContext context)
    {
        return RazorDocsStaticAssetBasePath;
    }

    private static string ResolveContentType(string relativePath)
    {
        return ContentTypeProvider.TryGetContentType(relativePath, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    private static string TrimLeadingSlash(string route)
    {
        return route.TrimStart('/');
    }

}
