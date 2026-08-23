using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
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
    private readonly AuthAspireKeycloakReadinessGateComponent _readinessGate;

    /// <summary>
    /// Creates the web component.
    /// </summary>
    /// <param name="keycloak">Keycloak component that supplies provider configuration.</param>
    /// <param name="readinessGate">Finite baseline gate that must complete before the web proof can start.</param>
    public AuthAspireKeycloakWebComponent(
        AuthAspireKeycloakComponent keycloak,
        AuthAspireKeycloakReadinessGateComponent readinessGate)
    {
        _keycloak = keycloak;
        _readinessGate = readinessGate;
    }

    /// <inheritdoc />
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var keycloak = context.Resolve(_keycloak);
        var readinessGate = context.Resolve(_readinessGate);
        var web = appBuilder
            .AddProject<Projects.AuthAspireKeycloakWeb>("auth-aspire-keycloak-web")
            .WithHttpEndpoint(targetPort: AppSurfaceKeycloakDefaults.WebProofPort, env: "ASPNETCORE_HTTP_PORTS")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithReference(keycloak)
            .WaitFor(keycloak)
            .WaitForCompletion(readinessGate);

        return _keycloak.Resolved.Configuration.ApplyTo(web);
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
