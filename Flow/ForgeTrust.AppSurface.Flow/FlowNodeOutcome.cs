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

    /// <summary>
    /// Gets the external event name.
    /// </summary>
    public string EventName { get; }

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
    public new FlowFault Fault { get; }
}
