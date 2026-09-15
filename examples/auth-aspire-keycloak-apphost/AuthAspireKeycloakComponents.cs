using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AuthAspireKeycloakLifecycleWorker;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace AuthAspireKeycloakAppHost;

/// <summary>
/// Adds the local Keycloak resource configured by the AppSurface proof package.
/// </summary>
public sealed class AuthAspireKeycloakComponent : IAspireComponent<KeycloakResource>
{
    private const string SampleAdminPassword = "appsurface-keycloak-admin-local-only";
    private const string SampleThemeImageEnvironmentVariable = "AUTH_ASPIRE_KEYCLOAK_THEME_IMAGE";
    private AppSurfaceKeycloakResource? _resolved;

    /// <summary>
    /// Gets the resolved AppSurface Keycloak wrapper after the component is generated.
    /// </summary>
    public AppSurfaceKeycloakResource Resolved =>
        _resolved ?? throw new InvalidOperationException("Resolve the Keycloak component before reading proof metadata.");

    /// <inheritdoc />
    public IResourceBuilder<KeycloakResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        _ = context;
        var theme = CreateSampleThemeFromEnvironment();
        var administratorUsername = appBuilder.AddParameter(
            AppSurfaceKeycloakDefaults.AdminUserParameterName,
            AppSurfaceKeycloakDefaults.AdminUser,
            secret: false);
        var administratorPassword = appBuilder.AddParameter(
            AppSurfaceKeycloakDefaults.AdminPasswordParameterName,
            SampleAdminPassword,
            secret: true);
        _resolved = appBuilder.AddAppSurfaceKeycloak(
            AppSurfaceKeycloakDefaults.ResourceName,
            administratorUsername,
            administratorPassword,
            options =>
        {
            options.LoginTheme = theme;
            options.UsePersistentDataVolume = true;
        });
        return _resolved.Resource;
    }

    private static AppSurfaceKeycloakThemeOptions? CreateSampleThemeFromEnvironment()
    {
        var image = Environment.GetEnvironmentVariable(SampleThemeImageEnvironmentVariable);
        return string.IsNullOrWhiteSpace(image)
            ? null
            : AppSurfaceKeycloakThemeOptions.Login(
                "appsurface-sample",
                "themes/appsurface-sample",
                AppSurfaceKeycloakImageReference.Parse(image));
    }
}

/// <summary>
/// Adds the web app that uses AppSurface OIDC against the local Keycloak resource.
/// </summary>
public sealed class AuthAspireKeycloakWebComponent : IAspireComponent<ProjectResource>
{
    /// <summary>
    /// Stable resource name for the paired local web proof.
    /// </summary>
    public const string ResourceName = "auth-aspire-keycloak-web";

    private readonly AuthAspireKeycloakComponent _keycloak;
    private readonly AuthAspireKeycloakCandidateFixtureComponent _candidateFixture;

    /// <summary>
    /// Creates the web component.
    /// </summary>
    /// <param name="keycloak">Keycloak component that supplies provider configuration.</param>
    /// <param name="candidateFixture">Final consumer-owned fixture stage that must complete before the web proof can start.</param>
    public AuthAspireKeycloakWebComponent(
        AuthAspireKeycloakComponent keycloak,
        AuthAspireKeycloakCandidateFixtureComponent candidateFixture)
    {
        _keycloak = keycloak;
        _candidateFixture = candidateFixture;
    }

    /// <inheritdoc />
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var keycloak = context.Resolve(_keycloak);
        var candidateFixture = context.Resolve(_candidateFixture);
        var web = appBuilder
            .AddProject<Projects.AuthAspireKeycloakWeb>(ResourceName)
            .WithHttpEndpoint(
                port: AppSurfaceKeycloakDefaults.WebProofPort,
                targetPort: AppSurfaceKeycloakDefaults.WebProofPort,
                env: "ASPNETCORE_HTTP_PORTS",
                isProxied: false)
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithReference(keycloak)
            .WaitFor(keycloak)
            .WaitForCompletion(candidateFixture);

        return _keycloak.Resolved.Configuration.ApplyTo(web);
    }
}

/// <summary>
/// Adds the first finite consumer-owned seed that upserts the local broker alias and founder subject map.
/// </summary>
public sealed class AuthAspireKeycloakIdentityBootstrapComponent : IAspireComponent<ProjectResource>
{
    private readonly AuthAspireKeycloakComponent _keycloak;
    private AppSurfaceKeycloakLocalSeed? _seed;

    /// <summary>
    /// Creates the identity-bootstrap component.
    /// </summary>
    /// <param name="keycloak">The Keycloak component that supplies the typed administrator parameter resources.</param>
    public AuthAspireKeycloakIdentityBootstrapComponent(AuthAspireKeycloakComponent keycloak)
    {
        _keycloak = keycloak;
    }

    /// <summary>
    /// Gets the registered local seed after this component has been resolved.
    /// </summary>
    public AppSurfaceKeycloakLocalSeed Seed =>
        _seed ?? throw new InvalidOperationException("Resolve the identity-bootstrap component before reading its seed handle.");

