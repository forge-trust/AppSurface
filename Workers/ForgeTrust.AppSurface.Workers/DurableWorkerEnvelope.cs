namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Carries a durable worker outcome, correlation identifiers, optional typed payload, and privacy-safe metadata.
/// </summary>
/// <typeparam name="TPayload">Typed payload associated with the outcome.</typeparam>
/// <remarks>
/// Envelopes are the stable boundary between app-owned worker contracts and host/runtime adapters. They describe what
/// happened; they do not execute side effects, persist data, or schedule runtime work by themselves.
/// </remarks>
public sealed record DurableWorkerEnvelope<TPayload>
{
    /// <summary>
    /// Initializes the original projection-compatible envelope shape without native runtime execution identity.
    /// </summary>
    /// <param name="outcome">Defined observable worker outcome.</param>
    /// <param name="reasonCode">Stable machine-readable safe reason code.</param>
    /// <param name="retryability">Defined retry classification.</param>
    /// <param name="correlation">Correlation identifiers for the operation.</param>
    /// <param name="payload">Optional typed payload.</param>
    /// <param name="metadata">Optional privacy-safe metadata.</param>
    /// <param name="diagnostic">Optional safe diagnostic.</param>
    /// <remarks>
    /// This exact seven-parameter overload is retained for already-compiled adapters. Native runtimes use the overload
    /// that also supplies <see cref="DurableWorkerExecutionIdentity"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when required text or metadata is invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="correlation"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="outcome"/> or <paramref name="retryability"/> is not defined.</exception>
    /// <exception cref="DurableWorkerUnsafeMetadataException">Thrown when metadata appears unsafe.</exception>
    public DurableWorkerEnvelope(
        DurableWorkerProjectionOutcome outcome,
        string reasonCode,
        DurableWorkerRetryability retryability,
        DurableWorkerCorrelation correlation,
        TPayload? payload = default,
        IReadOnlyDictionary<string, string>? metadata = null,
        DurableWorkerDiagnostic? diagnostic = null)
        : this(outcome, reasonCode, retryability, correlation, payload, metadata, diagnostic, executionIdentity: null, initialize: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableWorkerEnvelope{TPayload}"/> class.
    /// </summary>
    /// <param name="outcome">Defined observable worker outcome.</param>
    /// <param name="reasonCode">Stable machine-readable reason code that is sanitized with diagnostic-text safety rules.</param>
    /// <param name="retryability">Defined retry classification for the outcome.</param>
    /// <param name="correlation">Correlation identifiers for the operation.</param>
    /// <param name="payload">Optional typed payload associated with the outcome.</param>
    /// <param name="executionIdentity">Required native-runtime execution and fencing identity.</param>
    /// <param name="metadata">Optional safe metadata values.</param>
    /// <param name="diagnostic">Optional safe diagnostic details.</param>
    /// <exception cref="ArgumentException">Thrown when required text or metadata is invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="correlation"/> or <paramref name="executionIdentity"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="outcome"/> or <paramref name="retryability"/> is not defined.</exception>
    /// <exception cref="DurableWorkerUnsafeMetadataException">Thrown when metadata appears unsafe.</exception>
    public DurableWorkerEnvelope(
        DurableWorkerProjectionOutcome outcome,
        string reasonCode,
        DurableWorkerRetryability retryability,
        DurableWorkerCorrelation correlation,
        TPayload? payload,
        DurableWorkerExecutionIdentity executionIdentity,
        IReadOnlyDictionary<string, string>? metadata = null,
        DurableWorkerDiagnostic? diagnostic = null)
        : this(
            outcome,
            reasonCode,
            retryability,
            correlation,
            payload,
            metadata,
            diagnostic,
            executionIdentity ?? throw new ArgumentNullException(nameof(executionIdentity)),
            initialize: true)
    {
    }

    private DurableWorkerEnvelope(
        DurableWorkerProjectionOutcome outcome,
        string reasonCode,
        DurableWorkerRetryability retryability,
        DurableWorkerCorrelation correlation,
        TPayload? payload,
        IReadOnlyDictionary<string, string>? metadata,
        DurableWorkerDiagnostic? diagnostic,
        DurableWorkerExecutionIdentity? executionIdentity,
        bool initialize)
    {
        _ = initialize;
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), "Durable worker outcome must be defined.");
        }

        if (!Enum.IsDefined(retryability))
        {
            throw new ArgumentOutOfRangeException(nameof(retryability), "Durable worker retryability must be defined.");
        }

        Outcome = outcome;
        ReasonCode = DurableWorkerMetadataSafety.CopySafeDiagnosticText(reasonCode, "reason code", nameof(reasonCode));
        Retryability = retryability;
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        Payload = payload;
        Metadata = DurableWorkerMetadataSafety.CopySafe(metadata);
        Diagnostic = diagnostic;
        ExecutionIdentity = executionIdentity;
    }

    /// <summary>
    /// Gets the observable worker outcome.
    /// </summary>
    public DurableWorkerProjectionOutcome Outcome { get; }

    /// <summary>
    /// Gets the stable machine-readable reason code.
    /// </summary>
    public string ReasonCode { get; }

    /// <summary>
    /// Gets the retry classification for this outcome.
    /// </summary>
    public DurableWorkerRetryability Retryability { get; }

    /// <summary>
    /// Gets correlation identifiers for the operation.
    /// </summary>
    public DurableWorkerCorrelation Correlation { get; }

    /// <summary>
    /// Gets the optional typed payload associated with the outcome.
    /// </summary>
    public TPayload? Payload { get; }

    /// <summary>
    /// Gets sanitized metadata for logs, durable facts, and projection repair reports.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Gets optional sanitized diagnostic details.
    /// </summary>
    public DurableWorkerDiagnostic? Diagnostic { get; }

    /// <summary>
    /// Gets the native durable execution identity when the envelope was created by a fencing-aware runtime.
    /// </summary>
    /// <remarks>
    /// Legacy adapters may leave this value <see langword="null"/>. Native runtimes must populate it before invoking a
    /// provider executor; projection repair must not use it as authority to repeat an external effect.
    /// </remarks>
    public DurableWorkerExecutionIdentity? ExecutionIdentity { get; }
}
