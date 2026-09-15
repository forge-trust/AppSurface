using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Identifies one registered finite consumer project in an AppSurface local Keycloak seed chain.
/// </summary>
public sealed class AppSurfaceKeycloakLocalSeed
{
    internal AppSurfaceKeycloakLocalSeed(
        AppSurfaceKeycloakResource owner,
        string name,
        IResourceBuilder<ProjectResource> resource)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resource);

        Owner = owner;
        Name = name;
        Resource = resource;
    }

    internal AppSurfaceKeycloakResource Owner { get; }

    /// <summary>
    /// Gets the caller-supplied stage name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the consumer-owned finite project resource.
    /// </summary>
    /// <remarks>
    /// Applications may make their own resources wait for this completion handle, but the normal next seed should be
    /// registered through <see cref="AppSurfaceKeycloakResource.WithLocalSeed"/> with
    /// <see cref="AppSurfaceKeycloakLocalSeedOptions.After"/> so the package can validate the linear chain.
    /// </remarks>
    public IResourceBuilder<ProjectResource> Resource { get; }
}
