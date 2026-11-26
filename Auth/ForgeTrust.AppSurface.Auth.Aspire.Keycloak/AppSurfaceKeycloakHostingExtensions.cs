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
        => AddCore(builder, name, adminUsername: null, adminPassword: null, configure);

    /// <summary>
    /// Adds an official Aspire Keycloak resource configured with deterministic AppSurface local OIDC proof defaults and
    /// explicit typed administrator parameters for finite consumer-owned local seed projects.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="adminUsername">The typed administrator username parameter supplied to the Keycloak container.</param>
    /// <param name="adminPassword">The typed secret administrator password parameter supplied to the Keycloak container.</param>
    /// <param name="configure">Optional callback that customizes local proof options.</param>
    /// <returns>An AppSurface wrapper exposing the underlying Keycloak resource, secret-safe config projection, and readiness probe.</returns>
    /// <remarks>
    /// Use this overload only when a consumer-owned local seed must authenticate to the Keycloak Admin API. AppSurface
    /// does not read either parameter value. A seed receives the password only through
    /// <see cref="AppSurfaceKeycloakLocalSeedOptions.WithRequiredSecretParameter"/> and owns all administration work.
    /// </remarks>
    public static AppSurfaceKeycloakResource AddAppSurfaceKeycloak(
        this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ParameterResource> adminUsername,
        IResourceBuilder<ParameterResource> adminPassword,
        Action<AppSurfaceKeycloakOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(adminUsername);
        ArgumentNullException.ThrowIfNull(adminPassword);

        if (!adminPassword.Resource.Secret)
        {
            throw new AppSurfaceKeycloakException(
                AppSurfaceKeycloakDiagnosticCodes.InvalidOptions,
                $"Problem: the Keycloak administrator password parameter is not secret. Cause: local seed credentials must use a typed secret ParameterResource. Fix: create the administrator password with secret: true and bind it only to the declared seed worker. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#ordered-local-seed-projects. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}.");
        }

        return AddCore(builder, name, adminUsername, adminPassword, configure);
    }

    /// <summary>Adds a configured supervised local Keycloak run with separately typed execution credentials.</summary>
    /// <param name="builder">The local Run builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="adminUsername">Same-builder administrator username parameter.</param>
    /// <param name="adminPassword">Same-builder secret administrator password parameter.</param>
    /// <param name="credentials">Client and enabled-provider typed secret bindings.</param>
    /// <param name="configure">Identity options and explicit supervised execution profile.</param>
    /// <returns>The normal wrapper with its supervised lifetime helper.</returns>
    /// <remarks>No parameter is resolved and no directory, import, container or volume is created during registration.</remarks>
    public static AppSurfaceKeycloakResource AddAppSurfaceKeycloak(this IDistributedApplicationBuilder builder, string name,
        IResourceBuilder<ParameterResource> adminUsername, IResourceBuilder<ParameterResource> adminPassword,
        AppSurfaceKeycloakCredentialBindings credentials, Action<AppSurfaceKeycloakOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AppSurfaceKeycloakOptions();
        configure(options);
        var registration = options.CreateRegistrationSnapshot();
        var snapshot = registration.Options;
        if (snapshot.SupervisedExecution is null) throw AppSurfaceKeycloakCredentialBindings.Invalid();
        AppSurfaceKeycloakLocalSeedPolicy.EnsureRunOperation(builder);
        AppSurfaceKeycloakLocalSeedPolicy.EnsureAllowedEnvironment(builder, new[] { "Development", "Test", "Testing" }.Concat(snapshot.AdditionalLocalEnvironmentNames));
        var binding = credentials.CreateSnapshot(builder, registration.Desired, adminUsername, adminPassword);
        return AppSurfaceKeycloakSupervisedRun.Register(builder, name, registration, binding);
    }

    private static AppSurfaceKeycloakResource AddCore(
        IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ParameterResource>? adminUsername,
        IResourceBuilder<ParameterResource>? adminPassword,
        Action<AppSurfaceKeycloakOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AppSurfaceKeycloakOptions
        {
            RealmImportDirectory = AppSurfaceKeycloakRealmImportPaths.ResolveImportDirectory(AppContext.BaseDirectory, name),
        };
        configure?.Invoke(options);
        var registration = options.CreateRegistrationSnapshot();
        var snapshot = registration.Options;
        AppSurfaceKeycloakLocalSeedPolicy.EnsureRunOperation(builder);
        AppSurfaceKeycloakLocalSeedPolicy.EnsureAllowedEnvironment(
            builder,
            new[] { "Development", "Test", "Testing" }.Concat(snapshot.AdditionalLocalEnvironmentNames));
        if (snapshot.SupervisedExecution is not null || snapshot.PersistentVolumeName is not null
            || snapshot.ClientMode != global::ForgeTrust.AppSurface.Auth.Keycloak.KeycloakClientMode.Public
            || snapshot.Mappers.Count != 0 || snapshot.IdentityProviders.Count != 0 || snapshot.RequireIdentitySeed)
            throw AppSurfaceKeycloakCredentialBindings.Invalid();
        var themeRegistration = registration.Theme;

        AppSurfaceKeycloakPortPreflight.ThrowIfOccupied(snapshot.KeycloakPort, nameof(snapshot.KeycloakPort));
        AppSurfaceKeycloakPortPreflight.ThrowIfOccupied(snapshot.WebProofPort, nameof(snapshot.WebProofPort));

        var realmFile = AppSurfaceKeycloakRealmGenerator.WriteRealmImport(registration);
        var keycloak = builder.AddKeycloak(name, snapshot.KeycloakPort, adminUsername, adminPassword)
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

        return new AppSurfaceKeycloakResource(
            keycloak,
            registration.Projection,
            new AppSurfaceKeycloakReadinessProbe(registration),
            realmFile,
            themeRegistration?.Registration,
            new AppSurfaceKeycloakRealmReadyConfiguration(snapshot, realmFile, themeRegistration?.Registration));
    }
}
