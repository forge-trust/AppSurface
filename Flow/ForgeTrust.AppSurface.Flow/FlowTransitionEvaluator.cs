using System.Globalization;

namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Identifies the host-neutral decision produced by evaluating exactly one Flow node.
/// </summary>
/// <remarks>
/// Numeric values are a compatibility contract for durable hosts and telemetry. Do not reorder, renumber, remove, or
/// reuse values. Append new transition kinds with explicit numeric values.
/// </remarks>
public enum FlowTransitionKind
{
    /// <summary>
    /// Continue at another declared node.
    /// </summary>
    Next = 0,

    /// <summary>
    /// Wait for a named external event, optionally with a timeout.
    /// </summary>
    Wait = 1,

    /// <summary>
    /// Record that a timeout branch was handled.
    /// </summary>
    TimedOut = 2,

    /// <summary>
    /// Complete the Flow successfully.
    /// </summary>
    Complete = 3,

    /// <summary>
    /// Fault the Flow with a process-level error.
    /// </summary>
    Fault = 4,

    /// <summary>
    /// Schedule one typed external activity and wait for its result.
    /// </summary>
    Activity = 5,
}

/// <summary>
/// Input for one host-neutral Flow node evaluation.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the Flow.</typeparam>
public sealed record FlowTransitionInput<TContext>
{
    /// <summary>
    /// Initializes one node evaluation input.
    /// </summary>
    /// <param name="nodeId">Current stable node id.</param>
    /// <param name="context">Current Flow context.</param>
    /// <param name="resumeEvent">Optional external event or timeout that resumed the node.</param>
    /// <param name="activityResult">Optional typed activity result that resumed the node.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> is empty or both resume inputs are supplied.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    public FlowTransitionInput(
        string nodeId,
        TContext context,
        FlowResumeEvent? resumeEvent = null,
        FlowActivityWorkResult? activityResult = null)
    {
        if (resumeEvent is not null && activityResult is not null)
        {
            throw new ArgumentException(
                "A Flow node evaluation cannot carry both an external resume event and an activity result.",
                nameof(activityResult));
        }

        NodeId = FlowDefinition<object>.RequireText(nodeId, nameof(nodeId));
        Context = FlowNodeOutcome<TContext>.RequireContext(context);
        ResumeEvent = resumeEvent;
        ActivityResult = activityResult;
    }

    /// <summary>
    /// Gets the current node id.
    /// </summary>
    public string NodeId { get; }

    /// <summary>
    /// Gets the current Flow context.
    /// </summary>
    public TContext Context { get; }

    /// <summary>
    /// Gets the optional external event or timeout that resumed the node.
    /// </summary>
    public FlowResumeEvent? ResumeEvent { get; }

    /// <summary>
    /// Gets the optional typed activity result that resumed the node.
    /// </summary>
    public FlowActivityWorkResult? ActivityResult { get; }
}

/// <summary>
/// Host-neutral decision produced by evaluating exactly one Flow node.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the Flow.</typeparam>
/// <remarks>
/// Inspect <see cref="Kind"/> before reading kind-specific properties. A durable host commits this decision before
/// evaluating another node. It must atomically persist an <see cref="FlowTransitionKind.Activity"/> decision, its
/// activity command, and the waiting context.
/// </remarks>
public sealed record FlowTransition<TContext>
{
    private FlowTransition(
        FlowTransitionKind kind,
        string nodeId,
        TContext? context,
        string? nextNodeId,
        string? eventName,
        IFlowEventCallsite? eventCallsite,
        FlowTimeout? timeout,
        FlowFault? fault,
        IFlowActivityRequest<TContext>? activity)
    {
        Kind = kind;
        NodeId = nodeId;
        Context = context;
        NextNodeId = nextNodeId;
        EventName = eventName;
        EventCallsite = eventCallsite;
        Timeout = timeout;
        Fault = fault;
        Activity = activity;
    }

    /// <summary>
    /// Gets the transition kind.
    /// </summary>
    public FlowTransitionKind Kind { get; }

    /// <summary>
    /// Gets the node that produced the transition.
    /// </summary>
    public string NodeId { get; }

    /// <summary>
    /// Gets the context carried by non-fault transitions.
    /// </summary>
    public TContext? Context { get; }

    /// <summary>
    /// Gets the declared target for a <see cref="FlowTransitionKind.Next"/> transition.
    /// </summary>
    public string? NextNodeId { get; }

    /// <summary>
    /// Gets the event name carried by wait and timeout transitions.
    /// </summary>
    public string? EventName { get; }

    /// <summary>Gets the exact typed event contract for a wait, or null for an explicit no-payload wait.</summary>
    public IFlowEventCallsite? EventCallsite { get; }

    /// <summary>
    /// Gets the optional timeout carried by a wait transition.
    /// </summary>
    public FlowTimeout? Timeout { get; }

    /// <summary>
    /// Gets the process-level fault carried by a fault transition.
    /// </summary>
    public FlowFault? Fault { get; }

    /// <summary>
    /// Gets the typed request metadata carried by an activity transition.
    /// </summary>
    public IFlowActivityRequest<TContext>? Activity { get; }

    internal static FlowTransition<TContext> Next(string nodeId, string nextNodeId, TContext context) =>
        new(FlowTransitionKind.Next, nodeId, context, nextNodeId, null, null, null, null, null);

    internal static FlowTransition<TContext> Waiting(
        string nodeId,
        string eventName,
        IFlowEventCallsite? eventCallsite,
        TContext context,
        FlowTimeout? timeout) =>
        new(FlowTransitionKind.Wait, nodeId, context, null, eventName, eventCallsite, timeout, null, null);

