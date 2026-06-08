namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Marks a partial type as the generated authoring specification for an AppSurface Flow.
/// </summary>
/// <remarks>
/// Use generated authoring when a flow should be declared as typed transformer nodes with compile-time
/// validation of outcome coverage. Use <see cref="FlowGraphBuilder{TContext}"/> directly when a host needs
/// dynamic graph construction, low-level runtime control, or compatibility with hand-authored nodes.
///
/// The annotated type must be a non-generic partial class. Record and nested generated flow specifications are
/// not supported. Generated authoring is additive: the generator lowers the annotated specification into the
/// existing <see cref="FlowDefinition{TContext}"/> runtime contract without changing low-level flow APIs. Avoid
/// mixing generated definition helpers with manual low-level node registration for the same flow because that
/// makes mapping ownership unclear.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FlowAuthoringAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowAuthoringAttribute"/> class.
    /// </summary>
    /// <param name="flowId">Stable flow identifier.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="flowId"/> is null, empty, or whitespace.</exception>
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
/// <remarks>
/// Apply this attribute to a partial class nested inside a <see cref="FlowAuthoringAttribute"/> flow type. The
/// node class must implement <see cref="IFlowTransformerNode{TInput,TOutcome}"/> where <c>TInput</c> matches
/// <see cref="InputContextType"/> and <c>TOutcome</c> is the generated outcome union for that node.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FlowNodeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowNodeAttribute"/> class.
    /// </summary>
    /// <param name="nodeId">Stable node identifier.</param>
    /// <param name="inputContextType">Context type consumed by the node.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="nodeId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inputContextType"/> is null.</exception>
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
/// <remarks>
/// Exactly one node per generated flow must be marked with <see cref="FlowStartAttribute"/>.
/// If a flow declares zero or multiple start nodes, the generator reports diagnostic <c>ASFLOWA004</c>
/// and does not produce the generated definition helpers for that invalid flow.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FlowStartAttribute : Attribute
{
}

/// <summary>
/// Declares one generated outcome case a transformer node can return.
/// </summary>
/// <remarks>
/// Outcome contexts are nominal ports used by the generated graph validator. <see cref="FlowOutcomeKind.Next"/>
/// must carry the input context type of exactly one target node. <see cref="FlowOutcomeKind.Wait"/> and
/// <see cref="FlowOutcomeKind.TimedOut"/> resume the current node, so they must carry that node's input context
/// type. <see cref="FlowOutcomeKind.Fault"/> must carry <see cref="FlowFault"/>. <see cref="FlowOutcomeKind.Complete"/>
/// is terminal and does not require a target node.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class FlowOutcomeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowOutcomeAttribute"/> class.
    /// </summary>
    /// <param name="name">Stable outcome name.</param>
    /// <param name="kind">Outcome kind to lower into the runtime contract.</param>
    /// <param name="outputContextType">Context type carried by the outcome.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="outputContextType"/> is null.</exception>
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
/// Do not apply this attribute manually. The generator emits it on generated <c>GraphBuilder</c> mapping methods
/// so analyzers can validate explicit <c>BuildDefinition</c> lambda coverage. Manually applying it can make graph
/// coverage diagnostics misleading.
///
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
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="nodeId"/> or <paramref name="outcomeName"/> is null, empty, or whitespace.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="outputContextType"/> is null.</exception>
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
/// <remarks>
/// The numeric values are part of the public compatibility contract. Do not renumber or reorder existing values.
/// </remarks>
public enum FlowOutcomeKind
{
    /// <summary>
    /// Move to another generated-authoring node.
    /// </summary>
    Next = 0,

    /// <summary>
    /// Wait at the current node for an external event.
    /// </summary>
    Wait = 1,

    /// <summary>
    /// Report that timeout handling completed.
    /// </summary>
    TimedOut = 2,

    /// <summary>
    /// Complete the flow successfully.
    /// </summary>
    Complete = 3,

    /// <summary>
    /// Fault the flow with a process-level error.
    /// </summary>
    Fault = 4,
}

/// <summary>
/// Executes one generated-authoring transformer node.
/// </summary>
/// <remarks>
/// Implementations receive a <see cref="FlowTransformerContext{TInput}"/> and must return a non-null generated
/// outcome case. The generated adapter lowers that case into the v1 runtime contract and the runner enforces
/// the non-null outcome requirement. Exceptions thrown by an implementation propagate to the caller; use
/// <see cref="FlowOutcomeKind.Fault"/> with <see cref="FlowFault"/> for expected process failures that should be
/// represented as flow results. Nodes should honor cancellation and should keep externally visible side effects
/// idempotent or guarded by a host-owned outbox when a durable host may retry or replay work.
/// </remarks>
/// <typeparam name="TInput">Typed input context consumed by the node.</typeparam>
/// <typeparam name="TOutcome">Generated outcome union type returned by the node.</typeparam>
public interface IFlowTransformerNode<TInput, TOutcome>
{
    /// <summary>
    /// Executes the node and returns a generated outcome case.
    /// </summary>
    /// <param name="context">Typed authoring execution context.</param>
    /// <param name="cancellationToken">
    /// Cancellation token supplied by the runner. The default value is provided for manual invocation convenience.
    /// </param>
    /// <returns>A generated outcome case.</returns>
    ValueTask<TOutcome> ExecuteAsync(
        FlowTransformerContext<TInput> context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes one generated-authoring transformer execution.
/// </summary>
/// <remarks>
/// <see cref="FlowId"/>, <see cref="Version"/>, and <see cref="NodeId"/> identify the current definition and node
/// being executed. <see cref="State"/> is the typed input context unwrapped from the generated envelope; the record
/// does not validate it, and generated adapters assume it is non-null. <see cref="ResumeEvent"/> is null for initial
/// execution and non-null when a node resumes after an external event or timeout.
/// </remarks>
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
