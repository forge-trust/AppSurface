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

    private bool CleanupUnconfirmed => Volatile.Read(ref _cleanupUnconfirmed) != 0;

    /// <summary>Creates the producer's isolated workspace under the caller's scratch parent.</summary>
    internal EvidenceWorkspace Create(string parent, EvidenceWorkspaceOptions options) =>
        _workspace = EvidenceWorkspace.Create(parent, options);

    /// <summary>Retains the workspace when the command runner could not confirm cleanup.</summary>
    internal void RecordCommandFailure(EvidenceCommandException exception)
    {
        if (_workspace is { } workspace && exception.CleanupUnconfirmed &&
            Interlocked.Exchange(ref _cleanupUnconfirmed, 1) == 0)
        {
            Console.Error.WriteLine("Native producer scratch retained for inspection: " + workspace.RootPath);
        }
    }

    /// <summary>Releases settled scratch before the next journey. The supervisor reports any recorded command cleanup failure.</summary>
    internal ValueTask ReleaseIfSettledAsync() =>
        CleanupUnconfirmed ? ValueTask.CompletedTask : ReleaseAsync();

    /// <summary>Removes settled scratch or signals incomplete cleanup without deleting retained files.</summary>
    internal ValueTask DisposeAsync()
    {
        if (CleanupUnconfirmed)
        {
            Interlocked.Exchange(ref _workspace, null);
            return ValueTask.FromException(new EvidenceCommandException(EvidenceCommandException.CleanupFailedCode,
                "Native producer scratch was retained because command cleanup could not be confirmed."));
        }

        return ReleaseAsync();
    }

    private async ValueTask ReleaseAsync()
    {
        var workspace = Interlocked.Exchange(ref _workspace, null);
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
