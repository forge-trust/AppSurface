namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Exception thrown when product-intelligence contract packs cannot be safely composed.
/// </summary>
/// <remarks>
/// Registration exceptions describe contract names, owners, and schema conflicts, but never event payload values.
/// They are intended to fail startup or service-provider validation before capture paths begin dropping events.
/// </remarks>
public sealed class AppSurfaceProductEventContractRegistrationException : InvalidOperationException
{
    /// <summary>
    /// Creates a safe contract registration exception.
    /// </summary>
    /// <param name="message">Safe diagnostic message.</param>
    public AppSurfaceProductEventContractRegistrationException(string message)
        : base(AppSurfaceProductEventMetadata.RequireText(message, nameof(message)))
    {
    }
}
