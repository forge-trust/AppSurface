using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Workers.DurableTask;

/// <summary>
/// Durable Task-facing decision produced by the worker adapter.
/// </summary>
/// <typeparam name="TWork">Type of work carried by the chain.</typeparam>
/// <typeparam name="TResult">Type of terminal execution result.</typeparam>
/// <typeparam name="TProjection">Type of visible projection repaired from terminal facts.</typeparam>
/// <remarks>
/// Decisions are passive instructions for host-owned orchestration code. They do not schedule activities, persist
/// state, create timers, deliver external events, or own Durable Task worker/client setup.
/// </remarks>
public sealed record DurableTaskWorkerDecision<TWork, TResult, TProjection>
{
    private DurableTaskWorkerDecision(
        DurableTaskWorkerDecisionKind kind,
        DurableWorkerCorrelation correlation,
        DurableWorkerDiagnostic? diagnostic,
        TWork? work,
        TResult? result,
        TProjection? projection,
        string? eventName,
        FlowTimeout? timeout,
        FlowRetryPolicy? retryPolicy,
        DurableWorkerProjectionOutcome? sourceOutcome)
    {
        Kind = kind;
        Correlation = correlation;
        Diagnostic = diagnostic;
        Work = work;
        Result = result;
        Projection = projection;
        EventName = eventName;
        Timeout = timeout;
        RetryPolicy = retryPolicy;
        SourceOutcome = sourceOutcome;
    }

    /// <summary>
    /// Gets the decision kind.
    /// </summary>
    public DurableTaskWorkerDecisionKind Kind { get; }

    /// <summary>
    /// Gets the correlation identifiers associated with the decision.
    /// </summary>
    public DurableWorkerCorrelation Correlation { get; }

    /// <summary>
    /// Gets optional safe diagnostic details.
    /// </summary>
    public DurableWorkerDiagnostic? Diagnostic { get; }

    /// <summary>
    /// Gets the work payload when carried by this decision.
    /// </summary>
    public TWork? Work { get; }

    /// <summary>
    /// Gets the terminal result payload when carried by this decision.
    /// </summary>
    public TResult? Result { get; }

    /// <summary>
    /// Gets the projection payload when carried by this decision.
    /// </summary>
    public TProjection? Projection { get; }

    /// <summary>
    /// Gets the external event name for wait, timeout, or late-signal decisions.
    /// </summary>
    public string? EventName { get; }

    /// <summary>
    /// Gets the optional timeout associated with an external event wait.
    /// </summary>
    public FlowTimeout? Timeout { get; }

    /// <summary>
    /// Gets retry intent that host orchestration code can translate into Durable Task retry options.
    /// </summary>
    public FlowRetryPolicy? RetryPolicy { get; }

    /// <summary>
    /// Gets the worker contract outcome that produced the decision, when one exists.
    /// </summary>
    public DurableWorkerProjectionOutcome? SourceOutcome { get; }

    /// <summary>
    /// Creates an executor scheduling decision for a claimed work item.
    /// </summary>
    /// <param name="claim">Claim envelope that authorized executor scheduling.</param>
    /// <param name="retryPolicy">Optional retry policy for executor activity.</param>
    /// <returns>A schedule-executor decision.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="claim"/> is null.</exception>
    public static DurableTaskWorkerDecision<TWork, TResult, TProjection> ScheduleExecutor(
        DurableWorkerEnvelope<TWork> claim,
        FlowRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return new(
            DurableTaskWorkerDecisionKind.ScheduleExecutor,
            claim.Correlation,
            claim.Diagnostic,
            claim.Payload,
            default,
            default,
            null,
            null,
            retryPolicy,
            claim.Outcome);
    }

    /// <summary>
    /// Creates an external-event wait decision.
    /// </summary>
    /// <param name="correlation">Correlation identifiers for the wait.</param>
    /// <param name="eventName">External event name expected by host orchestration code.</param>
    /// <param name="timeout">Optional durable timeout for the wait.</param>
    /// <returns>A wait-for-external-event decision.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="eventName"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="correlation"/> is null.</exception>
    public static DurableTaskWorkerDecision<TWork, TResult, TProjection> WaitForExternalEvent(
        DurableWorkerCorrelation correlation,
        string eventName,
        FlowTimeout? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        return new(
            DurableTaskWorkerDecisionKind.WaitForExternalEvent,
            correlation,
            null,
            default,
            default,
            default,
            RequireText(eventName, nameof(eventName)),
            timeout,
            null,
            null);
    }

