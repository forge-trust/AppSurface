namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Classifies the privacy sensitivity of a product-event property.
/// </summary>
/// <remarks>
/// The numeric values are explicit because this public enum may be serialized, persisted, or used in generated
/// registry documentation. New values should be appended without changing the values documented here.
/// </remarks>
public enum AppSurfaceProductEventSensitivity
{
    /// <summary>
    /// The property is safe operational or product context with no user-entered content.
    /// </summary>
    Operational = 0,

    /// <summary>
    /// The property is derived from user behavior but does not contain raw user input.
    /// </summary>
    Behavioral = 1,

    /// <summary>
    /// The property may require explicit host review before export to a product analytics sink.
    /// </summary>
    Sensitive = 2
}
