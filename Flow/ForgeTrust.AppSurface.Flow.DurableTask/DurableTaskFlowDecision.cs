using ForgeTrust.AppSurface.Flow;

namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Identifies the durable orchestration decision produced from a flow node outcome.
/// </summary>
/// <remarks>
/// The numeric values are part of the Durable Task adapter compatibility contract and may appear in durable
/// persistence, telemetry, or wire payloads. Do not reorder, renumber, remove, or reuse values. Add new decisions only
/// at the end with explicit numeric values and migration/versioning considerations.
/// </remarks>
public enum DurableTaskFlowDecisionKind
{
    /// <summary>
    /// Schedule the next node.
    /// </summary>
    ScheduleNode = 0,

    /// <summary>
    /// Wait for an external event, optionally with a durable timer.
    /// </summary>
    WaitForExternalEvent = 1,

    /// <summary>
    /// Complete the durable flow instance.
    /// </summary>
    Complete = 2,

    /// <summary>
    /// Fault the durable flow instance.
    /// </summary>
    Fault = 3,

    /// <summary>
    /// Record that a timeout branch was handled.
    /// </summary>
    TimedOut = 4,

    /// <summary>
    /// Ignore a stale or mismatched external event.
    /// </summary>
    IgnoreLateEvent = 5,
}

