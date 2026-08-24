using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AuthAspireKeycloakLifecycleWorker;
using AuthAspireKeycloakReadinessGate;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace AuthAspireKeycloakAppHost;

/// <summary>
/// Adds the local Keycloak resource configured by the AppSurface proof package.
/// </summary>
public sealed class AuthAspireKeycloakComponent : IAspireComponent<KeycloakResource>
{
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
        _resolved = appBuilder.AddAppSurfaceKeycloak(configure: options => options.LoginTheme = theme);
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
    private readonly AuthAspireKeycloakComponent _keycloak;
    private readonly AuthAspireKeycloakLifecycleWorkerComponent _lifecycleWorker;

    /// <summary>
    /// Creates the web component.
    /// </summary>
    /// <param name="keycloak">Keycloak component that supplies provider configuration.</param>
    /// <param name="lifecycleWorker">Finite consumer-style worker that must complete before the web proof can start.</param>
    public AuthAspireKeycloakWebComponent(
        AuthAspireKeycloakComponent keycloak,
        AuthAspireKeycloakLifecycleWorkerComponent lifecycleWorker)
    {
        _keycloak = keycloak;
        _lifecycleWorker = lifecycleWorker;
    }

    /// <inheritdoc />
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var keycloak = context.Resolve(_keycloak);
        var lifecycleWorker = context.Resolve(_lifecycleWorker);
        var web = appBuilder
            .AddProject<Projects.AuthAspireKeycloakWeb>("auth-aspire-keycloak-web")
            .WithHttpEndpoint(
                port: AppSurfaceKeycloakDefaults.WebProofPort,
                targetPort: AppSurfaceKeycloakDefaults.WebProofPort,
                env: "ASPNETCORE_HTTP_PORTS",
                isProxied: false)
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithReference(keycloak)
            .WaitFor(keycloak)
            .WaitForCompletion(lifecycleWorker);

        return _keycloak.Resolved.Configuration.ApplyTo(web);
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
/// Adds the finite #782 feasibility process that proves a project can complete after AppSurface baseline readiness.
/// </summary>
/// <remarks>
/// The component deliberately remains sample-only. It proves public Aspire graph behavior without adding a
/// package-managed callback runner or a public local-seed API before the documented feasibility evidence exists.
/// </remarks>
public sealed class AuthAspireKeycloakReadinessGateComponent : IAspireComponent<ProjectResource>
{
    /// <summary>
    /// Stable resource name used by the feasibility graph and its captured state timeline.
    /// </summary>
    public const string ResourceName = "auth-aspire-keycloak-readiness-gate";

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
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var keycloak = context.Resolve(_keycloak);
        var metadata = _keycloak.Resolved;
        var redirectUri = new Uri($"http://localhost:{AppSurfaceKeycloakDefaults.WebProofPort}{metadata.Configuration.CallbackPath}", UriKind.Absolute);
        var postLogoutRedirectUri = new Uri($"http://localhost:{AppSurfaceKeycloakDefaults.WebProofPort}{metadata.Configuration.SignedOutCallbackPath}", UriKind.Absolute);
        var realmImportDirectory = Path.GetDirectoryName(metadata.RealmImportFile)
            ?? throw new InvalidOperationException("The generated Keycloak realm-import file must have a parent directory.");

        return appBuilder
            .AddProject<Projects.AuthAspireKeycloakReadinessGate>(ResourceName)
            .WithEnvironment(KeycloakReadinessGateEnvironment.Authority, metadata.Configuration.Authority)
            .WithEnvironment(KeycloakReadinessGateEnvironment.ClientId, metadata.Configuration.ClientId)
            .WithEnvironment(KeycloakReadinessGateEnvironment.CallbackPath, metadata.Configuration.CallbackPath)
            .WithEnvironment(KeycloakReadinessGateEnvironment.SignedOutCallbackPath, metadata.Configuration.SignedOutCallbackPath)
            .WithEnvironment(KeycloakReadinessGateEnvironment.RedirectUri, redirectUri.ToString())
            .WithEnvironment(KeycloakReadinessGateEnvironment.PostLogoutRedirectUri, postLogoutRedirectUri.ToString())
            .WithEnvironment(KeycloakReadinessGateEnvironment.RealmImportDirectory, realmImportDirectory)
            .WaitFor(keycloak);
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
            .WithReference(keycloak)
            .WaitFor(web)
            .WaitFor(keycloak);
    }
}
