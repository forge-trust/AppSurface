using System.Globalization;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Runs AppSurface Flow definitions in memory for local development and tests.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
/// <remarks>
/// This runner does not persist state, create durable timers, or buffer external events. It executes synchronously through
/// <see cref="FlowNext{TContext}"/> outcomes and stops at <see cref="FlowWait{TContext}"/>,
/// <see cref="FlowActivity{TContext,TWork,TResult}"/>, <see cref="FlowComplete{TContext}"/>,
/// <see cref="FlowTimedOut{TContext}"/>, or <see cref="FlowFaultOutcome{TContext}"/>. It reports activities to the
/// caller; it never executes external work itself.
/// </remarks>
public sealed class InMemoryFlowRunner<TContext> : IFlowRunner<TContext>
{
    private readonly IOptions<AppSurfaceFlowOptions> _options;
    private readonly IFlowTransitionEvaluator<TContext> _transitionEvaluator;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryFlowRunner{TContext}"/> class.
    /// </summary>
    /// <param name="options">Runner options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    public InMemoryFlowRunner(IOptions<AppSurfaceFlowOptions> options)
        : this(options, new FlowTransitionEvaluator<TContext>())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryFlowRunner{TContext}"/> class.
    /// </summary>
    /// <param name="options">Runner options.</param>
    /// <param name="transitionEvaluator">Host-neutral one-node evaluator.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> or <paramref name="transitionEvaluator"/> is null.
    /// </exception>
    public InMemoryFlowRunner(
        IOptions<AppSurfaceFlowOptions> options,
        IFlowTransitionEvaluator<TContext> transitionEvaluator)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transitionEvaluator = transitionEvaluator ?? throw new ArgumentNullException(nameof(transitionEvaluator));
    }

    /// <inheritdoc />
    public ValueTask<FlowRunResult<TContext>> RunAsync(
        FlowDefinition<TContext> definition,
        TContext initialContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return ExecuteAsync(definition, definition.StartExecutionNode, initialContext, null, null, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<FlowRunResult<TContext>> ResumeAsync(
        FlowDefinition<TContext> definition,
        string nodeId,
        TContext context,
        FlowResumeEvent resumeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(resumeEvent);
        return ExecuteAsync(
            definition,
            ResolveExecutionNode(definition, FlowDefinition<TContext>.RequireText(nodeId, nameof(nodeId))),
            context,
            resumeEvent,
            null,
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<FlowRunResult<TContext>> ResumeActivityAsync(
        FlowDefinition<TContext> definition,
        string nodeId,
        TContext context,
        FlowActivityWorkResult activityResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(activityResult);
        return ExecuteAsync(
            definition,
            ResolveExecutionNode(definition, FlowDefinition<TContext>.RequireText(nodeId, nameof(nodeId))),
            context,
            null,
            activityResult,
            cancellationToken);
    }

    private async ValueTask<FlowRunResult<TContext>> ExecuteAsync(
        FlowDefinition<TContext> definition,
        FlowExecutionNode<TContext> executionNode,
        TContext context,
        FlowResumeEvent? resumeEvent,
        FlowActivityWorkResult? activityResult,
        CancellationToken cancellationToken)
    {
        var maxSteps = _options.Value.MaxStepsPerRun;
        if (maxSteps < 1)
        {
            throw new InvalidOperationException("AppSurfaceFlowOptions.MaxStepsPerRun must be at least 1.");
        }

        var currentExecutionNode = executionNode;
        var currentContext = FlowNodeOutcome<TContext>.RequireContext(context);
        var currentResumeEvent = resumeEvent;
        var currentActivityResult = activityResult;

        for (var step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentNodeId = currentExecutionNode.Descriptor.NodeId;
            var transition = await _transitionEvaluator.EvaluateAsync(
                definition,
                new FlowTransitionInput<TContext>(
                    currentNodeId,
                    currentContext,
                    currentResumeEvent,
                    currentActivityResult),
                cancellationToken).ConfigureAwait(false);
            currentResumeEvent = null;
            currentActivityResult = null;

            switch (transition.Kind)
            {
                case FlowTransitionKind.Next:
                    currentExecutionNode = ResolveNextExecutionNode(
                        definition,
                        currentExecutionNode,
                        transition.NextNodeId!);
                    currentContext = transition.Context!;
                    break;
                case FlowTransitionKind.Wait:
                    return transition.EventCallsite is null
                        ? FlowRunResult<TContext>.Waiting(
                            currentNodeId,
                            transition.EventName!,
                            transition.Context!,
                            transition.Timeout)
                        : FlowRunResult<TContext>.Waiting(
                            currentNodeId,
                            transition.EventCallsite,
                            transition.Context!,
                            transition.Timeout);
                case FlowTransitionKind.TimedOut:
                    return FlowRunResult<TContext>.TimedOut(
                        currentNodeId,
                        transition.EventName!,
                        transition.Context!);
                case FlowTransitionKind.Complete:
                    return FlowRunResult<TContext>.Completed(currentNodeId, transition.Context!);
                case FlowTransitionKind.Fault when transition.Fault?.Code is
                    "flow.next-node-invalid":
                    throw new FlowDefinitionException(transition.Fault.Message);
                case FlowTransitionKind.Fault when transition.Fault?.Code is "flow.outcome-unsupported":
                    throw new FlowDefinitionException(transition.Fault.Message);
                case FlowTransitionKind.Fault:
                    return FlowRunResult<TContext>.Faulted(currentNodeId, transition.Fault!);
                case FlowTransitionKind.Activity:
                    return FlowRunResult<TContext>.ActivityPending(currentNodeId, transition.Activity!);
                default:
                    throw new FlowDefinitionException($"Unsupported flow transition kind '{transition.Kind}'.");
            }
        }

        return FlowRunResult<TContext>.Faulted(
            currentExecutionNode.Descriptor.NodeId,
            new FlowFault(
                "flow.max-steps-exceeded",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Flow '{definition.FlowId}' version '{definition.Version}' exceeded {maxSteps} in-memory steps.")));
    }

    private static FlowExecutionNode<TContext> ResolveExecutionNode(
        FlowDefinition<TContext> definition,
        string nodeId)
    {
        if (!definition.ExecutionNodes.TryGetValue(nodeId, out var executionNode))
        {
            throw new FlowDefinitionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Flow '{definition.FlowId}' version '{definition.Version}' does not contain node '{nodeId}'."));
        }

        return executionNode;
    }

    private static FlowExecutionNode<TContext> ResolveNextExecutionNode(
        FlowDefinition<TContext> definition,
        FlowExecutionNode<TContext> executionNode,
        string nextNodeId)
    {
        if (!executionNode.NextNodes.TryGetValue(nextNodeId, out var nextExecutionNode))
        {
            throw new FlowDefinitionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Flow '{definition.FlowId}' version '{definition.Version}' node '{executionNode.Descriptor.NodeId}' returned undeclared target '{nextNodeId}'."));
        }

        return nextExecutionNode;
    }
}
