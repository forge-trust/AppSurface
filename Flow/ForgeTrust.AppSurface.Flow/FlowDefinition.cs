using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Describes a typed AppSurface Flow graph.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried between flow nodes.</typeparam>
/// <remarks>
/// A definition is immutable after construction. Nodes are identified by stable string ids so local and durable hosts can
/// store the current node without serializing executable code. Flow ids and versions should remain stable once durable
/// instances have been started.
/// </remarks>
public sealed class FlowDefinition<TContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlowDefinition{TContext}"/> class.
    /// </summary>
    /// <param name="flowId">Stable flow identifier.</param>
    /// <param name="version">Stable graph version.</param>
    /// <param name="startNodeId">Node id where new instances start.</param>
    /// <param name="nodes">Node descriptors keyed by node id.</param>
    /// <exception cref="ArgumentException">Thrown when a required string is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="nodes"/> is <see langword="null"/>.</exception>
    public FlowDefinition(
        string flowId,
        string version,
        string startNodeId,
        IReadOnlyDictionary<string, FlowNodeDescriptor<TContext>> nodes)
    {
        FlowId = RequireText(flowId, nameof(flowId));
        Version = RequireText(version, nameof(version));
        StartNodeId = RequireText(startNodeId, nameof(startNodeId));
        var validatedNodes = ValidateAndCopyNodes(FlowId, Version, StartNodeId, nodes);
        Nodes = validatedNodes.Nodes;
        ExecutionNodes = validatedNodes.ExecutionNodes;
        StartExecutionNode = validatedNodes.StartExecutionNode;
    }

    /// <summary>
    /// Gets the stable flow identifier.
    /// </summary>
    public string FlowId { get; }

    /// <summary>
    /// Gets the graph version.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the node id where new instances start.
    /// </summary>
    public string StartNodeId { get; }

    /// <summary>
    /// Gets all node descriptors keyed by node id.
    /// </summary>
    public IReadOnlyDictionary<string, FlowNodeDescriptor<TContext>> Nodes { get; }

    /// <summary>
    /// Gets the internal execution view keyed by node id.
    /// </summary>
    /// <remarks>
    /// The execution view is built from the copied and validated node dictionary. It preserves the public
    /// <see cref="Nodes"/> graph view while giving runners pre-resolved next-node targets.
    /// </remarks>
    internal IReadOnlyDictionary<string, FlowExecutionNode<TContext>> ExecutionNodes { get; }

    /// <summary>
    /// Gets the pre-resolved start node used by in-process runners.
    /// </summary>
    internal FlowExecutionNode<TContext> StartExecutionNode { get; }

    internal static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value;
    }

    private static ValidatedFlowNodes ValidateAndCopyNodes(
        string flowId,
        string version,
        string startNodeId,
        IReadOnlyDictionary<string, FlowNodeDescriptor<TContext>> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (nodes.Count == 0)
        {
            throw new FlowDefinitionException($"Flow '{flowId}' version '{version}' does not contain any nodes.");
        }

        var copy = new Dictionary<string, FlowNodeDescriptor<TContext>>(StringComparer.Ordinal);
        foreach (var item in nodes)
        {
            var key = RequireText(item.Key, nameof(nodes));
            var descriptor = item.Value ?? throw new ArgumentException("Flow node descriptors must not be null.", nameof(nodes));

            if (!string.Equals(key, descriptor.NodeId, StringComparison.Ordinal))
            {
                throw new FlowDefinitionException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Flow '{flowId}' version '{version}' node key '{key}' does not match descriptor node id '{descriptor.NodeId}'."));
            }

            if (!copy.TryAdd(key, descriptor))
            {
                throw new FlowDefinitionException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Flow '{flowId}' version '{version}' declares node '{key}' more than once."));
            }
        }

        if (!copy.ContainsKey(startNodeId))
        {
            throw new FlowDefinitionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Flow '{flowId}' version '{version}' start node '{startNodeId}' does not exist."));
        }

        foreach (var descriptor in copy.Values)
        {
            foreach (var target in descriptor.NextNodeIds.Where(target => !copy.ContainsKey(target)))
            {
                throw new FlowDefinitionException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Flow '{flowId}' version '{version}' node '{descriptor.NodeId}' targets missing node '{target}'."));
            }
        }

        var publicNodes = new ReadOnlyDictionary<string, FlowNodeDescriptor<TContext>>(copy);
        var executionNodes = BuildExecutionNodes(copy);
        return new ValidatedFlowNodes(publicNodes, executionNodes, executionNodes[startNodeId]);
    }

    private static IReadOnlyDictionary<string, FlowExecutionNode<TContext>> BuildExecutionNodes(
        IReadOnlyDictionary<string, FlowNodeDescriptor<TContext>> nodes)
    {
        var executionNodes = new Dictionary<string, FlowExecutionNode<TContext>>(StringComparer.Ordinal);
        foreach (var item in nodes)
        {
            executionNodes.Add(item.Key, new FlowExecutionNode<TContext>(item.Value));
        }

        foreach (var executionNode in executionNodes.Values)
        {
            var nextNodes = new Dictionary<string, FlowExecutionNode<TContext>>(StringComparer.Ordinal);
            foreach (var nextNodeId in executionNode.Descriptor.NextNodeIds)
            {
                nextNodes.Add(nextNodeId, executionNodes[nextNodeId]);
            }

            executionNode.SetNextNodes(new ReadOnlyDictionary<string, FlowExecutionNode<TContext>>(nextNodes));
        }

        return new ReadOnlyDictionary<string, FlowExecutionNode<TContext>>(executionNodes);
    }

    private sealed record ValidatedFlowNodes(
        IReadOnlyDictionary<string, FlowNodeDescriptor<TContext>> Nodes,
        IReadOnlyDictionary<string, FlowExecutionNode<TContext>> ExecutionNodes,
        FlowExecutionNode<TContext> StartExecutionNode);
}