    internal static FlowTransition<TContext> TimedOut(string nodeId, string eventName, TContext context) =>
        new(FlowTransitionKind.TimedOut, nodeId, context, null, eventName, null, null, null, null);

    internal static FlowTransition<TContext> Completed(string nodeId, TContext context) =>
        new(FlowTransitionKind.Complete, nodeId, context, null, null, null, null, null, null);

    internal static FlowTransition<TContext> Faulted(string nodeId, FlowFault fault) =>
        new(FlowTransitionKind.Fault, nodeId, default, null, null, null, null, fault, null);

    internal static FlowTransition<TContext> ActivityRequested(
        string nodeId,
        IFlowActivityRequest<TContext> activity) =>
        new(FlowTransitionKind.Activity, nodeId, activity.Context, null, null, null, null, null, activity);
}

/// <summary>
/// Evaluates exactly one Flow node and maps its outcome to a host-neutral transition.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the Flow.</typeparam>
/// <remarks>
/// This is the shared semantic boundary for in-memory, Durable Task, and PostgreSQL hosts. It does not loop through
/// <see cref="FlowNext{TContext}"/> transitions, persist state, validate serialization, schedule timers, or execute
/// activities. Nodes can be evaluated again when a process dies before the host commits the returned transition, so
/// they must be side-effect-free and must receive nondeterministic inputs through explicit context or resume contracts.
/// </remarks>
public interface IFlowTransitionEvaluator<TContext>
{
    /// <summary>
    /// Gets the stable, host-independent evaluator identity used by durable execution manifests.
    /// </summary>
    string EvaluatorId { get; }

    /// <summary>
    /// Gets the evaluator semantics version used by durable execution manifests.
    /// </summary>
    string EvaluatorVersion { get; }

    /// <summary>
    /// Evaluates one declared node.
    /// </summary>
    /// <param name="definition">Immutable Flow definition containing the node.</param>
    /// <param name="input">Node id, context, and optional resume input.</param>
    /// <param name="cancellationToken">Token that cancels node execution.</param>
    /// <returns>
    /// One mapped transition. Missing nodes, undeclared next targets, and unsupported outcomes become fault
    /// transitions; caller argument errors, cancellation, and node exceptions propagate.
    /// </returns>
    ValueTask<FlowTransition<TContext>> EvaluateAsync(
        FlowDefinition<TContext> definition,
        FlowTransitionInput<TContext> input,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default host-neutral implementation of <see cref="IFlowTransitionEvaluator{TContext}"/>.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the Flow.</typeparam>
public sealed class FlowTransitionEvaluator<TContext> : IFlowTransitionEvaluator<TContext>
{
    /// <summary>Gets the stable identity of the built-in one-node evaluator.</summary>
    public const string StableEvaluatorId = "appsurface.flow-transition-evaluator";

    /// <summary>Gets the current semantics version of the built-in one-node evaluator.</summary>
    public const string CurrentEvaluatorVersion = "v1";

    /// <inheritdoc />
    public string EvaluatorId => StableEvaluatorId;

    /// <inheritdoc />
    public string EvaluatorVersion => CurrentEvaluatorVersion;

    /// <inheritdoc />
    public async ValueTask<FlowTransition<TContext>> EvaluateAsync(
        FlowDefinition<TContext> definition,
        FlowTransitionInput<TContext> input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (!definition.ExecutionNodes.TryGetValue(input.NodeId, out var executionNode))
        {
            return FlowTransition<TContext>.Faulted(
                input.NodeId,
                new FlowFault(
                    "flow.node-missing",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Flow '{definition.FlowId}' version '{definition.Version}' does not contain node '{input.NodeId}'.")));
        }

        var executionContext = new FlowExecutionContext<TContext>(
            definition.FlowId,
            definition.Version,
            input.NodeId,
            input.Context,
            input.ResumeEvent)
        {
            ActivityResult = input.ActivityResult,
        };
        var outcome = await executionNode.Descriptor.Node.ExecuteAsync(executionContext, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome switch
        {
            FlowNext<TContext> next => MapNext(definition, executionNode, next),
            FlowWait<TContext> wait => FlowTransition<TContext>.Waiting(
                input.NodeId,
                wait.EventName,
                wait.EventCallsite,
                wait.Context,
                wait.Timeout),
            FlowTimedOut<TContext> timedOut => FlowTransition<TContext>.TimedOut(
                input.NodeId,
                timedOut.EventName,
                timedOut.Context),
            FlowComplete<TContext> complete => FlowTransition<TContext>.Completed(input.NodeId, complete.Context),
            FlowFaultOutcome<TContext> fault => FlowTransition<TContext>.Faulted(input.NodeId, fault.Fault),
            IFlowActivityRequest<TContext> activity => FlowTransition<TContext>.ActivityRequested(input.NodeId, activity),
            _ => FlowTransition<TContext>.Faulted(
                input.NodeId,
                new FlowFault(
                    "flow.outcome-unsupported",
                    $"Unsupported flow outcome type '{outcome.GetType().FullName}'.")),
        };
    }

    private static FlowTransition<TContext> MapNext(
        FlowDefinition<TContext> definition,
        FlowExecutionNode<TContext> executionNode,
        FlowNext<TContext> next)
    {
        if (executionNode.NextNodes.ContainsKey(next.NodeId))
        {
            return FlowTransition<TContext>.Next(executionNode.Descriptor.NodeId, next.NodeId, next.Context);
        }

        return FlowTransition<TContext>.Faulted(
            executionNode.Descriptor.NodeId,
            new FlowFault(
                "flow.next-node-invalid",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Flow '{definition.FlowId}' version '{definition.Version}' node '{executionNode.Descriptor.NodeId}' returned invalid target '{next.NodeId}'; it is an undeclared target.")));
    }
}
