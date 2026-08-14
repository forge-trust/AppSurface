using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Intelligence;
using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web.Theming;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Represents one independently configured AppSurface Docs product declared by a host.
/// </summary>
/// <remarks>
/// A named instance is a complete Docs product with its own source boundary, routing, aggregation cache, catalog,
/// identity, theme, and harvest state. It is intentionally different from a reader-visible page audience filter.
/// Hosts register all instances during service configuration, map each handle once during endpoint configuration, and
/// call <see cref="AppSurfaceDocsEndpointRouteBuilderExtensions.FinalizeAppSurfaceDocsInstances" /> once after mapping.
/// </remarks>
public sealed class AppSurfaceDocsInstance
{
    private readonly AppSurfaceDocsInstanceDeclaration _declaration;

    internal AppSurfaceDocsInstance(AppSurfaceDocsInstanceDeclaration declaration)
    {
        _declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
    }

    /// <summary>
    /// Gets the normalized, case-insensitive identifier used to distinguish this Docs product.
    /// </summary>
    public string Name => _declaration.Name;

    /// <summary>
    /// Declares this Docs product's endpoint mapping and returns a convention builder for host authorization metadata.
    /// </summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <returns>
    /// A deferred convention builder. Add conventions such as <c>RequireAuthorization("DocsContributors")</c> before
    /// finalization; the conventions apply to every endpoint owned by this Docs product.
    /// </returns>
    /// <remarks>
    /// This method does not publish endpoints immediately. That lets finalization validate every declaration and build
    /// every runtime before publishing the first Docs route. Call
    /// <see cref="AppSurfaceDocsEndpointRouteBuilderExtensions.FinalizeAppSurfaceDocsInstances" /> after mapping every
    /// declared handle. A handle can be mapped exactly once. If endpoint publication later fails, the registry becomes
    /// terminal so the current route table cannot accidentally receive a duplicate partial mapping; recreate the host
    /// after correcting the failure.
    /// </remarks>
    public IEndpointConventionBuilder MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return _declaration.Map(endpoints);
    }
}

/// <summary>
/// Adds named AppSurface Docs instance finalization to an ASP.NET Core endpoint route builder.
/// </summary>
public static class AppSurfaceDocsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Validates and publishes every named AppSurface Docs product mapped by the current host.
    /// </summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <remarks>
    /// All returned <see cref="AppSurfaceDocsInstance" /> handles must be mapped exactly once before this call. The
    /// registry snapshots named configuration and constructs each isolated runtime before it maps any product routes.
    /// Calling this method twice, retrying it after an endpoint-publication failure, mapping after this call, or leaving
    /// a declared handle unmapped throws an actionable startup error instead of selecting a legacy/default runtime
    /// implicitly. A failed finalization is terminal because ASP.NET Core endpoint route builders do not support
    /// atomically removing a partially published endpoint family; correct the startup configuration or convention and
    /// recreate the host.
    /// </remarks>
    public static void FinalizeAppSurfaceDocsInstances(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var registry = endpoints.ServiceProvider.GetRequiredService<AppSurfaceDocsInstanceRegistry>();
        registry.FinalizeMappings(endpoints);
    }
}

/// <summary>
/// Carries the selected Docs product identity on every endpoint mapped for a named Docs instance.
/// </summary>
/// <param name="Name">The normalized instance name selected by this endpoint.</param>
public sealed record AppSurfaceDocsEndpointMetadata(string Name);

/// <summary>
/// Resolves the isolated Docs runtime selected by the current request endpoint.
/// </summary>
/// <remarks>
/// Request-time Docs code must use this accessor rather than resolving unkeyed Docs services when named composition is
/// enabled. The accessor consults endpoint metadata only; it never guesses from a request path, so a route rewrite or a
/// missed prefix cannot make one Docs product render another product's URLs, identity, or search state.
/// </remarks>
public interface IAppSurfaceDocsRequestRuntimeAccessor
{
    /// <summary>
    /// Gets the Docs runtime selected by the current endpoint.
    /// </summary>
    /// <returns>The current isolated Docs runtime.</returns>
    AppSurfaceDocsRuntime GetRequiredRuntime();
}

