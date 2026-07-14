namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Represents the discriminated outcome returned by a flow node.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
/// <remarks>
/// The sealed derived records form a stable discriminated-union style API that works on current C# compilers. Future
/// native union syntax can be layered on as authoring sugar without changing the runtime contract.
/// </remarks>
public abstract record FlowNodeOutcome<TContext>
{
    private protected FlowNodeOutcome()
    {
    }

    /// <summary>
    /// Creates an outcome that moves execution to another declared node.
    /// </summary>
    /// <param name="nodeId">Target node id.</param>
    /// <param name="context">Context to pass to the target node.</param>
    /// <returns>A next-node outcome.</returns>
    public static FlowNext<TContext> Next(string nodeId, TContext context) => new(nodeId, context);

    /// <summary>
    /// Creates an outcome that waits for an external event before executing this node again.
    /// </summary>
    /// <param name="eventName">External event name.</param>
    /// <param name="context">Context to persist while waiting.</param>
    /// <param name="timeout">Optional wait timeout.</param>
    /// <returns>A wait outcome.</returns>
    public static FlowWait<TContext> Wait(string eventName, TContext context, FlowTimeout? timeout = null) =>
        new(eventName, context, timeout);

    /// <summary>
    /// Creates an outcome that waits for an external event carrying one exact typed payload contract.
    /// </summary>
    /// <typeparam name="TPayload">Expected allowlisted event payload type.</typeparam>
    /// <param name="callsite">Stable event name and durable payload contract.</param>
    /// <param name="context">Context to persist while waiting.</param>
    /// <param name="timeout">Optional wait timeout.</param>
    /// <returns>A typed external-event wait outcome.</returns>
    public static FlowWait<TContext> Wait<TPayload>(
        FlowEventCallsite<TPayload> callsite,
        TContext context,
        FlowTimeout? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(callsite);
        return new(callsite, context, timeout);
    }

    /// <summary>
    /// Creates an outcome indicating the node handled a timeout branch.
    /// </summary>
    /// <param name="eventName">Event whose wait expired.</param>
    /// <param name="context">Context after timeout handling.</param>
    /// <returns>A timed-out outcome.</returns>
    public static FlowTimedOut<TContext> TimedOut(string eventName, TContext context) => new(eventName, context);

    /// <summary>
    /// Creates a successful completion outcome.
    /// </summary>
    /// <param name="context">Final context.</param>
    /// <returns>A completion outcome.</returns>
    public static FlowComplete<TContext> Complete(TContext context) => new(context);

    /// <summary>
    /// Creates a fault outcome.
    /// </summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="message">Human-readable diagnostic message.</param>
    /// <returns>A fault outcome.</returns>
    public static FlowFaultOutcome<TContext> Fault(string code, string message) => new(new FlowFault(code, message));

    /// <summary>
    /// Creates an outcome that schedules one typed external activity and pauses the Flow node for its result.
    /// </summary>
    /// <typeparam name="TWork">Serializable work contract sent to the activity executor.</typeparam>
    /// <typeparam name="TResult">Serializable result contract returned to the same node.</typeparam>
    /// <param name="callsite">Stable typed activity callsite.</param>
    /// <param name="work">Work value to persist and execute.</param>
    /// <param name="context">Context to persist atomically with the activity command.</param>
    /// <returns>An activity outcome.</returns>
    /// <remarks>
    /// Returning this outcome does not execute <paramref name="work"/>. A durable host records the transition and
    /// activity command atomically, executes the work through its provider-safe worker boundary, and resumes this same
    /// node with <see cref="FlowActivityWorkResult{TResult}"/>. Nodes must not perform the external effect themselves.
    /// </remarks>
    public static FlowActivity<TContext, TWork, TResult> Activity<TWork, TResult>(
        FlowActivityCallsite<TWork, TResult> callsite,
        TWork work,
        TContext context) =>
        new(callsite, work, context);

    internal static TContext RequireContext(TContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context;
    }
}

/// <summary>
/// Moves execution to another node in the same flow definition.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed record FlowNext<TContext> : FlowNodeOutcome<TContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowNext{TContext}"/> class.
    /// </summary>
    public FlowNext(string nodeId, TContext context)
    {
        NodeId = FlowDefinition<object>.RequireText(nodeId, nameof(nodeId));
        Context = RequireContext(context);
    }

    /// <summary>
    /// Gets the target node id.
    /// </summary>
    public string NodeId { get; }

    /// <summary>
    /// Gets the context to pass to the target node.
    /// </summary>
    public TContext Context { get; }
}

