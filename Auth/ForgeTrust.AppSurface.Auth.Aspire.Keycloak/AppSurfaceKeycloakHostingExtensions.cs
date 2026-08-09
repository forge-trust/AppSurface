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
            RealmImportDirectory = AppSurfaceKeycloakRealmImportPaths.ResolveImportDirectory(AppContext.BaseDirectory, name),
        };
        configure?.Invoke(options);
        var snapshot = options.CreateValidatedSnapshot();
        var themeRegistration = snapshot.CreateThemeRegistration(AppContext.BaseDirectory);

        AppSurfaceKeycloakPortPreflight.ThrowIfOccupied(snapshot.KeycloakPort, nameof(snapshot.KeycloakPort));
        AppSurfaceKeycloakPortPreflight.ThrowIfOccupied(snapshot.WebProofPort, nameof(snapshot.WebProofPort));

        var realmFile = AppSurfaceKeycloakRealmGenerator.WriteRealmImport(snapshot);
        var keycloak = builder.AddKeycloak(name, snapshot.KeycloakPort)
            .WithRealmImport(snapshot.RealmImportDirectory);
        if (themeRegistration is not null)
        {
            keycloak = keycloak
                .WithImage(themeRegistration.BaseImage.Image, themeRegistration.BaseImage.Tag)
                .WithImageRegistry(themeRegistration.BaseImage.Registry)
                .WithImageSHA256(themeRegistration.BaseImage.Sha256)
                .WithBindMount(themeRegistration.SourceDirectory, $"/opt/keycloak/themes/{themeRegistration.Registration.Name}", isReadOnly: true)
                .WithEnvironment("KC_SPI_THEME_CACHE_THEMES", "false")
                .WithEnvironment("KC_SPI_THEME_CACHE_TEMPLATES", "false");
        }
        if (snapshot.UsePersistentDataVolume)
        {
            keycloak = keycloak.WithDataVolume();
        }

        var projection = snapshot.CreateConfigurationProjection();
        return new AppSurfaceKeycloakResource(
            keycloak,
            projection,
            new AppSurfaceKeycloakReadinessProbe(snapshot),
            realmFile,
            themeRegistration?.Registration);
    }
}
