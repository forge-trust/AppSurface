namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Exception thrown when AppSurface DevAuth detects unsafe or invalid development-auth configuration.
/// </summary>
public sealed class AppSurfaceDevAuthException : InvalidOperationException
{
    /// <summary>
    /// Creates a DevAuth exception with a stable diagnostic code and safe message.
    /// </summary>
    /// <param name="diagnosticCode">Stable AppSurface DevAuth diagnostic code.</param>
    /// <param name="message">Safe diagnostic message.</param>
    public AppSurfaceDevAuthException(string diagnosticCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>
    /// Gets the stable AppSurface DevAuth diagnostic code.
    /// </summary>
    public string DiagnosticCode { get; }
}