    /// <inheritdoc />
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var keycloak = context.Resolve(_keycloak);
        var metadata = _keycloak.Resolved;
        var administratorUsernameParameter = metadata.Resource.Resource.AdminUserNameParameter
            ?? throw new InvalidOperationException("The local Keycloak resource must expose an administrator username parameter.");
        var administratorPasswordParameter = metadata.Resource.Resource.AdminPasswordParameter
            ?? throw new InvalidOperationException("The local Keycloak resource must expose an administrator password parameter.");
        var administratorUser = appBuilder.CreateResourceBuilder(administratorUsernameParameter);
        var administratorPassword = appBuilder.CreateResourceBuilder(administratorPasswordParameter);

        _seed = metadata.WithLocalSeed(
            "identity-bootstrap",
            seed => appBuilder
                .AddProject<Projects.AuthAspireKeycloakIdentityBootstrap>(seed.ResourceName)
                .WithEnvironment("LOCAL_SEED_ADMIN_USERNAME", administratorUser)
                .WithEnvironment("LOCAL_SEED_STORE_PATH", AuthAspireKeycloakLocalSeedSample.StorePath(appBuilder))
                .WithReference(keycloak),
            options =>
            {
                AuthAspireKeycloakLocalSeedSample.EnableExplicitLocalRun(appBuilder, options);
                options.WithRequiredSecretParameter("LOCAL_SEED_ADMIN_PASSWORD", administratorPassword);
            });

        return _seed.Resource;
    }
}

/// <summary>
/// Adds the final finite consumer-owned fixture stage that validates the identity map and converges one candidate fixture.
/// </summary>
public sealed class AuthAspireKeycloakCandidateFixtureComponent : IAspireComponent<ProjectResource>
{
    private const string InjectFailureEnvironmentVariable = "AUTH_ASPIRE_KEYCLOAK_INJECT_FIXTURE_FAILURE";

    private readonly AuthAspireKeycloakIdentityBootstrapComponent _identityBootstrap;
    private readonly AuthAspireKeycloakComponent _keycloak;
    private AppSurfaceKeycloakLocalSeed? _seed;

    /// <summary>
    /// Creates the candidate-fixture component.
    /// </summary>
    /// <param name="keycloak">The Keycloak component that owns the ordered local seed registry.</param>
    /// <param name="identityBootstrap">The immediately preceding identity-bootstrap stage.</param>
    public AuthAspireKeycloakCandidateFixtureComponent(
        AuthAspireKeycloakComponent keycloak,
        AuthAspireKeycloakIdentityBootstrapComponent identityBootstrap)
    {
        _keycloak = keycloak;
        _identityBootstrap = identityBootstrap;
    }

    /// <summary>
    /// Gets the registered candidate-fixture seed after this component has been resolved.
    /// </summary>
    public AppSurfaceKeycloakLocalSeed Seed =>
        _seed ?? throw new InvalidOperationException("Resolve the candidate-fixture component before reading its seed handle.");

    /// <inheritdoc />
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        _ = context.Resolve(_identityBootstrap);
        var metadata = _keycloak.Resolved;
        var injectFailure = Environment.GetEnvironmentVariable(InjectFailureEnvironmentVariable) ?? "false";

        _seed = metadata.WithLocalSeed(
            "candidate-fixture",
            seed => appBuilder
                .AddProject<Projects.AuthAspireKeycloakCandidateFixture>(seed.ResourceName)
                .WithEnvironment("LOCAL_SEED_STORE_PATH", AuthAspireKeycloakLocalSeedSample.StorePath(appBuilder))
                .WithEnvironment("LOCAL_SEED_INJECT_FIXTURE_FAILURE", injectFailure),
            options =>
            {
                AuthAspireKeycloakLocalSeedSample.EnableExplicitLocalRun(appBuilder, options);
                options.After(_identityBootstrap.Seed);
            });

        return _seed.Resource;
    }
}

/// <summary>
/// Holds the sample-only local execution configuration that remains outside the reusable package contract.
/// </summary>
internal static class AuthAspireKeycloakLocalSeedSample
{
    private const string EnableLocalSeedsEnvironmentVariable = "AUTH_ASPIRE_KEYCLOAK_ENABLE_LOCAL_SEEDS";

    internal static void EnableExplicitLocalRun(
        IDistributedApplicationBuilder appBuilder,
        AppSurfaceKeycloakLocalSeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(appBuilder);
        ArgumentNullException.ThrowIfNull(options);
        if (bool.TryParse(Environment.GetEnvironmentVariable(EnableLocalSeedsEnvironmentVariable), out var enabled)
            && enabled)
        {
            options.AllowedEnvironmentNames.Add(appBuilder.Environment.EnvironmentName);
        }
    }

    internal static string StorePath(IDistributedApplicationBuilder appBuilder)
    {
        ArgumentNullException.ThrowIfNull(appBuilder);
        return Path.Join(appBuilder.AppHostDirectory, ".appsurface", "auth-aspire-keycloak-local-seed-store.json");
    }
}

