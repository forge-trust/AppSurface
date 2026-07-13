namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Default local-only values used by the AppSurface Keycloak AppHost proof package.
/// </summary>
/// <remarks>
/// These defaults intentionally mirror the AppSurface OIDC package callback paths without taking a runtime dependency on
/// that package. Drift is covered by tests so the AppHost proof package does not become part of web auth registration.
/// </remarks>
public static class AppSurfaceKeycloakDefaults
{
    /// <summary>
    /// Default Aspire resource name for the Keycloak server.
    /// </summary>
    public const string ResourceName = "keycloak";

    /// <summary>
    /// Default local Keycloak realm imported by the package.
    /// </summary>
    public const string Realm = "appsurface-dev";

    /// <summary>
    /// Default public OIDC client id imported into the local realm.
    /// </summary>
    public const string ClientId = "appsurface-web";

    /// <summary>
    /// Default host port used by the Keycloak container in focused samples.
    /// </summary>
    public const int KeycloakPort = 8080;

    /// <summary>
    /// Default host port used by the paired web proof sample.
    /// </summary>
    public const int WebProofPort = 5059;

    /// <summary>
    /// Default OIDC callback path expected by AppSurface OIDC registration.
    /// </summary>
    public const string CallbackPath = "/signin-appsurface-oidc";

    /// <summary>
    /// Default OIDC signed-out callback path expected by AppSurface OIDC registration.
    /// </summary>
    public const string SignedOutCallbackPath = "/signout-callback-appsurface-oidc";

    /// <summary>
    /// Default local-only admin username used by the Aspire Keycloak container image.
    /// </summary>
    public const string AdminUser = "admin";

    /// <summary>
    /// Default Aspire parameter name a host may use when overriding the Keycloak admin username.
    /// </summary>
    public const string AdminUserParameterName = "keycloak-admin-user";

    /// <summary>
    /// Default Aspire parameter name a host may use when overriding the Keycloak admin password.
    /// </summary>
    public const string AdminPasswordParameterName = "keycloak-admin-password";

    /// <summary>
    /// Default bounded wait used by readiness probes.
    /// </summary>
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Builds the default authority URL for a local realm and port.
    /// </summary>
    /// <param name="realm">The Keycloak realm name.</param>
    /// <param name="port">The local Keycloak host port.</param>
    /// <returns>The local HTTP authority URL.</returns>
    public static string Authority(string realm = Realm, int port = KeycloakPort) =>
        $"http://localhost:{port}/realms/{realm}";
}
