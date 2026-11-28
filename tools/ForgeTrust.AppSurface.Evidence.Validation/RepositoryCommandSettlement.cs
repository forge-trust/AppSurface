using ForgeTrust.AppSurface.Evidence.Aspire;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Tracks unconfirmed child cleanup for one managed local validation run.</summary>
/// <remarks>A later command cannot clear a failure to confirm an earlier child's cleanup.</remarks>
internal sealed class RepositoryCommandSettlement
{
    private int _unconfirmedCleanup;

    /// <summary>Gets whether any observed command reported unconfirmed cleanup.</summary>
    internal bool HasUnconfirmedCleanup => Volatile.Read(ref _unconfirmedCleanup) != 0;

    /// <summary>Observes the runner's failure classification before a producer turns it into a diagnostic.</summary>
    /// <param name="run">The bounded command runner to observe.</param>
    /// <returns>A runner that preserves the original result or exception.</returns>
    internal Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> Track(
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> run) =>
        async (request, cancellationToken) =>
        {
            try
            {
                return await run(request, cancellationToken).ConfigureAwait(false);
            }
            catch (EvidenceCommandException exception)
            {
                Observe(exception);
                throw;
            }
        };

    /// <summary>Observes typed command failures from a source check before its caller handles them.</summary>
    /// <param name="check">The source check to observe.</param>
    /// <returns>A source check that preserves its result, cancellation, or exception.</returns>
    internal Func<CancellationToken, Task> TrackSourceCheck(Func<CancellationToken, Task> check) =>
        async cancellationToken =>
        {
            try
            {
                await check(cancellationToken).ConfigureAwait(false);
            }
            catch (EvidenceCommandException exception)
            {
                Observe(exception);
                throw;
            }
        };

    /// <summary>Records unconfirmed cleanup from a command failure propagated to the run boundary.</summary>
    /// <param name="failure">An exception from the current invocation.</param>
    internal void Observe(Exception failure)
    {
        if (IsUnconfirmedCleanup(failure))
        {
            Interlocked.Exchange(ref _unconfirmedCleanup, 1);
        }
    }

    /// <summary>Identifies command failures whose child-process cleanup was not confirmed.</summary>
    /// <param name="failure">The command failure to classify.</param>
    /// <returns><see langword="true"/> when cleanup may still be active.</returns>
    internal static bool IsUnconfirmedCleanup(Exception failure) =>
        failure is EvidenceCommandException { CleanupUnconfirmed: true };
}
