using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Wraps the official Aspire Keycloak resource with AppSurface local proof metadata.
/// </summary>
public sealed class AppSurfaceKeycloakResource
{
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
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentException.ThrowIfNullOrWhiteSpace(realmImportFile);

        Resource = resource;
        Configuration = configuration;
        Readiness = readiness;
        RealmImportFile = realmImportFile;
    }

    /// <summary>
    /// Gets the underlying Aspire Keycloak resource builder for normal Aspire APIs such as <c>WithReference</c> and
    /// <c>WaitFor</c>.
    /// </summary>
    public IResourceBuilder<KeycloakResource> Resource { get; }

    /// <summary>
    /// Gets the secret-safe web configuration projection.
    /// </summary>
    public AppSurfaceKeycloakConfigurationProjection Configuration { get; }

    /// <summary>
    /// Gets the readiness probe.
    /// </summary>
    public AppSurfaceKeycloakReadinessProbe Readiness { get; }

    /// <summary>
    /// Gets the generated realm import file path.
    /// </summary>
    public string RealmImportFile { get; }
}
