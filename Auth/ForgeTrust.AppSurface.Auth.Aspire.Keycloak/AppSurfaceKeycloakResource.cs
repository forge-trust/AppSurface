using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Wraps the official Aspire Keycloak resource with AppSurface local proof metadata.
/// </summary>
public sealed class AppSurfaceKeycloakResource
{
    private const string LegacyImportUnavailable = "Supervised Keycloak uses the RealmReady() reconciliation gate. Import metadata is available for legacy registration.";
    private readonly object _localSeedsLock = new();
    private readonly List<AppSurfaceKeycloakLocalSeed> _localSeeds = [];
    private readonly object _realmReadyLock = new();
    private readonly AppSurfaceKeycloakReadinessProbe? _readiness;
    private readonly string? _realmImportFile;
    private readonly AppSurfaceKeycloakRealmReadyConfiguration? _realmReadyConfiguration;
    private readonly HashSet<ParameterResource> _usedLocalSeedParameters = new(ReferenceEqualityComparer.Instance);
    private volatile AppSurfaceKeycloakRealmReady? _realmReady;

    /// <summary>
    /// Creates a new wrapper around an Aspire Keycloak resource.
    /// </summary>
    /// <param name="resource">The underlying Aspire Keycloak resource builder.</param>
    /// <param name="configuration">The secret-safe web configuration projection.</param>
    /// <param name="readiness">The readiness probe for this resource.</param>
    /// <param name="realmImportFile">The generated realm import file path.</param>
    public AppSurfaceKeycloakResource(
        IResourceBuilder<KeycloakResource> resource,
        AppSurfaceKeycloakConfigurationProjection configuration,
        AppSurfaceKeycloakReadinessProbe readiness,
        string realmImportFile)
        : this(resource, configuration, readiness, realmImportFile, theme: null)
    {
    }

    /// <summary>
    /// Creates a new wrapper around an Aspire Keycloak resource with optional secret-safe login-theme evidence.
    /// </summary>
    /// <param name="resource">The underlying Aspire Keycloak resource builder.</param>
    /// <param name="configuration">The secret-safe web configuration projection.</param>
    /// <param name="readiness">The readiness probe for this resource.</param>
    /// <param name="realmImportFile">The generated realm import file path.</param>
    /// <param name="theme">Optional secret-safe login-theme evidence.</param>
    public AppSurfaceKeycloakResource(
        IResourceBuilder<KeycloakResource> resource,
        AppSurfaceKeycloakConfigurationProjection configuration,
        AppSurfaceKeycloakReadinessProbe readiness,
        string realmImportFile,
        AppSurfaceKeycloakThemeRegistration? theme)
        : this(resource, configuration, readiness, realmImportFile, theme, realmReadyConfiguration: null)
    {
    }

    internal AppSurfaceKeycloakResource(
        IResourceBuilder<KeycloakResource> resource,
        AppSurfaceKeycloakConfigurationProjection configuration,
        AppSurfaceKeycloakReadinessProbe readiness,
        string realmImportFile,
        AppSurfaceKeycloakThemeRegistration? theme,
        AppSurfaceKeycloakRealmReadyConfiguration? realmReadyConfiguration)
        : this(resource, configuration, theme)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentException.ThrowIfNullOrWhiteSpace(realmImportFile);

