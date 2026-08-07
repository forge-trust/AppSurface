using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;
using Microsoft.Extensions.Configuration;

namespace AuthAspireKeycloakWeb;

/// <summary>
/// Configures the paired web proof to use the local AppHost Keycloak realm.
/// </summary>
/// <remarks>
/// The proof persists OIDC tokens so the sign-out handler can send Keycloak the required <c>id_token_hint</c>. This
/// setting is intentionally scoped to the local proof; production hosts should evaluate token persistence separately.
/// </remarks>
public static class AppSurfaceKeycloakWebOidcConfiguration
{
    private const string LocalAuthority = "https://localhost:8080/realms/appsurface-dev";
    private const string LocalClientId = "appsurface-web";

    /// <summary>
    /// Applies the local Keycloak authority, callback paths, client id, and sign-out token-persistence requirement.
    /// </summary>
    /// <param name="options">The AppSurface OIDC options to configure.</param>
    /// <param name="configuration">The application configuration that may override local defaults.</param>
    public static void Configure(AppSurfaceOidcAuthOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        options.RequireClientSecret = configuration.GetValue("Authentication:Oidc:RequireClientSecret", false);
        options.CallbackPath = configuration["Authentication:Oidc:CallbackPath"] ?? AppSurfaceOidcAuthOptions.DefaultCallbackPath;
        options.SignedOutCallbackPath = configuration["Authentication:Oidc:SignedOutCallbackPath"] ?? AppSurfaceOidcAuthOptions.DefaultSignedOutCallbackPath;
        options.ConfigureOpenIdConnect(oidc =>
        {
            oidc.Authority = configuration["Authentication:Oidc:Authority"] ?? LocalAuthority;
            oidc.ClientId = configuration["Authentication:Oidc:ClientId"] ?? LocalClientId;
            oidc.RequireHttpsMetadata = true;
            oidc.SaveTokens = configuration.GetValue(
                "Authentication:Oidc:SaveTokens",
                string.Equals(oidc.Authority, LocalAuthority, StringComparison.Ordinal)
                && string.Equals(oidc.ClientId, LocalClientId, StringComparison.Ordinal));
        });
    }
}
