using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace AuthAspireKeycloakAppHost;

/// <summary>
/// Adds the local Keycloak resource configured by the AppSurface proof package.
/// </summary>
public sealed class AuthAspireKeycloakComponent : IAspireComponent<KeycloakResource>
{
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
        _resolved = appBuilder.AddAppSurfaceKeycloak();
        return _resolved.Resource;
    }
}

/// <summary>
/// Adds the web app that uses AppSurface OIDC against the local Keycloak resource.
/// </summary>
public sealed class AuthAspireKeycloakWebComponent : IAspireComponent<ProjectResource>
{
    private readonly AuthAspireKeycloakComponent _keycloak;

    /// <summary>
    /// Creates the web component.
    /// </summary>
    /// <param name="keycloak">Keycloak component that supplies provider configuration.</param>
    public AuthAspireKeycloakWebComponent(AuthAspireKeycloakComponent keycloak)
    {
        _keycloak = keycloak;
    }

    /// <inheritdoc />
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var keycloak = context.Resolve(_keycloak);
        var web = appBuilder
            .AddProject<Projects.AuthAspireKeycloakWeb>("auth-aspire-keycloak-web")
            .WithHttpEndpoint(targetPort: AppSurfaceKeycloakDefaults.WebProofPort, env: "ASPNETCORE_HTTP_PORTS")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithReference(keycloak)
            .WaitFor(keycloak);

        return _keycloak.Resolved.Configuration.ApplyTo(web);
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
