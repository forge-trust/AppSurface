using ForgeTrust.AppSurface.Flow.DurableTask;

namespace ForgeTrust.AppSurface.Workers.DurableTask;

/// <summary>
/// Configures the passive Durable Task worker adapter.
/// </summary>
/// <remarks>
/// Options describe retry intent for host-owned Durable Task activity scheduling. They do not register workers, create
/// storage providers, or execute retries by themselves.
/// </remarks>
public sealed class AppSurfaceWorkersDurableTaskOptions
{
    /// <summary>
    /// Gets or sets the retry policy attached to executor scheduling decisions.
    /// </summary>
    /// <remarks>
    /// Hosts translate this value into Durable Task activity retry options when a claim maps to
    /// <see cref="DurableTaskWorkerDecisionKind.ScheduleExecutor"/>.
    /// </remarks>
    public FlowRetryPolicy? ExecutorRetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the retry policy attached to projection repair scheduling decisions.
    /// </summary>
    /// <remarks>
    /// Hosts translate this value into Durable Task activity retry options when a completion maps to
    /// <see cref="DurableTaskWorkerDecisionKind.RepairProjection"/>.
    /// </remarks>
    public FlowRetryPolicy? ProjectionRetryPolicy { get; set; }
}
