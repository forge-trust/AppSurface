namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Defines the nonsecret environment contract for the package-owned realm-ready executable.
/// </summary>
internal static class AppSurfaceKeycloakRealmReadyEnvironment
{
    internal const string Authority = "APPSURFACE_KEYCLOAK_REALM_READY_AUTHORITY";
    internal const string CallbackPath = "APPSURFACE_KEYCLOAK_REALM_READY_CALLBACK_PATH";
    internal const string ClientId = "APPSURFACE_KEYCLOAK_REALM_READY_CLIENT_ID";
    internal const string LoginThemeName = "APPSURFACE_KEYCLOAK_REALM_READY_LOGIN_THEME_NAME";
    internal const string PostLogoutRedirectUris = "APPSURFACE_KEYCLOAK_REALM_READY_POST_LOGOUT_REDIRECT_URIS";
    internal const string RealmImportDirectory = "APPSURFACE_KEYCLOAK_REALM_READY_REALM_IMPORT_DIRECTORY";
    internal const string RedirectUris = "APPSURFACE_KEYCLOAK_REALM_READY_REDIRECT_URIS";
    internal const string SeededUserNames = "APPSURFACE_KEYCLOAK_REALM_READY_SEEDED_USER_NAMES";
    internal const string SignedOutCallbackPath = "APPSURFACE_KEYCLOAK_REALM_READY_SIGNED_OUT_CALLBACK_PATH";
}
