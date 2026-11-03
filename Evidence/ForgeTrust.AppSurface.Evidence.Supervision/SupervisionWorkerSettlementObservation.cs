namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Orders the original worker unit/group settlement observations without creating native authority.</summary>
/// <remarks>
/// The private worker supplies its original cleanup guard, retained-start-connection read, short-circuited
/// stopped/PID/group comparison, existing 25 ms cancellation-bound wait, and local join publication.
/// This procedure adds no clock, token, retry allowance, transport, process, or native completion receipt.
/// Its callbacks are fixed bindings in production; ordinary task controls exercise ordering only.
/// </remarks>
internal static class SupervisionWorkerSettlementObservation
{
    /// <summary>Joins each original inspection and wait; publishes only after an inspection returns true.</summary>
    /// <param name="requireCleanup">Original owner/deadline guard, sampled before each inspection.</param>
    /// <param name="inspectSelectedUnitAndGroup">Original compound native inspection, including its short-circuit order.</param>
    /// <param name="waitForNextObservation">Original cancellation-bound observation delay, used only after false.</param>
    /// <param name="recordStopped">Original local milestone and join assignment, invoked once after true.</param>
    /// <remarks>
    /// Exceptions propagate unchanged into the caller's existing cleanup catch boundary. No failed, canceled,
    /// pending, or throwing callback supplies completion. Callbacks must not recursively await this procedure.
    /// </remarks>
    internal static async Task WaitAsync(Action requireCleanup, Func<Task<bool>> inspectSelectedUnitAndGroup,
        Func<Task> waitForNextObservation, Action recordStopped)
    {
        ArgumentNullException.ThrowIfNull(requireCleanup);
        ArgumentNullException.ThrowIfNull(inspectSelectedUnitAndGroup);
        ArgumentNullException.ThrowIfNull(waitForNextObservation);
        ArgumentNullException.ThrowIfNull(recordStopped);
        while (true)
        {
            requireCleanup();
            if (await inspectSelectedUnitAndGroup().ConfigureAwait(false))
            {
                recordStopped();
                return;
            }
            await waitForNextObservation().ConfigureAwait(false);
        }
    }
}
