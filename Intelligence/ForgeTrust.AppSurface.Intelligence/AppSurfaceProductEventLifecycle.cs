namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes the lifecycle promise attached to an AppSurface product event contract.
/// </summary>
/// <remarks>
/// The numeric values are explicit because this public enum may be serialized, persisted, or used in generated
/// registry documentation. New values should be appended without changing the values documented here.
/// </remarks>
public enum AppSurfaceProductEventLifecycle
{
    /// <summary>
    /// The event is available for dogfooding and may change before being recommended.
    /// </summary>
    Experimental = 0,

    /// <summary>
    /// The event is ready for regular consumer use, but AppSurface has not committed to long-term stability.
    /// </summary>
    Recommended = 1,

    /// <summary>
    /// The event name and schema are stable public contracts.
    /// </summary>
    Stable = 2,

    /// <summary>
    /// The event remains readable for compatibility but should not be used for new capture.
    /// </summary>
    Deprecated = 3
}
