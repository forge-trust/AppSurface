using System.Globalization;
using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Evaluates AppSurface Flow nodes as Durable Task orchestration decisions.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
/// <remarks>
/// This service is the AppSurface mapping layer between durable orchestration code and Flow definitions. Hosts still own
/// Durable Task worker/client setup, instance scheduling, persisted state, timers, replay, and external event delivery.
/// </remarks>
public interface IDurableTaskFlowRunner<TContext>
{
    /// <summary>
    /// Starts a durable flow by evaluating the definition's start node.
    /// </summary>
    /// <param name="flowId">Flow id to start.</param>
    /// <param name="version">Flow version to start.</param>
    /// <param name="instanceId">Durable Task instance id associated with the flow.</param>
    /// <param name="context">Initial flow context.</param>
    /// <param name="cancellationToken">Token that cancels node execution.</param>
    /// <returns>
    /// A durable decision. Missing definitions, missing nodes, non-durable contexts, invalid next targets, and
    /// unsupported outcomes are returned as <see cref="DurableTaskFlowDecisionKind.Fault"/> decisions.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="flowId"/>, <paramref name="version"/>, or <paramref name="instanceId"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is null.</exception>
    /// <remarks>
    /// Caller code should inspect the returned <see cref="DurableTaskFlowDecision{TContext}.Kind"/> instead of relying
    /// on exceptions for process-level failures. Exceptions are reserved for invalid caller arguments and cancellation.
    /// </remarks>
    ValueTask<DurableTaskFlowDecision<TContext>> StartAsync(
        string flowId,
        string version,
        string instanceId,
        TContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates one node and maps its outcome to a durable orchestration decision.
    /// </summary>
    /// <param name="step">Current durable flow step.</param>
    /// <param name="cancellationToken">Token that cancels node execution.</param>
    /// <returns>
    /// A durable decision. Missing definitions or nodes, serialization failures, invalid next targets, node faults, and
    /// unsupported outcomes are returned as <see cref="DurableTaskFlowDecisionKind.Fault"/> decisions.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="step"/> is null.</exception>
    /// <remarks>
    /// When <see cref="AppSurfaceFlowDurableTaskOptions.ValidateContextSerialization"/> is enabled, this method
    /// validates the input context before executing the node and validates returned contexts before scheduling, waiting,
    /// timing out, or completing. Disabling that option skips the serialization round-trip check. Callers must branch on
    /// the returned decision kind.
    /// </remarks>
    ValueTask<DurableTaskFlowDecision<TContext>> RunNodeAsync(
        DurableTaskFlowStep<TContext> step,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a waiting node if the delivered event matches the event the orchestration was waiting for.
    /// </summary>
    /// <param name="step">Current durable flow step carrying a resume event.</param>
    /// <param name="expectedEventName">Event name the orchestration is waiting for.</param>
    /// <param name="cancellationToken">Token that cancels resumed node execution.</param>
    /// <returns>
    /// A durable decision. Missing resume events return a fault decision. Mismatched events return
    /// <see cref="DurableTaskFlowDecisionKind.IgnoreLateEvent"/> when
    /// <see cref="AppSurfaceFlowDurableTaskOptions.IgnoreLateResumeEvents"/> is enabled, otherwise they return a fault
    /// decision.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="expectedEventName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="step"/> is null.</exception>
    /// <remarks>
    /// The <paramref name="expectedEventName"/> comparison uses <see cref="StringComparison.Ordinal"/>, so matching is
    /// case-sensitive. Deliver only the exact event the orchestration is currently waiting for, or normalize event names
    /// before calling this method. Mismatches are represented as late-event or fault decisions, so callers should
    /// inspect the returned kind before scheduling more work.
    /// </remarks>
    ValueTask<DurableTaskFlowDecision<TContext>> ResumeAsync(
        DurableTaskFlowStep<TContext> step,
        string expectedEventName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IDurableTaskFlowRunner{TContext}"/>.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed class DurableTaskFlowRunner<TContext> : IDurableTaskFlowRunner<TContext>
{
    private readonly IFlowDefinitionRegistry _registry;
    private readonly FlowContextSerializationValidator _serializationValidator;
    private readonly IOptions<AppSurfaceFlowDurableTaskOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableTaskFlowRunner{TContext}"/> class.
    /// </summary>
    public DurableTaskFlowRunner(
        IFlowDefinitionRegistry registry,
        FlowContextSerializationValidator serializationValidator,
        IOptions<AppSurfaceFlowDurableTaskOptions> options)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _serializationValidator = serializationValidator ?? throw new ArgumentNullException(nameof(serializationValidator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public ValueTask<DurableTaskFlowDecision<TContext>> StartAsync(
        string flowId,
        string version,
        string instanceId,
        TContext context,
        CancellationToken cancellationToken = default)
    {
        FlowDefinition<object>.RequireText(instanceId, nameof(instanceId));

        if (!_registry.TryGet<TContext>(flowId, version, out var definition))
        {
            return ValueTask.FromResult(MissingDefinition(flowId, version, definitionNodeId: "start"));
        }

        return RunNodeAsync(
            new DurableTaskFlowStep<TContext>(
                definition.FlowId,
                definition.Version,
                instanceId,
                definition.StartNodeId,
                context),
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<DurableTaskFlowDecision<TContext>> RunNodeAsync(
        DurableTaskFlowStep<TContext> step,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (!_registry.TryGet<TContext>(step.FlowId, step.Version, out var definition))
        {
            return MissingDefinition(step.FlowId, step.Version, step.NodeId);
        }

        if (!definition.Nodes.TryGetValue(step.NodeId, out var descriptor))
        {
            return DurableTaskFlowDecision<TContext>.Faulted(
                step.NodeId,
                new FlowFault(
                    "flow.node-missing",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Flow '{step.FlowId}' version '{step.Version}' does not contain node '{step.NodeId}'.")));
        }

        var contextValidation = ValidateContextIfEnabled(step.Context);
        if (!contextValidation.Succeeded)
        {
            return DurableTaskFlowDecision<TContext>.Faulted(
                step.NodeId,
                new FlowFault("flow.context-not-durable", contextValidation.Message),
                contextValidation.Exception?.Message);
        }

        var executionContext = new FlowExecutionContext<TContext>(
            step.FlowId,
            step.Version,
            step.NodeId,
            step.Context,
            step.ResumeEvent);
        var outcome = await descriptor.Node.ExecuteAsync(executionContext, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome switch
        {
            FlowNext<TContext> next => MapNext(definition, descriptor, next),
            FlowWait<TContext> wait => MapWait(step.NodeId, wait),
            FlowTimedOut<TContext> timedOut => MapTimedOut(step.NodeId, timedOut),
            FlowComplete<TContext> complete => MapComplete(step.NodeId, complete),
            FlowFaultOutcome<TContext> fault => DurableTaskFlowDecision<TContext>.Faulted(step.NodeId, fault.Fault),
            _ => DurableTaskFlowDecision<TContext>.Faulted(
                step.NodeId,
                new FlowFault("flow.outcome-unsupported", $"Unsupported flow outcome type '{outcome.GetType().FullName}'.")),
        };
    }

    /// <inheritdoc />
    public ValueTask<DurableTaskFlowDecision<TContext>> ResumeAsync(
        DurableTaskFlowStep<TContext> step,
        string expectedEventName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        var normalizedExpectedEventName = FlowDefinition<object>.RequireText(expectedEventName, nameof(expectedEventName));

        if (step.ResumeEvent is null)
        {
            return ValueTask.FromResult(DurableTaskFlowDecision<TContext>.Faulted(
                step.NodeId,
                new FlowFault("flow.resume-event-missing", "ResumeAsync requires a resume event.")));
        }

        if (!string.Equals(step.ResumeEvent.EventName, normalizedExpectedEventName, StringComparison.Ordinal))
        {
            var diagnostic = string.Create(
                CultureInfo.InvariantCulture,
                $"Ignored resume event '{step.ResumeEvent.EventName}' because node '{step.NodeId}' is waiting for '{normalizedExpectedEventName}'.");

            if (_options.Value.IgnoreLateResumeEvents)
            {
                return ValueTask.FromResult(DurableTaskFlowDecision<TContext>.IgnoreLateEvent(
                    step.NodeId,
                    step.ResumeEvent.EventName,
                    diagnostic));
            }

            return ValueTask.FromResult(DurableTaskFlowDecision<TContext>.Faulted(
                step.NodeId,
                new FlowFault("flow.resume-event-late", diagnostic),
                diagnostic));
        }

        return RunNodeAsync(step, cancellationToken);
    }

    private FlowContextSerializationResult ValidateContextIfEnabled(TContext context)
    {
        if (!_options.Value.ValidateContextSerialization)
        {
            return FlowContextSerializationResult.Success();
        }

        return _serializationValidator.Validate(context);
    }

    private DurableTaskFlowDecision<TContext> MapNext(
        FlowDefinition<TContext> definition,
        FlowNodeDescriptor<TContext> descriptor,
        FlowNext<TContext> next)
    {
        if (!descriptor.NextNodeIds.Contains(next.NodeId) || !definition.Nodes.ContainsKey(next.NodeId))
        {
            return DurableTaskFlowDecision<TContext>.Faulted(
                descriptor.NodeId,
                new FlowFault(
                    "flow.next-node-invalid",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Flow '{definition.FlowId}' version '{definition.Version}' node '{descriptor.NodeId}' returned invalid target '{next.NodeId}'.")));
        }

        var contextFault = ValidateOutcomeContext(descriptor.NodeId, next.Context);
        if (contextFault is not null)
        {
            return contextFault;
        }

        return DurableTaskFlowDecision<TContext>.ScheduleNode(next.NodeId, next.Context, _options.Value.NodeRetryPolicy);
    }

    private DurableTaskFlowDecision<TContext> MapWait(string nodeId, FlowWait<TContext> wait)
    {
        var contextFault = ValidateOutcomeContext(nodeId, wait.Context);
        return contextFault
            ?? DurableTaskFlowDecision<TContext>.WaitForExternalEvent(
                nodeId,
                wait.EventName,
                wait.Context,
                wait.Timeout);
    }

    private DurableTaskFlowDecision<TContext> MapTimedOut(string nodeId, FlowTimedOut<TContext> timedOut)
    {
        var contextFault = ValidateOutcomeContext(nodeId, timedOut.Context);
        return contextFault ?? DurableTaskFlowDecision<TContext>.TimedOut(nodeId, timedOut.EventName, timedOut.Context);
    }

    private DurableTaskFlowDecision<TContext> MapComplete(string nodeId, FlowComplete<TContext> complete)
    {
        var contextFault = ValidateOutcomeContext(nodeId, complete.Context);
        return contextFault ?? DurableTaskFlowDecision<TContext>.Complete(nodeId, complete.Context);
    }

    private DurableTaskFlowDecision<TContext>? ValidateOutcomeContext(string nodeId, TContext context)
    {
        var contextValidation = ValidateContextIfEnabled(context);
        if (contextValidation.Succeeded)
        {
            return null;
        }

        return DurableTaskFlowDecision<TContext>.Faulted(
            nodeId,
            new FlowFault("flow.context-not-durable", contextValidation.Message),
            contextValidation.Exception?.Message);
    }

    private static DurableTaskFlowDecision<TContext> MissingDefinition(
        string flowId,
        string version,
        string definitionNodeId) =>
        DurableTaskFlowDecision<TContext>.Faulted(
            definitionNodeId,
            new FlowFault(
                "flow.definition-missing",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Flow '{flowId}' version '{version}' is not registered for context '{typeof(TContext).FullName}'.")));
}
