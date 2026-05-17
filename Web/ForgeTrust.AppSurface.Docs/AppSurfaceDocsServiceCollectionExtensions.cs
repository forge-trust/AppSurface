using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Registers the AppSurface Docs dependency injection and options normalization pipeline.
/// </summary>
/// <remarks>
/// This extension binds <see cref="AppSurfaceDocsOptions"/> from configuration, rehydrates omitted nested option objects
/// such as <see cref="AppSurfaceDocsOptions.Harvest"/>, <see cref="AppSurfaceDocsOptions.Routing"/>, and
/// <see cref="AppSurfaceDocsOptions.Versioning"/> with their default
/// containers, normalizes caller-provided string settings, and validates the final shape on startup. Callers should
/// use this once per application when they want the standard AppSurface Docs harvesting, routing, preview, and versioned
/// published-release services to be available to downstream modules and controllers.
/// </remarks>
public static class AppSurfaceDocsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the AppSurface Docs package services, normalized options, and routing helpers to the service collection.
    /// </summary>
    /// <param name="services">The target service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// During post-configuration this method rehydrates null nested option blocks with defaults, trims nullable string
    /// settings such as repository roots and contributor URL templates, normalizes
    /// <see cref="AppSurfaceDocsRoutingOptions.RouteRootPath"/> and
    /// <see cref="AppSurfaceDocsRoutingOptions.DocsRootPath"/> through
    /// <see cref="DocsUrlBuilder.NormalizeRouteRootPath(string?, string, bool)"/> and
    /// <see cref="DocsUrlBuilder.NormalizeDocsRootPath(string?, bool)"/>, trims
    /// <see cref="AppSurfaceDocsVersioningOptions.CatalogPath"/>, normalizes
    /// <see cref="AppSurfaceDocsLocalizationOptions.DefaultLocale"/> to <c>en</c> when blank, trims locale
    /// <see cref="AppSurfaceDocsLocaleOptions.Code"/>, <see cref="AppSurfaceDocsLocaleOptions.Label"/>,
    /// <see cref="AppSurfaceDocsLocaleOptions.Lang"/>, and <see cref="AppSurfaceDocsLocaleOptions.RoutePrefix"/> values
    /// while skipping null locale entries, and removes blank or duplicate sidebar namespace prefixes. Callers that omit
    /// <see cref="AppSurfaceDocsOptions.Routing"/>, <see cref="AppSurfaceDocsOptions.Versioning"/>, or
    /// <see cref="AppSurfaceDocsOptions.Localization"/> can therefore still rely on a fully populated options object
    /// after registration. When <see cref="AppSurfaceDocsHarvestOptions.FailOnFailure"/> is enabled, the registered
    /// startup preflight fails the host only when the aggregate harvest health is failed.
    /// </para>
    /// <para>
    /// When <see cref="AppSurfaceDocsRoutingOptions.DocsRootPath"/> is omitted or whitespace, the live docs root is derived
    /// from the normalized <see cref="AppSurfaceDocsRoutingOptions.RouteRootPath"/> through
    /// <see cref="DocsUrlBuilder.ResolveDefaultDocsRootPath(string, bool)"/>. For example,
    /// <c>AppSurfaceDocs:Routing:RouteRootPath=/foo/bar</c> with versioning enabled produces <c>/foo/bar/next</c> as the
    /// default live root. Callers that set both <see cref="AppSurfaceDocsRoutingOptions.RouteRootPath"/> and
    /// <see cref="AppSurfaceDocsRoutingOptions.DocsRootPath"/> should keep the pair coherent so stable entry, archive,
    /// exact-version, and live preview routes compose predictably after
    /// <see cref="DocsUrlBuilder.NormalizeRouteRootPath(string?, string, bool)"/> and
    /// <see cref="DocsUrlBuilder.NormalizeDocsRootPath(string?, bool)"/> run.
    /// </para>
    /// <para>
    /// The method also registers <see cref="DocsUrlBuilder"/>, <see cref="AppSurfaceDocsAssetVersioner"/>, and
    /// <see cref="AppSurfaceDocsVersionCatalogService"/> as singleton downstream services alongside the standard
    /// harvesters, memo cache, and <see cref="DocAggregator"/>. <see cref="AppSurfaceDocsAssetVersioner"/> supplies
    /// content-derived cache keys for AppSurface Docs-owned CSS and JavaScript assets rendered by the package views.
    /// Consumers that resolve <see cref="AppSurfaceDocsOptions"/> directly should expect the normalized values rather than
    /// raw configuration text, and applications that need custom routing or catalog paths should provide those values
    /// before this method runs so the normalized singleton graph stays consistent.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAppSurfaceDocs(this IServiceCollection services)
    {
        services.AddOptions<AppSurfaceDocsOptions>()
            .BindConfiguration(AppSurfaceDocsOptions.SectionName)
            .PostConfigure(
                options =>
                {
                    options.Identity ??= new AppSurfaceDocsIdentityOptions();
                    options.Identity.Logo ??= new AppSurfaceDocsLogoOptions();
                    options.Identity.Favicon ??= new AppSurfaceDocsFaviconOptions();
                    options.Source ??= new AppSurfaceDocsSourceOptions();
                    options.Harvest ??= new AppSurfaceDocsHarvestOptions();
                    options.Harvest.Health ??= new AppSurfaceDocsHarvestHealthOptions();
                    options.Bundle ??= new AppSurfaceDocsBundleOptions();
                    options.Sidebar ??= new AppSurfaceDocsSidebarOptions();
                    options.Contributor ??= new AppSurfaceDocsContributorOptions();
                    options.Routing ??= new AppSurfaceDocsRoutingOptions();
                    options.Versioning ??= new AppSurfaceDocsVersioningOptions();
                    options.Localization ??= new AppSurfaceDocsLocalizationOptions();
                    options.Sidebar.NamespacePrefixes ??= [];
                    options.Localization.Locales ??= [];

                    options.Identity.DisplayName = NormalizeOrNull(options.Identity.DisplayName);
                    options.Identity.HomeHref = AppSurfaceDocsIdentityPath.NormalizeBrowserPathOrNull(options.Identity.HomeHref);
                    options.Identity.Logo.Path = AppSurfaceDocsIdentityPath.NormalizeBrowserPathOrNull(options.Identity.Logo.Path);
                    options.Identity.Logo.AltText = NormalizeOrNull(options.Identity.Logo.AltText);
                    options.Identity.Favicon.SvgPath = AppSurfaceDocsIdentityPath.NormalizeBrowserPathOrNull(options.Identity.Favicon.SvgPath);
                    options.Identity.Favicon.IcoPath = AppSurfaceDocsIdentityPath.NormalizeBrowserPathOrNull(options.Identity.Favicon.IcoPath);
                    options.Identity.Favicon.PngPath = AppSurfaceDocsIdentityPath.NormalizeBrowserPathOrNull(options.Identity.Favicon.PngPath);
                    options.Source.RepositoryRoot = options.Source.RepositoryRoot?.Trim();
                    options.Bundle.Path = NormalizeOrNull(options.Bundle.Path);
                    options.Contributor.DefaultBranch = NormalizeOrNull(options.Contributor.DefaultBranch);
                    options.Contributor.SourceUrlTemplate = NormalizeOrNull(options.Contributor.SourceUrlTemplate);
                    options.Contributor.EditUrlTemplate = NormalizeOrNull(options.Contributor.EditUrlTemplate);
                    var configuredDocsRootPath = options.Routing.DocsRootPath;
                    var normalizedDocsRootPath = DocsUrlBuilder.NormalizeDocsRootPath(
                        configuredDocsRootPath,
                        options.Versioning.Enabled);
                    options.Routing.RouteRootPath = DocsUrlBuilder.NormalizeRouteRootPath(
                        options.Routing.RouteRootPath,
                        normalizedDocsRootPath,
                        options.Versioning.Enabled);
                    options.Routing.DocsRootPath = string.IsNullOrWhiteSpace(configuredDocsRootPath)
                        ? DocsUrlBuilder.ResolveDefaultDocsRootPath(options.Routing.RouteRootPath, options.Versioning.Enabled)
                        : normalizedDocsRootPath;
                    options.Versioning.CatalogPath = NormalizeOrNull(options.Versioning.CatalogPath);
                    options.Contributor.SymbolSourceUrlTemplate = NormalizeOrNull(options.Contributor.SymbolSourceUrlTemplate);
                    options.Contributor.SourceRef = NormalizeOrNull(options.Contributor.SourceRef);
                    options.Localization.DefaultLocale = NormalizeOrNull(options.Localization.DefaultLocale) ?? "en";
                    foreach (var locale in options.Localization.Locales)
                    {
                        if (locale is null)
                        {
                            continue;
                        }

                        locale.Code = NormalizeOrNull(locale.Code) ?? string.Empty;
                        locale.Label = NormalizeOrNull(locale.Label);
                        locale.Lang = NormalizeOrNull(locale.Lang);
                        locale.RoutePrefix = NormalizeOrNull(locale.RoutePrefix);
                    }

                    options.Sidebar.NamespacePrefixes = options.Sidebar.NamespacePrefixes
                        .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                        .Select(prefix => prefix.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                })
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AppSurfaceDocsOptions>, AppSurfaceDocsOptionsValidator>());
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<AppSurfaceDocsOptions>>().Value);
        services.AddConfigAuditKey<AppSurfaceDocsOptions>(AppSurfaceDocsOptions.SectionName);
        services.AddConfigAuditKey<AppSurfaceDocsIdentityOptions>($"{AppSurfaceDocsOptions.SectionName}.Identity");
        services.TryAddSingleton(AppSurfaceDocsAssetPathResolver.CreateDefault());
        services.TryAddSingleton<AppSurfaceDocsAssetVersioner>();
        services.TryAddSingleton<DocsUrlBuilder>();
        services.TryAddSingleton<AppSurfaceDocsIdentityResolver>();
        services.TryAddSingleton<AppSurfaceDocsVersionCatalogService>();
        services.AddMemoryCache();
        services.TryAddSingleton<IMemo, Memo>();
        services.TryAddSingleton<IAppSurfaceDocsHtmlSanitizer, AppSurfaceDocsHtmlSanitizer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocHarvester, MarkdownHarvester>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocHarvester, CSharpDocHarvester>());
        services.TryAddSingleton<DocFeaturedPageResolver>();
        services.TryAddSingleton<DocAggregator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, AppSurfaceDocsHarvestFailurePreflightService>());

        return services;
    }

    private static string? NormalizeOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

}
