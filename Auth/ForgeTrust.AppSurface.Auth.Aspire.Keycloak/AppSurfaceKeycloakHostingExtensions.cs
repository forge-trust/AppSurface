using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Adds AppSurface local Keycloak proof resources to Aspire AppHosts.
/// </summary>
public static class AppSurfaceKeycloakHostingExtensions
{
    /// <summary>
    /// Adds an official Aspire Keycloak resource configured with deterministic AppSurface local OIDC proof defaults.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="configure">Optional callback that customizes local proof options.</param>
    /// <returns>An AppSurface wrapper exposing the underlying Keycloak resource, secret-safe config projection, and readiness probe.</returns>
    public static AppSurfaceKeycloakResource AddAppSurfaceKeycloak(
        this IDistributedApplicationBuilder builder,
        string name = AppSurfaceKeycloakDefaults.ResourceName,
        Action<AppSurfaceKeycloakOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AppSurfaceKeycloakOptions
        {
            RealmImportDirectory = AppSurfaceKeycloakRealmImportPaths.CreateDirectory(AppContext.BaseDirectory, name),
        };
        configure?.Invoke(options);
        options.Validate();

        AppSurfaceKeycloakPortPreflight.ThrowIfOccupied(options.KeycloakPort, nameof(options.KeycloakPort));
        AppSurfaceKeycloakPortPreflight.ThrowIfOccupied(options.WebProofPort, nameof(options.WebProofPort));

        var realmFile = AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        var keycloak = builder.AddKeycloak(name, options.KeycloakPort)
            .WithRealmImport(options.RealmImportDirectory);
        if (options.UsePersistentDataVolume)
        {
            keycloak.WithDataVolume();
        }

        var projection = options.CreateConfigurationProjection();
        return new AppSurfaceKeycloakResource(
            keycloak,
            projection,
            new AppSurfaceKeycloakReadinessProbe(options),
            realmFile);
    }
}
