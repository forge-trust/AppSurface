namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Describes one node in a flow definition.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed record FlowNodeDescriptor<TContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowNodeDescriptor{TContext}"/> class.
    /// </summary>
    /// <param name="nodeId">Stable node id.</param>
    /// <param name="node">Executable node implementation.</param>
    /// <param name="nextNodeIds">Allowed next-node targets.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="nodeId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="node"/> or <paramref name="nextNodeIds"/> is null.</exception>
    public FlowNodeDescriptor(string nodeId, IFlowNode<TContext> node, IReadOnlySet<string> nextNodeIds)
    {
        NodeId = FlowDefinition<object>.RequireText(nodeId, nameof(nodeId));
        Node = node ?? throw new ArgumentNullException(nameof(node));
        NextNodeIds = new ReadOnlySet<string>(nextNodeIds ?? throw new ArgumentNullException(nameof(nextNodeIds)));
    }

    /// <summary>
    /// Gets the stable node id.
    /// </summary>
    public string NodeId { get; }

    /// <summary>
    /// Gets the executable node implementation.
    /// </summary>
    public IFlowNode<TContext> Node { get; }

    /// <summary>
    /// Gets the allowed next-node targets.
    /// </summary>
    public IReadOnlySet<string> NextNodeIds { get; }
}
