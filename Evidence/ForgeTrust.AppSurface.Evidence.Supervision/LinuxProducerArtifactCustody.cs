using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Retains only root-discovered coverage reports from one settled native Linux producer.</summary>
/// <remarks>
/// This holder binds the original input, owner, accounts, producer, workspace, and results token. It is
/// constructed only after the authentic producer's reports-ready guard succeeds. The returned report
/// metadata and bytes are data only; this holder does not establish successful test exit, accepted
/// coverage, admission, or proof. All reads and disposal are serialized, and any failed operation is sticky.
/// </remarks>
internal sealed class LinuxProducerArtifactCustody : IDisposable
{
    internal const int MaximumRequestedChunkBytes = 128 * 1024;
    private const string ReportName = "coverage.cobertura.xml";

    private readonly object _gate = new();
    private readonly LinuxInputDeadline _deadline;
    private readonly string _resultsToken;
    private readonly IReadOnlyList<LinuxRetainedReportSet.ReportInfo> _inventory;
    private readonly HashSet<string> _inventoryPaths;
    private EvidenceProtectedLaunchInput? _input;
    private LinuxOwnerActivation? _owner;
    private LinuxRunAccounts? _accounts;
    private LinuxProducerWorkspace? _workspace;
    private LinuxProducerProcess? _producer;
    private LinuxRetainedReportSet? _reports;
    private bool _failed;
    private bool _closed;

    private LinuxProducerArtifactCustody(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxProducerWorkspace workspace, LinuxProducerProcess producer,
        string resultsToken, LinuxRetainedReportSet reports,
        IReadOnlyList<LinuxRetainedReportSet.ReportInfo> inventory)
    {
        _input = input;
        _owner = owner;
        _accounts = accounts;
        _workspace = workspace;
        _producer = producer;
        _deadline = input.Deadline;
        _resultsToken = resultsToken;
        _reports = reports;
        _inventory = Array.AsReadOnly(inventory.ToArray());
        _inventoryPaths = new HashSet<string>(_inventory.Select(static item => item.RelativePath), StringComparer.Ordinal);
    }

    /// <summary>Gets the opaque workspace-selected result directory token as data only.</summary>
    internal string ResultsToken => _resultsToken;

    /// <summary>Authenticates the producer and workspace before root discovers or opens any report.</summary>
    /// <remarks>The only accepted inventory is discovered beneath the actual selected token directory.</remarks>
    internal static LinuxProducerArtifactCustody Capture(EvidenceProtectedLaunchInput input,
        LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxProducerWorkspace workspace,
        LinuxProducerProcess producer, string resultsToken, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(producer);

        LinuxCoverageControlOperations.RequireToken(resultsToken);
        _ = input.Remaining;
        owner.RequireLaunchInput(input, token);
        accounts.RequireOwnedBy(owner, token);
        workspace.RequireOwnedBy(owner, accounts, token);

        // Readiness is checked before the results path is selected or this factory opens report data.
        producer.RequireReportsReady(owner, accounts, workspace, token);
        _ = input.Remaining;
        owner.RequireActive(token);
        accounts.RequireOwnedBy(owner, token);
        workspace.RequireOwnedBy(owner, accounts, token);

        LinuxRetainedReportSet? reports = null;
        try
        {
            LinuxProtectedDeployment.RequirePlatform();
            var selected = workspace.RequireResultsToken(resultsToken, token);
            var expectedPath = workspace.ResultsRoot.TrimEnd('/') + "/" + resultsToken;
            LinuxProtectedDeployment.ValidateAbsolute(selected.Path);
            if (!StringComparer.Ordinal.Equals(selected.Path, expectedPath)) throw Invalid();
            var uid = accounts.SubjectUid;
            var gid = accounts.SubjectGid;
            using var discovery = LinuxSelectedProducerResultsRoot.Open(selected.Path, selected.Metadata, uid, gid,
                input.Deadline, token);
            var paths = LinuxProducerReportDiscovery.Discover(discovery.Root, uid, gid, input.Deadline, token);
            var inventoryPaths = ValidateReportPaths(paths);
            discovery.Recheck(input.Deadline, token);

            // OpenActual owns exact names and descriptors; the expected root and paths came from native discovery.
            reports = LinuxRetainedReportSet.OpenActual(selected.Path, discovery.Metadata, inventoryPaths,
                uid, gid, input.Deadline, token);
            discovery.Recheck(input.Deadline, token);
            var inventory = reports.ListReports(token);
            if (!inventory.Select(static item => item.RelativePath).SequenceEqual(inventoryPaths, StringComparer.Ordinal))
                throw Invalid();
            _ = input.Remaining;
            owner.RequireActive(token);
            accounts.RequireOwnedBy(owner, token);
            workspace.RequireOwnedBy(owner, accounts, token);

            var result = new LinuxProducerArtifactCustody(input, owner, accounts, workspace, producer,
                resultsToken, reports, inventory);
            reports = null;
            return result;
        }
        catch (OperationCanceledException) { reports?.Dispose(); throw; }
        catch (Exception error) when (Recoverable(error))
        {
            reports?.Dispose();
            throw LinuxProtectedDeployment.InvalidDeployment();
        }
        catch
        {
            reports?.Dispose();
            throw;
        }
    }

