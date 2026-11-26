using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Tracks the managed run's paths and whether a producer still might own its scratch.</summary>
/// <remarks>Explicit-output runs do not use this state. Host cleanup is reported separately by the manifest.</remarks>
internal sealed class LocalEvidenceRun(string outputDirectory, string packageRestoreCache)
{
    private int _commandCleanupFailed;

    /// <summary>Gets the fresh directory for this run's unpacked results.</summary>
    internal string OutputDirectory { get; } = outputDirectory;

    /// <summary>Gets the private package-gate restore cache shared by this worktree's managed runs.</summary>
    internal string PackageRestoreCache { get; } = packageRestoreCache;

    /// <summary>Gets whether a command reported that it could not finish cleanup.</summary>
    internal bool CommandCleanupFailed => Volatile.Read(ref _commandCleanupFailed) != 0;

    /// <summary>Records command cleanup failure before a producer translates the exception into a diagnostic.</summary>
    internal async Task<EvidenceCommandResult> ObserveCommandAsync(
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> command,
        EvidenceCommandRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await command(request, cancellationToken).ConfigureAwait(false);
        }
        catch (EvidenceCommandException exception) when (IsUnsettledCommand(exception))
        {
            Interlocked.Exchange(ref _commandCleanupFailed, 1);
            throw;
        }
    }

    /// <summary>Recognizes the command runner's typed cleanup failure.</summary>
    internal static bool IsUnsettledCommand(Exception exception) =>
        exception is EvidenceCommandException { Code: "cleanup_failed" };
}

/// <summary>The plan and, for an executed run, the verified manifest. Empty inventories return no result.</summary>
internal sealed record LocalEvidenceRunResult(EvidencePlan Plan, EvidenceManifest? Manifest);
