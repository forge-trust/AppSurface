namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Identifies the terminal or pause status returned by an AppSurface Flow runner.
/// </summary>
public enum FlowRunStatus
{
    /// <summary>
    /// The flow is waiting for an external event.
    /// </summary>
    Waiting,

    /// <summary>
    /// The flow completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The flow returned a process-level fault.
    /// </summary>
    Faulted,

    /// <summary>
    /// The flow handled a timeout branch.
    /// </summary>
    TimedOut,
}

/// <summary>
/// Result returned by a flow runner after executing until the next pause or terminal outcome.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed record FlowRunResult<TContext>
{
    private FlowRunResult(
        FlowRunStatus status,
        TContext? context,
        string? nodeId,
        string? waitingEventName,
        FlowTimeout? timeout,
        string? timedOutEventName,
        FlowFault? fault)
    {
        Status = status;
        Context = context;
        NodeId = nodeId;
        WaitingEventName = waitingEventName;
        Timeout = timeout;
        TimedOutEventName = timedOutEventName;
        Fault = fault;
    }

    /// <summary>
    /// Gets the runner status.
    /// </summary>
    public FlowRunStatus Status { get; }

    /// <summary>
    /// Gets the latest context when the result carries one.
    /// </summary>
    public TContext? Context { get; }

    /// <summary>
    /// Gets the node id where execution paused or ended.
    /// </summary>
    public string? NodeId { get; }

    /// <summary>
    /// Gets the event name awaited by a waiting result.
    /// </summary>
    public string? WaitingEventName { get; }

    /// <summary>
    /// Gets the optional timeout associated with a waiting result.
    /// </summary>
    public FlowTimeout? Timeout { get; }

    /// <summary>
    /// Gets the event whose timeout branch completed.
    /// </summary>
    public string? TimedOutEventName { get; }

    /// <summary>
    /// Gets the fault details for a faulted result.
    /// </summary>
    public FlowFault? Fault { get; }

    /// <summary>
    /// Creates a waiting result.
    /// </summary>
    public static FlowRunResult<TContext> Waiting(
        string nodeId,
        string eventName,
        TContext context,
        FlowTimeout? timeout = null) =>
        new(
            FlowRunStatus.Waiting,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            FlowDefinition<object>.RequireText(eventName, nameof(eventName)),
            timeout,
            null,
            null);

    /// <summary>
    /// Creates a completed result.
    /// </summary>
    public static FlowRunResult<TContext> Completed(string nodeId, TContext context) =>
        new(
            FlowRunStatus.Completed,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            null,
            null);

    /// <summary>
    /// Creates a timed-out result.
    /// </summary>
    public static FlowRunResult<TContext> TimedOut(string nodeId, string eventName, TContext context) =>
        new(
            FlowRunStatus.TimedOut,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            FlowDefinition<object>.RequireText(eventName, nameof(eventName)),
            null);

    /// <summary>
    /// Creates a faulted result.
    /// </summary>
    public static FlowRunResult<TContext> Faulted(string nodeId, FlowFault fault) =>
        new(
            FlowRunStatus.Faulted,
            default,
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            null,
            fault ?? throw new ArgumentNullException(nameof(fault)));
}
