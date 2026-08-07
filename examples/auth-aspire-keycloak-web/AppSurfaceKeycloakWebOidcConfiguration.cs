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
            oidc.Authority = configuration["Authentication:Oidc:Authority"] ?? "https://localhost:8080/realms/appsurface-dev";
            oidc.ClientId = configuration["Authentication:Oidc:ClientId"] ?? "appsurface-web";
            oidc.RequireHttpsMetadata = true;
            oidc.SaveTokens = true;
        });
    }
}
