namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Executes one typed step in an AppSurface Flow graph.
/// </summary>
/// <typeparam name="TContext">Serializable context type carried by the flow.</typeparam>
/// <remarks>
/// Nodes should be deterministic, idempotent where practical, and explicit about process outcomes. Durable hosts may
/// replay orchestration decisions around node execution, so avoid hidden global side effects in nodes and keep external
/// I/O behind durable activity boundaries.
/// </remarks>
public interface IFlowNode<TContext>
{
    /// <summary>
    /// Executes the node.
    /// </summary>
    /// <param name="context">Execution context for the current node.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the runner.</param>
    /// <returns>A discriminated outcome that tells the runner how to continue.</returns>
    ValueTask<FlowNodeOutcome<TContext>> ExecuteAsync(
        FlowExecutionContext<TContext> context,
        CancellationToken cancellationToken = default);
}