internal sealed class AppSurfaceDocsRequestRuntimeAccessor : IAppSurfaceDocsRequestRuntimeAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppSurfaceDocsInstanceRegistry _registry;

    public AppSurfaceDocsRequestRuntimeAccessor(
        IHttpContextAccessor httpContextAccessor,
        AppSurfaceDocsInstanceRegistry registry)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public AppSurfaceDocsRuntime GetRequiredRuntime()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var metadata = httpContext?.GetEndpoint()?.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>();

        return _registry.GetRequiredRuntime(metadata?.Name);
    }
}

/// <summary>
/// Contains all state owned by one Docs product runtime.
/// </summary>
/// <remarks>
/// The runtime is built once from a normalized configuration snapshot during endpoint finalization. Named runtimes never
/// read <see cref="IOptionsMonitor{TOptions}" /> on a request, so post-finalization configuration reload cannot change
/// routes, source boundaries, identity, or authorization-adjacent behavior under an already published endpoint table.
/// </remarks>
public sealed class AppSurfaceDocsRuntime : IDisposable
{
    private readonly IDisposable? _ownedResources;
    private int _disposed;

    internal AppSurfaceDocsRuntime(
        string name,
        AppSurfaceDocsOptions options,
        DocsUrlBuilder docsUrlBuilder,
        DocsRecoveryLinkBuilder recoveryLinkBuilder,
        AppSurfaceDocsIdentityResolver identityResolver,
        AppSurfaceDocsThemeResolver themeResolver,
        AppSurfaceDocsVersionCatalogService versionCatalogService,
        AppSurfaceDocsSearchQualityReadModel searchQualityReadModel,
        AppSurfaceDocsHarvestPathPolicy harvestPathPolicy,
        DocFeaturedPageResolver featuredPageResolver,
        AppSurfaceDocsHarvestProgressReporter harvestProgressReporter,
        DocAggregator aggregator,
        AppSurfaceDocsHarvestCoordinator harvestCoordinator,
        AppSurfaceDocsAssetVersioner assetVersioner,
        AppSurfaceDocsPublishedTreeHandler? publishedTreeHandler = null,
        IDisposable? ownedResources = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        DocsUrlBuilder = docsUrlBuilder ?? throw new ArgumentNullException(nameof(docsUrlBuilder));
        RecoveryLinkBuilder = recoveryLinkBuilder ?? throw new ArgumentNullException(nameof(recoveryLinkBuilder));
        IdentityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        ThemeResolver = themeResolver ?? throw new ArgumentNullException(nameof(themeResolver));
        VersionCatalogService = versionCatalogService ?? throw new ArgumentNullException(nameof(versionCatalogService));
        SearchQualityReadModel = searchQualityReadModel ?? throw new ArgumentNullException(nameof(searchQualityReadModel));
        HarvestPathPolicy = harvestPathPolicy ?? throw new ArgumentNullException(nameof(harvestPathPolicy));
        FeaturedPageResolver = featuredPageResolver ?? throw new ArgumentNullException(nameof(featuredPageResolver));
        HarvestProgressReporter = harvestProgressReporter ?? throw new ArgumentNullException(nameof(harvestProgressReporter));
        Aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        HarvestCoordinator = harvestCoordinator ?? throw new ArgumentNullException(nameof(harvestCoordinator));
        AssetVersioner = assetVersioner ?? throw new ArgumentNullException(nameof(assetVersioner));
        PublishedTreeHandler = publishedTreeHandler;
        _ownedResources = ownedResources;
    }

    /// <summary>Gets the normalized Docs product name.</summary>
    public string Name { get; }

    /// <summary>Gets the immutable normalized configuration snapshot for this runtime.</summary>
    public AppSurfaceDocsOptions Options { get; }

    /// <summary>Gets the URL builder for this runtime's route family.</summary>
    public DocsUrlBuilder DocsUrlBuilder { get; }

    internal DocsRecoveryLinkBuilder RecoveryLinkBuilder { get; }

    internal AppSurfaceDocsIdentityResolver IdentityResolver { get; }

    internal AppSurfaceDocsThemeResolver ThemeResolver { get; }

    internal AppSurfaceDocsVersionCatalogService VersionCatalogService { get; }

    internal AppSurfaceDocsSearchQualityReadModel SearchQualityReadModel { get; }

    internal AppSurfaceDocsHarvestPathPolicy HarvestPathPolicy { get; }

    internal DocFeaturedPageResolver FeaturedPageResolver { get; }

