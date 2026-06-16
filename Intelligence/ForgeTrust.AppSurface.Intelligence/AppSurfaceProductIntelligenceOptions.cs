namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Configures AppSurface product-intelligence capture.
/// </summary>
public sealed class AppSurfaceProductIntelligenceOptions
{
    private readonly HashSet<string> _enabledExperimentalEventNames = new(StringComparer.Ordinal);
    private readonly List<AppSurfaceProductEventContract> _registeredEventContracts = [];

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
    /// Gets host/package event contracts registered for the composed product-intelligence registry.
    /// </summary>
    /// <remarks>
    /// The returned list is a copy so callers cannot mutate options state after configuration. The default registry
    /// composes these contracts with built-in AppSurface contracts during service construction. Identical semantic
    /// duplicates are idempotent; incompatible duplicate event names fail registry construction with safe diagnostics.
    /// </remarks>
    public IReadOnlyList<AppSurfaceProductEventContract> RegisteredEventContracts =>
        Array.AsReadOnly(_registeredEventContracts.ToArray());

    /// <summary>
    /// Gets a value indicating whether invalid events should throw instead of being silently dropped.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false" /> so capture remains best-effort in production request paths. Enable this
    /// in development or tests when missing registrations and malformed events should fail loudly. The exception is
    /// safe by design and never includes raw property values.
    /// </remarks>
    public bool ThrowOnInvalidEventsEnabled { get; private set; }

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
    /// Registers host/package event contracts for the composed product-intelligence registry.
    /// </summary>
    /// <param name="contracts">Contracts to compose with built-in AppSurface contracts.</param>
    /// <returns>The same options instance for fluent configuration.</returns>
    /// <remarks>
    /// Use this for reusable domain contract packs such as <c>SkoolieLaunchIntelligenceContracts.All</c>. The registry
    /// keeps normal capture best-effort while preserving AppSurface privacy protections: globally forbidden property
    /// names, forbidden value shapes, lifecycle gating, and safe diagnostics. Null contract sequences and null elements
    /// throw during configuration; empty sequences are a no-op.
    /// </remarks>
    public AppSurfaceProductIntelligenceOptions RegisterEventContracts(
        IEnumerable<AppSurfaceProductEventContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        foreach (var contract in contracts)
        {
            ArgumentNullException.ThrowIfNull(contract);
            _registeredEventContracts.Add(contract);
        }

        return this;
    }

    /// <summary>
    /// Registers host/package event contracts for the composed product-intelligence registry.
    /// </summary>
    /// <param name="contracts">Contracts to compose with built-in AppSurface contracts.</param>
    /// <returns>The same options instance for fluent configuration.</returns>
    public AppSurfaceProductIntelligenceOptions RegisterEventContracts(
        params AppSurfaceProductEventContract[] contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        return RegisterEventContracts((IEnumerable<AppSurfaceProductEventContract>)contracts);
    }

    /// <summary>
    /// Throws a safe development exception when an event would otherwise be dropped.
    /// </summary>
    /// <returns>The same options instance for fluent configuration.</returns>
    /// <remarks>
    /// This mode throws only for invalid events or disabled experimental events. It does not throw when optional
    /// properties are sanitized away and the remaining event is still valid for sink emission.
    /// </remarks>
    public AppSurfaceProductIntelligenceOptions ThrowOnInvalidEvents()
    {
        ThrowOnInvalidEventsEnabled = true;
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
