using System.Collections.ObjectModel;

namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Internal, prevalidated node view used by runners to avoid repeated graph-routing lookups.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
/// <remarks>
/// Instances are created only by <see cref="FlowDefinition{TContext}"/> after the public node dictionary has been
/// copied and validated. The public API remains <see cref="FlowDefinition{TContext}.Nodes"/>; this type exists only to
/// keep execution routing internal and fail-closed.
/// </remarks>
internal sealed class FlowExecutionNode<TContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowExecutionNode{TContext}"/> class.
    /// </summary>
    /// <param name="descriptor">Public node descriptor represented by this execution node.</param>
    internal FlowExecutionNode(FlowNodeDescriptor<TContext> descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        NextNodes = ReadOnlyDictionary<string, FlowExecutionNode<TContext>>.Empty;
    }

    /// <summary>
    /// Gets the public descriptor executed for this node.
    /// </summary>
    internal FlowNodeDescriptor<TContext> Descriptor { get; }

    /// <summary>
    /// Gets the prevalidated next-node targets keyed by allowed target id.
    /// </summary>
    internal IReadOnlyDictionary<string, FlowExecutionNode<TContext>> NextNodes { get; private set; }

    /// <summary>
    /// Assigns the prevalidated next-node map once all execution nodes have been created.
    /// </summary>
    /// <param name="nextNodes">Resolved next-node targets.</param>
    internal void SetNextNodes(IReadOnlyDictionary<string, FlowExecutionNode<TContext>> nextNodes)
    {
        NextNodes = nextNodes ?? throw new ArgumentNullException(nameof(nextNodes));
    }
}
