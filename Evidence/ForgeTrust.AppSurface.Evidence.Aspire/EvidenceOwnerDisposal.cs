namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Disposes ordered owners while preserving reference identity and recording cleanup failures.</summary>
internal static class EvidenceOwnerDisposal
{
    /// <summary>Disposes each unseen owner in order, preferring asynchronous disposal when available.</summary>
    /// <param name="owners">Owners in the caller's required disposal order.</param>
    /// <param name="disposedOwners">Reference-identity set shared with nested or earlier cleanup.</param>
    /// <returns>The first failure, plus the first nonfatal and fatal failures independently.</returns>
    /// <remarks>Prepared cleanup attempts every owner. The caller chooses which failure to surface.</remarks>
    internal static async Task<EvidenceOwnerDisposalResult> DisposeAsync(
        IEnumerable<object> owners,
        ISet<object> disposedOwners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(disposedOwners);

        Exception? firstFailure = null;
        Exception? firstNonFatalFailure = null;
        Exception? firstFatalFailure = null;
        foreach (var owner in owners)
        {
            if (!disposedOwners.Add(owner)) continue;
            try
            {
                if (owner is IAsyncDisposable asynchronous)
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                else if (owner is IDisposable synchronous)
                    synchronous.Dispose();
            }
            catch (Exception exception) when (EvidenceHostBootstrap.IsNonFatalException(exception))
            {
                firstFailure ??= exception;
                firstNonFatalFailure ??= exception;
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
                firstFatalFailure ??= exception;
            }
        }

        return new EvidenceOwnerDisposalResult(firstFailure, firstNonFatalFailure, firstFatalFailure);
    }
}

/// <summary>Failures captured while attempting an ordered owner-disposal sequence.</summary>
/// <param name="FirstFailure">First exception from any owner.</param>
/// <param name="FirstNonFatalFailure">First exception that can be reported as a cleanup failure.</param>
/// <param name="FirstFatalFailure">First fatal exception, which callers may need to rethrow.</param>
internal readonly record struct EvidenceOwnerDisposalResult(
    Exception? FirstFailure,
    Exception? FirstNonFatalFailure,
    Exception? FirstFatalFailure);
