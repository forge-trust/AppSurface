using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Registers the RazorDocs dependency injection and options normalization pipeline.
/// </summary>
/// <remarks>
/// This extension binds <see cref="RazorDocsOptions"/> from configuration, rehydrates omitted nested option objects
/// such as <see cref="RazorDocsOptions.Harvest"/>, <see cref="RazorDocsOptions.Routing"/>, and
/// <see cref="RazorDocsOptions.Versioning"/> with their default
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
    /// <see cref="RazorDocsHarvestPathOptions.IncludeGlobs"/>,
    /// <see cref="RazorDocsHarvestPathOptions.ExcludeGlobs"/>,
    /// <see cref="RazorDocsMarkdownHarvestOptions.IncludeGlobs"/>,
    /// <see cref="RazorDocsMarkdownHarvestOptions.ExcludeGlobs"/>,
    /// <see cref="RazorDocsCSharpHarvestOptions.IncludeGlobs"/>, and
    /// <see cref="RazorDocsCSharpHarvestOptions.ExcludeGlobs"/> by trimming, using forward-slash separators,
    /// removing blanks, and case-insensitively deduplicating values, normalizes default exclusion group IDs and allow
    /// globs, normalizes
    /// <see cref="RazorDocsRoutingOptions.RouteRootPath"/> and
    /// <see cref="RazorDocsRoutingOptions.DocsRootPath"/> through
    /// <see cref="DocsUrlBuilder.NormalizeRouteRootPath(string?, string, bool)"/> and
    /// <see cref="DocsUrlBuilder.NormalizeDocsRootPath(string?, bool)"/>, trims
    /// <see cref="RazorDocsVersioningOptions.CatalogPath"/>, normalizes
    /// <see cref="RazorDocsLocalizationOptions.DefaultLocale"/> to <c>en</c> when blank, trims locale
    /// <see cref="RazorDocsLocaleOptions.Code"/>, <see cref="RazorDocsLocaleOptions.Label"/>,
    /// <see cref="RazorDocsLocaleOptions.Lang"/>, and <see cref="RazorDocsLocaleOptions.RoutePrefix"/> values while
    /// skipping null locale entries, and removes blank or duplicate sidebar namespace prefixes. Callers that omit
    /// <see cref="RazorDocsOptions.Routing"/>, <see cref="RazorDocsOptions.Versioning"/>, or
    /// <see cref="RazorDocsOptions.Localization"/> can therefore still rely on a fully populated options object after
    /// registration. When <see cref="RazorDocsHarvestOptions.FailOnFailure"/> is enabled, the registered startup
    /// preflight fails the host only when the aggregate harvest health is failed.
    /// </para>
    /// <para>
    /// When <see cref="RazorDocsRoutingOptions.DocsRootPath"/> is omitted or whitespace, the live docs root is derived
    /// from the normalized <see cref="RazorDocsRoutingOptions.RouteRootPath"/> through
    /// <see cref="DocsUrlBuilder.ResolveDefaultDocsRootPath(string, bool)"/>. For example,
    /// <c>RazorDocs:Routing:RouteRootPath=/foo/bar</c> with versioning enabled produces <c>/foo/bar/next</c> as the
    /// default live root. Callers that set both <see cref="RazorDocsRoutingOptions.RouteRootPath"/> and
    /// <see cref="RazorDocsRoutingOptions.DocsRootPath"/> should keep the pair coherent so stable entry, archive,
    /// exact-version, and live preview routes compose predictably after
    /// <see cref="DocsUrlBuilder.NormalizeRouteRootPath(string?, string, bool)"/> and
    /// <see cref="DocsUrlBuilder.NormalizeDocsRootPath(string?, bool)"/> run.
    /// </para>
    /// <para>
    /// The method also registers <see cref="DocsUrlBuilder"/>, <see cref="RazorDocsAssetVersioner"/>, and
    /// <see cref="RazorDocsVersionCatalogService"/> as singleton downstream services alongside the standard harvesters,
    /// memo cache, and <see cref="DocAggregator"/>. <see cref="RazorDocsAssetVersioner"/> supplies content-derived
    /// cache keys for RazorDocs-owned CSS and JavaScript assets rendered by the package views.
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
                    options.Harvest ??= new RazorDocsHarvestOptions();
                    options.Harvest.Health ??= new RazorDocsHarvestHealthOptions();
                    options.Harvest.Paths ??= new RazorDocsHarvestPathOptions();
                    options.Harvest.Paths.DefaultExclusions ??= new RazorDocsHarvestDefaultExclusionOptions();
                    options.Harvest.Markdown ??= new RazorDocsMarkdownHarvestOptions();
                    options.Harvest.Markdown.DefaultExclusions ??= new RazorDocsHarvestDefaultExclusionOptions();
                    options.Harvest.CSharp ??= new RazorDocsCSharpHarvestOptions();
                    options.Harvest.CSharp.DefaultExclusions ??= new RazorDocsHarvestDefaultExclusionOptions();
                    options.Bundle ??= new RazorDocsBundleOptions();
                    options.Sidebar ??= new RazorDocsSidebarOptions();
                    options.Contributor ??= new RazorDocsContributorOptions();
                    options.Routing ??= new RazorDocsRoutingOptions();
                    options.Versioning ??= new RazorDocsVersioningOptions();
                    options.Localization ??= new RazorDocsLocalizationOptions();
                    options.Sidebar.NamespacePrefixes ??= [];
                    options.Localization.Locales ??= [];
                    options.Harvest.Paths.IncludeGlobs = NormalizeGlobArray(options.Harvest.Paths.IncludeGlobs);
                    options.Harvest.Paths.ExcludeGlobs = NormalizeGlobArray(options.Harvest.Paths.ExcludeGlobs);
                    options.Harvest.Paths.DefaultExclusions =
                        NormalizeDefaultExclusions(options.Harvest.Paths.DefaultExclusions);
                    options.Harvest.Markdown.IncludeGlobs = NormalizeGlobArray(options.Harvest.Markdown.IncludeGlobs);
                    options.Harvest.Markdown.ExcludeGlobs = NormalizeGlobArray(options.Harvest.Markdown.ExcludeGlobs);
                    options.Harvest.Markdown.DefaultExclusions =
                        NormalizeDefaultExclusions(options.Harvest.Markdown.DefaultExclusions);
                    options.Harvest.CSharp.IncludeGlobs = NormalizeGlobArray(options.Harvest.CSharp.IncludeGlobs);
                    options.Harvest.CSharp.ExcludeGlobs = NormalizeGlobArray(options.Harvest.CSharp.ExcludeGlobs);
                    options.Harvest.CSharp.DefaultExclusions =
                        NormalizeDefaultExclusions(options.Harvest.CSharp.DefaultExclusions);

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
            ServiceDescriptor.Singleton<IValidateOptions<RazorDocsOptions>, RazorDocsOptionsValidator>());
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<RazorDocsOptions>>().Value);
        services.TryAddSingleton(RazorDocsAssetPathResolver.CreateDefault());
        services.TryAddSingleton<RazorDocsAssetVersioner>();
        services.TryAddSingleton<DocsUrlBuilder>();
        services.TryAddSingleton<RazorDocsVersionCatalogService>();
        services.AddMemoryCache();
        services.TryAddSingleton<IMemo, Memo>();
        services.TryAddSingleton<IRazorDocsHtmlSanitizer, RazorDocsHtmlSanitizer>();
        services.TryAddSingleton<RazorDocsHarvestPathPolicy>();
        TryAddMarkdownHarvester(services);
        TryAddCSharpDocHarvester(services);
        services.TryAddSingleton<DocFeaturedPageResolver>();
        services.TryAddSingleton<DocAggregator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, RazorDocsHarvestFailurePreflightService>());

        return services;
    }

    private static void TryAddMarkdownHarvester(IServiceCollection services)
    {
        if (services.Any(
                descriptor => descriptor.ServiceType == typeof(MarkdownHarvesterRegistrationMarker)
                              || descriptor.ServiceType == typeof(IDocHarvester)
                              && descriptor.ImplementationType == typeof(MarkdownHarvester)))
        {
            return;
        }

        services.AddSingleton(new MarkdownHarvesterRegistrationMarker());
        services.AddSingleton<IDocHarvester>(
            sp => new MarkdownHarvester(
                sp.GetRequiredService<ILogger<MarkdownHarvester>>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<RazorDocsHarvestPathPolicy>()));
    }

    private static void TryAddCSharpDocHarvester(IServiceCollection services)
    {
        if (services.Any(
                descriptor => descriptor.ServiceType == typeof(CSharpDocHarvesterRegistrationMarker)
                              || descriptor.ServiceType == typeof(IDocHarvester)
                              && descriptor.ImplementationType == typeof(CSharpDocHarvester)))
        {
            return;
        }

        services.AddSingleton(new CSharpDocHarvesterRegistrationMarker());
        services.AddSingleton<IDocHarvester>(
            sp => new CSharpDocHarvester(
                sp.GetRequiredService<ILogger<CSharpDocHarvester>>(),
                sp.GetRequiredService<RazorDocsHarvestPathPolicy>()));
    }

    private static string? NormalizeOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string[] NormalizeGlobArray(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => RazorDocsHarvestPathPatternValidator.NormalizeSlashes(value.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static RazorDocsHarvestDefaultExclusionOptions NormalizeDefaultExclusions(
        RazorDocsHarvestDefaultExclusionOptions options)
    {
        options.DisabledGroups = (options.DisabledGroups ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(RazorDocsHarvestPathPolicy.NormalizeDefaultGroupId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var allowGlobs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (groupId, patterns) in options.AllowGlobs ?? [])
        {
            var normalizedGroupId = RazorDocsHarvestPathPolicy.NormalizeDefaultGroupId(groupId);
            var normalizedPatterns = NormalizeGlobArray(patterns);
            if (allowGlobs.TryGetValue(normalizedGroupId, out var existingPatterns))
            {
                normalizedPatterns = existingPatterns
                    .Concat(normalizedPatterns)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            allowGlobs[normalizedGroupId] = normalizedPatterns;
        }

        options.AllowGlobs = allowGlobs;
        return options;
    }

    private sealed class MarkdownHarvesterRegistrationMarker;

    private sealed class CSharpDocHarvesterRegistrationMarker;
}
