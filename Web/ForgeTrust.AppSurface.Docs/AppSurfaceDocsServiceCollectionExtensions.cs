using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Streams;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    /// It also registers RazorWire through <see cref="RazorWireServiceCollectionExtensions"/> and adds harvest
    /// observability services such as <see cref="AppSurfaceDocsHarvestCoordinator"/>,
    /// <see cref="AppSurfaceDocsHarvestProgressReporter"/>, and <see cref="AppSurfaceDocsHarvestPathPolicy"/>.
    /// </para>
    /// <para>
    /// If an <see cref="IRazorWireChannelAuthorizer"/> is already registered, AppSurface Docs wraps that authorizer with
    /// <see cref="AppSurfaceDocsHarvestChannelAuthorizer"/> so its harvest-progress channel rules run before delegating
    /// to the existing authorizer. Register a custom authorizer before calling this method to participate in that
    /// wrapper. Registering an authorizer after this method is an advanced replacement mode: the application replaces
    /// the AppSurface Docs wrapper and must apply any AppSurface Docs harvest-progress checks itself, typically with
    /// <see cref="AppSurfaceDocsStreamAuthorization.IsHarvestProgressChannel(string?)"/>. In non-development
    /// environments, <c>AppSurfaceDocs:Harvest:Health:ExposeRoutes=Always</c> exposes the health routes but does not
    /// authorize the live harvest stream unless a custom authorizer allows it. Built-in RazorWire allow-all/deny-all
    /// authorizers are not considered custom authorization for that docs-owned stream. Call this method once during
    /// startup; repeated registration can nest authorizer wrappers and obscure the
    /// intended channel policy.
    /// Consumers that resolve <see cref="AppSurfaceDocsOptions"/> directly should expect the normalized values rather than
    /// raw configuration text, and applications that need custom routing or catalog paths should provide those values
    /// before this method runs so the normalized singleton graph stays consistent.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAppSurfaceDocs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AppSurfaceDocsOptions>()
            .BindConfiguration(AppSurfaceDocsOptions.SectionName)
            .PostConfigure(
                options =>
                {
                    options.Identity ??= new AppSurfaceDocsIdentityOptions();
                    options.Identity.Logo ??= new AppSurfaceDocsLogoOptions();
                    options.Identity.Wordmark ??= new AppSurfaceDocsWordmarkOptions();
                    options.Identity.Favicon ??= new AppSurfaceDocsFaviconOptions();
                    options.Identity.BrandingAssets ??= new AppSurfaceDocsBrandingAssetsOptions();
                    options.Source ??= new AppSurfaceDocsSourceOptions();
                    options.Harvest ??= new AppSurfaceDocsHarvestOptions();
                    options.Harvest.Health ??= new AppSurfaceDocsHarvestHealthOptions();
                    options.Diagnostics ??= new AppSurfaceDocsDiagnosticsOptions();
                    options.Harvest.Paths ??= new AppSurfaceDocsHarvestPathOptions();
                    options.Harvest.Paths.DefaultExclusions ??= new AppSurfaceDocsHarvestDefaultExclusionOptions();
                    options.Harvest.Paths.VcsIgnore ??= new AppSurfaceDocsHarvestVcsIgnoreOptions();
                    options.Harvest.Markdown ??= new AppSurfaceDocsMarkdownHarvestOptions();
                    options.Harvest.Markdown.DefaultExclusions ??= new AppSurfaceDocsHarvestDefaultExclusionOptions();
                    options.Harvest.CSharp ??= new AppSurfaceDocsCSharpHarvestOptions();
                    options.Harvest.CSharp.DefaultExclusions ??= new AppSurfaceDocsHarvestDefaultExclusionOptions();
                    options.Harvest.JavaScript ??= new AppSurfaceDocsJavaScriptHarvestOptions();
                    options.Harvest.JavaScript.DefaultExclusions ??= new AppSurfaceDocsHarvestDefaultExclusionOptions();
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
                    options.Identity.Wordmark.HighlightText = NormalizeOrNull(options.Identity.Wordmark.HighlightText);
                    options.Identity.Wordmark.HighlightColor = AppSurfaceDocsIdentityPath.NormalizeCssHexColorOrNull(
                        options.Identity.Wordmark.HighlightColor);
                    options.Identity.Favicon.SvgPath = AppSurfaceDocsIdentityPath.NormalizeBrowserPathOrNull(options.Identity.Favicon.SvgPath);
                    options.Identity.Favicon.IcoPath = AppSurfaceDocsIdentityPath.NormalizeBrowserPathOrNull(options.Identity.Favicon.IcoPath);
                    options.Identity.Favicon.PngPath = AppSurfaceDocsIdentityPath.NormalizeBrowserPathOrNull(options.Identity.Favicon.PngPath);
                    options.Identity.BrandingAssets.DirectoryPath = NormalizeOrNull(options.Identity.BrandingAssets.DirectoryPath);
                    options.Identity.BrandingAssets.RequestPath =
                        AppSurfaceDocsIdentityPath.NormalizeBrowserPathOrNull(options.Identity.BrandingAssets.RequestPath)
                        ?? AppSurfaceDocsBrandingAssetsOptions.DefaultRequestPath;
                    options.Harvest.Paths.IncludeGlobs = NormalizeGlobArray(options.Harvest.Paths.IncludeGlobs);
                    options.Harvest.Paths.ExcludeGlobs = NormalizeGlobArray(options.Harvest.Paths.ExcludeGlobs);
                    options.Harvest.Paths.DefaultExclusions =
                        NormalizeDefaultExclusions(options.Harvest.Paths.DefaultExclusions);
                    options.Harvest.Paths.VcsIgnore.AllowGlobs = NormalizeGlobArray(options.Harvest.Paths.VcsIgnore.AllowGlobs);
                    options.Diagnostics.SearchIndexRefreshPolicy = NormalizeOrNull(options.Diagnostics.SearchIndexRefreshPolicy);
                    options.Harvest.Markdown.IncludeGlobs = NormalizeGlobArray(options.Harvest.Markdown.IncludeGlobs);
                    options.Harvest.Markdown.ExcludeGlobs = NormalizeGlobArray(options.Harvest.Markdown.ExcludeGlobs);
                    options.Harvest.Markdown.DefaultExclusions =
                        NormalizeDefaultExclusions(options.Harvest.Markdown.DefaultExclusions);
                    options.Harvest.CSharp.IncludeGlobs = NormalizeGlobArray(options.Harvest.CSharp.IncludeGlobs);
                    options.Harvest.CSharp.ExcludeGlobs = NormalizeGlobArray(options.Harvest.CSharp.ExcludeGlobs);
                    options.Harvest.CSharp.DefaultExclusions =
                        NormalizeDefaultExclusions(options.Harvest.CSharp.DefaultExclusions);
                    options.Harvest.JavaScript.IncludeGlobs = NormalizeGlobArray(options.Harvest.JavaScript.IncludeGlobs);
                    options.Harvest.JavaScript.ExcludeGlobs = NormalizeGlobArray(options.Harvest.JavaScript.ExcludeGlobs);
                    options.Harvest.JavaScript.DefaultExclusions =
                        NormalizeDefaultExclusions(options.Harvest.JavaScript.DefaultExclusions);
                    options.Harvest.JavaScript.GroupNameRules =
                        NormalizeJavaScriptGroupNameRules(options.Harvest.JavaScript.GroupNameRules);

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
                    options.Routing.PublicOrigin =
                        DocsUrlBuilder.TryNormalizePublicOrigin(options.Routing.PublicOrigin, out var normalizedPublicOrigin)
                            ? normalizedPublicOrigin
                            : NormalizeOrNull(options.Routing.PublicOrigin);

                    options.Versioning.CatalogPath = NormalizeOrNull(options.Versioning.CatalogPath);
                    options.Versioning.TrustedReleaseRootPath =
                        NormalizeOrNull(options.Versioning.TrustedReleaseRootPath);
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
        TryAddHarvestChannelAuthorizer(services);
        services.AddRazorWire();
        services.TryAddSingleton<IAppSurfaceDocsHtmlSanitizer, AppSurfaceDocsHtmlSanitizer>();
        services.TryAddSingleton<AppSurfaceDocsHarvestPathPolicy>();
        TryAddMarkdownHarvester(services);
        TryAddCSharpDocHarvester(services);
        TryAddJavaScriptDocHarvester(services);
        services.TryAddSingleton<DocFeaturedPageResolver>();
        services.TryAddSingleton<AppSurfaceDocsHarvestProgressReporter>();
        services.TryAddSingleton<DocAggregator>();
        services.TryAddSingleton<AppSurfaceDocsHarvestCoordinator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, AppSurfaceDocsHarvestFailurePreflightService>());

        return services;
    }

    private static void TryAddHarvestChannelAuthorizer(IServiceCollection services)
    {
        var existing = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IRazorWireChannelAuthorizer));
        services.RemoveAll<IRazorWireChannelAuthorizer>();
        var lifetime = existing?.Lifetime ?? ServiceLifetime.Singleton;
        services.Add(
            ServiceDescriptor.Describe(
                typeof(IRazorWireChannelAuthorizer),
                provider => new AppSurfaceDocsHarvestChannelAuthorizer(
                    provider.GetRequiredService<AppSurfaceDocsOptions>(),
                    ResolveHostEnvironment(provider),
                    CreateInnerChannelAuthorizer(provider, existing)),
                lifetime));
    }

    private static IHostEnvironment ResolveHostEnvironment(IServiceProvider provider)
    {
        return provider.GetService<IHostEnvironment>()
               ?? provider.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
    }

    private static IRazorWireChannelAuthorizer? CreateInnerChannelAuthorizer(
        IServiceProvider provider,
        ServiceDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return null;
        }

        if (descriptor.ImplementationInstance is IRazorWireChannelAuthorizer instance)
        {
            return FilterBuiltInDenyAllAuthorizer(instance);
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return FilterBuiltInDenyAllAuthorizer(
                descriptor.ImplementationFactory(provider) as IRazorWireChannelAuthorizer);
        }

        if (descriptor.ImplementationType is not null)
        {
            return descriptor.ImplementationType == typeof(DenyAllRazorWireChannelAuthorizer)
                ? null
                : (IRazorWireChannelAuthorizer)ActivatorUtilities.CreateInstance(
                    provider,
                    descriptor.ImplementationType);
        }

        return null;
    }

    private static IRazorWireChannelAuthorizer? FilterBuiltInDenyAllAuthorizer(
        IRazorWireChannelAuthorizer? authorizer)
    {
        return authorizer is DenyAllRazorWireChannelAuthorizer ? null : authorizer;
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
                sp.GetRequiredService<AppSurfaceDocsHarvestPathPolicy>()));
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
                sp.GetRequiredService<AppSurfaceDocsHarvestPathPolicy>()));
    }

    private static void TryAddJavaScriptDocHarvester(IServiceCollection services)
    {
        if (services.Any(
                descriptor => descriptor.ServiceType == typeof(JavaScriptDocHarvesterRegistrationMarker)
                              || descriptor.ServiceType == typeof(IDocHarvester)
                              && descriptor.ImplementationType == typeof(JavaScriptDocHarvester)))
        {
            return;
        }

        services.AddSingleton(new JavaScriptDocHarvesterRegistrationMarker());
        services.AddSingleton<IDocHarvester>(
            sp => new JavaScriptDocHarvester(
                sp.GetRequiredService<AppSurfaceDocsOptions>(),
                sp.GetRequiredService<ILogger<JavaScriptDocHarvester>>(),
                sp.GetRequiredService<AppSurfaceDocsHarvestPathPolicy>()));
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
            .Select(value => AppSurfaceDocsHarvestPathPatternValidator.NormalizeSlashes(value.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AppSurfaceDocsHarvestDefaultExclusionOptions NormalizeDefaultExclusions(
        AppSurfaceDocsHarvestDefaultExclusionOptions options)
    {
        options.DisabledGroups = (options.DisabledGroups ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(AppSurfaceDocsHarvestPathPolicy.NormalizeDefaultGroupId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var allowGlobs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (groupId, patterns) in options.AllowGlobs ?? [])
        {
            var normalizedGroupId = AppSurfaceDocsHarvestPathPolicy.NormalizeDefaultGroupId(groupId);
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

    private static AppSurfaceDocsJavaScriptGroupNameRule[] NormalizeJavaScriptGroupNameRules(
        IEnumerable<AppSurfaceDocsJavaScriptGroupNameRule>? rules)
    {
        return (rules ?? [])
            .Where(rule => rule is not null)
            .Select(
                rule => new AppSurfaceDocsJavaScriptGroupNameRule
                {
                    Name = NormalizeOrNull(rule.Name),
                    IncludeGlobs = NormalizeGlobArray(rule.IncludeGlobs)
                })
            .ToArray();
    }

    private sealed class MarkdownHarvesterRegistrationMarker;

    private sealed class CSharpDocHarvesterRegistrationMarker;

    private sealed class JavaScriptDocHarvesterRegistrationMarker;
}