        _readiness = readiness;
        _realmImportFile = realmImportFile;
        _realmReadyConfiguration = realmReadyConfiguration;
    }

    /// <summary>Wraps a supervised resource whose realm readiness comes from its reconciliation worker.</summary>
    /// <param name="resource">The underlying Aspire Keycloak resource builder.</param>
    /// <param name="configuration">The secret-safe web configuration projection.</param>
    /// <param name="run">The registered supervised run and its completion gate.</param>
    /// <param name="theme">Optional secret-safe login-theme evidence.</param>
    internal AppSurfaceKeycloakResource(
        IResourceBuilder<KeycloakResource> resource,
        AppSurfaceKeycloakConfigurationProjection configuration,
        AppSurfaceKeycloakSupervisedRun run,
        AppSurfaceKeycloakThemeRegistration? theme)
        : this(resource, configuration, theme)
    {
        ArgumentNullException.ThrowIfNull(run);
        SupervisedRun = run;
    }

    private AppSurfaceKeycloakResource(
        IResourceBuilder<KeycloakResource> resource,
        AppSurfaceKeycloakConfigurationProjection configuration,
        AppSurfaceKeycloakThemeRegistration? theme)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuration);

        Resource = resource;
        Configuration = configuration;
        Theme = theme;
    }

    /// <summary>
    /// Gets the underlying Aspire Keycloak resource builder for normal Aspire APIs such as <c>WithReference</c> and
    /// <c>WaitFor</c>.
    /// </summary>
    public IResourceBuilder<KeycloakResource> Resource { get; }

    /// <summary>Gets the explicit supervised lifetime helper, or null for the legacy local proof path.</summary>
    public AppSurfaceKeycloakSupervisedRun? SupervisedRun { get; }

    /// <summary>
    /// Gets the secret-safe web configuration projection.
    /// </summary>
    public AppSurfaceKeycloakConfigurationProjection Configuration { get; }

    /// <summary>Gets the legacy readiness probe for the generated import.</summary>
    /// <exception cref="InvalidOperationException">The resource is supervised. Use <see cref="RealmReady"/>.</exception>
    public AppSurfaceKeycloakReadinessProbe Readiness => _readiness ?? throw new InvalidOperationException(LegacyImportUnavailable);

    /// <summary>Gets the generated realm import file path for legacy registration.</summary>
    /// <exception cref="InvalidOperationException">The resource is supervised. Use <see cref="RealmReady"/>.</exception>
    public string RealmImportFile => _realmImportFile ?? throw new InvalidOperationException(LegacyImportUnavailable);

    /// <summary>
    /// Gets secret-safe evidence for the optional local login theme.
    /// </summary>
    public AppSurfaceKeycloakThemeRegistration? Theme { get; }

    /// <summary>
    /// Gets the finite resource that confirms realm readiness for this registration mode.
    /// </summary>
    /// <returns>The completion-bearing resource that local seeds depend on.</returns>
    /// <remarks>
    /// Legacy registration creates a credential-free baseline gate that waits for Keycloak and checks metadata,
    /// generated import evidence, and the authorization challenge. Supervised registration returns the gate
    /// for the managed realm reconciliation worker. Repeated calls return the same resource.
    /// </remarks>
    /// <exception cref="AppSurfaceKeycloakException">The wrapper was not created by <c>AddAppSurfaceKeycloak(...)</c>
    /// or the package-owned worker cannot be resolved.</exception>
    public AppSurfaceKeycloakRealmReady RealmReady()
    {
        if (SupervisedRun is not null) return SupervisedRun.RealmReady;
        if (_realmReady is not null)
        {
            return _realmReady;
        }

        lock (_realmReadyLock)
        {
            if (_realmReady is not null)
            {
                return _realmReady;
            }

            if (_realmReadyConfiguration is null)
            {
                throw AppSurfaceKeycloakRealmReadyConfiguration.WorkerUnavailable();
            }

            _realmReady = _realmReadyConfiguration.Create(Resource);
            return _realmReady;
        }
    }

    /// <summary>
    /// Registers one finite, consumer-owned project that seeds local Keycloak-adjacent state after baseline realm readiness.
    /// </summary>
    /// <param name="name">A unique lower-case local seed stage name.</param>
    /// <param name="factory">Creates exactly one finite Aspire <see cref="ProjectResource"/> using the supplied safe context.</param>
    /// <param name="configure">Optional local-only ordering, environment-policy, and typed-secret configuration.</param>
    /// <returns>The registered local seed handle, including its consumer-owned project resource.</returns>
    /// <remarks>
    /// Registration is permitted only while the AppHost executes <c>Run</c> in <c>Development</c>, <c>Test</c>, or
    /// <c>Testing</c> by default. The first seed waits for <see cref="RealmReady"/>. Each later seed must nominate the
    /// immediately previous handle via <see cref="AppSurfaceKeycloakLocalSeedOptions.After"/>; this intentionally
    /// creates one linear, observable completion chain. The package never starts a callback runner or performs
    /// Keycloak administration. The factory owns the actual client, mutation, retry, idempotence, and finite exit code.
    /// </remarks>
    /// <exception cref="AppSurfaceKeycloakException">Local policy, ordering, factory, or secret-binding validation fails.</exception>
    public AppSurfaceKeycloakLocalSeed WithLocalSeed(
        string name,
        Func<AppSurfaceKeycloakLocalSeedContext, IResourceBuilder<ProjectResource>> factory,
        Action<AppSurfaceKeycloakLocalSeedOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (SupervisedRun is not null) throw AppSurfaceKeycloakLocalSeedPolicy.Invalid();

        lock (_localSeedsLock)
        {
            var applicationBuilder = Resource.ApplicationBuilder;
            AppSurfaceKeycloakLocalSeedPolicy.EnsureRunOperation(applicationBuilder);

            var options = new AppSurfaceKeycloakLocalSeedOptions();
            configure?.Invoke(options);
            AppSurfaceKeycloakLocalSeedPolicy.EnsureAllowedEnvironment(applicationBuilder, options.AllowedEnvironmentNames);
            AppSurfaceKeycloakLocalSeedPolicy.ValidateRegistration(
                name,
                options,
                applicationBuilder,
                _localSeeds,
                _usedLocalSeedParameters);

            var resourceName = $"{Resource.Resource.Name}-seed-{name}";
            var context = new AppSurfaceKeycloakLocalSeedContext(
                resourceName,
                Configuration.Authority,
                RealmName(),
                Configuration.ClientId);
            var realmReady = RealmReady();
            var project = factory(context) ?? throw AppSurfaceKeycloakLocalSeedPolicy.Invalid();
            if (!ReferenceEquals(project.ApplicationBuilder, applicationBuilder)
                || !string.Equals(project.Resource.Name, resourceName, StringComparison.Ordinal))
            {
                throw AppSurfaceKeycloakLocalSeedPolicy.Invalid();
            }

            context.ApplyTo(project);
            foreach (var binding in options.RequiredSecretBindings)
            {
                project.WithEnvironment(binding.EnvironmentVariableName, binding.Parameter);
            }

            var predecessor = options.Predecessor;
            project.WaitForCompletion(realmReady.Resource);
            if (predecessor is not null)
            {
                project.WaitForCompletion(predecessor.Resource);
            }

            var seed = new AppSurfaceKeycloakLocalSeed(this, name, project);
            _localSeeds.Add(seed);
            foreach (var binding in options.RequiredSecretBindings)
            {
                _usedLocalSeedParameters.Add(binding.Parameter.Resource);
            }

            return seed;
        }
    }

    private string RealmName()
    {
        const string realmPathPrefix = "/realms/";
        if (!Uri.TryCreate(Configuration.Authority, UriKind.Absolute, out var authority)
            || !authority.AbsolutePath.StartsWith(realmPathPrefix, StringComparison.Ordinal)
            || authority.AbsolutePath.Length == realmPathPrefix.Length)
        {
            throw AppSurfaceKeycloakLocalSeedPolicy.Invalid();
        }

        return authority.AbsolutePath[realmPathPrefix.Length..];
    }
}
