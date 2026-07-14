namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Represents a safe AppSurface Keycloak proof diagnostic with a stable code.
/// </summary>
public sealed class AppSurfaceKeycloakException : InvalidOperationException
{
    /// <summary>
    /// Creates a new exception with a stable diagnostic code and safe message.
    /// </summary>
    /// <param name="code">The AppSurface Keycloak diagnostic code.</param>
    /// <param name="message">The safe diagnostic message.</param>
    public AppSurfaceKeycloakException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Gets the stable AppSurface Keycloak diagnostic code.
    /// </summary>
    public string Code { get; }
}
