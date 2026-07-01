namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Identifies the observable result of durable worker claim, completion, or projection repair work.
/// </summary>
/// <remarks>
/// The numeric values are part of the public worker contract. Do not reorder, remove, renumber, or reuse values.
/// Additive values must be appended with explicit numbers and documented migration behavior.
/// </remarks>
public enum DurableWorkerProjectionOutcome
{
    /// <summary>
    /// The worker claim was accepted and executor activity may be scheduled.
    /// </summary>
    Claimed = 0,

    /// <summary>
    /// A terminal execution fact was recorded and projection repair may run.
    /// </summary>
    Completed = 1,

    /// <summary>
    /// The same work had already completed, so no executor activity should be scheduled.
    /// </summary>
    AlreadyCompleted = 2,

    /// <summary>
    /// A visible projection was reconciled from a durable terminal fact.
    /// </summary>
    Reconciled = 3,

    /// <summary>
    /// The request was valid but did not require any runtime or projection change.
    /// </summary>
    Noop = 4,

    /// <summary>
    /// A stale fence, signal, attempt, or generation was ignored.
    /// </summary>
    StaleFence = 5,

    /// <summary>
    /// The work conflicted with durable state and needs retry or operator handling.
    /// </summary>
    Conflict = 6,

    /// <summary>
    /// The work cannot continue without external intervention.
    /// </summary>
    Unrecoverable = 7,
}
