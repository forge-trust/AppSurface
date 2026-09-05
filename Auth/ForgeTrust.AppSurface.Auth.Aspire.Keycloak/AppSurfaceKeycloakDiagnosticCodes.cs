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

    /// <summary>
    /// Login-theme configuration or its immutable image reference is invalid.
    /// </summary>
    public const string InvalidThemeConfiguration = "ASKEYC010";

    /// <summary>
    /// Login-theme source is missing, unsafe, unsupported, or exceeds deterministic bounds.
    /// </summary>
    public const string ThemeSourceInvalid = "ASKEYC011";

    /// <summary>
    /// Theme source entries collide after normalized or case-insensitive path comparison.
    /// </summary>
    public const string ThemeSourceCollision = "ASKEYC012";

    /// <summary>
    /// A required login-theme property or resource declaration cannot be satisfied safely.
    /// </summary>
    public const string ThemePropertiesInvalid = "ASKEYC013";

    /// <summary>
    /// Copied FreeMarker templates are missing a bounded upstream baseline or contain an unreviewed override.
    /// </summary>
    public const string ThemeTemplateBaselineInvalid = "ASKEYC014";

    /// <summary>
    /// The pinned Keycloak image does not expose the supported theme archive layout.
    /// </summary>
    public const string ThemeArchiveLayoutInvalid = "ASKEYC015";

    /// <summary>
    /// A live theme source changed after its validated manifest was generated.
    /// </summary>
    public const string ThemeSourceChanged = "ASKEYC016";

    /// <summary>
    /// A materialized build context or packaged theme does not match deterministic source evidence.
    /// </summary>
    public const string ThemeBuildContractInvalid = "ASKEYC017";

    /// <summary>
    /// A built image identity, labels, or packaged theme subtree does not match the release evidence.
    /// </summary>
    public const string ThemePackagedImageInvalid = "ASKEYC018";

    /// <summary>
    /// The required Linux/amd64 runtime proof could not run or did not finish before its deadline.
    /// </summary>
    public const string ThemeRuntimeProofUnavailable = "ASKEYC019";

    /// <summary>
    /// Disposable-realm readback did not select the expected login theme.
    /// </summary>
    public const string ThemeRealmReadbackInvalid = "ASKEYC020";

    /// <summary>
    /// A declared login resource failed same-origin, hash, size, or availability verification.
    /// </summary>
    public const string ThemeRequiredResourceInvalid = "ASKEYC021";

    /// <summary>
    /// The package-owned finite realm-ready worker could not be resolved for the current AppHost execution.
    /// </summary>
    public const string RealmReadyWorkerUnavailable = "ASKEYC022";

    /// <summary>
    /// Local seed registration was attempted outside the permitted local AppHost execution policy.
    /// </summary>
    public const string LocalSeedNotAllowed = "ASKEYC023";

    /// <summary>
    /// A local seed name, predecessor, factory result, or secret binding is invalid.
    /// </summary>
    public const string LocalSeedInvalid = "ASKEYC024";
}
