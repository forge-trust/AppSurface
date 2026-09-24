namespace ForgeTrust.AppSurface.Config.LocalSecrets;

using ForgeTrust.AppSurface.Config;

/// <summary>
/// Identifies one local secret across application, environment, prefix, and AppSurface config key.
/// </summary>
/// <param name="ApplicationName">The normalized application name.</param>
/// <param name="Environment">The normalized environment name.</param>
/// <param name="KeyPrefix">The optional normalized key prefix.</param>
/// <param name="Key">The normalized AppSurface config key.</param>
/// <param name="StorageName">The stable cross-platform storage name.</param>
public sealed record AppSurfaceLocalSecretIdentity(
    string ApplicationName,
    string Environment,
    string? KeyPrefix,
    AppSurfaceConfigKey Key,
    string StorageName)
{
    /// <summary>Exact historical native key used only by migration; it is never parsed as application input.</summary>
    internal string? MigrationStoredKey { get; init; }
    internal string StoredKey => MigrationStoredKey ?? Key.Value;
}
