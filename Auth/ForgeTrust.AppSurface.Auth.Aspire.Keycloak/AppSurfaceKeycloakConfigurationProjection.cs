using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Secret-safe app configuration produced by the AppSurface Keycloak proof package.
/// </summary>
/// <remarks>
/// The projection only contains OIDC authority, client id, callback paths, and the public-client secret policy. It never
/// contains admin credentials, seeded user passwords, realm JSON, tokens, raw claims, or client secrets.
/// </remarks>
public sealed class AppSurfaceKeycloakConfigurationProjection
{
    /// <summary>
    /// Creates a secret-safe projection.
    /// </summary>
    /// <param name="authority">The local Keycloak realm authority.</param>
    /// <param name="clientId">The local public client id.</param>
    /// <param name="callbackPath">The OIDC callback path.</param>
    /// <param name="signedOutCallbackPath">The OIDC signed-out callback path.</param>
    /// <param name="requireClientSecret">Whether the paired web app should require a client secret.</param>
    public AppSurfaceKeycloakConfigurationProjection(
        string authority,
        string clientId,
        string callbackPath,
        string signedOutCallbackPath,
        bool requireClientSecret)
    {
        Authority = authority;
        ClientId = clientId;
        CallbackPath = callbackPath;
        SignedOutCallbackPath = signedOutCallbackPath;
        RequireClientSecret = requireClientSecret;
    }

    /// <summary>
    /// Gets the local Keycloak realm authority.
    /// </summary>
    public string Authority { get; }

    /// <summary>
    /// Gets the local public client id.
    /// </summary>
    public string ClientId { get; }

    /// <summary>
    /// Gets the OIDC callback path.
    /// </summary>
    public string CallbackPath { get; }

    /// <summary>
    /// Gets the OIDC signed-out callback path.
    /// </summary>
    public string SignedOutCallbackPath { get; }

    /// <summary>
    /// Gets a value indicating whether the paired proof app should require a client secret.
    /// </summary>
    public bool RequireClientSecret { get; }

    /// <summary>
    /// Gets the allowlisted environment variables used by the proof web app.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authentication__Oidc__Authority"] = Authority,
            ["Authentication__Oidc__ClientId"] = ClientId,
            ["Authentication__Oidc__CallbackPath"] = CallbackPath,
            ["Authentication__Oidc__SignedOutCallbackPath"] = SignedOutCallbackPath,
            ["Authentication__Oidc__RequireClientSecret"] = RequireClientSecret ? "true" : "false",
        };

    /// <summary>
    /// Applies the allowlisted environment variables to an Aspire project resource.
    /// </summary>
    /// <param name="project">The project resource builder.</param>
    /// <returns>The same project resource builder for chaining.</returns>
    public IResourceBuilder<ProjectResource> ApplyTo(IResourceBuilder<ProjectResource> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var pair in EnvironmentVariables)
        {
            project.WithEnvironment(pair.Key, pair.Value);
        }

        return project;
    }
}
