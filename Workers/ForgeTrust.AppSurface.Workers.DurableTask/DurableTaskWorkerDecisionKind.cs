namespace ForgeTrust.AppSurface.Workers.DurableTask;

/// <summary>
/// Identifies the Durable Task-facing decision produced by the AppSurface worker adapter.
/// </summary>
/// <remarks>
/// Numeric values are part of the adapter compatibility contract and may appear in durable history, tests, or
/// telemetry. Do not reorder, remove, renumber, or reuse values.
/// </remarks>
public enum DurableTaskWorkerDecisionKind
{
    /// <summary>
    /// Schedule the side-effecting executor activity.
    /// </summary>
    ScheduleExecutor = 0,

    /// <summary>
    /// Wait for a host-owned external event.
    /// </summary>
    WaitForExternalEvent = 1,

    /// <summary>
    /// Schedule projection repair from a durable terminal fact.
    /// </summary>
    RepairProjection = 2,

    /// <summary>
    /// Complete the durable worker chain.
    /// </summary>
    Complete = 3,

    /// <summary>
    /// Fault the durable worker chain.
    /// </summary>
    Fault = 4,

    /// <summary>
    /// Ignore a stale, late, or mismatched signal.
    /// </summary>
    IgnoreLateSignal = 5,

    /// <summary>
    /// Wait for retry according to host-owned Durable Task timer behavior.
    /// </summary>
    WaitForRetry = 6,

    /// <summary>
    /// Record that a timeout branch has won.
    /// </summary>
    TimedOut = 7,
}
