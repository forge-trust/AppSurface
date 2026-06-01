using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Builds and validates typed AppSurface Flow graphs.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
/// <remarks>
/// The builder validates declared transitions before returning a definition. Every target passed to
/// <see cref="AddNode(string, IFlowNode{TContext}, string[])"/> must exist in the final graph. Cycles are allowed because
/// long-running business processes often revisit a review, remediation, or wait step.
/// </remarks>
public sealed class FlowGraphBuilder<TContext>
{
    private readonly Dictionary<string, FlowNodeDescriptor<TContext>> _nodes = new(StringComparer.Ordinal);
    private readonly string _flowId;
    private readonly string _version;
    private string? _startNodeId;

    private FlowGraphBuilder(string flowId, string version)
    {
        _flowId = FlowDefinition<TContext>.RequireText(flowId, nameof(flowId));
        _version = FlowDefinition<TContext>.RequireText(version, nameof(version));
    }

    /// <summary>
    /// Creates a new builder.
    /// </summary>
    /// <param name="flowId">Stable flow id.</param>
    /// <param name="version">Flow graph version. Defaults to <c>1</c>.</param>
    /// <returns>A new builder instance.</returns>
    public static FlowGraphBuilder<TContext> Create(string flowId, string version = "1") => new(flowId, version);

    /// <summary>
    /// Adds a node and declares the node ids it may return from a <see cref="FlowNext{TContext}"/> outcome.
    /// </summary>
    /// <param name="nodeId">Stable node id.</param>
    /// <param name="node">Node implementation.</param>
    /// <param name="nextNodeIds">Allowed <see cref="FlowNext{TContext}.NodeId"/> targets.</param>
    /// <returns>The current builder.</returns>
    /// <exception cref="ArgumentException">Thrown when the id is empty or duplicated.</exception>
    public FlowGraphBuilder<TContext> AddNode(
        string nodeId,
        IFlowNode<TContext> node,
        params string[] nextNodeIds)
    {
        var normalizedNodeId = FlowDefinition<TContext>.RequireText(nodeId, nameof(nodeId));
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nextNodeIds);

        if (_nodes.ContainsKey(normalizedNodeId))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Flow node '{normalizedNodeId}' is already registered."),
                nameof(nodeId));
        }

        var normalizedNextIds = nextNodeIds
            .Select((nextNodeId, index) => FlowDefinition<TContext>.RequireText(nextNodeId, $"{nameof(nextNodeIds)}[{index}]"))
            .ToArray();

        _nodes.Add(
            normalizedNodeId,
            new FlowNodeDescriptor<TContext>(normalizedNodeId, node, new ReadOnlySet<string>(normalizedNextIds)));

        return this;
    }

    /// <summary>
    /// Sets the node id used for new flow instances.
    /// </summary>
    /// <param name="nodeId">Start node id.</param>
    /// <returns>The current builder.</returns>
    public FlowGraphBuilder<TContext> StartAt(string nodeId)
    {
        _startNodeId = FlowDefinition<TContext>.RequireText(nodeId, nameof(nodeId));
        return this;
    }

    /// <summary>
    /// Validates and creates an immutable flow definition.
    /// </summary>
    /// <returns>The validated definition.</returns>
    /// <exception cref="FlowDefinitionException">Thrown when the graph is incomplete or references missing nodes.</exception>
    public FlowDefinition<TContext> Build()
    {
        if (_nodes.Count == 0)
        {
            throw new FlowDefinitionException($"Flow '{_flowId}' version '{_version}' does not contain any nodes.");
        }

        if (_startNodeId is null)
        {
            throw new FlowDefinitionException($"Flow '{_flowId}' version '{_version}' does not define a start node.");
        }

        if (!_nodes.ContainsKey(_startNodeId))
        {
            throw new FlowDefinitionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Flow '{_flowId}' version '{_version}' start node '{_startNodeId}' does not exist."));
        }

        foreach (var descriptor in _nodes.Values)
        {
            foreach (var target in descriptor.NextNodeIds.Where(target => !_nodes.ContainsKey(target)))
            {
                throw new FlowDefinitionException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Flow '{_flowId}' version '{_version}' node '{descriptor.NodeId}' targets missing node '{target}'."));
            }
        }

        return new FlowDefinition<TContext>(_flowId, _version, _startNodeId, _nodes);
    }
}
