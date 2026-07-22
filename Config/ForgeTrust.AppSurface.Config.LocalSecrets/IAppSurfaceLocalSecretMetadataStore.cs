namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Probes LocalSecrets metadata without returning raw secret values.
/// </summary>
/// <remarks>
/// Transfer workflows use this seam for dry-run planning and overwrite checks so they can answer whether a local
/// identity is known without calling <see cref="IAppSurfaceLocalSecretStore.Get(AppSurfaceLocalSecretIdentity)"/>.
/// Implementations must return display-safe diagnostics and must not put secret payloads in successful probe results.
/// Platform stores may use their LocalSecrets index as metadata; stale indexed entries are verified later when an apply
/// operation performs the value read that is required for transfer.
/// </remarks>
public interface IAppSurfaceLocalSecretMetadataStore
{
    /// <summary>
    /// Probes whether a local secret identity is present in store metadata.
    /// </summary>
    /// <param name="identity">The normalized local secret identity.</param>
    /// <returns>
    /// A display-safe result. <see cref="LocalSecretResultStatus.Found"/> means the identity is present in metadata;
    /// <see cref="LocalSecretResultStatus.Missing"/> means the store can confirm no metadata entry exists.
    /// </returns>
    AppSurfaceLocalSecretResult Probe(AppSurfaceLocalSecretIdentity identity);
}
