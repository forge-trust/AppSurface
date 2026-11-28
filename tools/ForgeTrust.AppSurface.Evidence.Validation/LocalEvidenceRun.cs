using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Tracks the local run's paths and whether its commands and host have confirmed cleanup.</summary>
internal sealed class LocalEvidenceRun(string outputDirectory, string? packageRestoreCache)
{
    private int _commandCleanupFailed;
    private bool _hostCleanupUnconfirmed;

    /// <summary>Gets the fresh directory for this run's unpacked results.</summary>
    internal string OutputDirectory { get; } = outputDirectory;

    /// <summary>Gets the private package-gate restore cache for managed runs, or null for explicit output.</summary>
    internal string? PackageRestoreCache { get; } = packageRestoreCache;

    /// <summary>Gets whether a command reported incomplete cleanup or the started host has yet to confirm cleanup.</summary>
    internal bool CleanupUnconfirmed => Volatile.Read(ref _commandCleanupFailed) != 0 || _hostCleanupUnconfirmed;

    /// <summary>Marks host work as potentially active until its cleanup result is available.</summary>
    internal void BeginHostExecution() => _hostCleanupUnconfirmed = true;

    /// <summary>Records the host's cleanup result before manifest publication can fail.</summary>
    internal void RecordHostCleanup(bool completed) => _hostCleanupUnconfirmed = !completed;

    /// <summary>Records command cleanup failure before a producer translates the exception into a diagnostic.</summary>
    internal async Task<EvidenceCommandResult> ObserveCommandAsync(
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> command,
        EvidenceCommandRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await command(request, cancellationToken).ConfigureAwait(false);
        }
        catch (EvidenceCommandException exception) when (exception.CleanupUnconfirmed)
        {
            RecordCommandCleanupFailure();
            throw;
        }
    }

    /// <summary>Binds a source verifier to this run so every checkpoint records unconfirmed Git cleanup.</summary>
    /// <remarks>Pass the returned callback to producers that may translate a verification exception into a diagnostic.</remarks>
    /// <param name="verify">Checks the original source and policy snapshot.</param>
    /// <returns>The tracked verifier to use at every checkpoint.</returns>
    internal Func<CancellationToken, Task> TrackSourceVerification(Func<CancellationToken, Task> verify) => async cancellationToken =>
    {
        try
        {
            await verify(cancellationToken).ConfigureAwait(false);
        }
        catch (EvidenceCommandException exception) when (exception.CleanupUnconfirmed)
        {
            RecordCommandCleanupFailure();
            throw;
        }
    };

    private void RecordCommandCleanupFailure() => Interlocked.Exchange(ref _commandCleanupFailed, 1);
}

/// <summary>The plan and, for an executed run, the verified manifest. Empty inventories return no result.</summary>
/// <param name="Plan">The resolved plan used for this run.</param>
/// <param name="Manifest">The verified manifest when the plan was executed.</param>
/// <param name="ReplayInputs">The original policy and changed-path snapshots needed to replay verification.</param>
internal sealed record LocalEvidenceRunResult(
    EvidencePlan Plan, EvidenceManifest? Manifest, IReadOnlyList<LocalEvidenceReplayInput> ReplayInputs);

/// <summary>Identifies the captured bytes of a required replay input at the archive root.</summary>
/// <param name="RelativePath">The entry name relative to the archive root.</param>
/// <param name="LengthBytes">The original input length in bytes.</param>
/// <param name="Sha256">The SHA-256 digest of the original input.</param>
internal sealed record LocalEvidenceReplayInput(string RelativePath, long LengthBytes, string Sha256)
{
    /// <summary>Binds a replay input to the original bytes written by the local workflow.</summary>
    internal static LocalEvidenceReplayInput Snapshot(string relativePath, ReadOnlySpan<byte> content) =>
        new(relativePath, content.Length, EvidenceDigest.Sha256(content));
}