    internal AppSurfaceDocsHarvestProgressReporter HarvestProgressReporter { get; }

    /// <summary>
    /// Gets the complete host authorization metadata applied to the instance route group.
    /// </summary>
    /// <remarks>
    /// RazorWire streams do not have a conventional endpoint on which ASP.NET Core can evaluate route metadata. Named
    /// harvest streams therefore retain this immutable metadata snapshot and evaluate the same requirements before
    /// delegating to the host stream authorizer.
    /// </remarks>
    internal IReadOnlyList<IAuthorizeData> HarvestProgressAuthorizationData { get; set; } = [];

    internal DocAggregator Aggregator { get; }

    internal AppSurfaceDocsHarvestCoordinator HarvestCoordinator { get; }

    internal AppSurfaceDocsAssetVersioner AssetVersioner { get; }

    internal AppSurfaceDocsPublishedTreeHandler? PublishedTreeHandler { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ownedResources?.Dispose();
    }
}

internal sealed class AppSurfaceDocsInstanceDeclaration
{
    private readonly object _gate = new();
    private readonly List<Action<EndpointBuilder>> _conventions = [];
    private IEndpointRouteBuilder? _mappedEndpoints;
    private bool _finalized;

    public AppSurfaceDocsInstanceDeclaration(string name, IConfigurationSection configurationSection)
    {
        Name = NormalizeName(name);
        ConfigurationSection = configurationSection ?? throw new ArgumentNullException(nameof(configurationSection));
    }

    public string Name { get; }

    public IConfigurationSection ConfigurationSection { get; }

    public IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints)
    {
        lock (_gate)
        {
            if (_finalized)
            {
                throw new InvalidOperationException(
                    $"AppSurface Docs instance '{Name}' cannot be mapped after finalization. "
                    + "Move MapEndpoints before FinalizeAppSurfaceDocsInstances().");
            }

            if (_mappedEndpoints is not null)
            {
                throw new InvalidOperationException(
                    $"AppSurface Docs instance '{Name}' was mapped more than once. Each handle may be mapped exactly once.");
            }

            _mappedEndpoints = endpoints;
            return new AppSurfaceDocsDeferredConventionBuilder(Name, _conventions, _gate, () => _finalized);
        }
    }

    public void EnsureMappedTo(IEndpointRouteBuilder endpoints)
    {
        lock (_gate)
        {
            if (_finalized)
            {
                throw new InvalidOperationException(
                    "AppSurface Docs endpoint mapping is already finalized. "
                    + "FinalizeAppSurfaceDocsInstances() may be called exactly once.");
            }

            if (!ReferenceEquals(_mappedEndpoints, endpoints))
            {
                throw new InvalidOperationException(
                    $"AppSurface Docs instance '{Name}' was registered but never mapped on this endpoint route builder. "
                    + $"Call {Name}Docs.MapEndpoints(endpoints) before FinalizeAppSurfaceDocsInstances().");
            }

        }
    }

    public void MarkFinalized(IEndpointRouteBuilder endpoints)
    {
        EnsureMappedTo(endpoints);

        lock (_gate)
        {
            _finalized = true;
        }
    }

    public void ApplyConventions(IEndpointConventionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Action<EndpointBuilder>[] conventions;
        lock (_gate)
        {
            conventions = _conventions.ToArray();
        }

        foreach (var convention in conventions)
        {
            builder.Add(convention);
        }
    }

    public Action<EndpointBuilder>[] GetConventions()
    {
        lock (_gate)
        {
            return _conventions.ToArray();
        }
    }

    internal static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalized = name.Trim();
        if (normalized.Length > 64
            || !normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException(
                "AppSurface Docs instance names must be 1-64 ASCII letters, digits, hyphens, or underscores.",
                nameof(name));
        }

        return normalized;
    }
}

internal sealed class AppSurfaceDocsDeferredConventionBuilder : IEndpointConventionBuilder
{
    private readonly string _name;
    private readonly List<Action<EndpointBuilder>> _conventions;
    private readonly object _gate;
    private readonly Func<bool> _isFinalized;

    public AppSurfaceDocsDeferredConventionBuilder(
        string name,
        List<Action<EndpointBuilder>> conventions,
        object gate,
        Func<bool> isFinalized)
    {
        _name = name;
        _conventions = conventions;
        _gate = gate;
        _isFinalized = isFinalized;
    }

