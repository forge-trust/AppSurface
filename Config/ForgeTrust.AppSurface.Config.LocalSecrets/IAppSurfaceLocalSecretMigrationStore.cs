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

    /// <summary>Prepares the selected store's exact destination identifier without reading or writing secret values.</summary>
    /// <param name="applicationName">The pinned application namespace to normalize.</param>
    /// <param name="environment">The target environment namespace to normalize.</param>
    /// <param name="keyPrefix">The optional namespace prefix to normalize.</param>
    /// <param name="destinationKey">The strict logical destination key.</param>
    /// <returns>The normalized identity whose storage name uses this backend's migration encoding, or a value-free diagnostic.</returns>
    /// <remarks>
    /// Use this metadata-only operation for previews. Implementations use the same preparation inside
    /// <see cref="MigrateKey"/>; preparation neither acquires a lease nor proves that a later migration will succeed.
    /// The default rejects stores that do not expose their destination encoding.
    /// </remarks>
    AppSurfaceLocalSecretIdentityResult GetKeyMigrationDestinationIdentity(
        string applicationName, string environment, string? keyPrefix,
        ForgeTrust.AppSurface.Config.AppSurfaceConfigKey destinationKey) =>
        LocalSecretMigrationIdentity.UnsupportedDestination();

    /// <summary>
    /// Migrates one exact source backend identifier to a strict logical destination key.
    /// </summary>
    /// <param name="applicationName">The pinned application namespace.</param>
    /// <param name="environment">The target environment namespace.</param>
    /// <param name="keyPrefix">The optional namespace prefix.</param>
    /// <param name="sourceStoredKey">An exact native identifier, never parsed as a logical key.</param>
    /// <param name="destinationKey">A strict destination; dots and separator-like characters remain literal.</param>
    /// <returns>Operation id, last acknowledged state and a value-free diagnostic or completed status.</returns>
    /// <remarks>
    /// Unsupported stores stop before preparing a journal or reading values. Supported stores hold the shared writer
    /// lease through copy, verification, index publication and confirmed deletion. Retry the identical request after
    /// failure; the durable journal determines the resume point. See the package README's exact-key migration reference.
    /// </remarks>
    AppSurfaceLocalSecretKeyMigrationResult MigrateKey(
        string applicationName,
        string environment,
        string? keyPrefix,
        string sourceStoredKey,
        ForgeTrust.AppSurface.Config.AppSurfaceConfigKey destinationKey) =>
        AppSurfaceLocalSecretKeyMigrationResult.Failed(
            LocalSecretResultStatus.UnsupportedPlatform,
            "unsupported",
            AppSurfaceLocalSecretMigrationState.Prepared,
            sourceStoredKey,
            destinationKey,
            new AppSurfaceLocalSecretDiagnostic(
                "local-secret-migration-unsupported",
                "Exact-key migration is unavailable for this store.",
                "The store does not prove the required shared lease, journal, verification, and index ordering.",
                "Use a store with complete exact-key migration support and retry.",
                "local-secrets-migration"),
            "LocalSecrets");
}
