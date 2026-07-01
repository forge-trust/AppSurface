namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Maps one AppSurface logical configuration key to a Google Secret Manager secret.
/// </summary>
/// <param name="LogicalKey">The logical AppSurface configuration key.</param>
/// <param name="SecretIdOrResourceName">A short secret id or full Secret Manager version resource name.</param>
/// <param name="Version">The optional version or alias for short secret ids.</param>
public sealed record AppSurfaceGoogleSecretMapping(
    string LogicalKey,
    string SecretIdOrResourceName,
    string? Version);
