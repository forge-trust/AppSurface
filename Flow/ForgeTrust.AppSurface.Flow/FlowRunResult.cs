namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Identifies the terminal or pause status returned by an AppSurface Flow runner.
/// </summary>
/// <remarks>
/// The numeric values are part of the public compatibility contract for logs, persisted state, and tests. Do not
/// reorder, renumber, remove, or reuse values. Add new statuses only at the end with explicit numeric values and a
/// migration/versioning plan.
/// </remarks>
public enum FlowRunStatus
{
    /// <summary>
    /// The flow is waiting for an external event.
    /// </summary>
    Waiting = 0,

    /// <summary>
    /// The flow completed successfully.
    /// </summary>
    Completed = 1,

    /// <summary>
    /// The flow returned a process-level fault.
    /// </summary>
    Faulted = 2,

    /// <summary>
    /// The flow handled a timeout branch.
    /// </summary>
    TimedOut = 3,

    /// <summary>
    /// The Flow is waiting for one typed external activity result.
    /// </summary>
    ActivityPending = 4,
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
        IFlowEventCallsite? eventCallsite,
        FlowTimeout? timeout,
        string? timedOutEventName,
        FlowFault? fault,
        IFlowActivityRequest<TContext>? activity)
    {
        Status = status;
        Context = context;
        NodeId = nodeId;
        WaitingEventName = waitingEventName;
        EventCallsite = eventCallsite;
        Timeout = timeout;
        TimedOutEventName = timedOutEventName;
        Fault = fault;
        Activity = activity;
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
    /// Gets the immutable typed event contract for a waiting result, or <see langword="null"/> for a string wait.
    /// </summary>
    /// <remarks>
    /// The runner preserves this metadata so a host can select an allowlisted payload codec. The result does not
    /// authorize, decode, or deduplicate event delivery.
    /// </remarks>
    public IFlowEventCallsite? EventCallsite { get; }

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
    /// Gets the typed activity request when <see cref="Status"/> is <see cref="FlowRunStatus.ActivityPending"/>.
    /// </summary>
    public IFlowActivityRequest<TContext>? Activity { get; }

    /// <summary>
    /// Creates a waiting result.
    /// </summary>
    /// <param name="nodeId">Node id where the flow paused.</param>
    /// <param name="eventName">External event name the node is waiting for.</param>
    /// <param name="context">Context to preserve while the flow is waiting.</param>
    /// <param name="timeout">Optional timeout associated with the wait.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> or <paramref name="eventName"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    /// <remarks>
    /// Inspect <see cref="Status"/> before reading wait-specific properties. <paramref name="timeout"/> is optional and
    /// remains null for waits without a timeout.
    /// </remarks>
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
            null,
            timeout,
            null,
            null,
            null);

    /// <summary>
    /// Creates a waiting result with an exact typed payload contract.
    /// </summary>
    /// <param name="nodeId">Node id where the Flow paused.</param>
    /// <param name="eventCallsite">Exact event name and durable payload contract.</param>
    /// <param name="context">Context to preserve while the Flow is waiting.</param>
    /// <param name="timeout">Optional timeout associated with the wait.</param>
    /// <returns>A waiting result that preserves immutable event metadata.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> or callsite text metadata is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="eventCallsite"/>, its payload type, or <paramref name="context"/> is null.
    /// </exception>
    public static FlowRunResult<TContext> Waiting(
        string nodeId,
        IFlowEventCallsite eventCallsite,
        TContext context,
        FlowTimeout? timeout = null)
    {
        var snapshot = FlowEventCallsiteContract.Snapshot(eventCallsite, nameof(eventCallsite));
        return new(
            FlowRunStatus.Waiting,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            snapshot.EventName,
            snapshot,
            timeout,
            null,
            null,
            null);
    }

    /// <summary>
    /// Creates a completed result.
    /// </summary>
    /// <param name="nodeId">Node id that completed the flow.</param>
    /// <param name="context">Final flow context.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="nodeId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    /// <remarks>
    /// A completed result does not carry wait, timeout, or fault details. Inspect <see cref="Status"/> before reading
    /// status-specific properties.
    /// </remarks>
    public static FlowRunResult<TContext> Completed(string nodeId, TContext context) =>
        new(
            FlowRunStatus.Completed,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            null,
            null,
            null,
            null);

    /// <summary>
    /// Creates a timed-out result.
    /// </summary>
    /// <param name="nodeId">Node id that handled the timeout branch.</param>
    /// <param name="eventName">Event whose wait timed out.</param>
    /// <param name="context">Context after timeout handling.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> or <paramref name="eventName"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    /// <remarks>
    /// A timed-out result means timeout handling ran; it is distinct from a waiting result that carries a future
    /// timeout. Inspect <see cref="Status"/> before reading timeout-specific properties.
    /// </remarks>
    public static FlowRunResult<TContext> TimedOut(string nodeId, string eventName, TContext context) =>
        new(
            FlowRunStatus.TimedOut,
            FlowNodeOutcome<TContext>.RequireContext(context),
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            null,
            FlowDefinition<object>.RequireText(eventName, nameof(eventName)),
            null,
            null);

    /// <summary>
    /// Creates a faulted result.
    /// </summary>
    /// <param name="nodeId">Node id that produced the fault.</param>
    /// <param name="fault">Flow fault details.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="nodeId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="fault"/> is null.</exception>
    /// <remarks>
    /// Faulted results do not carry a context. Inspect <see cref="Status"/> before reading fault-specific properties.
    /// </remarks>
    public static FlowRunResult<TContext> Faulted(string nodeId, FlowFault fault) =>
        new(
            FlowRunStatus.Faulted,
            default,
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            null,
            null,
            fault ?? throw new ArgumentNullException(nameof(fault)),
            null);

    /// <summary>
    /// Creates a result that pauses local execution for one typed external activity.
    /// </summary>
    /// <param name="nodeId">Node that requested the activity.</param>
    /// <param name="activity">Typed activity request and context.</param>
    /// <returns>An activity-pending result.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> or activity metadata is empty, a contract version is invalid, or the work
    /// value does not implement its declared type.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="activity"/> or one of its required values is null.
    /// </exception>
    /// <remarks>
    /// The in-memory runner does not execute activities. Execute the work through a test or host boundary, construct a
    /// result with the request's typed callsite, and call <see cref="IFlowRunner{TContext}.ResumeActivityAsync"/>.
    /// </remarks>
    public static FlowRunResult<TContext> ActivityPending(
        string nodeId,
        IFlowActivityRequest<TContext> activity)
    {
        var snapshot = FlowActivityRequestContract.Snapshot(activity, nameof(activity));
        return new(
            FlowRunStatus.ActivityPending,
            snapshot.Context,
            FlowDefinition<object>.RequireText(nodeId, nameof(nodeId)),
            null,
            null,
            null,
            null,
            null,
            snapshot);
    }
}
