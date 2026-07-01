namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Classifies Google Secret Manager configuration resolution outcomes.
/// </summary>
public enum GoogleSecretManagerResultStatus
{
    /// <summary>The provider did not claim this key.</summary>
    Unclaimed = 0,

    /// <summary>The secret value was found and converted.</summary>
    Found = 1,

    /// <summary>The mapped secret or version was missing.</summary>
    Missing = 2,

    /// <summary>Secret Manager denied access.</summary>
    AccessDenied = 3,

    /// <summary>Secret Manager or its transport was unavailable or timed out.</summary>
    Unavailable = 4,

    /// <summary>The configured secret resource was invalid.</summary>
    InvalidResource = 5,

    /// <summary>The payload was not valid UTF-8 text.</summary>
    InvalidPayload = 6,

    /// <summary>The text payload could not be converted to the requested config type.</summary>
    ConversionFailed = 7,

    /// <summary>The lookup was cancelled by the underlying client.</summary>
    Cancelled = 8,

    /// <summary>The provider failed unexpectedly.</summary>
    ProviderFailed = 9
}
