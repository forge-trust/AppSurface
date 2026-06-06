namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Classifies the privacy sensitivity of a product-event property.
/// </summary>
public enum AppSurfaceProductEventSensitivity
{
    /// <summary>
    /// The property is safe operational or product context with no user-entered content.
    /// </summary>
    Operational,

    /// <summary>
    /// The property is derived from user behavior but does not contain raw user input.
    /// </summary>
    Behavioral,

    /// <summary>
    /// The property may require explicit host review before export to a product analytics sink.
    /// </summary>
    Sensitive
}
