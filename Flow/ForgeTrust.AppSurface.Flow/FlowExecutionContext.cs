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
/// <remarks>
/// The context is an immutable value-type snapshot so in-process runners can pass it to synchronous nodes without
/// allocating a new reference object for every step. Runners always populate all members before invoking a node. Avoid
/// using <c>default</c> instances as real execution contexts because value types can be default-created without the
/// required flow, version, node, or state values.
/// </remarks>
public readonly record struct FlowExecutionContext<TContext>(
    string FlowId,
    string Version,
    string NodeId,
    TContext State,
    FlowResumeEvent? ResumeEvent = null);
