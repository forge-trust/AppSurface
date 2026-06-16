namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Stores AppSurface LocalSecrets values for one machine and user context.
/// </summary>
/// <remarks>
/// Implementations should use display-safe diagnostics for expected platform failures. Raw secret values may be returned
/// only through <see cref="Get(AppSurfaceLocalSecretIdentity)"/> when the status is <see cref="LocalSecretResultStatus.Found"/>.
/// </remarks>
public interface IAppSurfaceLocalSecretStore
{
    /// <summary>
    /// Gets the display-safe store name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Reads a local secret.
    /// </summary>
    /// <param name="identity">The normalized local secret identity.</param>
    /// <returns>The store result.</returns>
    AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity);

    /// <summary>
    /// Writes a local secret.
    /// </summary>
    /// <param name="identity">The normalized local secret identity.</param>
    /// <param name="value">The raw secret value.</param>
    /// <returns>The store result. Successful writes return <see cref="LocalSecretResultStatus.Found"/> with no value.</returns>
    AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value);

    /// <summary>
    /// Deletes a local secret.
    /// </summary>
    /// <param name="identity">The normalized local secret identity.</param>
    /// <returns>The store result.</returns>
    AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity);

    /// <summary>
    /// Lists known local secret config keys for an application/environment namespace.
    /// </summary>
    /// <param name="applicationName">The normalized application name.</param>
    /// <param name="environment">The normalized environment name.</param>
    /// <param name="keyPrefix">The optional normalized key prefix.</param>
    /// <returns>The list result.</returns>
    AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix);

    /// <summary>
    /// Diagnoses whether the store is usable for the supplied namespace.
    /// </summary>
    /// <param name="applicationName">The normalized application name.</param>
    /// <param name="environment">The normalized environment name.</param>
    /// <param name="keyPrefix">The optional normalized key prefix.</param>
    /// <returns>A display-safe store diagnostic result.</returns>
    AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix);
}
