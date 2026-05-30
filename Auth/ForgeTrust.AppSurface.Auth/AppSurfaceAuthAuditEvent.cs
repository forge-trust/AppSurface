namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Describes a passive AppSurface auth audit event.
/// </summary>
/// <remarks>
/// This value does not write logs, traces, metrics, or persistent audit records. Host applications own audit transport,
/// retention, redaction, and access control. Metadata should remain non-sensitive and diagnostic.
/// </remarks>
public sealed class AppSurfaceAuthAuditEvent
{
    /// <summary>
    /// Creates a passive auth audit event description.
    /// </summary>
    /// <param name="name">Stable event name. The value must be non-empty and is preserved exactly.</param>
    /// <param name="timestamp">Timestamp supplied by the host.</param>
    /// <param name="outcome">High-level auth outcome associated with the event.</param>
    /// <param name="reason">Concrete auth reason associated with the event.</param>
    /// <param name="userId">Optional user identifier. Null or whitespace values are normalized to <see langword="null"/>.</param>
    /// <param name="sessionId">Optional session identifier. Null or whitespace values are normalized to <see langword="null"/>.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    public AppSurfaceAuthAuditEvent(
        string name,
        DateTimeOffset timestamp,
        AppSurfaceAuthOutcome outcome,
        AppSurfaceAuthReason reason,
        string? userId = null,
        string? sessionId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        AppSurfaceAuthResult.ValidateCombination(outcome, reason);
        Name = AppSurfaceAuthMetadata.RequireIdentifier(name, nameof(name));
        Timestamp = timestamp;
        Outcome = outcome;
        Reason = reason;
        UserId = AppSurfaceAuthMetadata.NormalizeOptionalText(userId);
        SessionId = AppSurfaceAuthMetadata.NormalizeOptionalText(sessionId);
        Metadata = AppSurfaceAuthMetadata.Normalize(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the stable event name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the timestamp supplied by the host.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Gets the high-level auth outcome associated with the event.
    /// </summary>
    public AppSurfaceAuthOutcome Outcome { get; }

    /// <summary>
    /// Gets the concrete auth reason associated with the event.
    /// </summary>
    public AppSurfaceAuthReason Reason { get; }

    /// <summary>
    /// Gets the optional user identifier associated with the event.
    /// </summary>
    public string? UserId { get; }

    /// <summary>
    /// Gets the optional session identifier associated with the event.
    /// </summary>
    public string? SessionId { get; }

    /// <summary>
    /// Gets copied metadata that can help adapters or diagnostics preserve host-specific context.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
