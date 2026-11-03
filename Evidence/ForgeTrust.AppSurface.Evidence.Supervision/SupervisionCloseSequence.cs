namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Attempts every borrowed local-owner close in order without issuing native settlement or custody.</summary>
/// <remarks>
/// The native caller supplies its original local owners after process, group and pump joins. This helper
/// cannot stop a process, create a handle, add a deadline or establish that those earlier joins happened.
/// Null entries denote owners never acquired. Each failure immediately sets the supplied failure latch;
/// the caller still performs its remaining native checks before publication.
/// </remarks>
internal static class SupervisionCloseSequence
{
    /// <summary>Closes all present owners, continuing after each failure, as required by producer cleanup.</summary>
    /// <param name="owners">Original local owners in the caller's existing cleanup order; never replaced or retried.</param>
    /// <param name="failed">The caller’s sticky failure latch; failures set it atomically to one and never clear it.</param>
    /// <remarks>
    /// Like the original producer cleanup blocks, this attempts later closes even when a close throws a
    /// fatal-class exception. It retains no exception text or reference. Enumeration errors propagate;
    /// the production caller supplies a fixed local array. It performs no asynchronous wait or retry.
    /// </remarks>
    internal static void CloseAll(IEnumerable<IDisposable?> owners, ref int failed)
    {
        ArgumentNullException.ThrowIfNull(owners);
        foreach (var owner in owners)
        {
            try { owner?.Dispose(); }
            catch { Interlocked.Exchange(ref failed, 1); }
        }
    }
}