    public void Add(Action<EndpointBuilder> convention)
    {
        ArgumentNullException.ThrowIfNull(convention);

        lock (_gate)
        {
            if (_isFinalized())
            {
                throw new InvalidOperationException(
                    $"AppSurface Docs instance '{_name}' cannot receive endpoint conventions after finalization.");
            }

            _conventions.Add(convention);
        }
    }
}

internal sealed class AppSurfaceDocsInstanceRegistry : IDisposable
{
    private const string LegacyDefaultName = "default";
    private const int MaxNamedInstances = 8;

    private readonly IReadOnlyList<AppSurfaceDocsInstanceDeclaration> _declarations;
    private readonly bool _hasLegacyDefault;
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, AppSurfaceDocsRuntime>? _runtimes;
    private bool _finalized;
    private bool _finalizationFailed;
    private int _disposed;

    public AppSurfaceDocsInstanceRegistry(IEnumerable<AppSurfaceDocsInstanceDeclaration> declarations)
    {
        _declarations = declarations?.ToArray() ?? throw new ArgumentNullException(nameof(declarations));
    }

    private AppSurfaceDocsInstanceRegistry(IServiceProvider legacyServices)
    {
        ArgumentNullException.ThrowIfNull(legacyServices);
        _declarations = [];
        _hasLegacyDefault = true;
        _runtimes = new Dictionary<string, AppSurfaceDocsRuntime>(StringComparer.OrdinalIgnoreCase)
        {
            [LegacyDefaultName] = CreateLegacyRuntime(legacyServices)
        };
        _finalized = true;
    }

    public static AppSurfaceDocsInstanceRegistry CreateLegacyDefault(IServiceProvider services)
    {
        return new AppSurfaceDocsInstanceRegistry(services);
    }

    public void FinalizeMappings(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_hasLegacyDefault)
            {
                throw new InvalidOperationException(
                    "Named AppSurface Docs composition cannot be finalized in a host using the legacy AddAppSurfaceDocs() "
                    + "or AppSurfaceDocsWebModule path. Use either one legacy default Docs surface or named composition, never both.");
            }

            if (_finalized)
            {
                throw new InvalidOperationException(
                    "AppSurface Docs endpoint mapping is already finalized. "
                    + "FinalizeAppSurfaceDocsInstances() may be called exactly once.");
            }

            if (_finalizationFailed)
            {
                throw new InvalidOperationException(
                    "A previous AppSurface Docs endpoint finalization attempt failed after composition began. "
                    + "Recreate the host after correcting the startup configuration or endpoint convention instead of retrying "
                    + "FinalizeAppSurfaceDocsInstances() on the partially published route table.");
            }

            if (_declarations.Count == 0)
            {
                throw new InvalidOperationException(
                    "No named AppSurface Docs instances were registered. "
                    + "Call AddAppSurfaceDocs(name, configurationSection) before finalization.");
            }

            if (_declarations.Count > MaxNamedInstances)
            {
                throw new InvalidOperationException(
                    $"AppSurface Docs supports at most {MaxNamedInstances} named instances per host. "
                    + $"The host declared {_declarations.Count}.");
            }

            ValidateDeclarationNames();
            ValidateMappedDeclarations(endpoints);

