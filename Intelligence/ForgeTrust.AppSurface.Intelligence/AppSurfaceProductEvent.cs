namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes one AppSurface product-intelligence event instance.
/// </summary>
/// <remarks>
/// AppSurface product events are semantic product signals, not logs, traces, request captures, or vendor-specific
/// analytics payloads. Hosts own transport, retention, access control, and downstream product analytics setup.
/// </remarks>
public sealed class AppSurfaceProductEvent
{
    /// <summary>
    /// Creates a product-intelligence event instance.
    /// </summary>
    /// <param name="name">Registered event name.</param>
    /// <param name="timestamp">Timestamp supplied by the caller.</param>
    /// <param name="properties">Optional property values copied with ordinal keys.</param>
    /// <param name="actorId">Optional host-normalized actor identifier. Unsafe values are dropped before sinks run.</param>
    /// <param name="sessionId">Optional host-normalized session identifier. Unsafe values are dropped before sinks run.</param>
    /// <param name="correlationId">Optional low-cardinality correlation identifier for trace or request joins. Unsafe values are dropped before sinks run.</param>
    /// <param name="route">Optional route template or surface name. Full URLs, query strings, fragments, and unsafe values are dropped before sinks run.</param>
    public AppSurfaceProductEvent(
        string name,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, string>? properties = null,
        string? actorId = null,
        string? sessionId = null,
        string? correlationId = null,
        string? route = null)
    {
        Name = AppSurfaceProductEventMetadata.RequireIdentifier(name, nameof(name));
        Timestamp = timestamp;
        Properties = AppSurfaceProductEventMetadata.NormalizeProperties(properties, nameof(properties));
        ActorId = AppSurfaceProductEventMetadata.NormalizeOptionalText(actorId);
        SessionId = AppSurfaceProductEventMetadata.NormalizeOptionalText(sessionId);
        CorrelationId = AppSurfaceProductEventMetadata.NormalizeOptionalText(correlationId);
        Route = AppSurfaceProductEventMetadata.NormalizeOptionalText(route);
    }

    /// <summary>
    /// Gets the registered event name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the timestamp supplied by the caller.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Gets property values copied with ordinal keys.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; }

    /// <summary>
    /// Gets the optional host-normalized actor identifier.
    /// </summary>
    public string? ActorId { get; }

    /// <summary>
    /// Gets the optional host-normalized session identifier.
    /// </summary>
    public string? SessionId { get; }

    /// <summary>
    /// Gets the optional low-cardinality correlation identifier for trace or request joins.
    /// </summary>
    public string? CorrelationId { get; }

    /// <summary>
    /// Gets the optional route template or surface name.
    /// </summary>
    public string? Route { get; }

    /// <summary>
    /// Creates a new event with sanitized envelope metadata and replacement properties.
    /// </summary>
    /// <remarks>
    /// This internal API does not mutate the current event. The supplied <paramref name="properties"/> replace the
    /// original property set, while <see cref="ActorId"/>, <see cref="SessionId"/>, and <see cref="CorrelationId"/> are
    /// filtered through <see cref="AppSurfaceProductEventMetadata.SanitizeEnvelopeIdentifier"/> and <see cref="Route"/>
    /// is filtered through <see cref="AppSurfaceProductEventMetadata.SanitizeRoute"/>. Sanitization may drop optional
    /// envelope values by returning <see langword="null"/> when they are too long, contain unsafe characters, or look
    /// like secrets, PII, bearer headers, query strings, fragments, full URLs, or concrete high-cardinality route
    /// values. Callers must supply an already-sanitized property dictionary and must not assume original envelope values
    /// survive this step.
    /// </remarks>
    /// <param name="properties">Replacement event properties that have already passed registry sanitization.</param>
    /// <returns>A new event with sanitized envelope metadata.</returns>
    internal AppSurfaceProductEvent WithSanitizedEnvelope(IReadOnlyDictionary<string, string> properties)
    {
        return new AppSurfaceProductEvent(
            Name,
            Timestamp,
            properties,
            AppSurfaceProductEventMetadata.SanitizeEnvelopeIdentifier(ActorId),
            AppSurfaceProductEventMetadata.SanitizeEnvelopeIdentifier(SessionId),
            AppSurfaceProductEventMetadata.SanitizeEnvelopeIdentifier(CorrelationId),
            AppSurfaceProductEventMetadata.SanitizeRoute(Route));
    }
}
