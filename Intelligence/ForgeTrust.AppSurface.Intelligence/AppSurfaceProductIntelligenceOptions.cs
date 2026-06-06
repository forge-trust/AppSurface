namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Configures AppSurface product-intelligence capture.
/// </summary>
public sealed class AppSurfaceProductIntelligenceOptions
{
    /// <summary>
    /// Gets a value indicating whether experimental event contracts may be emitted.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false" />. Keep this disabled for hosts that have not yet chosen product-event
    /// sinks, retention rules, and access controls. Enable it only during explicit dogfooding or host configuration where
    /// experimental AppSurface event contracts are expected to flow.
    /// </remarks>
    public bool ExperimentalEventsEnabled { get; private set; }

    /// <summary>
    /// Allows experimental event contracts to be emitted.
    /// </summary>
    /// <remarks>
    /// This method is a one-way toggle on the same options instance: it sets
    /// <see cref="ExperimentalEventsEnabled" /> to <see langword="true" /> and does not provide a matching disable
    /// operation. Avoid sharing and mutating one options instance across unrelated components when that one-way behavior
    /// would be surprising.
    /// </remarks>
    /// <returns>The same options instance for fluent configuration.</returns>
    public AppSurfaceProductIntelligenceOptions EnableExperimentalEvents()
    {
        ExperimentalEventsEnabled = true;
        return this;
    }
}