            IReadOnlyDictionary<string, AppSurfaceDocsRuntime>? runtimes = null;
            try
            {
                runtimes = BuildRuntimes(endpoints.ServiceProvider);
                ValidateRuntimeOwnership(runtimes.Values);
                foreach (var declaration in _declarations)
                {
                    declaration.MarkFinalized(endpoints);
                }

                // Route groups collect metadata and conventions before the concrete endpoint builders materialize. The mapper
                // then publishes each product's complete, fixed route family in the same order as the legacy mapper.
                foreach (var declaration in _declarations)
                {
                    var runtime = runtimes[declaration.Name];
                    var group = endpoints.MapGroup(string.Empty);
                    var endpointMetadata = new AppSurfaceDocsEndpointMetadata(runtime.Name);
                    group.WithMetadata(endpointMetadata);
                    declaration.ApplyConventions(group);
                    runtime.HarvestProgressAuthorizationData = ResolveAuthorizationData(declaration);
                    AppSurfaceDocsWebModule.MapNamedInstanceEndpoints(
                        group,
                        runtime,
                        builder =>
                        {
                            builder.WithMetadata(endpointMetadata);
                        });
                }

                _runtimes = runtimes;
                _finalized = true;
            }
            catch
            {
                _finalizationFailed = true;
                foreach (var runtime in runtimes?.Values ?? [])
                {
                    runtime.Dispose();
                }

                throw;
            }
        }
    }

    public AppSurfaceDocsRuntime GetRequiredRuntime(string? name)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_hasLegacyDefault)
            {
                return _runtimes![LegacyDefaultName];
            }

            if (!_finalized)
            {
                throw new InvalidOperationException(
                    "AppSurface Docs endpoint mapping is incomplete: the registry was not finalized. "
                    + "Call FinalizeAppSurfaceDocsInstances() after mapping all instances.");
            }

            if (string.IsNullOrWhiteSpace(name)
                || !_runtimes!.TryGetValue(name, out var runtime))
            {
                throw new InvalidOperationException(
                    "The current endpoint does not identify an AppSurface Docs runtime. "
                    + "Ensure the endpoint was mapped by an AppSurfaceDocsInstance handle.");
            }

            return runtime;
        }
    }

    internal IReadOnlyList<AppSurfaceDocsRuntime> GetFinalizedRuntimes()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_hasLegacyDefault)
            {
                return [];
            }

            if (!_finalized)
            {
                throw new InvalidOperationException(
                    "AppSurface Docs startup preflight requires finalized endpoint mapping. "
                    + "Call FinalizeAppSurfaceDocsInstances() after mapping all instances before starting the host.");
            }

            return _runtimes!.Values.ToArray();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var runtime in _runtimes?.Values ?? [])
            {
                runtime.Dispose();
            }
        }
    }

    private IReadOnlyDictionary<string, AppSurfaceDocsRuntime> BuildRuntimes(IServiceProvider services)
    {
        var runtimes = new Dictionary<string, AppSurfaceDocsRuntime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var declaration in _declarations)
            {
                var options = declaration.ConfigurationSection.Get<AppSurfaceDocsOptions>() ?? new AppSurfaceDocsOptions();
                AppSurfaceDocsServiceCollectionExtensions.NormalizeOptions(options);
                if (string.IsNullOrWhiteSpace(options.Source?.RepositoryRoot))
                {
                    throw new InvalidOperationException(
                        $"Named AppSurface Docs instance '{declaration.Name}' requires an explicit Source:RepositoryRoot. "
                        + "Named products cannot rely on repository-root discovery because that could join their source boundaries.");
                }

                var validation = new AppSurfaceDocsOptionsValidator().Validate(declaration.Name, options);
                if (validation.Failed)
                {
                    throw new OptionsValidationException(declaration.Name, typeof(AppSurfaceDocsOptions), validation.Failures);
                }

                runtimes[declaration.Name] = CreateNamedRuntime(declaration.Name, options, services);
            }

            return runtimes;
        }
        catch
        {
            foreach (var runtime in runtimes.Values)
            {
                runtime.Dispose();
            }

            throw;
        }
    }

    private void ValidateMappedDeclarations(IEndpointRouteBuilder endpoints)
    {
        foreach (var declaration in _declarations)
        {
            declaration.EnsureMappedTo(endpoints);
        }
    }

    private static AppSurfaceDocsRuntime CreateLegacyRuntime(IServiceProvider services)
    {
        return new AppSurfaceDocsRuntime(
            LegacyDefaultName,
            services.GetRequiredService<AppSurfaceDocsOptions>(),
            services.GetRequiredService<DocsUrlBuilder>(),
            services.GetRequiredService<DocsRecoveryLinkBuilder>(),
            services.GetRequiredService<AppSurfaceDocsIdentityResolver>(),
            services.GetRequiredService<AppSurfaceDocsThemeResolver>(),
            services.GetRequiredService<AppSurfaceDocsVersionCatalogService>(),
            services.GetRequiredService<AppSurfaceDocsSearchQualityReadModel>(),
            services.GetRequiredService<AppSurfaceDocsHarvestPathPolicy>(),
            services.GetRequiredService<DocFeaturedPageResolver>(),
            services.GetRequiredService<AppSurfaceDocsHarvestProgressReporter>(),
            services.GetRequiredService<DocAggregator>(),
            services.GetRequiredService<AppSurfaceDocsHarvestCoordinator>(),
            services.GetRequiredService<AppSurfaceDocsAssetVersioner>());
    }

    private static AppSurfaceDocsRuntime CreateNamedRuntime(
        string name,
        AppSurfaceDocsOptions options,
        IServiceProvider services)
    {
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var environment = services.GetRequiredService<IWebHostEnvironment>();
        var sanitizer = services.GetRequiredService<IAppSurfaceDocsHtmlSanitizer>();
        var docsUrlBuilder = new DocsUrlBuilder(options);
        var pathPolicy = new AppSurfaceDocsHarvestPathPolicy(
            options,
            loggerFactory.CreateLogger<AppSurfaceDocsHarvestPathPolicy>());
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var memo = new Memo(cache);
        var progress = new AppSurfaceDocsHarvestProgressReporter(
            services,
            loggerFactory.CreateLogger<AppSurfaceDocsHarvestProgressReporter>(),
            AppSurfaceDocsStreamAuthorization.GetHarvestProgressChannel(name));
        var harvesters = new IDocHarvester[]
        {
            new MarkdownHarvester(
                loggerFactory.CreateLogger<MarkdownHarvester>(),
                loggerFactory,
                options,
                pathPolicy),
            new CSharpDocHarvester(
                options,
                loggerFactory.CreateLogger<CSharpDocHarvester>(),
                pathPolicy),
            new JavaScriptDocHarvester(
                options,
                loggerFactory.CreateLogger<JavaScriptDocHarvester>(),
                pathPolicy)
        };
        var aggregator = new DocAggregator(
            harvesters,
            options,
            environment,
            memo,
            sanitizer,
            docsUrlBuilder,
            loggerFactory.CreateLogger<DocAggregator>(),
            harvestProgress: progress);
        var themeResolver = new AppSurfaceDocsThemeResolver(
            options,
            services.GetService<IAppSurfaceThemeResolver>(),
            services.GetService<AppSurfaceThemePreferenceOptions>());
        var versionCatalog = new AppSurfaceDocsVersionCatalogService(
            options,
            environment,
            loggerFactory.CreateLogger<AppSurfaceDocsVersionCatalogService>());

        var publishedTree = CreateNamedPublishedTree(
            versionCatalog,
            docsUrlBuilder,
            options,
            loggerFactory.CreateLogger<AppSurfaceDocsPublishedTreeHandler>());

        return new AppSurfaceDocsRuntime(
            name,
            options,
            docsUrlBuilder,
            new DocsRecoveryLinkBuilder(docsUrlBuilder),
            new AppSurfaceDocsIdentityResolver(options, docsUrlBuilder),
            themeResolver,
            versionCatalog,
            new AppSurfaceDocsSearchQualityReadModel(),
            pathPolicy,
            new DocFeaturedPageResolver(loggerFactory.CreateLogger<DocFeaturedPageResolver>(), docsUrlBuilder),
            progress,
            aggregator,
            new AppSurfaceDocsHarvestCoordinator(aggregator, progress),
            services.GetRequiredService<AppSurfaceDocsAssetVersioner>(),
            publishedTree.Handler,
            ownedResources: new AppSurfaceDocsRuntimeOwnedResources(cache, publishedTree.Providers));
    }

    private static (AppSurfaceDocsPublishedTreeHandler? Handler, IReadOnlyList<IDisposable> Providers) CreateNamedPublishedTree(
        AppSurfaceDocsVersionCatalogService versionCatalog,
        DocsUrlBuilder docsUrlBuilder,
        AppSurfaceDocsOptions options,
        ILogger<AppSurfaceDocsPublishedTreeHandler>? logger)
    {
        ArgumentNullException.ThrowIfNull(versionCatalog);
        ArgumentNullException.ThrowIfNull(docsUrlBuilder);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Versioning?.Enabled != true)
        {
            return (null, []);
        }

        var (mounts, providers) = AppSurfaceDocsWebModule.BuildPublishedTreeMounts(
            versionCatalog.GetCatalog(),
            docsUrlBuilder);
        if (mounts.Count == 0)
        {
            foreach (var provider in providers)
            {
                provider.Dispose();
            }

            return (null, []);
        }

        return (
            new AppSurfaceDocsPublishedTreeHandler(
                mounts,
                docsUrlBuilder.CurrentDocsRootPath,
                docsUrlBuilder.RouteRootPath,
                docsUrlBuilder.PublicOrigin,
                options.Versioning.MaxRewrittenFileSizeBytes,
                logger),
            providers);
    }

    private void ValidateDeclarationNames()
    {
        var duplicates = _declarations
            .GroupBy(declaration => declaration.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            var values = string.Join(", ", duplicates.Select(declaration => $"'{declaration.Name}'"));
            throw new InvalidOperationException(
                $"AppSurface Docs instance names are case-insensitive. Conflicting names: {values}. Choose unique names.");
        }
    }

    private static void ValidateRuntimeOwnership(IEnumerable<AppSurfaceDocsRuntime> runtimes)
    {
        var runtimeArray = runtimes.ToArray();
        for (var index = 0; index < runtimeArray.Length; index++)
        {
            for (var otherIndex = index + 1; otherIndex < runtimeArray.Length; otherIndex++)
            {
                var first = runtimeArray[index];
                var second = runtimeArray[otherIndex];
                EnsureDisjointRouteFamilies(first, second);
                EnsureDistinctSourceRoots(first, second);
                EnsureDistinctBrandingPrefixes(first, second);
            }
        }
    }

    private static void EnsureDisjointRouteFamilies(AppSurfaceDocsRuntime first, AppSurfaceDocsRuntime second)
    {
        var firstRoot = first.DocsUrlBuilder.RouteRootPath;
        var secondRoot = second.DocsUrlBuilder.RouteRootPath;
        if (IsSameOrDescendantRoute(firstRoot, secondRoot) || IsSameOrDescendantRoute(secondRoot, firstRoot))
        {
            throw new InvalidOperationException(
                $"AppSurface Docs instances '{first.Name}' ({firstRoot}) and '{second.Name}' ({secondRoot}) have overlapping "
                + "route families. Use sibling roots such as /docs and /internal/docs.");
        }
    }

    private static void EnsureDistinctSourceRoots(AppSurfaceDocsRuntime first, AppSurfaceDocsRuntime second)
    {
        var firstRoot = NormalizePhysicalPath(first.Options.Source?.RepositoryRoot);
        var secondRoot = NormalizePhysicalPath(second.Options.Source?.RepositoryRoot);
        if (firstRoot is not null
            && secondRoot is not null
            && (IsSameOrDescendantPhysicalPath(firstRoot, secondRoot)
                || IsSameOrDescendantPhysicalPath(secondRoot, firstRoot)))
        {
            throw new InvalidOperationException(
                $"AppSurface Docs instances '{first.Name}' ('{firstRoot}') and '{second.Name}' ('{secondRoot}') have overlapping "
                + "source roots. Each named Docs product requires a disjoint source boundary.");
        }
    }

    private static void EnsureDistinctBrandingPrefixes(AppSurfaceDocsRuntime first, AppSurfaceDocsRuntime second)
    {
        var firstPrefix = AppSurfaceDocsWebModule.ResolveBrandingAssetsRequestPath(first.Options);
        var secondPrefix = AppSurfaceDocsWebModule.ResolveBrandingAssetsRequestPath(second.Options);
        var firstHasBrandingDirectory = !string.IsNullOrWhiteSpace(first.Options.Identity?.BrandingAssets?.DirectoryPath);
        var secondHasBrandingDirectory = !string.IsNullOrWhiteSpace(second.Options.Identity?.BrandingAssets?.DirectoryPath);

        if (firstHasBrandingDirectory && firstPrefix is not null)
        {
            EnsureBrandingPrefixDoesNotOverlapRouteFamily(first.Name, firstPrefix, second.Name, second.DocsUrlBuilder.RouteRootPath);
        }

        if (secondHasBrandingDirectory && secondPrefix is not null)
        {
            EnsureBrandingPrefixDoesNotOverlapRouteFamily(second.Name, secondPrefix, first.Name, first.DocsUrlBuilder.RouteRootPath);
        }

        if (firstHasBrandingDirectory
            && secondHasBrandingDirectory
            && firstPrefix is not null
            && secondPrefix is not null
            && (IsSameOrDescendantRoute(firstPrefix, secondPrefix)
                || IsSameOrDescendantRoute(secondPrefix, firstPrefix)))
        {
            throw new InvalidOperationException(
                $"AppSurface Docs instances '{first.Name}' ({firstPrefix}) and '{second.Name}' ({secondPrefix}) have overlapping "
                + "branding request paths. Configure a distinct instance-owned branding prefix for each Docs product.");
        }
    }

    private static void EnsureBrandingPrefixDoesNotOverlapRouteFamily(
        string brandingInstanceName,
        string brandingPrefix,
        string docsInstanceName,
        string docsRouteRoot)
    {
        if (IsSameOrDescendantRoute(brandingPrefix, docsRouteRoot)
            || IsSameOrDescendantRoute(docsRouteRoot, brandingPrefix))
        {
            throw new InvalidOperationException(
                $"AppSurface Docs instance '{brandingInstanceName}' branding request path '{brandingPrefix}' overlaps the "
                + $"route family '{docsRouteRoot}' owned by instance '{docsInstanceName}'. Configure disjoint route and branding prefixes.");
        }
    }

    private static bool IsSameOrDescendantRoute(string candidate, string root)
    {
        return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePhysicalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsSameOrDescendantPhysicalPath(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IAuthorizeData> ResolveAuthorizationData(AppSurfaceDocsInstanceDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var endpointBuilder = new RouteEndpointBuilder(
            static _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/"),
            order: 0);
        foreach (var convention in declaration.GetConventions())
        {
            convention(endpointBuilder);
        }

        return endpointBuilder.Metadata
            .OfType<IAuthorizeData>()
            .ToArray();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AppSurfaceDocsInstanceRegistry));
        }
    }
}

