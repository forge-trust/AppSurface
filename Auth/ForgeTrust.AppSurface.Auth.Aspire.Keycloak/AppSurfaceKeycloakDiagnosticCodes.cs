namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Stable diagnostic codes emitted by the AppSurface Keycloak AppHost proof package.
/// </summary>
public static class AppSurfaceKeycloakDiagnosticCodes
{
    /// <summary>
    /// The configured realm, client, user, path, URI, or port option is invalid.
    /// </summary>
    public const string InvalidOptions = "ASKEYC001";

    /// <summary>
    /// A fixed local port is already occupied before the AppHost graph starts.
    /// </summary>
    public const string PortOccupied = "ASKEYC002";

    /// <summary>
    /// Keycloak OpenID metadata could not be reached before the bounded timeout.
    /// </summary>
    public const string MetadataUnavailable = "ASKEYC003";

    /// <summary>
    /// Keycloak OpenID metadata was reachable but did not match the expected realm.
    /// </summary>
    public const string MetadataInvalid = "ASKEYC004";

    /// <summary>
    /// Generated realm import evidence is missing expected client, redirect, or user data.
    /// </summary>
    public const string RealmEvidenceInvalid = "ASKEYC005";

    /// <summary>
    /// The Keycloak authorization endpoint rejected the configured public client or redirect URI.
    /// </summary>
    public const string AuthorizationChallengeInvalid = "ASKEYC006";
}
