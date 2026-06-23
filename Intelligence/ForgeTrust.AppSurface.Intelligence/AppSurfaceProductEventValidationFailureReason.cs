namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Identifies safe, stable product-event validation failure categories.
/// </summary>
/// <remarks>
/// Reason codes are intentionally coarse so they can be logged, surfaced in development exceptions, or used by tests
/// without exposing raw event payload values.
/// </remarks>
public enum AppSurfaceProductEventValidationFailureReason
{
    /// <summary>
    /// The event name has no registered contract.
    /// </summary>
    EventNotRegistered = 0,

    /// <summary>
    /// The event contains a property that is not registered on the matched contract.
    /// </summary>
    PropertyNotRegistered = 1,

    /// <summary>
    /// The event contains a globally forbidden property name.
    /// </summary>
    ForbiddenPropertyName = 2,

    /// <summary>
    /// A property value failed its contract value-shape rules.
    /// </summary>
    InvalidPropertyValue = 3,

    /// <summary>
    /// A required property was absent or was rejected during sanitization.
    /// </summary>
    RequiredPropertyMissing = 4,

    /// <summary>
    /// The event matched an experimental contract that has not been enabled.
    /// </summary>
    ExperimentalEventNotEnabled = 5
}