    /// <summary>
    /// Creates a projection repair scheduling decision from a completion envelope.
    /// </summary>
    /// <param name="completion">Completion envelope that recorded a terminal fact.</param>
    /// <param name="retryPolicy">Optional retry policy for projection repair activity.</param>
    /// <returns>A repair-projection decision.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="completion"/> is null.</exception>
    public static DurableTaskWorkerDecision<TWork, TResult, TProjection> RepairProjection(
        DurableWorkerEnvelope<TResult> completion,
        FlowRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(completion);

        return new(
            DurableTaskWorkerDecisionKind.RepairProjection,
            completion.Correlation,
            completion.Diagnostic,
            default,
            completion.Payload,
            default,
            null,
            null,
            retryPolicy,
            completion.Outcome);
    }

    /// <summary>
    /// Creates a complete decision from a worker envelope.
    /// </summary>
    /// <typeparam name="TPayload">Envelope payload type.</typeparam>
    /// <param name="envelope">Envelope that completed the worker chain.</param>
    /// <returns>A complete decision.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="envelope"/> is null.</exception>
    public static DurableTaskWorkerDecision<TWork, TResult, TProjection> Complete<TPayload>(
        DurableWorkerEnvelope<TPayload> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return new(
            DurableTaskWorkerDecisionKind.Complete,
            envelope.Correlation,
            envelope.Diagnostic,
            default,
            default,
            envelope.Payload is TProjection projection ? projection : default,
            null,
            null,
            null,
            envelope.Outcome);
    }

    /// <summary>
    /// Creates a fault decision from a worker envelope.
    /// </summary>
    /// <typeparam name="TPayload">Envelope payload type.</typeparam>
    /// <param name="envelope">Envelope that faulted the worker chain.</param>
    /// <returns>A fault decision.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="envelope"/> is null.</exception>
    public static DurableTaskWorkerDecision<TWork, TResult, TProjection> Fault<TPayload>(
        DurableWorkerEnvelope<TPayload> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return new(
            DurableTaskWorkerDecisionKind.Fault,
            envelope.Correlation,
            envelope.Diagnostic,
            default,
            default,
            default,
            null,
            null,
            null,
            envelope.Outcome);
    }

    /// <summary>
    /// Creates a late-signal decision from a worker envelope.
    /// </summary>
    /// <typeparam name="TPayload">Envelope payload type.</typeparam>
    /// <param name="envelope">Envelope that identified a stale signal or fence.</param>
    /// <param name="eventName">Optional event name associated with the stale signal.</param>
    /// <returns>An ignore-late-signal decision.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="envelope"/> is null.</exception>
    public static DurableTaskWorkerDecision<TWork, TResult, TProjection> IgnoreLateSignal<TPayload>(
        DurableWorkerEnvelope<TPayload> envelope,
        string? eventName = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return new(
            DurableTaskWorkerDecisionKind.IgnoreLateSignal,
            envelope.Correlation,
            envelope.Diagnostic,
            default,
            default,
            default,
            eventName is null ? null : RequireText(eventName, nameof(eventName)),
            null,
            null,
            envelope.Outcome);
    }

    /// <summary>
    /// Creates a retry wait decision from a worker envelope.
    /// </summary>
    /// <typeparam name="TPayload">Envelope payload type.</typeparam>
    /// <param name="envelope">Retryable envelope that should wait before retry.</param>
    /// <param name="retryPolicy">Optional retry policy that explains host retry intent.</param>
    /// <returns>A wait-for-retry decision.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="envelope"/> is null.</exception>
    public static DurableTaskWorkerDecision<TWork, TResult, TProjection> WaitForRetry<TPayload>(
        DurableWorkerEnvelope<TPayload> envelope,
        FlowRetryPolicy? retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return new(
            DurableTaskWorkerDecisionKind.WaitForRetry,
            envelope.Correlation,
            envelope.Diagnostic,
            default,
            default,
            default,
            null,
            null,
            retryPolicy,
            envelope.Outcome);
    }

    /// <summary>
    /// Creates a timeout decision for a wait branch that has expired.
    /// </summary>
    /// <param name="correlation">Correlation identifiers for the timed-out wait.</param>
    /// <param name="eventName">External event name whose wait timed out.</param>
    /// <param name="diagnostic">Optional safe diagnostic details.</param>
    /// <returns>A timed-out decision.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="eventName"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="correlation"/> is null.</exception>
    public static DurableTaskWorkerDecision<TWork, TResult, TProjection> TimedOut(
        DurableWorkerCorrelation correlation,
        string eventName,
        DurableWorkerDiagnostic? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        return new(
            DurableTaskWorkerDecisionKind.TimedOut,
            correlation,
            diagnostic,
            default,
            default,
            default,
            RequireText(eventName, nameof(eventName)),
            null,
            null,
            null);
    }

    private static string RequireText(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Durable Task worker decision text must not be null, empty, or whitespace.", paramName);
        }

        return value.Trim();
    }
}
