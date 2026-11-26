using ForgeTrust.AppSurface.Evidence.Aspire;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Owns one native producer workspace and preserves it when child shutdown cannot be confirmed.</summary>
/// <remarks>A retained path is printed locally. Disposal reports incomplete cleanup to either recipe supervisor.</remarks>
internal sealed class NativeProducerWorkspace
{
    private EvidenceWorkspace? _workspace;
    private int _cleanupUnconfirmed;

    /// <summary>Gets the producer's currently owned workspace.</summary>
    internal EvidenceWorkspace? Current => _workspace;

    /// <summary>Gets whether early disposal must leave the workspace available for inspection.</summary>
    internal bool CleanupUnconfirmed => Volatile.Read(ref _cleanupUnconfirmed) != 0;

    /// <summary>Creates the producer's isolated workspace under the caller's scratch parent.</summary>
    internal EvidenceWorkspace Create(string parent, EvidenceWorkspaceOptions options) =>
        _workspace = EvidenceWorkspace.Create(parent, options);

    /// <summary>Retains the workspace when the command runner could not confirm cleanup.</summary>
    internal void RecordCommandFailure(EvidenceCommandException exception)
    {
        if (_workspace is { } workspace && LocalEvidenceRun.IsUnsettledCommand(exception) &&
            Interlocked.Exchange(ref _cleanupUnconfirmed, 1) == 0)
        {
            Console.Error.WriteLine("Native producer scratch retained for inspection: " + workspace.RootPath);
        }
    }

    /// <summary>Removes settled scratch or signals incomplete cleanup without deleting retained files.</summary>
    internal async ValueTask DisposeAsync()
    {
        var workspace = Interlocked.Exchange(ref _workspace, null);
        if (CleanupUnconfirmed)
            throw new EvidenceCommandException("cleanup_failed", "Native producer scratch was retained because command cleanup could not be confirmed.");
        if (workspace is null)
            return;

        try
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Keep ownership if deletion failed so the supervisor can retry cleanup.
            Interlocked.CompareExchange(ref _workspace, workspace, null);
            throw;
        }
    }
}
