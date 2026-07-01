using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Web module configuration for the AppSurface Docs documentation system.
/// </summary>
/// <remarks>
/// This module owns both the live source-backed AppSurface Docs surface and the optional published-version overlay used when
/// <see cref="AppSurfaceDocsVersioningOptions.Enabled" /> is turned on. Service registration wires up harvesting,
/// aggregation, sanitization, URL generation, and version-catalog resolution through <c>services.AddAppSurfaceDocs()</c>,
/// while endpoint and middleware hooks decide whether the host behaves like a plain live docs site or a mixed
/// live-plus-archive experience.
/// </remarks>
/// <remarks>
/// When versioning is enabled and the resolved catalog exposes available published trees, the module can mount those
/// exact-version exports into the request pipeline, reserve the configured route-family root for the recommended
/// release alias, short-circuit matching requests through <see cref="AppSurfaceDocsPublishedTreeHandler" />, and register
/// disposal for any mounted <see cref="PhysicalFileProvider" /> instances when the host stops. Hosts that leave
/// versioning disabled, omit the catalog service, or resolve an empty/unavailable catalog skip those mounts and
/// continue serving only the live source-backed preview surface.
/// </remarks>
public class AppSurfaceDocsWebModule : IAppSurfaceWebModule
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private static readonly Regex MalformedPercentEncodingPattern = new(
        "%(?![0-9A-Fa-f]{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedBrandingAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif",
        ".gif",
        ".ico",
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    private const string SvgAssetExtension = ".svg";
    private static readonly Assembly AppSurfaceDocsAssembly = typeof(AppSurfaceDocsWebModule).Assembly;
    private const string AppSurfaceDocsStaticAssetBasePath = "/_content/ForgeTrust.AppSurface.Docs/docs";
    private const string AppSurfaceDocsPackagedStylesheetPath = "/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css";
    private const string AppSurfaceDocsPackagedBrandIconPath = "docs/appsurface-docs-icon.svg";
    private const string AppSurfaceDocsRootFaviconPath = "/favicon.ico";
    private const string AppSurfaceDocsRootStylesheetPath = "/css/site.gen.css";
    private const string EmbeddedAssetResourcePrefix = "AppSurfaceDocsEmbeddedAssets/";

    /// <inheritdoc />
    public bool IncludeAsApplicationPart => true;

    /// <inheritdoc />
    public void ConfigureWebOptions(StartupContext context, WebOptions options)
    {
        options.StaticFiles.EnableStaticWebAssets = true;
    }

    /// <summary>
    /// Registers services required by the AppSurface Docs module into the provided service collection.
    /// </summary>
    /// <remarks>
    /// Adds the AppSurface Docs harvesting, aggregation, and sanitization services via <c>services.AddAppSurfaceDocs()</c>.
    /// AppSurface Docs styling is compiled into the package during the AppSurface Docs build and the layout resolves the correct
    /// static asset path for root-module versus embedded consumer hosts, so hosts do not register
    /// <c>services.AddTailwind()</c> just to light up the embedded docs UI.
    /// </remarks>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddAppSurfaceDocs();
        services.Replace(ServiceDescriptor.Singleton(AppSurfaceDocsAssetPathResolver.CreateForRootModule(context.RootModuleAssembly)));
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
    /// <see cref="AppSurfaceDocsVersionCatalogService" /> yields at least one available published tree. In that case the
    /// module mounts exact-version exports, optionally adds the configured route-family root alias for the recommended
    /// release, and inserts a short-circuiting middleware branch that lets <see cref="AppSurfaceDocsPublishedTreeHandler" /> serve
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

        var catalogService = app.ApplicationServices.GetService(typeof(AppSurfaceDocsVersionCatalogService)) as AppSurfaceDocsVersionCatalogService;
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

        var publishedTreeHandler = new AppSurfaceDocsPublishedTreeHandler(
            mounts,
            docsUrlBuilder.CurrentDocsRootPath,
            docsUrlBuilder.RouteRootPath,
            docsUrlBuilder.PublicOrigin,
            options.Versioning.MaxRewrittenFileSizeBytes,
            app.ApplicationServices.GetService<ILogger<AppSurfaceDocsPublishedTreeHandler>>());
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
    /// Frozen route manifest caches follow the same reuse rule: exact tree paths are resolved to full paths, trimmed of
    /// trailing directory separators, and compared with the platform-aware physical path comparer so canonical and
    /// recommended mounts that point at the same tree share one <see cref="AppSurfaceDocsFrozenRouteManifestCache" /> when
    /// they have the same verification model. If a later mount for the same tree has a verified release archive and the
    /// existing cache is still an unverified lazy filesystem cache, the helper replaces that cache entry with a verified
    /// snapshot built from <see cref="AppSurfaceDocsVerifiedReleaseArchive.FrozenRouteManifest" />. Consumers should treat
    /// the returned cache instances as immutable mount dependencies: recommended aliases and public version mounts observe
    /// the verified snapshot when one exists, otherwise they share the same lazy filesystem-backed read model.
    /// </remarks>
    /// <param name="catalog">The resolved version catalog that describes available published trees.</param>
    /// <param name="docsUrlBuilder">The configured URL builder that supplies the route-family alias root.</param>
    /// <returns>
    /// The ordered mount list plus the unique provider instances that should be disposed with the host lifetime.
    /// </returns>
    internal static (IReadOnlyList<AppSurfaceDocsPublishedTreeMount> Mounts, IReadOnlyList<PhysicalFileProvider> Providers) BuildPublishedTreeMounts(
        AppSurfaceDocsResolvedVersionCatalog catalog,
        DocsUrlBuilder docsUrlBuilder)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(docsUrlBuilder);

        var providersByPath = new Dictionary<string, PhysicalFileProvider>(
            AppSurfaceDocsTrustedReleasePathGuard.PhysicalPathComparer);
        var manifestCachesByPath = new Dictionary<string, AppSurfaceDocsFrozenRouteManifestCache>(
            AppSurfaceDocsTrustedReleasePathGuard.PhysicalPathComparer);
        var mounts = new List<AppSurfaceDocsPublishedTreeMount>();

        foreach (var version in catalog.PublicVersions.Where(version => version.IsAvailable && version.ExactTreePath is not null))
        {
            var provider = GetOrCreateProvider(version.ExactTreePath!, providersByPath);
            var manifestCache = GetOrCreateFrozenRouteManifestCache(
                version.ExactTreePath!,
                provider,
                version.VerifiedReleaseArchive,
                manifestCachesByPath);
            mounts.Add(new AppSurfaceDocsPublishedTreeMount(
                version.ExactRootUrl,
                provider,
                version.ExactTreePath,
                manifestCache,
                version.ArchiveVerificationState,
                version.VerifiedReleaseArchive));
        }

        if (catalog.RecommendedVersion is { IsAvailable: true, ExactTreePath: not null } recommendedVersion)
        {
            var provider = GetOrCreateProvider(recommendedVersion.ExactTreePath, providersByPath);
            var manifestCache = GetOrCreateFrozenRouteManifestCache(
                recommendedVersion.ExactTreePath,
                provider,
                recommendedVersion.VerifiedReleaseArchive,
                manifestCachesByPath);
            mounts.Add(new AppSurfaceDocsPublishedTreeMount(
                docsUrlBuilder.DocsEntryRootPath,
                provider,
                recommendedVersion.ExactTreePath,
                manifestCache,
                recommendedVersion.ArchiveVerificationState,
                recommendedVersion.VerifiedReleaseArchive,
                recommendedVersion.ExactRootUrl));
        }

        return (mounts, providersByPath.Values.ToList());
    }

    private static PhysicalFileProvider GetOrCreateProvider(
        string exactTreePath,
        IDictionary<string, PhysicalFileProvider> providersByPath)
    {
        var normalizedPath = AppSurfaceDocsTrustedReleasePathGuard.NormalizePhysicalPath(exactTreePath);
        if (providersByPath.TryGetValue(normalizedPath, out var provider))
        {
            return provider;
        }

        provider = new PhysicalFileProvider(normalizedPath, ExclusionFilters.None);
        providersByPath[normalizedPath] = provider;
        return provider;
    }

    private static AppSurfaceDocsFrozenRouteManifestCache GetOrCreateFrozenRouteManifestCache(
        string exactTreePath,
        PhysicalFileProvider provider,
        AppSurfaceDocsVerifiedReleaseArchive? verifiedReleaseArchive,
        IDictionary<string, AppSurfaceDocsFrozenRouteManifestCache> manifestCachesByPath)
    {
        var normalizedPath = AppSurfaceDocsTrustedReleasePathGuard.NormalizePhysicalPath(exactTreePath);
        if (manifestCachesByPath.TryGetValue(normalizedPath, out var cache))
        {
            if (verifiedReleaseArchive is not null && !cache.UsesVerifiedSnapshot)
            {
                cache = new AppSurfaceDocsFrozenRouteManifestCache(verifiedReleaseArchive.FrozenRouteManifest, normalizedPath);
                manifestCachesByPath[normalizedPath] = cache;
            }

            return cache;
        }

        cache = verifiedReleaseArchive is null
            ? new AppSurfaceDocsFrozenRouteManifestCache(provider, normalizedPath)
            : new AppSurfaceDocsFrozenRouteManifestCache(verifiedReleaseArchive.FrozenRouteManifest, normalizedPath);
        manifestCachesByPath[normalizedPath] = cache;
        return cache;
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
    /// When AppSurface Docs is the root module assembly, standalone and static-export hosts preserve the historical
    /// <c>/css/site.gen.css</c> URL by redirecting it to the packaged Razor Class Library stylesheet at
    /// <c>/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css</c>. Embedded hosts do not register that
    /// redirect because they already link to the packaged asset directly. Redirects preserve the request
    /// <see cref="HttpRequest.PathBase"/> and query string so legacy links continue to work behind a virtual path, but
    /// the path base and configured package target must remain single-slash app-relative path components.
    /// </para>
    /// <para>
    /// When versioning is enabled, this hook also reserves the stable version entry route at the configured route-family
    /// root, adds the archive surface below that route root, and serves preview-surface assets from either the live web
    /// root or the packaged Razor Class Library depending on whether published-tree mounts can shadow the stable docs root.
    /// Asset routes are built with <see cref="DocsUrlBuilder.BuildAssetUrl(string)"/> for <c>search.css</c>,
    /// <c>minisearch.min.js</c>, <c>search-client.js</c>, and the page-local <c>outline-client.js</c>. The packaged
    /// AppSurface brand icon is also served from an embedded fallback so static exports that disable static-web-assets
    /// can still validate the layout image. Consumer-owned branding assets can be served from a configured filesystem
    /// directory, including directories outside AppSurface Docs packaged static web assets, under a dedicated request
    /// prefix. Preview hosts can serve those files directly from the web root; otherwise the current-surface URLs redirect through
    /// <see cref="ResolveLegacySearchAssetBasePath"/> to the packaged AppSurface Docs assets. Legacy asset redirects preserve
    /// only the request query string, while the redirect path itself is constrained to an app-relative URL so
    /// cache-busting parameters cannot turn the redirect into an external hop.
    /// Route ordering matters: index, search, search-index, section, and catch-all routes are registered from most to
    /// least specific so the live preview root continues to behave correctly even when the current docs root is
    /// root-mounted or overlaps published exact-version aliases.
    /// </para>
    /// <para>
    /// When AppSurface Docs is the root module, the bare application root redirects to the configured docs home and
    /// <c>/favicon.ico</c> serves the packaged AppSurface Docs document-layers SVG mark unless the host configures
    /// <see cref="AppSurfaceDocsFaviconOptions.SvgPath" />. In that case <c>/favicon.ico</c> redirects to the configured
    /// SVG path so standalone docs hosts can keep the conventional browser favicon probe aligned with rendered
    /// <c>&lt;link rel="icon"&gt;</c> metadata. Embedded hosts do not get these root routes; their owning app should
    /// decide what <c>/</c> and <c>/favicon.ico</c> mean.
    /// </para>
    /// <para>
    /// The operator-facing diagnostics route patterns are always registered before the catch-all docs route so
    /// <c>{DocsRootPath}/_harvest</c>, <c>{DocsRootPath}/_harvest/rebuild</c>, <c>{DocsRootPath}/_health</c>,
    /// <c>{DocsRootPath}/_health.json</c>, <c>{DocsRootPath}/_routes</c>, and <c>{DocsRootPath}/_routes.json</c>
    /// remain reserved operator paths rather than falling through to document lookup.
    /// The route named <c>appsurfacedocs_harvest</c> maps the current docs root harvest observatory pattern from
    /// <see cref="DocsUrlBuilder.BuildHarvestUrl"/> to <c>DocsController.Harvest</c>. The route named
    /// <c>appsurfacedocs_harvest_rebuild</c> maps <see cref="DocsUrlBuilder.BuildHarvestRebuildUrl"/> to
    /// <c>DocsController.RebuildHarvest</c>. The route named <c>appsurfacedocs_harvest_health</c> maps the current docs
    /// root health pattern from <see cref="DocsUrlBuilder.BuildHealthUrl"/> to <c>DocsController.HarvestHealth</c>, and
    /// <c>appsurfacedocs_harvest_health_json</c> maps <see cref="DocsUrlBuilder.BuildHealthJsonUrl"/> to
    /// <c>DocsController.HarvestHealthJson</c>. Route inspector routes map
    /// <see cref="DocsUrlBuilder.BuildRouteInspectorUrl"/> and <see cref="DocsUrlBuilder.BuildRouteInspectorJsonUrl"/>
    /// to <c>DocsController.RouteInspector</c> and <c>DocsController.RouteInspectorJson</c>. The controller actions still gate
    /// responses with their exposure options: harvest health uses
    /// <see cref="AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(AppSurfaceDocsOptions, IHostEnvironment)"/>: by default
    /// they return health only in Development, while production hosts must opt in with
    /// <see cref="AppSurfaceDocsHarvestHealthOptions.ExposeRoutes"/>; route inspector uses
    /// <see cref="AppSurfaceDocsDiagnosticsOptions.ExposeRouteInspector"/>. These routes are intended for local and
    /// operator verification, not as unauthenticated public reader navigation.
    /// </para>
    /// <para>
    /// The route named <c>appsurfacedocs_search_index_refresh</c> maps the pattern produced by
    /// <see cref="DocsUrlBuilder.BuildSearchIndexRefreshUrl"/> to <c>DocsController.RefreshSearchIndex</c>. That
    /// operator route is browser-form-shaped and accepts only <c>POST</c>; the companion unsupported-method endpoint
    /// rejects common non-POST verbs with HTTP 405 and an <c>Allow: POST</c> response header instead of letting the
    /// request fall through to reader document lookup or status-code-page rendering.
    /// </para>
    /// </remarks>
    /// <param name="context">Startup context for the application and environment.</param>
    /// <param name="endpoints">Endpoint route builder used to map the module's routes.</param>
    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        var docsOptions = ResolveOptions(endpoints.ServiceProvider);
        var docsUrlBuilder = endpoints.ServiceProvider.GetService(typeof(DocsUrlBuilder)) as DocsUrlBuilder
                             ?? new DocsUrlBuilder(docsOptions);
        MapEmbeddedAssetFallback(endpoints, AppSurfaceDocsPackagedStylesheetPath, "css/site.gen.css");
        MapEmbeddedAssetFallback(
            endpoints,
            $"{AppSurfaceDocsStaticAssetBasePath}/appsurface-docs-icon.svg",
            AppSurfaceDocsPackagedBrandIconPath);
        MapEmbeddedAssetFallback(endpoints, $"{AppSurfaceDocsStaticAssetBasePath}/search.css", "docs/search.css");
        MapEmbeddedAssetFallback(endpoints, $"{AppSurfaceDocsStaticAssetBasePath}/minisearch.min.js", "docs/minisearch.min.js");
        MapEmbeddedAssetFallback(endpoints, $"{AppSurfaceDocsStaticAssetBasePath}/search-client.js", "docs/search-client.js");
        MapEmbeddedAssetFallback(endpoints, $"{AppSurfaceDocsStaticAssetBasePath}/outline-client.js", "docs/outline-client.js");
        MapBrandingAssetDirectory(endpoints, docsOptions);

        if (ShouldPreserveRootStylesheetPath(context))
        {
            // Published/exported standalone hosts can resolve the packaged stylesheet only under /_content.
            // Preserve the historical root stylesheet URL so docs HTML and static exports stay portable.
            MapLegacyAssetRedirect(endpoints, AppSurfaceDocsRootStylesheetPath, AppSurfaceDocsPackagedStylesheetPath);

            var configuredSvgFaviconPath = ResolveConfiguredRootFaviconRedirectPath(docsOptions);
            if (configuredSvgFaviconPath is not null)
            {
                // Browsers request /favicon.ico implicitly. Keep that conventional probe aligned
                // with a standalone host's configured SVG favicon while embedded hosts keep ownership.
                MapLegacyAssetRedirect(endpoints, AppSurfaceDocsRootFaviconPath, configuredSvgFaviconPath);
            }
            else
            {
                // Serve the packaged document-layers mark for standalone docs hosts without taking over embedded app roots.
                MapEmbeddedAssetFallback(endpoints, AppSurfaceDocsRootFaviconPath, AppSurfaceDocsPackagedBrandIconPath);
            }
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

            // Preserve the active live-surface asset URLs even though the assets now live in the AppSurface Docs RCL package.
            MapLegacyAssetRedirect(endpoints, docsUrlBuilder.BuildAssetUrl("search.css"), $"{searchAssetBasePath}/search.css");
            MapLegacyAssetRedirect(endpoints, docsUrlBuilder.BuildAssetUrl("minisearch.min.js"), $"{searchAssetBasePath}/minisearch.min.js");
            MapLegacyAssetRedirect(endpoints, docsUrlBuilder.BuildAssetUrl("search-client.js"), $"{searchAssetBasePath}/search-client.js");
            MapLegacyAssetRedirect(endpoints, docsUrlBuilder.BuildAssetUrl("outline-client.js"), $"{searchAssetBasePath}/outline-client.js");
        }

        if (docsOptions.Versioning?.Enabled == true)
        {
            endpoints.MapControllerRoute(
                name: "appsurfacedocs_version_entry",
                pattern: TrimLeadingSlash(docsUrlBuilder.DocsEntryRootPath),
                defaults: new
                {
                    controller = "Docs",
                    action = "VersionEntry"
                });

            endpoints.MapControllerRoute(
                name: "appsurfacedocs_versions",
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
        var currentSearchIndexRefreshPattern = TrimLeadingSlash(docsUrlBuilder.BuildSearchIndexRefreshUrl());
        var currentHarvestPattern = TrimLeadingSlash(docsUrlBuilder.BuildHarvestUrl());
        var currentHarvestRebuildPattern = TrimLeadingSlash(docsUrlBuilder.BuildHarvestRebuildUrl());
        var currentHealthPattern = TrimLeadingSlash(docsUrlBuilder.BuildHealthUrl());
        var currentHealthJsonPattern = TrimLeadingSlash(docsUrlBuilder.BuildHealthJsonUrl());
        var currentRouteInspectorPattern = TrimLeadingSlash(docsUrlBuilder.BuildRouteInspectorUrl());
        var currentRouteInspectorJsonPattern = TrimLeadingSlash(docsUrlBuilder.BuildRouteInspectorJsonUrl());
        var currentMetricsCollectPattern = TrimLeadingSlash(docsUrlBuilder.BuildMetricsCollectUrl());
        var currentSearchQualityPattern = TrimLeadingSlash(docsUrlBuilder.BuildSearchQualityUrl());
        var currentSectionPattern = TrimLeadingSlash(DocsUrlBuilder.JoinPath(docsUrlBuilder.CurrentDocsRootPath, "sections/{sectionSlug}"));
        var currentDetailsPattern = TrimLeadingSlash(DocsUrlBuilder.JoinPath(docsUrlBuilder.CurrentDocsRootPath, "{*path}"));

        if (ShouldMapRootDocsRedirect(context, docsUrlBuilder))
        {
            MapRootDocsRedirect(endpoints, docsUrlBuilder.BuildHomeUrl());
        }

        // Index route MUST come before catch-all to be matched first
        endpoints.MapControllerRoute(
            name: "appsurfacedocs_index",
            pattern: currentRootPattern,
            defaults: new
            {
                controller = "Docs",
                action = "Index"
            });

        endpoints.MapControllerRoute(
            name: "appsurfacedocs_search",
            pattern: currentSearchPattern,
            defaults: new
            {
                controller = "Docs",
                action = "Search"
            });

        endpoints.MapControllerRoute(
            name: "appsurfacedocs_search_index",
            pattern: currentSearchIndexPattern,
            defaults: new
            {
                controller = "Docs",
                action = "SearchIndex"
            });

        endpoints.MapMethods(
            currentSearchIndexRefreshPattern,
            [
                HttpMethods.Delete,
                HttpMethods.Get,
                HttpMethods.Head,
                HttpMethods.Options,
                HttpMethods.Patch,
                HttpMethods.Put
            ],
            RejectSearchIndexRefreshUnsupportedMethodAsync);

        endpoints.MapControllerRoute(
                name: "appsurfacedocs_search_index_refresh",
                pattern: currentSearchIndexRefreshPattern,
                defaults: new
                {
                    controller = "Docs",
                    action = "RefreshSearchIndex"
                })
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

        var harvest = endpoints.MapControllerRoute(
            name: "appsurfacedocs_harvest",
            pattern: currentHarvestPattern,
            defaults: new
            {
                controller = "Docs",
                action = "Harvest"
            });
        ApplyHarvestReadAuthorizationPolicy(harvest, docsOptions, endpoints.ServiceProvider);

        if (AreHarvestRoutesHidden(docsOptions, endpoints.ServiceProvider))
        {
            endpoints.Map(currentHarvestRebuildPattern, ReturnNotFoundAsync)
                .WithMetadata(new AllowAnonymousAttribute());
        }
        else
        {
            endpoints.MapMethods(
                currentHarvestRebuildPattern,
                [
                    HttpMethods.Delete,
                    HttpMethods.Get,
                    HttpMethods.Head,
                    HttpMethods.Options,
                    HttpMethods.Patch,
                    HttpMethods.Put
                ],
                RejectHarvestRebuildUnsupportedMethodAsync);

            endpoints.MapControllerRoute(
                    name: "appsurfacedocs_harvest_rebuild",
                    pattern: currentHarvestRebuildPattern,
                    defaults: new
                    {
                        controller = "Docs",
                        action = "RebuildHarvest"
                    })
                .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));
        }

        var health = endpoints.MapControllerRoute(
            name: "appsurfacedocs_harvest_health",
            pattern: currentHealthPattern,
            defaults: new
            {
                controller = "Docs",
                action = "HarvestHealth"
            });

        var healthJson = endpoints.MapControllerRoute(
            name: "appsurfacedocs_harvest_health_json",
            pattern: currentHealthJsonPattern,
            defaults: new
            {
                controller = "Docs",
                action = "HarvestHealthJson"
            });
        ApplyHealthAuthorizationPolicy(health, healthJson, docsOptions, endpoints.ServiceProvider);

        var routeInspector = endpoints.MapControllerRoute(
            name: "appsurfacedocs_route_inspector",
            pattern: currentRouteInspectorPattern,
            defaults: new
            {
                controller = "Docs",
                action = "RouteInspector"
            });

        var routeInspectorJson = endpoints.MapControllerRoute(
            name: "appsurfacedocs_route_inspector_json",
            pattern: currentRouteInspectorJsonPattern,
            defaults: new
            {
                controller = "Docs",
                action = "RouteInspectorJson"
            });
        ApplyRouteInspectorReadAuthorizationPolicy(
            routeInspector,
            routeInspectorJson,
            docsOptions,
            endpoints.ServiceProvider);

        if (ShouldMapHostedMetricsCollection(docsOptions))
        {
            endpoints.MapMethods(
                currentMetricsCollectPattern,
                [
                    HttpMethods.Delete,
                    HttpMethods.Get,
                    HttpMethods.Head,
                    HttpMethods.Options,
                    HttpMethods.Patch,
                    HttpMethods.Put
                ],
                RejectMetricsCollectUnsupportedMethodAsync);

            endpoints.MapControllerRoute(
                    name: "appsurfacedocs_metrics_collect",
                    pattern: currentMetricsCollectPattern,
                    defaults: new
                    {
                        controller = "Docs",
                        action = "CollectMetrics"
                    })
                .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));
        }

        if (ShouldMapHostedSearchQualityReview(docsOptions))
        {
            endpoints.MapControllerRoute(
                name: "appsurfacedocs_search_quality",
                pattern: currentSearchQualityPattern,
                defaults: new
                {
                    controller = "Docs",
                    action = "SearchQuality"
                });
        }

        endpoints.MapControllerRoute(
            name: "appsurfacedocs_section",
            pattern: currentSectionPattern,
            defaults: new
            {
                controller = "Docs",
                action = "Section"
            });

        endpoints.MapControllerRoute(
            name: "appsurfacedocs_doc",
            pattern: currentDetailsPattern,
            defaults: new
            {
                controller = "Docs",
                action = "Details"
            });
    }

    private static void ApplyHealthAuthorizationPolicy(
        IEndpointConventionBuilder health,
        IEndpointConventionBuilder healthJson,
        AppSurfaceDocsOptions docsOptions,
        IServiceProvider services)
    {
        var policyName = ResolveHealthAuthorizationPolicyName(docsOptions, services);
        if (policyName is null)
        {
            if (AreHarvestRoutesHidden(docsOptions, services))
            {
                AllowAnonymousFallbackBypass(health);
                AllowAnonymousFallbackBypass(healthJson);
            }

            return;
        }

        health.RequireAuthorization(policyName);
        healthJson.RequireAuthorization(policyName);
    }

    private static void ApplyHarvestReadAuthorizationPolicy(
        IEndpointConventionBuilder harvest,
        AppSurfaceDocsOptions docsOptions,
        IServiceProvider services)
    {
        var policyName = ResolveHarvestReadAuthorizationPolicyName(docsOptions, services);
        if (policyName is null)
        {
            if (AreHarvestRoutesHidden(docsOptions, services))
            {
                AllowAnonymousFallbackBypass(harvest);
            }

            return;
        }

        harvest.RequireAuthorization(policyName);
    }

    private static void ApplyRouteInspectorReadAuthorizationPolicy(
        IEndpointConventionBuilder routeInspector,
        IEndpointConventionBuilder routeInspectorJson,
        AppSurfaceDocsOptions docsOptions,
        IServiceProvider services)
    {
        var policyName = ResolveRouteInspectorAuthorizationPolicyName(docsOptions, services);
        if (policyName is null)
        {
            if (AreRouteInspectorRoutesHidden(docsOptions, services))
            {
                AllowAnonymousFallbackBypass(routeInspector);
                AllowAnonymousFallbackBypass(routeInspectorJson);
            }

            return;
        }

        routeInspector.RequireAuthorization(policyName);
        routeInspectorJson.RequireAuthorization(policyName);
    }

    /// <summary>
    /// Resolves the effective health-route authorization policy name, or <see langword="null"/> when no policy should be
    /// applied.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when no health or shared read policy should be applied, when no
    /// <see cref="IWebHostEnvironment"/> is available, or when the health routes are not exposed for the current
    /// environment. <see cref="ApplyHealthAuthorizationPolicy"/> depends on this ordering so hidden health routes keep
    /// returning <c>404</c> before authorization metadata is added. The legacy health policy takes precedence when it is
    /// configured; otherwise the shared diagnostics read policy protects exposed health routes.
    /// </remarks>
    internal static string? ResolveHealthAuthorizationPolicyName(AppSurfaceDocsOptions docsOptions, IServiceProvider services)
    {
        var environment = services.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
        if (environment is null || !AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(docsOptions, environment))
        {
            return null;
        }

        var healthPolicyName = docsOptions.Harvest?.Health?.AuthorizationPolicy;
        return !string.IsNullOrWhiteSpace(healthPolicyName)
            ? healthPolicyName
            : NormalizeReadPolicyName(docsOptions);
    }

    internal static string? ResolveHarvestReadAuthorizationPolicyName(
        AppSurfaceDocsOptions docsOptions,
        IServiceProvider services)
    {
        var policyName = NormalizeReadPolicyName(docsOptions);
        if (policyName is null)
        {
            return null;
        }

        var environment = services.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
        if (environment is null || !AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(docsOptions, environment))
        {
            return null;
        }

        return policyName;
    }

    internal static string? ResolveRouteInspectorAuthorizationPolicyName(
        AppSurfaceDocsOptions docsOptions,
        IServiceProvider services)
    {
        var policyName = NormalizeReadPolicyName(docsOptions);
        if (policyName is null)
        {
            return null;
        }

        var environment = services.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
        if (environment is null || !AppSurfaceDocsDiagnosticsVisibility.IsRouteInspectorExposed(docsOptions, environment))
        {
            return null;
        }

        return policyName;
    }

    private static string? NormalizeReadPolicyName(AppSurfaceDocsOptions docsOptions)
    {
        var policyName = docsOptions.Diagnostics?.OperatorReadPolicy;
        return string.IsNullOrWhiteSpace(policyName) ? null : policyName;
    }

    private static bool AreHarvestRoutesHidden(AppSurfaceDocsOptions docsOptions, IServiceProvider services)
    {
        var environment = services.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
        return environment is not null && !AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(docsOptions, environment);
    }

    private static bool AreRouteInspectorRoutesHidden(AppSurfaceDocsOptions docsOptions, IServiceProvider services)
    {
        var environment = services.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
        return environment is not null && !AppSurfaceDocsDiagnosticsVisibility.IsRouteInspectorExposed(docsOptions, environment);
    }

    private static void AllowAnonymousFallbackBypass(IEndpointConventionBuilder endpoint)
    {
        endpoint.WithMetadata(new AllowAnonymousAttribute());
    }

    private static void MapLegacyAssetRedirect(IEndpointRouteBuilder endpoints, string route, string targetPath)
    {
        endpoints.MapMethods(
            route,
            [HttpMethods.Get, HttpMethods.Head],
            context =>
            {
                var redirectPath = BuildLegacyAssetRedirectPath(
                    context.Request.PathBase,
                    targetPath,
                    context.Request.QueryString);

                return Results.LocalRedirect(redirectPath, permanent: false).ExecuteAsync(context);
            });
    }

    /// <summary>
    /// Builds the local redirect target used when historical docs asset URLs move to packaged Razor Class Library assets.
    /// </summary>
    /// <param name="pathBase">The current request path base to preserve for applications mounted below a prefix.</param>
    /// <param name="targetPath">The package asset path selected by AppSurface Docs endpoint configuration.</param>
    /// <param name="queryString">The request query string to preserve for cache-busting or diagnostics parameters.</param>
    /// <returns>An escaped, single-slash app-relative redirect target with the original query string appended.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="pathBase"/> or <paramref name="targetPath"/> is not a safe app-relative path
    /// component.
    /// </exception>
    /// <remarks>
    /// This helper intentionally treats query text as data appended after the validated path. It validates the unescaped
    /// path base so unsafe separators are not hidden by URI formatting, then emits the escaped path base used for
    /// redirect headers. A root path base (<c>/</c>) is normalized to empty so the redirect stays single-slash local.
    /// It does not allow the path base or target path to be absolute, protocol-relative,
    /// backslash-prefixed, or control-character-bearing because those shapes can be interpreted by clients as redirects
    /// away from the current host.
    /// </remarks>
    internal static string BuildLegacyAssetRedirectPath(PathString pathBase, string targetPath, QueryString queryString)
    {
        var pathBaseValue = pathBase.Value ?? string.Empty;
        if (!IsSafeLocalPathComponent(pathBaseValue, allowEmpty: true))
        {
            throw new InvalidOperationException(
                "The AppSurface Docs legacy asset redirect path base is not app-relative.");
        }

        if (!IsSafeLocalPathComponent(targetPath, allowEmpty: false))
        {
            throw new InvalidOperationException(
                "The AppSurface Docs legacy asset redirect target is not app-relative.");
        }

        var pathBasePrefix = string.Equals(pathBaseValue, "/", StringComparison.Ordinal)
            ? string.Empty
            : pathBase.ToUriComponent();

        return string.Concat(pathBasePrefix, targetPath, queryString.ToUriComponent());
    }

    private static bool IsSafeLocalPathComponent(string? path, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(path))
        {
            return allowEmpty;
        }

        if (path[0] != '/'
            || path.Length > 1 && (path[1] == '/' || path[1] == '\\')
            || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in path)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static void MapRootDocsRedirect(IEndpointRouteBuilder endpoints, string docsHomeUrl)
    {
        endpoints.MapMethods(
            "/",
            [HttpMethods.Get, HttpMethods.Head],
            context =>
            {
                context.Response.Redirect(
                    BuildPathBaseAwareRedirectUrl(context, docsHomeUrl),
                    permanent: false);
                return Task.CompletedTask;
            });
    }

    private static Task RejectSearchIndexRefreshUnsupportedMethodAsync(HttpContext context)
    {
        var statusCodePages = context.Features.Get<IStatusCodePagesFeature>();
        if (statusCodePages is not null)
        {
            statusCodePages.Enabled = false;
        }

        context.Response.OnStarting(
            static state =>
            {
                var httpContext = (HttpContext)state;
                httpContext.Response.Headers["Allow"] = DocsUrlBuilder.SearchIndexRefreshMethod;
                return Task.CompletedTask;
            },
            context);
        context.Response.Headers["Allow"] = DocsUrlBuilder.SearchIndexRefreshMethod;
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return Task.CompletedTask;
    }

    private static Task ReturnNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    private static Task RejectHarvestRebuildUnsupportedMethodAsync(HttpContext context)
    {
        var statusCodePages = context.Features.Get<IStatusCodePagesFeature>();
        if (statusCodePages is not null)
        {
            statusCodePages.Enabled = false;
        }

        context.Response.Headers["Allow"] = DocsUrlBuilder.HarvestRebuildMethod;
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return Task.CompletedTask;
    }

    private static Task RejectMetricsCollectUnsupportedMethodAsync(HttpContext context)
    {
        var statusCodePages = context.Features.Get<IStatusCodePagesFeature>();
        if (statusCodePages is not null)
        {
            statusCodePages.Enabled = false;
        }

        context.Response.OnStarting(
            static state =>
            {
                var httpContext = (HttpContext)state;
                httpContext.Response.Headers["Allow"] = HttpMethods.Post;
                return Task.CompletedTask;
            },
            context);
        context.Response.Headers["Allow"] = HttpMethods.Post;
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return Task.CompletedTask;
    }

    private static bool ShouldMapHostedMetricsCollection(AppSurfaceDocsOptions options)
    {
        return options.Metrics?.Enabled == true
               && options.Metrics.HostedCollection?.Enabled == true;
    }

    private static bool ShouldMapHostedSearchQualityReview(AppSurfaceDocsOptions options)
    {
        return ShouldMapHostedMetricsCollection(options)
               && options.Metrics?.HostedReview?.Enabled == true;
    }

    private static AppSurfaceDocsOptions ResolveOptions(IServiceProvider? services)
    {
        return services?.GetService(typeof(AppSurfaceDocsOptions)) as AppSurfaceDocsOptions
               ?? (services?.GetService(typeof(IOptionsMonitor<AppSurfaceDocsOptions>)) as IOptionsMonitor<AppSurfaceDocsOptions>)?.CurrentValue
               ?? new AppSurfaceDocsOptions();
    }

    [ExcludeFromCodeCoverage(
        Justification = "Private endpoint closure for preview asset serving; integration coverage verifies the mapped public asset routes.")]
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
                    if (!await TryWriteEmbeddedAssetAsync(context, webRootSubPath))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                    }

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

    [ExcludeFromCodeCoverage(
        Justification = "Private endpoint closure for static asset fallback; integration coverage verifies the mapped public asset routes.")]
    private static void MapEmbeddedAssetFallback(IEndpointRouteBuilder endpoints, string route, string webRootSubPath)
    {
        endpoints.MapMethods(
            route,
            [HttpMethods.Get, HttpMethods.Head],
            async context =>
            {
                if (!await TryWriteEmbeddedAssetAsync(context, webRootSubPath))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                }
            });
    }

    [ExcludeFromCodeCoverage(
        Justification = "Private endpoint closure for configured branding assets; integration coverage verifies successful and rejected asset requests.")]
    private static void MapBrandingAssetDirectory(IEndpointRouteBuilder endpoints, AppSurfaceDocsOptions options)
    {
        var directoryPath = ResolveBrandingAssetsDirectoryPath(endpoints.ServiceProvider, options);
        if (directoryPath is null)
        {
            return;
        }

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Configured AppSurfaceDocs:Identity:BrandingAssets:DirectoryPath does not exist: '{directoryPath}'.");
        }

        var requestPath = ResolveBrandingAssetsRequestPath(options);
        if (requestPath is null)
        {
            return;
        }

        var provider = CreateLifetimeOwnedBrandingAssetProvider(endpoints.ServiceProvider, directoryPath);
        var allowSvgAssets = options.Identity?.BrandingAssets?.AllowSvgAssets == true;

        endpoints.MapMethods(
            $"{requestPath}/{{*assetPath}}",
            [HttpMethods.Get, HttpMethods.Head],
            async context =>
            {
                if (!TryResolveSafeBrandingAssetPath(
                        context.Request.RouteValues["assetPath"],
                        allowSvgAssets,
                        out var assetPath))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var fileInfo = provider.GetFileInfo(assetPath);
                if (!fileInfo.Exists || fileInfo.IsDirectory)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = ResolveContentType(assetPath);
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

    [ExcludeFromCodeCoverage(
        Justification = "Private provider ownership transfer; route integration covers success and failure cleanup is defensive.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The provider is captured by the endpoint route and disposed through IHostApplicationLifetime when the host stops.")]
    private static PhysicalFileProvider CreateLifetimeOwnedBrandingAssetProvider(IServiceProvider services, string directoryPath)
    {
        var provider = new PhysicalFileProvider(directoryPath);
        try
        {
            RegisterMountedProviderDisposal(services, [provider]);
            return provider;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolves the configured branding asset directory against the repository root or content root.
    /// </summary>
    internal static string? ResolveBrandingAssetsDirectoryPath(IServiceProvider services, AppSurfaceDocsOptions options)
    {
        var configuredPath = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(options.Identity?.BrandingAssets?.DirectoryPath);
        if (configuredPath is null)
        {
            return null;
        }

        var fullPath = Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(configuredPath, ResolveBrandingAssetsBaseDirectory(services, options));
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string ResolveBrandingAssetsBaseDirectory(IServiceProvider services, AppSurfaceDocsOptions options)
    {
        var environment = services.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
        var contentRootPath = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(environment?.ContentRootPath)
                              ?? Directory.GetCurrentDirectory();
        var fullContentRootPath = Path.GetFullPath(contentRootPath);
        var repositoryRoot = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(options.Source?.RepositoryRoot);

        return repositoryRoot is null
            ? fullContentRootPath
            : Path.IsPathRooted(repositoryRoot)
                ? Path.GetFullPath(repositoryRoot)
                : Path.GetFullPath(repositoryRoot, fullContentRootPath);
    }

    /// <summary>
    /// Resolves the browser route prefix used for configured branding assets.
    /// </summary>
    internal static string? ResolveBrandingAssetsRequestPath(AppSurfaceDocsOptions options)
    {
        var requestPath = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(options.Identity?.BrandingAssets?.RequestPath)
                          ?? AppSurfaceDocsBrandingAssetsOptions.DefaultRequestPath;
        if (!AppSurfaceDocsIdentityPath.TryNormalizeBrowserPath(requestPath, out var normalizedPath, out _))
        {
            return null;
        }

        var rootPath = normalizedPath?.StartsWith("~/", StringComparison.Ordinal) == true
            ? "/" + normalizedPath[2..]
            : normalizedPath;
        if (string.IsNullOrWhiteSpace(rootPath) || string.Equals(rootPath, "/", StringComparison.Ordinal))
        {
            return null;
        }

        return rootPath.TrimEnd('/');
    }

    /// <summary>
    /// Resolves a route-captured branding asset path only when it stays relative and points at an allowed image asset.
    /// </summary>
    /// <param name="routeValue">The route-captured asset path to validate and decode.</param>
    /// <param name="allowSvgAssets">
    /// <see langword="true" /> to allow <c>.svg</c> files for a trusted branding directory; otherwise SVG files are
    /// denied by default and only bitmap/icon extensions are accepted.
    /// </param>
    /// <param name="assetPath">
    /// The decoded relative asset path when validation succeeds, or an empty string when validation fails.
    /// </param>
    /// <remarks>
    /// <para>
    /// This method accepts only relative browser paths that decode cleanly, do not contain rooted paths, backslashes,
    /// control characters, or traversal segments, and end with an allowed branding image extension. It rejects
    /// <c>.svg</c> unless <paramref name="allowSvgAssets" /> is <see langword="true" />.
    /// </para>
    /// <para>
    /// Callers should pass the raw route value before filesystem joining or other path normalization. Do not pass
    /// already-normalized absolute paths, rely on the filename alone, or treat a successful result as content-level
    /// sanitization. Enabling SVG serving expands the attack surface because SVG is active document content; this helper
    /// only enforces path and extension safety and does not optimize or sanitize SVG bytes.
    /// </para>
    /// </remarks>
    internal static bool TryResolveSafeBrandingAssetPath(object? routeValue, bool allowSvgAssets, out string assetPath)
    {
        assetPath = string.Empty;
        var rawPath = Convert.ToString(routeValue, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        if (MalformedPercentEncodingPattern.IsMatch(rawPath))
        {
            return false;
        }

        var decodedPath = DecodeBrandingAssetPath(rawPath);
        if (decodedPath.StartsWith("/", StringComparison.Ordinal)
            || decodedPath.Contains('\\', StringComparison.Ordinal)
            || decodedPath.Any(char.IsControl))
        {
            return false;
        }

        var segments = decodedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            return false;
        }

        var extension = Path.GetExtension(decodedPath);
        if (!AllowedBrandingAssetExtensions.Contains(extension)
            && !(allowSvgAssets && extension.Equals(SvgAssetExtension, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        assetPath = decodedPath;
        return true;
    }

    [ExcludeFromCodeCoverage(
        Justification = "Defensive adapter for platform URI decoding failures; caller rejects malformed percent escapes before decoding.")]
    private static string DecodeBrandingAssetPath(string rawPath)
    {
        try
        {
            return Uri.UnescapeDataString(rawPath);
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }

    [ExcludeFromCodeCoverage(
        Justification = "Private assembly-resource adapter with a defensive missing-resource branch; public route tests cover packaged asset availability.")]
    private static async Task<bool> TryWriteEmbeddedAssetAsync(HttpContext context, string webRootSubPath)
    {
        var resourceName = EmbeddedAssetResourcePrefix + webRootSubPath.Replace('\\', '/').TrimStart('/');
        await using var stream = AppSurfaceDocsAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ResolveContentType(webRootSubPath);
        context.Response.ContentLength = stream.Length;
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return true;
        }

        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        return true;
    }

    private static bool ShouldPreserveRootStylesheetPath(StartupContext context)
    {
        return AppSurfaceDocsAssetPathResolver.IsRootModuleAssembly(context.RootModuleAssembly);
    }

    private static bool ShouldMapRootDocsRedirect(StartupContext context, DocsUrlBuilder docsUrlBuilder)
    {
        return ShouldPreserveRootStylesheetPath(context)
               && !string.Equals(docsUrlBuilder.CurrentDocsRootPath, "/", StringComparison.Ordinal);
    }

    private static bool ShouldServePreviewAssetsDirectlyFromWebRoot(
        StartupContext context,
        AppSurfaceDocsOptions options,
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
        return AppSurfaceDocsStaticAssetBasePath;
    }

    /// <summary>
    /// Resolves the standalone root favicon redirect target for a configured SVG favicon.
    /// </summary>
    internal static string? ResolveConfiguredRootFaviconRedirectPath(AppSurfaceDocsOptions options)
    {
        if (!AppSurfaceDocsIdentityPath.TryNormalizeBrowserPath(
                options.Identity?.Favicon?.SvgPath,
                out var configuredPath,
                out _)
            || string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var rootPath = configuredPath.StartsWith("~/", StringComparison.Ordinal)
            ? "/" + configuredPath[2..]
            : configuredPath;
        return string.Equals(rootPath, AppSurfaceDocsRootFaviconPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : rootPath;
    }

    private static string BuildPathBaseAwareRedirectUrl(HttpContext context, string appRelativeUrl)
    {
        var pathBase = context.Request.PathBase.Value;
        if (string.IsNullOrWhiteSpace(pathBase) || string.Equals(pathBase, "/", StringComparison.Ordinal))
        {
            return appRelativeUrl;
        }

        var normalizedPathBase = pathBase.TrimEnd('/');
        return normalizedPathBase + appRelativeUrl;
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
