namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Describes one AppSurface Flow node execution.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
/// <param name="FlowId">Stable flow id.</param>
/// <param name="Version">Flow graph version.</param>
/// <param name="NodeId">Current node id.</param>
/// <param name="State">Current typed state.</param>
/// <param name="ResumeEvent">Optional external event or timeout that resumed the node.</param>
public sealed record FlowExecutionContext<TContext>(
    string FlowId,
    string Version,
    string NodeId,
    TContext State,
    FlowResumeEvent? ResumeEvent = null);
