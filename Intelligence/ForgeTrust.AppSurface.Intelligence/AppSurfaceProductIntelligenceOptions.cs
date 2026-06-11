namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Configures AppSurface product-intelligence capture.
/// </summary>
public sealed class AppSurfaceProductIntelligenceOptions
{
    private readonly HashSet<string> _enabledExperimentalEventNames = new(StringComparer.Ordinal);

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
    /// Gets experimental event names that are enabled without enabling every experimental contract.
    /// </summary>
    /// <remarks>
    /// This allowlist lets package integrations enable one product area, such as AppSurface Docs search-quality
    /// metrics, without turning on unrelated experimental dogfood events. Values are event names from
    /// <see cref="AppSurfaceProductEventRegistry"/>. The returned set is a copy so callers cannot mutate options state
    /// after configuration.
    /// </remarks>
    public IReadOnlySet<string> EnabledExperimentalEventNames =>
        new HashSet<string>(_enabledExperimentalEventNames, StringComparer.Ordinal);

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

    /// <summary>
    /// Allows selected experimental event contracts to be emitted.
    /// </summary>
    /// <param name="eventNames">Registered experimental event names to allow.</param>
    /// <returns>The same options instance for fluent configuration.</returns>
    /// <remarks>
    /// Use this when a host or package wants a narrow product-intelligence surface without enabling every experimental
    /// AppSurface event. Blank names are rejected during configuration so typos do not silently widen or narrow capture.
    /// Registered sinks still receive only events that pass <see cref="AppSurfaceProductEventRegistry.Validate(AppSurfaceProductEvent)"/>.
    /// </remarks>
    public AppSurfaceProductIntelligenceOptions EnableExperimentalEvents(params string[] eventNames)
    {
        ArgumentNullException.ThrowIfNull(eventNames);
        foreach (var eventName in eventNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
            _enabledExperimentalEventNames.Add(eventName);
        }

        return this;
    }

    /// <summary>
    /// Determines whether one experimental event name is allowed by this options instance.
    /// </summary>
    /// <param name="eventName">The experimental event name to test.</param>
    /// <returns><see langword="true"/> when all experimental events or the specific event name are enabled.</returns>
    public bool IsExperimentalEventEnabled(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return ExperimentalEventsEnabled || _enabledExperimentalEventNames.Contains(eventName);
    }
}