/// <summary>
/// Adds the finite consumer-style worker used only to observe #782 completion behavior.
/// </summary>
/// <remarks>
/// The worker is not a public seeding API and performs no Keycloak administration. Its bounded modes let the
/// feasibility spike observe how a normal consumer project reports successful completion, failure, timeout, and a
/// non-completing process through public Aspire resource relationships.
/// </remarks>
public sealed class AuthAspireKeycloakLifecycleWorkerComponent : IAspireComponent<ProjectResource>
{
    /// <summary>
    /// Stable resource name used by the finite-worker feasibility graph.
    /// </summary>
    public const string ResourceName = "auth-aspire-keycloak-lifecycle-worker";

    private readonly AuthAspireKeycloakReadinessGateComponent _readinessGate;

    /// <summary>
    /// Creates the finite worker component.
    /// </summary>
    /// <param name="readinessGate">Baseline gate that must complete before the worker starts.</param>
    public AuthAspireKeycloakLifecycleWorkerComponent(AuthAspireKeycloakReadinessGateComponent readinessGate)
    {
        _readinessGate = readinessGate;
    }

    /// <inheritdoc />
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var readinessGate = context.Resolve(_readinessGate);
        var mode = Environment.GetEnvironmentVariable(AuthAspireKeycloakLifecycleWorkerEnvironment.Mode)
            ?? AuthAspireKeycloakLifecycleWorkerEnvironment.Success;

        return appBuilder
            .AddProject<Projects.AuthAspireKeycloakLifecycleWorker>(ResourceName)
            .WithEnvironment(AuthAspireKeycloakLifecycleWorkerEnvironment.Mode, mode)
            .WaitForCompletion(readinessGate);
    }
}

/// <summary>
/// Resolves the package-owned finite realm-ready process used by the local proof graph.
/// </summary>
/// <remarks>
/// The underlying <see cref="AppSurfaceKeycloakResource.RealmReady"/> resource checks the generated baseline after
/// Keycloak health and provides the completion handle consumed by finite local seed projects. It does not perform
/// provider administration or consume seed credentials.
/// </remarks>
public sealed class AuthAspireKeycloakReadinessGateComponent : IAspireComponent<ExecutableResource>
{
    /// <summary>
    /// Stable resource name used by the realm-ready completion graph.
    /// </summary>
    public const string ResourceName = "keycloak-realm-ready";

    private readonly AuthAspireKeycloakComponent _keycloak;

    /// <summary>
    /// Creates the finite readiness-gate component.
    /// </summary>
    /// <param name="keycloak">The component that supplies the local Keycloak resource and safe proof metadata.</param>
    public AuthAspireKeycloakReadinessGateComponent(AuthAspireKeycloakComponent keycloak)
    {
        _keycloak = keycloak;
    }

    /// <inheritdoc />
    public IResourceBuilder<ExecutableResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        _ = appBuilder;
        _ = context.Resolve(_keycloak);
        return _keycloak.Resolved.RealmReady().Resource;
    }
}

/// <summary>
/// Adds the verifier that checks the AppHost-backed Keycloak and web proof.
/// </summary>
public sealed class AuthAspireKeycloakVerifierComponent : IAspireComponent<ProjectResource>
{
    private readonly AuthAspireKeycloakComponent _keycloak;
    private readonly AuthAspireKeycloakWebComponent _web;

    /// <summary>
    /// Creates the verifier component.
    /// </summary>
    /// <param name="keycloak">Keycloak component that supplies proof metadata.</param>
    /// <param name="web">Web component to verify.</param>
    public AuthAspireKeycloakVerifierComponent(
        AuthAspireKeycloakComponent keycloak,
        AuthAspireKeycloakWebComponent web)
    {
        _keycloak = keycloak;
        _web = web;
    }

    /// <inheritdoc />
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var web = context.Resolve(_web);
        var keycloak = context.Resolve(_keycloak);
        var metadata = _keycloak.Resolved;

        return appBuilder
            .AddProject<Projects.AuthAspireKeycloakVerifier>("auth-aspire-keycloak-verifier")
            .WithEnvironment("AUTH_ASPIRE_KEYCLOAK_TARGET_URL", web.GetEndpoint("http"))
            .WithEnvironment("AUTH_ASPIRE_KEYCLOAK_CLIENT_ID", metadata.Configuration.ClientId)
            .WithEnvironment("AUTH_ASPIRE_KEYCLOAK_REALM_IMPORT_FILE", metadata.RealmImportFile)
            .WithEnvironment("APPSURFACE_KEYCLOAK_LOCAL_SEED_AUTHORITY", metadata.Configuration.Authority)
            .WithEnvironment("APPSURFACE_KEYCLOAK_LOCAL_SEED_PUBLIC_CLIENT_ID", metadata.Configuration.ClientId)
            .WithEnvironment("LOCAL_SEED_STORE_PATH", AuthAspireKeycloakLocalSeedSample.StorePath(appBuilder))
            .WithReference(keycloak)
            .WaitFor(web)
            .WaitFor(keycloak);
    }
}
