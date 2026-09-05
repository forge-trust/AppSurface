using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Represents the finite AppHost resource that proves an AppSurface local Keycloak realm is ready for dependent work.
/// </summary>
/// <remarks>
/// The package owns only this baseline proof. Provider administration, client creation, broker policy, credentials,
/// mutations, retries, and convergence remain the responsibility of the consumer-owned finite project that waits for
/// <see cref="Resource"/> to complete.
/// </remarks>
public sealed class AppSurfaceKeycloakRealmReady
{
    internal AppSurfaceKeycloakRealmReady(IResourceBuilder<ExecutableResource> resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Resource = resource;
    }

    /// <summary>
    /// Gets the completion-bearing Aspire executable resource.
    /// </summary>
    /// <remarks>
    /// Depend on this resource through Aspire's <c>WaitForCompletion</c> relationship. A dependent project starts
    /// only after the realm-ready process exits successfully.
    /// </remarks>
    public IResourceBuilder<ExecutableResource> Resource { get; }
}
