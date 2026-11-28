using ForgeTrust.AppSurface.Evidence.Aspire;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Owns a native producer workspace and retains it when subprocess cleanup is unconfirmed.</summary>
/// <remarks>Retained workspace paths are written to standard error for local inspection.</remarks>
internal sealed class NativeProducerWorkspace : IAsyncDisposable
{
    private readonly EvidenceWorkspace _workspace;
    private int _cleanupUnconfirmed;

    private NativeProducerWorkspace(EvidenceWorkspace workspace)
    {
        _workspace = workspace;
    }

    /// <summary>Gets the newly created absolute workspace path.</summary>
    internal string RootPath => _workspace.RootPath;

    /// <summary>Gets whether the workspace must be retained after an unconfirmed subprocess cleanup.</summary>
    internal bool CleanupUnconfirmed => Volatile.Read(ref _cleanupUnconfirmed) != 0;

    /// <summary>Creates an isolated workspace beneath the supplied scratch parent.</summary>
    internal static NativeProducerWorkspace Create(string parent, EvidenceWorkspaceOptions options) =>
        new(EvidenceWorkspace.Create(parent, options));

    /// <summary>Returns a normalized absolute path for a workspace artifact.</summary>
    internal string GetPath(string relativePath) => _workspace.GetPath(relativePath);

    /// <summary>Checks logical scratch usage and actual volume free space.</summary>
    internal void CheckBudget() => _workspace.CheckBudget();

    /// <summary>Marks the workspace for retention after unconfirmed subprocess cleanup.</summary>
    internal void RecordCommandFailure(EvidenceCommandException exception)
    {
        if (exception.CleanupUnconfirmed && Interlocked.Exchange(ref _cleanupUnconfirmed, 1) == 0)
        {
            Console.Error.WriteLine("Native producer scratch retained for inspection: " + RootPath);
        }
    }

    /// <summary>Deletes settled scratch or reports unconfirmed cleanup while retaining the files.</summary>
    public ValueTask DisposeAsync()
    {
        if (CleanupUnconfirmed)
        {
            return ValueTask.FromException(new EvidenceCommandException(
                EvidenceCommandException.CleanupFailedCode,
                "Native producer scratch was retained because command cleanup could not be confirmed.",
                cleanupUnconfirmed: true));
        }

        return _workspace.DisposeAsync();
    }
}
