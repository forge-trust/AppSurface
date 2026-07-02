namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Describes a scoped convention resolver for Google Secret Manager mappings.
/// </summary>
/// <param name="LogicalKeyPrefix">The logical-key prefix this convention may claim.</param>
/// <param name="SecretIdPrefix">The optional prefix prepended to normalized secret ids.</param>
/// <param name="Version">The optional version or alias used by this convention.</param>
public sealed record AppSurfaceGoogleSecretConvention(
    string LogicalKeyPrefix,
    string SecretIdPrefix,
    string? Version);