/// <summary>
/// Durable Task adapter decision created from one flow node evaluation.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed record DurableTaskFlowDecision<TContext>
{
    private DurableTaskFlowDecision(
        DurableTaskFlowDecisionKind kind,
        TContext? context,
        string? nodeId,
        string? eventName,
        FlowTimeout? timeout,
        FlowRetryPolicy? retryPolicy,
        FlowFault? fault,
        string? diagnostic)
    {
        Kind = kind;
        Context = context;
        NodeId = nodeId;
        EventName = eventName;
        Timeout = timeout;
        RetryPolicy = retryPolicy;
        Fault = fault;
        Diagnostic = diagnostic;
    }

    /// <summary>
    /// Gets the decision kind.
    /// </summary>
    public DurableTaskFlowDecisionKind Kind { get; }

    /// <summary>
    /// Gets the context carried by the decision, when present.
    /// </summary>
    public TContext? Context { get; }

    /// <summary>
    /// Gets the node id associated with the decision.
    /// </summary>
    public string? NodeId { get; }

    /// <summary>
    /// Gets the external event associated with the decision.
    /// </summary>
    public string? EventName { get; }

    /// <summary>
    /// Gets the optional timeout for a wait decision.
    /// </summary>
    public FlowTimeout? Timeout { get; }

    /// <summary>
    /// Gets the retry policy requested for scheduled node work, when present.
    /// </summary>
    public FlowRetryPolicy? RetryPolicy { get; }

    /// <summary>
    /// Gets fault details for a fault decision.
    /// </summary>
    public FlowFault? Fault { get; }

    /// <summary>
    /// Gets a human-readable diagnostic message.
    /// </summary>
    public string? Diagnostic { get; }

    /// <summary>
    /// Creates a schedule-node decision.
    /// </summary>
    /// <param name="nodeId">Node id to schedule next.</param>
    /// <param name="context">Context to pass to the scheduled node.</param>
    /// <param name="retryPolicy">Optional retry policy requested for the scheduled node work.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> is null, empty, or white space.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    /// <remarks>
    /// Schedule decisions carry the next node id, the context to persist, and optional retry metadata. They do not carry
    /// event, timeout, fault, or diagnostic details. Inspect <see cref="Kind"/> before reading kind-specific properties.
    /// </remarks>
    public static DurableTaskFlowDecision<TContext> ScheduleNode(
        string nodeId,
        TContext context,
        FlowRetryPolicy? retryPolicy = null) =>
        new(
            DurableTaskFlowDecisionKind.ScheduleNode,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            retryPolicy,
            null,
            null);

    /// <summary>
    /// Creates a wait-for-external-event decision.
    /// </summary>
    /// <param name="nodeId">Node id where the durable flow is waiting.</param>
    /// <param name="eventName">External event name the orchestration should wait for.</param>
    /// <param name="context">Context to persist while waiting.</param>
    /// <param name="timeout">Optional timeout associated with the external event wait.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> or <paramref name="eventName"/> is null, empty, or white space.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    /// <remarks>
    /// Wait decisions pause durable execution until the named event arrives or the optional timeout expires. The timeout
    /// may be null for waits without a durable timer. Inspect <see cref="Kind"/> before reading wait-specific
    /// properties.
    /// </remarks>
    public static DurableTaskFlowDecision<TContext> WaitForExternalEvent(
        string nodeId,
        string eventName,
        TContext context,
        FlowTimeout? timeout) =>
        new(
            DurableTaskFlowDecisionKind.WaitForExternalEvent,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            FlowDefinition<object>.RequireText(eventName, nameof(eventName)),
            timeout,
            null,
            null,
            null);

    /// <summary>
    /// Creates a complete decision.
    /// </summary>
    /// <param name="nodeId">Node id that completed the durable flow.</param>
    /// <param name="context">Final flow context.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> is null, empty, or white space.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    /// <remarks>
    /// Complete decisions are terminal and do not carry wait, timeout, retry, fault, or diagnostic details. Inspect
    /// <see cref="Kind"/> before reading kind-specific properties.
    /// </remarks>
    public static DurableTaskFlowDecision<TContext> Complete(string nodeId, TContext context) =>
        new(
            DurableTaskFlowDecisionKind.Complete,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            null,
            null,
            null);

    /// <summary>
    /// Creates a timeout decision.
    /// </summary>
    /// <param name="nodeId">Node id that handled the timeout branch.</param>
    /// <param name="eventName">Event whose wait timed out.</param>
    /// <param name="context">Context after timeout handling.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> or <paramref name="eventName"/> is null, empty, or white space.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    /// <remarks>
    /// Timed-out decisions mean timeout handling has already run; they are distinct from wait decisions that carry a
    /// future timeout. Inspect <see cref="Kind"/> before reading timeout-specific properties.
    /// </remarks>
    public static DurableTaskFlowDecision<TContext> TimedOut(string nodeId, string eventName, TContext context) =>
        new(
            DurableTaskFlowDecisionKind.TimedOut,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            FlowDefinition<object>.RequireText(eventName, nameof(eventName)),
            null,
            null,
            null,
            null);

    /// <summary>
    /// Creates a fault decision.
    /// </summary>
    /// <param name="nodeId">Node id associated with the fault.</param>
    /// <param name="fault">Flow fault details.</param>
    /// <param name="diagnostic">Optional human-readable diagnostic detail.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> is null, empty, or white space.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="fault"/> is null.</exception>
    /// <remarks>
    /// Fault decisions do not carry a context. Use <paramref name="fault"/> for stable machine-readable failure details
    /// and <paramref name="diagnostic"/> only for explanatory text. Inspect <see cref="Kind"/> before reading
    /// fault-specific properties.
    /// </remarks>
    public static DurableTaskFlowDecision<TContext> Faulted(string nodeId, FlowFault fault, string? diagnostic = null) =>
        new(
            DurableTaskFlowDecisionKind.Fault,
            default,
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            null,
            fault ?? throw new ArgumentNullException(nameof(fault)),
            diagnostic);

    /// <summary>
    /// Creates an ignored-late-event decision.
    /// </summary>
    /// <param name="nodeId">Node id that received the late or mismatched event.</param>
    /// <param name="eventName">Late or mismatched event name.</param>
    /// <param name="diagnostic">Human-readable reason the event was ignored.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/>, <paramref name="eventName"/>, or <paramref name="diagnostic"/> is null,
    /// empty, or white space.
    /// </exception>
    /// <remarks>
    /// Ignored-late-event decisions are non-scheduling decisions used when stale external events should not fault the
    /// durable instance. They do not carry context, timeout, retry, or fault details. Inspect <see cref="Kind"/> before
    /// reading late-event-specific properties.
    /// </remarks>
    public static DurableTaskFlowDecision<TContext> IgnoreLateEvent(
        string nodeId,
        string eventName,
        string diagnostic) =>
        new(
            DurableTaskFlowDecisionKind.IgnoreLateEvent,
            default,
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            FlowDefinition<object>.RequireText(eventName, nameof(eventName)),
            null,
            null,
            null,
            FlowDefinition<object>.RequireText(diagnostic, nameof(diagnostic)));
}
