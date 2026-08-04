namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Supports an explicit, value-safe migration from a legacy LocalSecrets namespace to its current storage format.
/// </summary>
/// <remarks>
/// This optional capability is implemented only by stores that have retained readable legacy records. It never performs
/// migration during configuration resolution: callers must invoke it deliberately and handle the returned per-key status
/// without rendering secret values.
/// </remarks>
public interface IAppSurfaceLocalSecretMigrationStore
{
    /// <summary>
    /// Copies currently readable legacy records into the current storage format for one normalized namespace.
    /// </summary>
    /// <param name="applicationName">The normalized application identity.</param>
    /// <param name="environment">The normalized environment identity.</param>
    /// <param name="keyPrefix">The optional normalized key prefix.</param>
    /// <returns>A value-safe migration summary.</returns>
    AppSurfaceLocalSecretMigrationResult Migrate(string applicationName, string environment, string? keyPrefix);
}
