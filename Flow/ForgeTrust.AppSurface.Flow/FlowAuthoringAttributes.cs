namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Marks a partial type as the generated authoring specification for an AppSurface Flow.
/// </summary>
/// <remarks>
/// Generated authoring is additive. The generator lowers the annotated specification into the existing
/// <see cref="FlowDefinition{TContext}"/> runtime contract without changing low-level flow APIs.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FlowAuthoringAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowAuthoringAttribute"/> class.
    /// </summary>
    /// <param name="flowId">Stable flow identifier.</param>
    public FlowAuthoringAttribute(string flowId)
    {
        FlowId = FlowDefinition<object>.RequireText(flowId, nameof(flowId));
    }

    /// <summary>
    /// Gets the stable flow identifier used by generated definitions.
    /// </summary>
    public string FlowId { get; }

    /// <summary>
    /// Gets or sets the stable graph version. Defaults to <c>1</c>.
    /// </summary>
    public string Version { get; set; } = "1";
}

/// <summary>
/// Marks a partial nested type as a generated-authoring flow node.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FlowNodeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowNodeAttribute"/> class.
    /// </summary>
    /// <param name="nodeId">Stable node identifier.</param>
    /// <param name="inputContextType">Context type consumed by the node.</param>
    public FlowNodeAttribute(string nodeId, Type inputContextType)
    {
        NodeId = FlowDefinition<object>.RequireText(nodeId, nameof(nodeId));
        InputContextType = inputContextType ?? throw new ArgumentNullException(nameof(inputContextType));
    }

    /// <summary>
    /// Gets the stable node identifier.
    /// </summary>
    public string NodeId { get; }

    /// <summary>
    /// Gets the context type consumed by the node.
    /// </summary>
    public Type InputContextType { get; }
}

/// <summary>
/// Marks one generated-authoring node as the flow start node.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FlowStartAttribute : Attribute
{
}

/// <summary>
/// Declares one generated outcome case a transformer node can return.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class FlowOutcomeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowOutcomeAttribute"/> class.
    /// </summary>
    /// <param name="name">Stable outcome name.</param>
    /// <param name="kind">Outcome kind to lower into the runtime contract.</param>
    /// <param name="outputContextType">Context type carried by the outcome.</param>
    public FlowOutcomeAttribute(string name, FlowOutcomeKind kind, Type outputContextType)
    {
        Name = FlowDefinition<object>.RequireText(name, nameof(name));
        Kind = kind;
        OutputContextType = outputContextType ?? throw new ArgumentNullException(nameof(outputContextType));
    }

    /// <summary>
    /// Gets the stable outcome name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the outcome kind.
    /// </summary>
    public FlowOutcomeKind Kind { get; }

    /// <summary>
    /// Gets the context type carried by this outcome.
    /// </summary>
    public Type OutputContextType { get; }
}

/// <summary>
/// Describes one generated graph mapping method for analyzer validation.
/// </summary>
/// <remarks>
/// This attribute is emitted by generated authoring code. Application code should use
/// <see cref="FlowOutcomeAttribute"/> to declare outcomes instead of applying this attribute manually.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class FlowGraphMappingAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowGraphMappingAttribute"/> class.
    /// </summary>
    /// <param name="nodeId">Stable source node identifier.</param>
    /// <param name="outcomeName">Stable outcome name.</param>
    /// <param name="outputContextType">Context type carried by the mapped outcome.</param>
    public FlowGraphMappingAttribute(string nodeId, string outcomeName, Type outputContextType)
    {
        NodeId = FlowDefinition<object>.RequireText(nodeId, nameof(nodeId));
        OutcomeName = FlowDefinition<object>.RequireText(outcomeName, nameof(outcomeName));
        OutputContextType = outputContextType ?? throw new ArgumentNullException(nameof(outputContextType));
    }

    /// <summary>
    /// Gets the stable source node identifier.
    /// </summary>
    public string NodeId { get; }

    /// <summary>
    /// Gets the stable outcome name.
    /// </summary>
    public string OutcomeName { get; }

    /// <summary>
    /// Gets the context type carried by this outcome.
    /// </summary>
    public Type OutputContextType { get; }
}

/// <summary>
/// Runtime outcome kinds supported by generated AppSurface Flow authoring.
/// </summary>
public enum FlowOutcomeKind
{
    /// <summary>
    /// Move to another generated-authoring node.
    /// </summary>
    Next,

    /// <summary>
    /// Wait at the current node for an external event.
    /// </summary>
    Wait,

    /// <summary>
    /// Report that timeout handling completed.
    /// </summary>
    TimedOut,

    /// <summary>
    /// Complete the flow successfully.
    /// </summary>
    Complete,

    /// <summary>
    /// Fault the flow with a process-level error.
    /// </summary>
    Fault,
}

/// <summary>
/// Executes one generated-authoring transformer node.
/// </summary>
/// <typeparam name="TInput">Typed input context consumed by the node.</typeparam>
/// <typeparam name="TOutcome">Generated outcome union type returned by the node.</typeparam>
public interface IFlowTransformerNode<TInput, TOutcome>
{
    /// <summary>
    /// Executes the node and returns a generated outcome case.
    /// </summary>
    /// <param name="context">Typed authoring execution context.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the runner.</param>
    /// <returns>A generated outcome case.</returns>
    ValueTask<TOutcome> ExecuteAsync(
        FlowTransformerContext<TInput> context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes one generated-authoring transformer execution.
/// </summary>
/// <typeparam name="TInput">Typed input context consumed by the node.</typeparam>
/// <param name="FlowId">Stable flow id.</param>
/// <param name="Version">Flow graph version.</param>
/// <param name="NodeId">Current node id.</param>
/// <param name="State">Typed input state.</param>
/// <param name="ResumeEvent">Optional external event or timeout that resumed the node.</param>
public sealed record FlowTransformerContext<TInput>(
    string FlowId,
    string Version,
    string NodeId,
    TInput State,
    FlowResumeEvent? ResumeEvent = null);
