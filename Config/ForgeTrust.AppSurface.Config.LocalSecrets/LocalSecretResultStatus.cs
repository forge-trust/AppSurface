namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Identifies the outcome of a local secret lookup or mutation.
/// </summary>
/// <remarks>
/// Only <see cref="Missing"/> represents true absence and may fall through to lower-priority configuration providers.
/// All other non-found states are terminal for a LocalSecrets-claimed key.
/// </remarks>
public enum LocalSecretResultStatus
{
    /// <summary>The secret value was found.</summary>
    Found = 0,

    /// <summary>The secret is truly absent.</summary>
    Missing = 1,

    /// <summary>The local store cannot be reached.</summary>
    Unavailable = 2,

    /// <summary>The local store exists but is locked or permission denied.</summary>
    Locked = 3,

    /// <summary>The current platform or session is unsupported.</summary>
    UnsupportedPlatform = 4,

    /// <summary>The configured local-secret posture disables the lookup.</summary>
    DisabledByPosture = 5,

    /// <summary>The app, environment, prefix, or key cannot form a valid local secret identity.</summary>
    InvalidIdentity = 6,

    /// <summary>The raw secret was found but could not be converted to the requested config type.</summary>
    ConversionFailed = 7,

    /// <summary>The provider failed unexpectedly.</summary>
    ProviderFailed = 8
}
