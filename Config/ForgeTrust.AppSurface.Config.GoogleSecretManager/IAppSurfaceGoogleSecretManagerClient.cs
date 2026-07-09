namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Reads Secret Manager version payloads for the AppSurface Google Secret Manager provider.
/// </summary>
public interface IAppSurfaceGoogleSecretManagerClient
{
    /// <summary>
    /// Accesses one Secret Manager version.
    /// </summary>
    /// <param name="resourceName">The full Secret Manager version resource name.</param>
    /// <param name="timeout">The bounded lookup timeout.</param>
    /// <returns>The secret version payload.</returns>
    AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout);
}
