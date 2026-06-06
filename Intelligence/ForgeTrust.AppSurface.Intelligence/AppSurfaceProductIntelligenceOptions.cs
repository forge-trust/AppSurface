namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Configures AppSurface product-intelligence capture.
/// </summary>
public sealed class AppSurfaceProductIntelligenceOptions
{
    /// <summary>
    /// Gets a value indicating whether experimental event contracts may be emitted.
    /// </summary>
    public bool ExperimentalEventsEnabled { get; private set; }

    /// <summary>
    /// Allows experimental event contracts to be emitted.
    /// </summary>
    /// <returns>The same options instance for fluent configuration.</returns>
    public AppSurfaceProductIntelligenceOptions EnableExperimentalEvents()
    {
        ExperimentalEventsEnabled = true;
        return this;
    }
}
