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
/// <see cref="FlowComplete{TContext}"/>, <see cref="FlowTimedOut{TContext}"/>, or <see cref="FlowFaultOutcome{TContext}"/>.
/// </remarks>
public sealed class InMemoryFlowRunner<TContext> : IFlowRunner<TContext>
{
    private readonly IOptions<AppSurfaceFlowOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryFlowRunner{TContext}"/> class.
    /// </summary>
    /// <param name="options">Runner options.</param>
    public InMemoryFlowRunner(IOptions<AppSurfaceFlowOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public ValueTask<FlowRunResult<TContext>> RunAsync(
        FlowDefinition<TContext> definition,
        TContext initialContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return ExecuteAsync(definition, definition.StartExecutionNode, initialContext, null, cancellationToken);
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
            cancellationToken);
    }

    private async ValueTask<FlowRunResult<TContext>> ExecuteAsync(
        FlowDefinition<TContext> definition,
        FlowExecutionNode<TContext> executionNode,
        TContext context,
        FlowResumeEvent? resumeEvent,
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

        for (var step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptor = currentExecutionNode.Descriptor;
            var currentNodeId = descriptor.NodeId;
            var executionContext = new FlowExecutionContext<TContext>(
                definition.FlowId,
                definition.Version,
                currentNodeId,
                currentContext,
                currentResumeEvent);
            var outcome = await descriptor.Node.ExecuteAsync(executionContext, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(outcome);
            currentResumeEvent = null;

            switch (outcome)
            {
                case FlowNext<TContext> next:
                    currentExecutionNode = ResolveNextExecutionNode(definition, currentExecutionNode, next.NodeId);
                    currentContext = next.Context;
                    break;
                case FlowWait<TContext> wait:
                    return FlowRunResult<TContext>.Waiting(currentNodeId, wait.EventName, wait.Context, wait.Timeout);
                case FlowTimedOut<TContext> timedOut:
                    return FlowRunResult<TContext>.TimedOut(currentNodeId, timedOut.EventName, timedOut.Context);
                case FlowComplete<TContext> complete:
                    return FlowRunResult<TContext>.Completed(currentNodeId, complete.Context);
                case FlowFaultOutcome<TContext> fault:
                    return FlowRunResult<TContext>.Faulted(currentNodeId, fault.Fault);
                default:
                    throw new FlowDefinitionException($"Unsupported flow outcome type '{outcome.GetType().FullName}'.");
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