internal sealed class LegacyAppSurfaceDocsRegistrationMarker;

internal sealed class NamedAppSurfaceDocsRegistrationMarker;

internal sealed class AppSurfaceDocsNamedHarvestStreamAuthorizationFilter : IRazorWireStreamAuthorizationFilter
{
    private readonly AppSurfaceDocsInstanceRegistry _instances;

    public AppSurfaceDocsNamedHarvestStreamAuthorizationFilter(AppSurfaceDocsInstanceRegistry instances)
    {
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
    }

    public async ValueTask<AppSurfaceAuthResult?> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!AppSurfaceDocsStreamAuthorization.TryGetNamedInstanceFromHarvestProgressChannel(
                context.Channel,
                out var instanceName))
        {
            return null;
        }

        AppSurfaceDocsRuntime runtime;
        try
        {
            runtime = _instances.GetRequiredRuntime(instanceName);
        }
        catch (InvalidOperationException)
        {
            return AppSurfaceAuthResult.Forbidden();
        }

        var environment = context.HttpContext.RequestServices.GetService<IHostEnvironment>();
        if (environment is null || !AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(runtime.Options, environment))
        {
            return AppSurfaceAuthResult.Forbidden();
        }

        var readPolicy = runtime.Options.Diagnostics?.OperatorReadPolicy;
        if (!string.IsNullOrWhiteSpace(readPolicy))
        {
            var readResult = await AppSurfaceDocsOperatorReadPolicyEvaluator.AuthorizeAsync(
                context.HttpContext,
                readPolicy,
                context.HttpContext.RequestAborted);
            if (!readResult.IsAllowed)
            {
                return readResult;
            }
        }

        var hostAuthorizationData = runtime.HarvestProgressAuthorizationData;
        if (hostAuthorizationData.Count == 0)
        {
            return null;
        }

        var result = await AppSurfaceDocsOperatorReadPolicyEvaluator.AuthorizeAsync(
            context.HttpContext,
            hostAuthorizationData,
            context.HttpContext.RequestAborted);
        return result.IsAllowed ? null : result;
    }
}

internal sealed class AppSurfaceDocsRuntimeOwnedResources : IDisposable
{
    private readonly IReadOnlyList<IDisposable> _resources;
    private int _disposed;

    public AppSurfaceDocsRuntimeOwnedResources(params IDisposable[] resources)
        : this((IReadOnlyList<IDisposable>)resources)
    {
    }

    public AppSurfaceDocsRuntimeOwnedResources(IDisposable resource, IReadOnlyList<IDisposable> additionalResources)
        : this([resource, .. additionalResources])
    {
    }

    private AppSurfaceDocsRuntimeOwnedResources(IReadOnlyList<IDisposable> resources)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var resource in _resources)
        {
            resource.Dispose();
        }
    }
}
