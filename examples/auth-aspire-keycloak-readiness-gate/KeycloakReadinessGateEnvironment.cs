namespace AuthAspireKeycloakReadinessGate;

/// <summary>
/// Defines the non-secret environment contract between the #782 sample AppHost and its finite readiness worker.
/// </summary>
/// <remarks>
/// This is linked into the sample AppHost as source because an Aspire project-resource reference does not expose the
/// executable assembly for ordinary compile-time use. It is not a public AppSurface package API.
/// </remarks>
internal static class KeycloakReadinessGateEnvironment
{
    internal const string Authority = "AUTH_ASPIRE_KEYCLOAK_GATE_AUTHORITY";
    internal const string ClientId = "AUTH_ASPIRE_KEYCLOAK_GATE_CLIENT_ID";
    internal const string CallbackPath = "AUTH_ASPIRE_KEYCLOAK_GATE_CALLBACK_PATH";
    internal const string SignedOutCallbackPath = "AUTH_ASPIRE_KEYCLOAK_GATE_SIGNED_OUT_CALLBACK_PATH";
    internal const string RedirectUri = "AUTH_ASPIRE_KEYCLOAK_GATE_REDIRECT_URI";
    internal const string PostLogoutRedirectUri = "AUTH_ASPIRE_KEYCLOAK_GATE_POST_LOGOUT_REDIRECT_URI";
    internal const string RealmImportDirectory = "AUTH_ASPIRE_KEYCLOAK_GATE_REALM_IMPORT_DIRECTORY";
}
