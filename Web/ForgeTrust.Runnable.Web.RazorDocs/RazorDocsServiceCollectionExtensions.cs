using ForgeTrust.Runnable.Caching;
using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.Runnable.Web.RazorDocs;

/// <summary>
/// Registers the RazorDocs dependency injection and options normalization pipeline.
/// </summary>
/// <remarks>
/// This extension binds <see cref="RazorDocsOptions"/> from configuration, rehydrates omitted nested option objects
/// such as <see cref="RazorDocsOptions.Routing"/> and <see cref="RazorDocsOptions.Versioning"/> with their default
/// containers, normalizes caller-provided string settings, and validates the final shape on startup. Callers should
/// use this once per application when they want the standard RazorDocs harvesting, routing, preview, and versioned
/// published-release services to be available to downstream modules and controllers.
/// </remarks>
public static class RazorDocsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the RazorDocs package services, normalized options, and routing helpers to the service collection.
    /// </summary>
    /// <param name="services">The target service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// During post-configuration this method rehydrates null nested option blocks with defaults, trims nullable string
    /// settings such as repository roots and contributor URL templates, normalizes
    /// <see cref="RazorDocsRoutingOptions.RouteRootPath"/> and
    /// <see cref="RazorDocsRoutingOptions.DocsRootPath"/> through
    /// <see cref="DocsUrlBuilder.NormalizeRouteRootPath(string?, string, bool)"/> and
    /// <see cref="DocsUrlBuilder.NormalizeDocsRootPath(string?, bool)"/>, trims
    /// <see cref="RazorDocsVersioningOptions.CatalogPath"/>, and removes blank or duplicate sidebar namespace
    /// prefixes. Callers that omit <see cref="RazorDocsOptions.Routing"/> or
    /// <see cref="RazorDocsOptions.Versioning"/> can therefore still rely on a fully populated options object after
    /// registration.
    /// </para>
    /// <para>
    /// The method also registers <see cref="DocsUrlBuilder"/> and <see cref="RazorDocsVersionCatalogService"/> as
    /// singleton downstream services alongside the standard harvesters, memo cache, and <see cref="DocAggregator"/>.
    /// Consumers that resolve <see cref="RazorDocsOptions"/> directly should expect the normalized values rather than
    /// raw configuration text, and applications that need custom routing or catalog paths should provide those values
    /// before this method runs so the normalized singleton graph stays consistent.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRazorDocs(this IServiceCollection services)
    {
        services.AddOptions<RazorDocsOptions>()
            .BindConfiguration(RazorDocsOptions.SectionName)
            .PostConfigure<IConfiguration>(
                (options, configuration) =>
                {
                    options.Source ??= new RazorDocsSourceOptions();
                    options.Bundle ??= new RazorDocsBundleOptions();
                    options.Sidebar ??= new RazorDocsSidebarOptions();
                    options.Contributor ??= new RazorDocsContributorOptions();
                    options.Routing ??= new RazorDocsRoutingOptions();
                    options.Versioning ??= new RazorDocsVersioningOptions();
                    options.Sidebar.NamespacePrefixes ??= [];

                    if (options.Source.RepositoryRoot is null)
                    {
                        options.Source.RepositoryRoot = NormalizeOrNull(configuration["RepositoryRoot"]);
                    }
                    else
                    {
                        options.Source.RepositoryRoot = options.Source.RepositoryRoot.Trim();
                    }

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
                    options.Sidebar.NamespacePrefixes = options.Sidebar.NamespacePrefixes
                        .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                        .Select(prefix => prefix.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                })
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RazorDocsOptions>, RazorDocsOptionsValidator>());
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<RazorDocsOptions>>().Value);
        services.TryAddSingleton(RazorDocsAssetPathResolver.CreateDefault());
        services.TryAddSingleton<DocsUrlBuilder>();
        services.TryAddSingleton<RazorDocsVersionCatalogService>();
        services.AddMemoryCache();
        services.TryAddSingleton<IMemo, Memo>();
        services.TryAddSingleton<IRazorDocsHtmlSanitizer, RazorDocsHtmlSanitizer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocHarvester, MarkdownHarvester>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocHarvester, CSharpDocHarvester>());
        services.TryAddSingleton<DocFeaturedPageResolver>();
        services.TryAddSingleton<DocAggregator>();

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
