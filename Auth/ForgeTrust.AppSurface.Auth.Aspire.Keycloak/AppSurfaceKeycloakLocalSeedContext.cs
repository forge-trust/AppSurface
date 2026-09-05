using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Supplies a local seed factory with the safe Keycloak metadata and resource name it may bind to its own project.
/// </summary>
/// <remarks>
/// This context never carries Keycloak administrator credentials, client secrets, tokens, claims, external subjects,
/// seeded-user passwords, provider responses, or consumer state. Bind a required consumer credential through
/// <see cref="AppSurfaceKeycloakLocalSeedOptions.WithRequiredSecretParameter"/> instead.
/// </remarks>
public sealed class AppSurfaceKeycloakLocalSeedContext
{
    internal AppSurfaceKeycloakLocalSeedContext(string resourceName, string authority, string realmName, string publicClientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(realmName);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicClientId);

        ResourceName = resourceName;
        Authority = authority;
        RealmName = realmName;
        PublicClientId = publicClientId;
    }

    /// <summary>
    /// Gets the exact required name for the consumer project resource.
    /// </summary>
    public string ResourceName { get; }

    /// <summary>
    /// Gets the safe local realm authority.
    /// </summary>
    public string Authority { get; }

    /// <summary>
    /// Gets the safe local realm name.
    /// </summary>
    public string RealmName { get; }

    /// <summary>
    /// Gets the safe public OIDC client identifier.
    /// </summary>
    public string PublicClientId { get; }

    internal IResourceBuilder<ProjectResource> ApplyTo(IResourceBuilder<ProjectResource> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project
            .WithEnvironment("APPSURFACE_KEYCLOAK_LOCAL_SEED_AUTHORITY", Authority)
            .WithEnvironment("APPSURFACE_KEYCLOAK_LOCAL_SEED_REALM_NAME", RealmName)
            .WithEnvironment("APPSURFACE_KEYCLOAK_LOCAL_SEED_PUBLIC_CLIENT_ID", PublicClientId);
    }
}
