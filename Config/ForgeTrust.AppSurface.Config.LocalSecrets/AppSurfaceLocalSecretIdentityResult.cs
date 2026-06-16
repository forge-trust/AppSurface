namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Describes local secret identity normalization.
/// </summary>
/// <param name="Identity">The normalized identity when valid.</param>
/// <param name="Diagnostic">The display-safe diagnostic when invalid.</param>
public sealed record AppSurfaceLocalSecretIdentityResult(
    AppSurfaceLocalSecretIdentity? Identity,
    AppSurfaceLocalSecretDiagnostic? Diagnostic)
{
    /// <summary>
    /// Gets a value indicating whether normalization succeeded.
    /// </summary>
    public bool Succeeded => Identity != null;

    /// <summary>
    /// Creates a successful identity result.
    /// </summary>
    /// <param name="identity">The normalized identity.</param>
    /// <returns>A successful result.</returns>
    public static AppSurfaceLocalSecretIdentityResult Valid(AppSurfaceLocalSecretIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return new AppSurfaceLocalSecretIdentityResult(identity, null);
    }

    /// <summary>
    /// Creates a failed identity result.
    /// </summary>
    /// <param name="diagnostic">The display-safe diagnostic.</param>
    /// <returns>A failed result.</returns>
    public static AppSurfaceLocalSecretIdentityResult Invalid(AppSurfaceLocalSecretDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new AppSurfaceLocalSecretIdentityResult(null, diagnostic);
    }
}
