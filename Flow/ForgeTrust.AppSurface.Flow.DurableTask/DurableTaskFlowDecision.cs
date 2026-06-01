using ForgeTrust.AppSurface.Flow;

namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Identifies the durable orchestration decision produced from a flow node outcome.
/// </summary>
public enum DurableTaskFlowDecisionKind
{
    /// <summary>
    /// Schedule the next node.
    /// </summary>
    ScheduleNode,

    /// <summary>
    /// Wait for an external event, optionally with a durable timer.
    /// </summary>
    WaitForExternalEvent,

    /// <summary>
    /// Complete the durable flow instance.
    /// </summary>
    Complete,

    /// <summary>
    /// Fault the durable flow instance.
    /// </summary>
    Fault,

    /// <summary>
    /// Record that a timeout branch was handled.
    /// </summary>
    TimedOut,

    /// <summary>
    /// Ignore a stale or mismatched external event.
    /// </summary>
    IgnoreLateEvent,
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
