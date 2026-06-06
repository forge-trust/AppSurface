namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes the lifecycle promise attached to an AppSurface product event contract.
/// </summary>
public enum AppSurfaceProductEventLifecycle
{
    /// <summary>
    /// The event is available for dogfooding and may change before being recommended.
    /// </summary>
    Experimental,

    /// <summary>
    /// The event is ready for regular consumer use, but AppSurface has not committed to long-term stability.
    /// </summary>
    Recommended,

    /// <summary>
    /// The event name and schema are stable public contracts.
    /// </summary>
    Stable,

    /// <summary>
    /// The event remains readable for compatibility but should not be used for new capture.
    /// </summary>
    Deprecated
}
