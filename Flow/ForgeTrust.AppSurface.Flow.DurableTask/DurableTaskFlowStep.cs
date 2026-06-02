using ForgeTrust.AppSurface.Flow;

namespace ForgeTrust.AppSurface.Flow.DurableTask;

/// <summary>
/// Input used by the Durable Task adapter to evaluate one flow node.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
public sealed record DurableTaskFlowStep<TContext>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableTaskFlowStep{TContext}"/> class.
    /// </summary>
    /// <param name="flowId">Flow id.</param>
    /// <param name="version">Flow version.</param>
    /// <param name="instanceId">Durable instance id.</param>
    /// <param name="nodeId">Current node id.</param>
    /// <param name="context">Current flow context.</param>
    /// <param name="resumeEvent">Optional external event or timeout used to resume the node.</param>
    public DurableTaskFlowStep(
        string flowId,
        string version,
        string instanceId,
        string nodeId,
        TContext context,
        FlowResumeEvent? resumeEvent = null)
    {
        FlowId = FlowDefinition<object>.RequireText(flowId, nameof(flowId));
        Version = FlowDefinition<object>.RequireText(version, nameof(version));
        InstanceId = FlowDefinition<object>.RequireText(instanceId, nameof(instanceId));
        NodeId = FlowDefinition<object>.RequireText(nodeId, nameof(nodeId));
        Context = FlowNodeOutcome<TContext>.RequireContext(context);
        ResumeEvent = resumeEvent;
    }

    /// <summary>
    /// Gets the flow id.
    /// </summary>
    public string FlowId { get; }

    /// <summary>
    /// Gets the flow version.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the durable instance id.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// Gets the current node id.
    /// </summary>
    public string NodeId { get; }

    /// <summary>
    /// Gets the current flow context.
    /// </summary>
    public TContext Context { get; }

    /// <summary>
    /// Gets the optional external event or timeout used to resume the node.
    /// </summary>
    public FlowResumeEvent? ResumeEvent { get; }
}
