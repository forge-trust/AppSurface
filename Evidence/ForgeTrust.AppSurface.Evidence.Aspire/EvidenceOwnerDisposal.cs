namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Disposes ordered owners while preserving reference identity and recording cleanup failures.</summary>
internal static class EvidenceOwnerDisposal
{
    /// <summary>Disposes prepared registrations, then joins any transferred resource scope.</summary>
    /// <param name="owners">Registrations in the caller's disposal order.</param>
    /// <param name="scope">The transferred scope, if the factory provided one.</param>
    /// <returns>The first nonfatal and first fatal failures, recorded independently.</returns>
    /// <remarks>Both steps share reference deduplication, and scope cleanup still runs after a registration failure.</remarks>
    internal static async Task<EvidenceOwnerDisposalResult> DisposePreparedAsync(
        IEnumerable<object> owners, EvidenceHostResourceScope? scope)
    {
        var disposedOwners = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var result = await DisposeAsync(owners, disposedOwners).ConfigureAwait(false);
        if (scope is null) return result;

        try { await scope.DisposeAsync(disposedOwners).ConfigureAwait(false); }
        catch (Exception exception) when (EvidenceHostBootstrap.IsNonFatalException(exception))
        {
            return result with { FirstNonFatalFailure = result.FirstNonFatalFailure ?? exception };
        }
        catch (Exception exception)
        {
            return result with { FirstFatalFailure = result.FirstFatalFailure ?? exception };
        }

        return result;
    }

    /// <summary>Disposes each unseen owner in order, preferring asynchronous disposal when available.</summary>
    /// <param name="owners">Owners in the caller's required disposal order.</param>
    /// <param name="disposedOwners">Reference-identity set shared with nested or earlier cleanup.</param>
    /// <returns>The first nonfatal and first fatal failures, recorded independently.</returns>
    /// <remarks>Prepared cleanup attempts every owner. The caller chooses which failure to surface.</remarks>
    internal static async Task<EvidenceOwnerDisposalResult> DisposeAsync(
        IEnumerable<object> owners,
        ISet<object> disposedOwners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(disposedOwners);

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
                firstNonFatalFailure ??= exception;
            }
            catch (Exception exception)
            {
                firstFatalFailure ??= exception;
            }
        }

        return new EvidenceOwnerDisposalResult(firstNonFatalFailure, firstFatalFailure);
    }
}

/// <summary>Failures captured while attempting an ordered owner-disposal sequence.</summary>
/// <param name="FirstNonFatalFailure">First exception that can be reported as a cleanup failure.</param>
/// <param name="FirstFatalFailure">First fatal exception, which callers may need to rethrow.</param>
internal readonly record struct EvidenceOwnerDisposalResult(
    Exception? FirstNonFatalFailure,
    Exception? FirstFatalFailure);