/// <summary>
/// Pauses execution until a named external event or timeout is delivered by the host.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed record FlowWait<TContext> : FlowNodeOutcome<TContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowWait{TContext}"/> class.
    /// </summary>
    public FlowWait(string eventName, TContext context, FlowTimeout? timeout = null)
    {
        EventName = FlowDefinition<object>.RequireText(eventName, nameof(eventName));
        Context = RequireContext(context);
        Timeout = timeout;
    }

    /// <summary>Initializes a typed external-event wait.</summary>
    /// <param name="eventCallsite">Exact event and payload contract.</param>
    /// <param name="context">Context to persist while waiting.</param>
    /// <param name="timeout">Optional wait timeout.</param>
    public FlowWait(IFlowEventCallsite eventCallsite, TContext context, FlowTimeout? timeout = null)
    {
        EventCallsite = eventCallsite ?? throw new ArgumentNullException(nameof(eventCallsite));
        EventName = eventCallsite.EventName;
        Context = RequireContext(context);
        Timeout = timeout;
    }

    /// <summary>
    /// Gets the external event name.
    /// </summary>
    public string EventName { get; }

    /// <summary>
    /// Gets the exact typed event contract, or <see langword="null"/> when this wait explicitly accepts no payload.
    /// </summary>
    public IFlowEventCallsite? EventCallsite { get; }

    /// <summary>
    /// Gets the context to persist while waiting.
    /// </summary>
    public TContext Context { get; }

    /// <summary>
    /// Gets the optional wait timeout.
    /// </summary>
    public FlowTimeout? Timeout { get; }
}

/// <summary>
/// Reports that a timeout branch has been handled by a node.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed record FlowTimedOut<TContext> : FlowNodeOutcome<TContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowTimedOut{TContext}"/> class.
    /// </summary>
    public FlowTimedOut(string eventName, TContext context)
    {
        EventName = FlowDefinition<object>.RequireText(eventName, nameof(eventName));
        Context = RequireContext(context);
    }

    /// <summary>
    /// Gets the event whose wait expired.
    /// </summary>
    public string EventName { get; }

    /// <summary>
    /// Gets the context after timeout handling.
    /// </summary>
    public TContext Context { get; }
}

/// <summary>
/// Completes a flow instance successfully.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed record FlowComplete<TContext> : FlowNodeOutcome<TContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowComplete{TContext}"/> class.
    /// </summary>
    public FlowComplete(TContext context)
    {
        Context = RequireContext(context);
    }

    /// <summary>
    /// Gets the final context.
    /// </summary>
    public TContext Context { get; }
}

/// <summary>
/// Fails a flow instance with a process-level fault.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed record FlowFaultOutcome<TContext> : FlowNodeOutcome<TContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowFaultOutcome{TContext}"/> class.
    /// </summary>
    public FlowFaultOutcome(FlowFault fault)
    {
        Fault = fault ?? throw new ArgumentNullException(nameof(fault));
    }

    /// <summary>
    /// Gets the fault details.
    /// </summary>
    /// <remarks>
    /// This member intentionally hides the static fault factory on <see cref="FlowNodeOutcome{TContext}"/>.
    /// </remarks>
    public new FlowFault Fault { get; }
}

/// <summary>
/// Schedules one typed external activity and pauses the current Flow node until its result is available.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the Flow.</typeparam>
/// <typeparam name="TWork">Serializable work contract sent to the activity executor.</typeparam>
/// <typeparam name="TResult">Serializable result contract returned to the same node.</typeparam>
/// <remarks>
/// The generic properties are convenient for node tests. Durable hosts should consume
/// <see cref="IFlowActivityRequest{TContext}"/> to read declared CLR types, versions, work, and context without
/// reflection.
/// </remarks>
public sealed record FlowActivity<TContext, TWork, TResult> :
    FlowNodeOutcome<TContext>,
    IFlowActivityRequest<TContext>
{
    /// <summary>
    /// Initializes an activity outcome.
    /// </summary>
    /// <param name="callsite">Stable typed activity callsite.</param>
    /// <param name="work">Work value to persist and execute.</param>
    /// <param name="context">Context to persist while waiting for the result.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="callsite"/>, <paramref name="work"/>, or <paramref name="context"/> is null.
    /// </exception>
    public FlowActivity(
        FlowActivityCallsite<TWork, TResult> callsite,
        TWork work,
        TContext context)
    {
        Callsite = callsite ?? throw new ArgumentNullException(nameof(callsite));
        ArgumentNullException.ThrowIfNull(work);
        Work = work;
        Context = RequireContext(context);
    }

    /// <summary>
    /// Gets the stable typed callsite.
    /// </summary>
    public FlowActivityCallsite<TWork, TResult> Callsite { get; }

    /// <summary>
    /// Gets the typed work value.
    /// </summary>
    public TWork Work { get; }

    /// <summary>
    /// Gets the Flow context to persist atomically with the activity command.
    /// </summary>
    public TContext Context { get; }

    /// <inheritdoc />
    public string CallsiteId => Callsite.CallsiteId;

    /// <inheritdoc />
    public Type WorkType => typeof(TWork);

    /// <inheritdoc />
    public int WorkContractVersion => Callsite.WorkContractVersion;

    /// <inheritdoc />
    public Type ResultType => typeof(TResult);

    /// <inheritdoc />
    public int ResultContractVersion => Callsite.ResultContractVersion;

    object IFlowActivityRequest<TContext>.Work => Work!;

    FlowActivityWorkResult IFlowActivityRequest<TContext>.CreateResult(object result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result is not TResult typed)
        {
            throw new ArgumentException(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Activity callsite '{CallsiteId}' expected decoded result type '{typeof(TResult).FullName}', but received '{result.GetType().FullName}'."),
                nameof(result));
        }

        return Callsite.CreateResult(typed);
    }
}