    /// <summary>Returns an immutable copy of the exact root-discovered retained inventory.</summary>
    internal IReadOnlyList<LinuxRetainedReportSet.ReportInfo> ListReports(CancellationToken token = default)
    {
        lock (_gate)
        {
            RequireOpen();
            try
            {
                CheckWork(token);
                var current = (_reports ?? throw Invalid()).ListReports(token);
                if (!LinuxReportCustodyMetadata.SameInventory(current, _inventory)) throw Invalid();
                CheckWork(token);
                return _inventory;
            }
            catch { _failed = true; throw; }
        }
    }

#if EVIDENCE_PRIVATE_C2_ARTIFACT_MUTATION
    /// <summary>Forwards the compile-private mutation to this genuine set after normal inventory succeeds.</summary>
    internal byte[] MutateRetainedReportForC2(CancellationToken token)
    {
        lock (_gate)
        {
            RequireOpen();
            CheckWork(token);
            return (_reports ?? throw Invalid()).MutateRetainedReportForC2(token);
        }
    }
#endif

    /// <summary>Reads only an exact inventoried report at its next offset, returning at most 128 KiB.</summary>
    /// <remarks>The retained engine serves 64 KiB pieces; this wrapper joins at most two pieces per call.</remarks>
    internal byte[] ReadChunk(string relativePath, long offset, int maximumBytes, CancellationToken token = default)
    {
        lock (_gate)
        {
            RequireOpen();
            try
            {
                CheckWork(token);
                if (relativePath is null || !_inventoryPaths.Contains(relativePath)
                    || maximumBytes is < 1 or > MaximumRequestedChunkBytes || offset < 0) throw Invalid();
                var report = _inventory.Single(item => item.RelativePath == relativePath);
                if (offset > report.Length || offset == report.Length && report.Length != 0) throw Invalid();

                var reports = _reports ?? throw Invalid();
                return SupervisionReportChunkTransfer.ReadValidated(report.Length, offset, maximumBytes,
                    () => CheckWork(token), (position, count) => reports.ReadChunk(relativePath, position, count, token));
            }
            catch { _failed = true; throw; }
        }
    }

    /// <summary>Joins this holder's serialized reads before closing every retained report descriptor.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            try { _reports?.Dispose(); }
            finally
            {
                _reports = null;
                _input = null;
                _owner = null;
                _accounts = null;
                _workspace = null;
                _producer = null;
            }
        }
    }

    /// <summary>Validates detached relative report names; no paths are opened by this data-only guard.</summary>
    internal static string[] ValidateReportPaths(IReadOnlyList<string> paths)
    {
        var validated = LinuxRetainedReportSet.ValidatePaths(paths);
        if (validated.Any(static path => !StringComparer.Ordinal.Equals(
                path[(path.LastIndexOf('/') + 1)..], ReportName))) throw Invalid();
        return validated;
    }

    private void CheckWork(CancellationToken token)
    {
        var input = _input ?? throw Invalid();
        var owner = _owner ?? throw Invalid();
        var accounts = _accounts ?? throw Invalid();
        var workspace = _workspace ?? throw Invalid();
        _ = input.Remaining;
        _deadline.Check(token);
        owner.RequireActive(token);
        accounts.RequireOwnedBy(owner, token);
        workspace.RequireOwnedBy(owner, accounts, token);
        _deadline.Check(token);
        _ = input.Remaining;
    }

    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_failed) throw Invalid();
    }

    /// <summary>Creates the original fixed custody failure without selecting any native object.</summary>
    internal static IOException Invalid() => new("The producer report set is unavailable or changed.");
    private static bool Recoverable(Exception error) => SupervisionExceptionPolicy.IsRecoverable(error);

}
