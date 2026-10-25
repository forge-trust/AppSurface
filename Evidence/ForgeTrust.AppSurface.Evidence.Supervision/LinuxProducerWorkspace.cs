using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Path and mode data for a producer workspace; it cannot create or adopt native directories.</summary>
internal sealed class LinuxProducerWorkspaceLayout
{
    internal const string ResultsName = "results";
    internal const string SubjectName = "subject";
    internal const string ScratchName = "scratch";
    internal const string PackageName = "nuget";
    internal const int MaximumNodes = LinuxProtectedDeployment.MaximumNodes;
    internal const long MaximumFileBytes = LinuxProtectedDeployment.MaximumFileBytes;
    internal const long MaximumTreeBytes = 1024L * 1024 * 1024;

    private LinuxProducerWorkspaceLayout(Guid runId, LinuxRunAccountSnapshot accounts,
        string originalSubjectRoot, string solution)
    {
        Accounts = accounts with { };
        RootName = "appsurface-evidence-producer-data-" + runId.ToString("N");
        Root = "/run/" + RootName;
        ResultsRoot = Root + "/" + ResultsName;
        SubjectRoot = Root + "/" + SubjectName;
        ScratchRoot = Root + "/" + ScratchName;
        PackageRoot = ScratchRoot + "/" + PackageName;
        var relative = RelativeSolution(originalSubjectRoot, solution);
        SolutionPath = SubjectRoot + "/" + relative;
    }

    internal LinuxRunAccountSnapshot Accounts { get; }
    internal string RootName { get; }
    internal string Root { get; }
    internal string ResultsRoot { get; }
    internal string SubjectRoot { get; }
    internal string SolutionPath { get; }
    internal string ScratchRoot { get; }
    internal string PackageRoot { get; }

    internal static LinuxProducerWorkspaceLayout Create(Guid runId, LinuxRunAccountSnapshot accounts,
        string originalSubjectRoot, string? solution)
    {
        if (runId == Guid.Empty || accounts is null || solution is null
            || accounts.WorkerUid is 0 or uint.MaxValue || accounts.SubjectUid is 0 or uint.MaxValue
            || accounts.WorkerUid == accounts.SubjectUid || accounts.WorkerGid is 0 or uint.MaxValue
            || accounts.SubjectGid is 0 or uint.MaxValue || accounts.ResultsGid is 0 or uint.MaxValue
            || accounts.WorkerGid == accounts.SubjectGid || accounts.WorkerGid == accounts.ResultsGid
            || accounts.SubjectGid == accounts.ResultsGid)
            throw Invalid();
        _ = RelativeSolution(originalSubjectRoot, solution);
        return new(runId, accounts, originalSubjectRoot, solution);
    }

    internal static string RelativeSolution(string originalSubjectRoot, string solution)
    {
        LinuxProtectedDeployment.ValidateAbsolute(originalSubjectRoot);
        LinuxProtectedDeployment.ValidateAbsolute(solution);
        var prefix = originalSubjectRoot + "/";
        if (!solution.StartsWith(prefix, StringComparison.Ordinal)) throw Invalid();
        var relative = solution[prefix.Length..];
        if (Encoding.UTF8.GetByteCount(relative) is < 1 or > 4095
            || relative.Split('/').Any(static part => part.Length is < 1 or > 255)) throw Invalid();
        return relative;
    }

    internal static bool IsResultsToken(string value) => value is { Length: >= 1 and <= 96 }
        && value.All(static c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '-');

    internal static void RequireDirectory(LinuxProtectedMetadata value, uint uid, uint gid, ushort permissions)
    {
        if (value.Inode == 0 || value.Uid != uid || value.Gid != gid
            || value.Mode != (ushort)(0x4000 | permissions)) throw Invalid();
    }

    internal static void RequireAncestor(LinuxProtectedMetadata value)
    {
        if (value.Uid != 0 || value.Gid != 0) throw Invalid();
        value.RequireAncestor(sharedTraversal: true);
    }

    internal static EvidenceAdmissionException Invalid() =>
        new("ASEVD402", "The protected producer workspace is unavailable or changed.");
}

/// <summary>Irreversible copy reservations checked before any associated destination node or byte write.</summary>
/// <remarks>This bookkeeping grants no filesystem authority. Native copying independently validates every actual file.</remarks>
internal sealed class LinuxProducerCopyBudget
{
    private int _nodes;
    private long _bytes;
    internal int Nodes => _nodes;
    internal long Bytes => _bytes;

    /// <summary>Reserves a directory node before mkdir; failure preserves all earlier consumption.</summary>
    internal void ReserveDirectory() => Reserve(0);

    /// <summary>Reserves the original positive file size and one node before open/copy, across all copied trees.</summary>
    internal void ReserveFile(long length)
    {
        if (length <= 0 || length > LinuxProducerWorkspaceLayout.MaximumFileBytes) throw LinuxProducerWorkspaceLayout.Invalid();
        Reserve(length);
    }

    private void Reserve(long length)
    {
        if (_nodes >= LinuxProducerWorkspaceLayout.MaximumNodes
            || length > LinuxProducerWorkspaceLayout.MaximumTreeBytes - _bytes) throw LinuxProducerWorkspaceLayout.Invalid();
        _nodes++;
        _bytes += length;
    }
}

/// <summary>Privately retained producer inputs, result-token storage and scratch under one generated /run root.</summary>
/// <remarks>
/// Creation never adopts a generation. The account holder retains this object before the first mutation, and
/// any partial failure leaves the reservation quarantined. Subject and NuGet files are copied from independently
/// audited protected deployments under the input's original deadline; no project is evaluated here. Cleanup
/// first joins the retained producer and requires its native physical settlement, then inventories every mutable
/// node before changing any owner or mode. No recursive delete or caller Boolean can release account identities.
/// </remarks>
internal sealed class LinuxProducerWorkspace : IDisposable
{
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong PathFlags = 0x200000 | 0x80000;
    private const ulong ReadFileFlags = 0x80000 | 0x800;
    private const ulong ParentResolution = 0x02 | 0x04 | 0x08;
    private const ulong ChildResolution = ParentResolution | 0x01;
    private const ushort RootPermissions = 0x1c9; // 0711.
    private const ushort PrivatePermissions = LinuxWorkspaceMetadata.ProducerDirectoryMode; // 0700.
    private const ushort PrivateFilePermissions = LinuxWorkspaceMetadata.ProducerFileMode; // 0600.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object _gate = new();
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxProducerWorkspaceLayout _layout;
    private readonly LinuxInputDeadline _inputDeadline;
    private readonly List<DirectoryNode> _nodes = [];
    private DirectoryNode? _slash;
    private DirectoryNode? _run;
    private DirectoryNode? _root;
    private DirectoryNode? _resultsRoot;
    private DirectoryNode? _subjectRoot;
    private DirectoryNode? _scratchRoot;
    private DirectoryNode? _packageRoot;
    private DirectoryNode? _resultsTokenRoot;
    private LinuxProducerProcess? _producer;
    private Task? _sealTask;
    private string? _resultsToken;
    private string? _resultsTokenPath;
    private bool _resultsTokenAttempted;
    private bool _producerRetentionAttempted;
    private bool _sealAttempted;
    private bool _finalizingSeal;
    private bool _sealed;
    private bool _quarantined;
    private bool _handlesClosed;
    private bool _releaseGuardPassed;
    private readonly LinuxProducerCopyBudget _copyBudget = new();

    private LinuxProducerWorkspace(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxProducerWorkspaceLayout layout, LinuxInputDeadline inputDeadline)
    {
        _owner = owner;
        _accounts = accounts;
        _layout = layout;
        _inputDeadline = inputDeadline;
    }

    internal string Root => _layout.Root;
    internal string RootPath => _layout.Root;
    internal string SubjectRoot => _layout.SubjectRoot;
    internal string SolutionPath => _layout.SolutionPath;
    internal string ResultsRoot => _layout.ResultsRoot;
    internal string ScratchRoot => _layout.ScratchRoot;
    internal string PackageRoot => _layout.PackageRoot;
    internal bool IsSealedForCleanup { get { lock (_gate) return _sealed && !_quarantined; } }

    /// <summary>Creates a fresh root-owned generation and copies all audited subject and NuGet files.</summary>
    internal static LinuxProducerWorkspace Create(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts);
        LinuxProtectedDeployment.RequirePlatform();
        owner.RequireActive(token);
        accounts.RequireOwnedBy(owner, token);
        input.Deadline.Check(token);
        var snapshot = new LinuxRunAccountSnapshot(accounts.WorkerUid, accounts.WorkerGid,
            accounts.SubjectUid, accounts.SubjectGid, accounts.ResultsGid);
        var layout = LinuxProducerWorkspaceLayout.Create(owner.RunId, snapshot,
            input.Request.SubjectRoot, input.Request.Solution);
        var workspace = new LinuxProducerWorkspace(owner, accounts, layout, input.Deadline);
        accounts.RetainProducerWorkspaceForCustody(workspace, owner);
        try
        {
            workspace.CreateDirectories(token);
            workspace.CopyProtectedInputs(input, token);
            workspace.RecheckCritical(token);
            return workspace;
        }
        catch (OperationCanceledException) { workspace.Quarantine(); throw; }
        catch (Exception error) when (Recoverable(error))
        {
            workspace.Quarantine();
            throw LinuxProducerWorkspaceLayout.Invalid();
        }
    }

    /// <summary>Checks only the original private owner/account references before account custody reservation.</summary>
    internal void RequireAccountOwner(LinuxRunAccounts accounts, LinuxOwnerActivation owner)
    {
        if (!ReferenceEquals(accounts, _accounts) || !ReferenceEquals(owner, _owner))
            throw LinuxProducerWorkspaceLayout.Invalid();
        LinuxWorkspaceOwnerPolicy.RequireProducerData(_layout.Accounts, _layout.RootName, owner.RunId);
    }

    /// <summary>Retains the exact producer before any unit-start I/O can be dispatched.</summary>
    internal void RetainProducer(LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxProducerProcess producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        lock (_gate)
        {
            RequireAccountOwner(accounts, owner);
            owner.RequireActive(default);
            accounts.RequireOwnedBy(owner, default);
            LinuxProducerWorkspaceStatePolicy.RequireRetainProducer(new(
                _quarantined, _sealed, _handlesClosed, _sealAttempted,
                _producerRetentionAttempted, _resultsTokenAttempted, _slash is not null, _run is not null));
            _producerRetentionAttempted = true;
            _producer = producer;
        }
    }

    /// <summary>Rechecks the live owner, account holder and retained native critical paths for producer work.</summary>
    internal void RequireOwnedBy(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        lock (_gate)
        {
            RequireAccountOwner(accounts, owner);
            LinuxProducerWorkspaceStatePolicy.RequireOwnedBy(new(
                _quarantined, _sealed, _handlesClosed, _sealAttempted,
                _producerRetentionAttempted, _resultsTokenAttempted, _slash is not null, _run is not null));
            owner.RequireActive(token);
            accounts.RequireOwnedBy(owner, token);
            RecheckCriticalCore(token);
        }
    }

    /// <summary>Rechecks this retained generation for stop and cleanup after work admission closes.</summary>
    internal void RequireControlOwnedBy(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireAccountOwner(accounts, owner);
            LinuxProducerWorkspaceStatePolicy.RequireControlOwnedBy(new(
                _quarantined, _sealed, _handlesClosed, _sealAttempted,
                _producerRetentionAttempted, _resultsTokenAttempted, _slash is not null, _run is not null));
            owner.RequireControlIdentity(token);
            RecheckCriticalCore(token, cleanup: true);
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Creates exactly one subject-owned results directory and returns its full path for the producer.</summary>
    internal string PrepareResultsToken(string resultsToken, CancellationToken token)
    {
        lock (_gate)
        {
            try
            {
                RequireOwnedBy(_owner, _accounts, token);
                LinuxProducerWorkspaceStatePolicy.RequireResultsTokenPreparation(new(
                    _quarantined, _sealed, _handlesClosed, _sealAttempted,
                    _producerRetentionAttempted, _resultsTokenAttempted, _slash is not null, _run is not null));
                _resultsTokenAttempted = true;
                _inputDeadline.Check(token);
                if (!LinuxProducerWorkspaceLayout.IsResultsToken(resultsToken))
                    throw LinuxProducerWorkspaceLayout.Invalid();
                _resultsToken = resultsToken;
                var selectedPath = ResultsRoot + "/" + resultsToken;
                _resultsTokenPath = selectedPath;
                _resultsTokenRoot = CreateDirectory(_resultsRoot!, resultsToken, _layout.Accounts.SubjectUid,
                    _layout.Accounts.SubjectGid, PrivatePermissions, token, _inputDeadline);
                RecheckCriticalCore(token);
                return selectedPath;
            }
            catch (OperationCanceledException) { _quarantined = true; throw; }
            catch (Exception error) when (Recoverable(error))
            { _quarantined = true; throw LinuxProducerWorkspaceLayout.Invalid(); }
        }
    }

    /// <summary>Validates the one retained opaque token and returns its actual path and current root metadata.</summary>
    internal (string Path, LinuxProtectedMetadata Metadata) RequireResultsToken(string resultsToken,
        CancellationToken token)
    {
        lock (_gate)
        {
            try
            {
                RequireOwnedBy(_owner, _accounts, token);
                if (!LinuxProducerWorkspaceLayout.IsResultsToken(resultsToken)
                    || _resultsTokenRoot is null || _resultsTokenPath is null
                    || !StringComparer.Ordinal.Equals(resultsToken, _resultsToken))
                    throw LinuxProducerWorkspaceLayout.Invalid();
                _inputDeadline.Check(token);
                var current = LinuxProtectedMetadata.From(StatFd(_resultsTokenRoot.Handle));
                LinuxProducerWorkspaceLayout.RequireDirectory(current, _layout.Accounts.SubjectUid,
                    _layout.Accounts.SubjectGid, PrivatePermissions);
                if (!current.SameAncestorAs(_resultsTokenRoot.Metadata)) throw LinuxProducerWorkspaceLayout.Invalid();
                RequireSameNamed(_resultsRoot!.Handle, resultsToken, current);
                _inputDeadline.Check(token);
                return (_resultsTokenPath, current);
            }
            catch (OperationCanceledException) { _quarantined = true; throw; }
            catch (Exception error) when (Recoverable(error))
            { _quarantined = true; throw LinuxProducerWorkspaceLayout.Invalid(); }
        }
    }

    /// <summary>Joins the actual producer, verifies physical settlement, then seals every mutable node as root.</summary>
    internal Task SealForCleanup(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        lock (_gate)
        {
            RequireAccountOwner(accounts, owner);
            if (_sealTask is not null) return _sealTask;
            if (_quarantined || _sealed || _sealAttempted) throw LinuxProducerWorkspaceLayout.Invalid();
            _sealAttempted = true;
            _sealTask = SealCoreAsync(owner, accounts, token);
            return _sealTask;
        }
    }

    /// <summary>Requires completed sealing and rechecks the actual root tree before any account ID is released.</summary>
    internal void RequireSealedForAccountRelease(LinuxRunAccounts accounts, LinuxOwnerActivation owner,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireAccountOwner(accounts, owner);
            owner.RequireControlIdentity(token);
            LinuxProducerWorkspaceStatePolicy.RequireSealedForAccountRelease(new(
                _quarantined, _sealed, _handlesClosed, _sealAttempted,
                _producerRetentionAttempted, _resultsTokenAttempted, _slash is not null, _run is not null));
            try
            {
                RecheckCriticalCore(token, cleanup: true);
                VerifySealedTrees(token);
                _releaseGuardPassed = true;
            }
            catch (OperationCanceledException) { _quarantined = true; throw; }
            catch (Exception error) when (Recoverable(error))
            { _quarantined = true; throw LinuxProducerWorkspaceLayout.Invalid(); }
        }
    }

    private async Task SealCoreAsync(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        var phase = "reference-binding";
        try
        {
            LinuxProducerProcess? producer;
            lock (_gate)
            {
                RequireAccountOwner(accounts, owner);
                if (_quarantined || _sealed || _slash is null || _run is null) throw LinuxProducerWorkspaceLayout.Invalid();
                producer = _producer;
                if (producer is null && _producerRetentionAttempted) throw LinuxProducerWorkspaceLayout.Invalid();
            }

            phase = "owner-binding";
            owner.RequireControlIdentity(token);
            if (producer is not null)
            {
                phase = "producer-stop";
                await producer.StopAndJoinAsync(token).ConfigureAwait(false);
                phase = "producer-settlement";
                producer.RequirePhysicalSettlement(owner, accounts, this, token);
            }
            else if (_producerRetentionAttempted) throw LinuxProducerWorkspaceLayout.Invalid();

            phase = "workspace-binding";
            RequireControlOwnedBy(owner, accounts, token);
            lock (_gate)
            {
                if (_quarantined || _sealed || _handlesClosed) throw LinuxProducerWorkspaceLayout.Invalid();
                RecheckCriticalCore(token, cleanup: true);
                var entries = new List<SealEntry>();
                try
                {
                    phase = "collect";
                    CollectMutableTrees(entries, token, sealedTree: false);
                    phase = "entry-recheck";
                    foreach (var entry in entries) RecheckEntry(entry, token, sealedTree: false);
                    phase = "entry-seal";
                    foreach (var entry in entries) SealEntryNode(entry, token);
                    _finalizingSeal = true;
                    phase = "sealed-snapshot";
                    StoreSealedMetadata();
                    phase = "sealed-verify";
                    VerifySealedTrees(token);
                    phase = "final-binding";
                    RecheckCriticalCore(token, cleanup: true);
                    _sealed = true;
                    _finalizingSeal = false;
                }
                catch { _finalizingSeal = false; throw; }
                finally { CloseTemporaryEntries(entries); }
            }
        }
        catch (OperationCanceledException) { EmitSealFailure(phase, "Cancelled"); Quarantine(); throw; }
        catch (Exception error) when (Recoverable(error))
        { EmitSealFailure(phase, error is EvidenceAdmissionException ? "Admission" : error is IOException ? "Io" : "Other"); Quarantine(); throw LinuxProducerWorkspaceLayout.Invalid(); }
    }

    // Closed diagnostic labels only; no error text, file path, cleanup fact or fresh reader is exposed.
    private static void EmitSealFailure(string phase, string errorKind)
    {
        try
        {
            var bytes = EvidenceCanonicalJson.Serialize(new
                { schema = "evidence-producer-seal-failure-v1", phase, error_kind = errorKind });
            using var output = Console.OpenStandardError();
            output.Write(bytes);
            output.Write("\n"u8);
            output.Flush();
        }
        catch (Exception error) when (Recoverable(error)) { }
    }

    private static void EmitSealGuard(object fields)
    {
        try
        {
            var bytes = EvidenceCanonicalJson.Serialize(new { schema = "evidence-producer-seal-guard-v1", fields });
            if (bytes.Length > 4096) return;
            using var output = Console.OpenStandardError();
            output.Write(bytes);
            output.Write("\n"u8);
            output.Flush();
        }
        catch (Exception error) when (Recoverable(error)) { }
    }

    private void CreateDirectories(CancellationToken token)
    {
        CheckInput(token);
        _slash = OpenAncestor(null, "/", token, allowMount: true);
        _run = OpenAncestor(_slash, "run", token, allowMount: true);
        _root = CreateDirectory(_run, _layout.RootName, 0, 0, RootPermissions, token, _inputDeadline);
        _resultsRoot = CreateDirectory(_root, LinuxProducerWorkspaceLayout.ResultsName, 0, 0,
            RootPermissions, token, _inputDeadline);
        _subjectRoot = CreateDirectory(_root, LinuxProducerWorkspaceLayout.SubjectName,
            _layout.Accounts.SubjectUid, _layout.Accounts.SubjectGid, PrivatePermissions, token, _inputDeadline);
        _scratchRoot = CreateDirectory(_root, LinuxProducerWorkspaceLayout.ScratchName,
            _layout.Accounts.SubjectUid, _layout.Accounts.SubjectGid, PrivatePermissions, token, _inputDeadline);
        _packageRoot = CreateDirectory(_scratchRoot, LinuxProducerWorkspaceLayout.PackageName,
            _layout.Accounts.SubjectUid, _layout.Accounts.SubjectGid, PrivatePermissions, token, _inputDeadline);
        _ = CreateDirectory(_scratchRoot, "tmp", _layout.Accounts.SubjectUid, _layout.Accounts.SubjectGid,
            PrivatePermissions, token, _inputDeadline);
        _ = CreateDirectory(_scratchRoot, "dotnet-home", _layout.Accounts.SubjectUid, _layout.Accounts.SubjectGid,
            PrivatePermissions, token, _inputDeadline);
        RecheckCriticalCore(token);
    }

    private void CopyProtectedInputs(EvidenceProtectedLaunchInput input, CancellationToken token)
    {
        _inputDeadline.Check(token);
        using (var subject = LinuxProtectedDeployment.Open(input.Request.SubjectRoot, token, _inputDeadline))
        {
            var relativeSolution = LinuxProducerWorkspaceLayout.RelativeSolution(input.Request.SubjectRoot,
                input.Request.Solution ?? throw LinuxProducerWorkspaceLayout.Invalid());
            _ = subject.RequireFile(relativeSolution);
            CopyTree(subject, _subjectRoot!, token);
            _ = subject.RequireFile(relativeSolution);
        }
        _inputDeadline.Check(token);
        using (var packages = LinuxProtectedDeployment.Open(input.Request.ToolRoot + "/nuget", token, _inputDeadline))
            CopyTree(packages, _packageRoot!, token);
        _inputDeadline.Check(token);
    }

    private void CopyTree(LinuxProtectedDeployment source, DirectoryNode destination, CancellationToken token) =>
        LinuxProducerFileTree.Copy(source, destination.Handle, _layout.Accounts.SubjectUid,
            _layout.Accounts.SubjectGid, _copyBudget, _inputDeadline, token);

    private DirectoryNode OpenAncestor(DirectoryNode? parent, string name, CancellationToken token, bool allowMount)
    {
        CheckInput(token);
        var handle = OpenAt2(parent is null ? -100 : Fd(parent.Handle), name, DirectoryFlags, 0,
            parent is null ? 0 : allowMount ? ParentResolution : ChildResolution);
        var node = new DirectoryNode(handle, parent, name, 0, 0, 0, ancestor: true);
        _nodes.Add(node);
        node.Metadata = LinuxProtectedMetadata.From(StatFd(handle));
        LinuxProducerWorkspaceLayout.RequireAncestor(node.Metadata);
        CheckInput(token);
        return node;
    }

    private DirectoryNode CreateDirectory(DirectoryNode parent, string name, uint uid, uint gid,
        ushort permissions, CancellationToken token, LinuxInputDeadline? deadline)
    {
        deadline?.Check(token);
        RecheckNode(parent, token, allowMount: parent.Ancestor);
        if (MkdirAt(Fd(parent.Handle), name, permissions) != 0) throw LinuxProducerWorkspaceLayout.Invalid();
        deadline?.Check(token);
        var handle = OpenAt2(Fd(parent.Handle), name, DirectoryFlags, 0, ChildResolution);
        var node = new DirectoryNode(handle, parent, name, uid, gid, permissions);
        _nodes.Add(node); // Retain before owner, mode, and name checks.
        var created = LinuxProtectedMetadata.From(StatFd(handle));
        if (created.Inode == 0 || created.Uid != 0 || created.Gid != 0
            || (created.Mode & 0xf000) != 0x4000 || (created.Mode & 0x0e00) != 0)
            throw LinuxProducerWorkspaceLayout.Invalid();
        RequireSameNamed(parent.Handle, name, created);
        deadline?.Check(token);
        SetOwnershipAndMode(handle, uid, gid, permissions);
        node.Metadata = LinuxProtectedMetadata.From(StatFd(handle));
        LinuxProducerWorkspaceLayout.RequireDirectory(node.Metadata, uid, gid, permissions);
        RequireSameNamed(parent.Handle, name, node.Metadata);
        deadline?.Check(token);
        return node;
    }

    private void CollectMutableTrees(List<SealEntry> entries, CancellationToken token, bool sealedTree)
    {
        if (_subjectRoot is null || _scratchRoot is null || _resultsRoot is null || _root is null)
            throw LinuxProducerWorkspaceLayout.Invalid();
        var rootNames = ReadNames(_root.Handle, token);
        RequireExactNames(rootNames, LinuxProducerWorkspaceLayout.ResultsName,
            LinuxProducerWorkspaceLayout.SubjectName, LinuxProducerWorkspaceLayout.ScratchName);
        var resultNames = ReadNames(_resultsRoot.Handle, token);
        if (_resultsToken is null) RequireExactNames(resultNames);
        else RequireExactNames(resultNames, _resultsToken);

        CollectTree(_subjectRoot, entries, token, sealedTree);
        CollectTree(_scratchRoot, entries, token, sealedTree);
        if (_resultsTokenRoot is not null) CollectTree(_resultsTokenRoot, entries, token, sealedTree);
        var totalBytes = 0L;
        foreach (var entry in entries)
        {
            if (entries.Count > LinuxProducerWorkspaceLayout.MaximumNodes)
            { EmitSealGuard(new { reason = "node-count", count = entries.Count }); throw LinuxProducerWorkspaceLayout.Invalid(); }
            if (entry.Directory) continue;
            if (entry.Original.Length > (ulong)(LinuxProducerWorkspaceLayout.MaximumTreeBytes - totalBytes))
            {
                EmitSealGuard(new { reason = "aggregate-bytes", bytes = totalBytes, next_bytes = entry.Original.Length });
                throw LinuxProducerWorkspaceLayout.Invalid();
            }
            totalBytes += checked((long)entry.Original.Length);
        }
    }

    private void CollectTree(DirectoryNode root, List<SealEntry> entries, CancellationToken token, bool sealedTree)
    {
        var expectedUid = sealedTree ? 0u : _layout.Accounts.SubjectUid;
        var expectedGid = sealedTree ? 0u : _layout.Accounts.SubjectGid;
        var rootMetadata = LinuxProtectedMetadata.From(StatFd(root.Handle));
        RequireMutableMetadata(rootMetadata, directory: true, expectedUid, expectedGid, sealedTree);
        entries.Add(new(root.Handle, root.Parent!.Handle, root.Name, rootMetadata, directory: true, ownsHandle: false));
        CollectChildren(root.Handle, root.Name, entries, token, sealedTree, 0);
    }

    private void CollectChildren(SafeFileHandle parent, string parentName, List<SealEntry> entries,
        CancellationToken token, bool sealedTree, int depth)
    {
        if (depth > 32)
        { EmitSealGuard(new { reason = "depth", depth }); throw LinuxProducerWorkspaceLayout.Invalid(); }
        var expectedUid = sealedTree ? 0u : _layout.Accounts.SubjectUid;
        var expectedGid = sealedTree ? 0u : _layout.Accounts.SubjectGid;
        foreach (var name in ReadNames(parent, token))
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= LinuxProducerWorkspaceLayout.MaximumNodes)
            { EmitSealGuard(new { reason = "node-count", count = entries.Count }); throw LinuxProducerWorkspaceLayout.Invalid(); }
            SafeFileHandle? probe = null;
            SafeFileHandle? opened = null;
            try
            {
                probe = OpenAt2(Fd(parent), name, PathFlags, 0, ChildResolution);
                var sampled = LinuxProtectedMetadata.From(StatFd(probe));
                var type = sampled.Mode & 0xf000;
                var directory = type == 0x4000;
                if (!directory && type != 0x8000)
                { EmitSealGuard(new { reason = "node-type", mode = sampled.Mode }); throw LinuxProducerWorkspaceLayout.Invalid(); }
                RequireMutableMetadata(sampled, directory, expectedUid, expectedGid, sealedTree);
                opened = OpenAt2(Fd(parent), name, directory ? DirectoryFlags : ReadFileFlags, 0,
                    ChildResolution);
                var actual = LinuxProtectedMetadata.From(StatFd(opened));
                if (directory ? !actual.SameAncestorAs(sampled) : actual != sampled)
                { EmitSealGuard(new { reason = "opened-metadata", directory, sampled, actual }); throw LinuxProducerWorkspaceLayout.Invalid(); }
                var entry = new SealEntry(opened, parent, name, actual, directory, ownsHandle: true);
                entries.Add(entry);
                opened = null;
                if (directory) CollectChildren(entry.Handle, parentName + "/" + name, entries, token,
                    sealedTree, depth + 1);
            }
            finally { probe?.Dispose(); opened?.Dispose(); }
        }
    }

    private void SealEntryNode(SealEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RecheckEntry(entry, token, sealedTree: false);
        var permissions = entry.Directory ? PrivatePermissions : PrivateFilePermissions;
        if (Fchown(entry.Handle, 0, 0) != 0 || Fchmod(entry.Handle, permissions) != 0)
            throw LinuxProducerWorkspaceLayout.Invalid();
        var terminal = LinuxProtectedMetadata.From(StatFd(entry.Handle));
        RequireSealedMetadata(terminal, entry.Original, entry.Directory, permissions);
        RequireSameNamed(entry.Parent, entry.Name, terminal);
        entry.Terminal = terminal;
    }

    private void RecheckEntry(SealEntry entry, CancellationToken token, bool sealedTree)
    {
        token.ThrowIfCancellationRequested();
        var current = LinuxProtectedMetadata.From(StatFd(entry.Handle));
        if (entry.Directory ? !current.SameAncestorAs(entry.Original) : current != entry.Original)
            throw LinuxProducerWorkspaceLayout.Invalid();
        RequireMutableMetadata(current, entry.Directory, sealedTree ? 0 : _layout.Accounts.SubjectUid,
            sealedTree ? 0 : _layout.Accounts.SubjectGid, sealedTree);
        RequireSameNamed(entry.Parent, entry.Name, entry.Original);
    }

    private void VerifySealedTrees(CancellationToken token)
    {
        var entries = new List<SealEntry>();
        try { CollectMutableTrees(entries, token, sealedTree: true); }
        finally { CloseTemporaryEntries(entries); }
    }

    private void StoreSealedMetadata()
    {
        foreach (var node in _nodes.Where(node => node.Metadata.Uid == _layout.Accounts.SubjectUid))
        {
            if (node is null) continue;
            var value = LinuxProtectedMetadata.From(StatFd(node.Handle));
            LinuxProducerWorkspaceLayout.RequireDirectory(value, 0, 0, PrivatePermissions);
            node.SealedMetadata = value;
        }
    }

    private void RecheckCritical(CancellationToken token)
    {
        lock (_gate) RecheckCriticalCore(token);
    }

    private void RecheckNode(DirectoryNode node, CancellationToken token, bool allowMount = false)
    {
        token.ThrowIfCancellationRequested();
        var current = LinuxProtectedMetadata.From(StatFd(node.Handle));
        if (node.Ancestor) LinuxProducerWorkspaceLayout.RequireAncestor(current);
        else
        {
            LinuxProducerWorkspaceLayout.RequireDirectory(current, node.Uid, node.Gid, node.Permissions);
            if (!current.SameAncestorAs(node.Metadata)) throw LinuxProducerWorkspaceLayout.Invalid();
        }
        RequireSameNamed(node.Parent?.Handle, node.Name, node.Metadata, allowMount || node.AllowMount);
        token.ThrowIfCancellationRequested();
    }

    private void RecheckCriticalCore(CancellationToken token, bool cleanup = false)
    {
        if (!cleanup) CheckInput(token);
        if (_slash is null || _run is null || _root is null || _resultsRoot is null
            || _subjectRoot is null || _scratchRoot is null || _packageRoot is null || _handlesClosed)
            throw LinuxProducerWorkspaceLayout.Invalid();
        foreach (var node in _nodes)
        {
            token.ThrowIfCancellationRequested();
            var current = LinuxProtectedMetadata.From(StatFd(node.Handle));
            if (node.Ancestor) LinuxProducerWorkspaceLayout.RequireAncestor(current);
            else
            {
                var expected = (_sealed || _finalizingSeal) && node.SealedMetadata is { } terminal
                    ? terminal : node.Metadata;
                LinuxProducerWorkspaceLayout.RequireDirectory(current, expected.Uid, expected.Gid,
                    checked((ushort)(expected.Mode & 0x1ff)));
                if (!current.SameAncestorAs(expected)) throw LinuxProducerWorkspaceLayout.Invalid();
            }
            RequireSameNamed(node.Parent?.Handle, node.Name, node.Ancestor ? node.Metadata
                : (_sealed || _finalizingSeal) && node.SealedMetadata is { } sealedValue ? sealedValue : node.Metadata,
                node.AllowMount);
        }
        if (!cleanup) CheckInput(token);
    }

    private static void RequireMutableMetadata(LinuxProtectedMetadata value, bool directory,
        uint uid, uint gid, bool sealedTree)
    {
        if (!LinuxWorkspaceMetadata.MatchesMutableProducer(value, directory, uid, gid, sealedTree))
        {
            EmitSealGuard(new { reason = "node-metadata", directory, sealed_tree = sealedTree,
                expected_uid = uid, expected_gid = gid, actual_uid = value.Uid, actual_gid = value.Gid,
                mode = value.Mode, links = value.LinkCount, bytes = value.Length, inode = value.Inode });
            throw LinuxProducerWorkspaceLayout.Invalid();
        }
    }

    private static void RequireSealedMetadata(LinuxProtectedMetadata terminal, LinuxProtectedMetadata original,
        bool directory, ushort permissions)
    {
        if (!LinuxWorkspaceMetadata.MatchesProducerSeal(terminal, original, directory, permissions))
            throw LinuxProducerWorkspaceLayout.Invalid();
    }

    private static string[] ReadNames(SafeFileHandle directory, CancellationToken token)
    {
        var phase = "directory-open";
        string? parseFailure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            using var fresh = OpenAt2(Fd(directory), ".", DirectoryFlags, 0, ChildResolution);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var buffer = new byte[32 * 1024];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                phase = "getdents";
                var count = ReadDirectory(fresh, buffer);
                if (count == 0) break;
                phase = "directory-parse";
                foreach (var name in LinuxDirectoryData.ParseMutableWorkspace(buffer.AsSpan(0, count), out parseFailure))
                {
                    phase = "unique-directory-name";
                    if (names.Count >= LinuxProducerWorkspaceLayout.MaximumNodes || !names.Add(name))
                        throw LinuxProducerWorkspaceLayout.Invalid();
                }
            }
            return names.Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (Recoverable(error))
        {
            EmitSealGuard(new { reason = "directory-read", phase, parse_failure = parseFailure,
                error_kind = error is OperationCanceledException ? "Cancelled" : error is EvidenceAdmissionException ? "Admission" : error is IOException ? "Io" : "Other" });
            throw;
        }
    }

    private static void RequireExactNames(IReadOnlyCollection<string> actual, params string[] expected)
    {
        if (actual.Count != expected.Length || expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            EmitSealGuard(new { reason = "directory-names", actual_count = actual.Count, expected_count = expected.Length });
            throw LinuxProducerWorkspaceLayout.Invalid();
        }
    }

    private static void SetOwnershipAndMode(SafeFileHandle handle, uint uid, uint gid, ushort permissions)
    {
        if (Fchown(handle, uid, gid) != 0 || Fchmod(handle, permissions) != 0)
            throw LinuxProducerWorkspaceLayout.Invalid();
    }

    private static void RequireSameNamed(SafeFileHandle? parent, string name, LinuxProtectedMetadata expected,
        bool allowMount = false)
    {
        using var named = OpenAt2(parent is null ? -100 : Fd(parent), name, PathFlags, 0,
            parent is null ? 0 : allowMount ? ParentResolution : ChildResolution);
        var actual = LinuxProtectedMetadata.From(StatFd(named));
        if (!LinuxNamedMetadataComparison.Matches(actual, expected))
            throw LinuxProducerWorkspaceLayout.Invalid();
    }

    private void CheckInput(CancellationToken token) => _inputDeadline.Check(token);

    private void Quarantine() { lock (_gate) _quarantined = true; }

    private void CloseTemporaryEntries(IEnumerable<SealEntry> entries)
    {
        foreach (var entry in entries.Where(static entry => entry.OwnsHandle)) entry.Handle.Dispose();
    }

    private void CloseAllRetainedHandles()
    {
        foreach (var node in _nodes) node.Handle.Dispose();
    }

    /// <summary>Closes retained descriptors only after successful sealing and a repeated native sealed-tree check.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_handlesClosed) return;
            try
            {
                if (!_sealed || _quarantined || !_releaseGuardPassed)
                    throw LinuxProducerWorkspaceLayout.Invalid();
                _owner.RequireControlIdentity(default);
                RecheckCriticalCore(default, cleanup: true);
                VerifySealedTrees(default);
                CloseAllRetainedHandles();
                _handlesClosed = true;
            }
            catch (OperationCanceledException) { _quarantined = true; throw; }
            catch (Exception error) when (Recoverable(error))
            {
                _quarantined = true;
                throw LinuxProducerWorkspaceLayout.Invalid();
            }
        }
    }

    private static void CloseHandles(IEnumerable<SafeFileHandle> handles)
    {
        foreach (var handle in handles) handle.Dispose();
    }

    private static bool Recoverable(Exception error) => SupervisionExceptionPolicy.IsProducerWorkspaceRecoverable(error);

    private sealed class DirectoryNode(SafeFileHandle handle, DirectoryNode? parent, string name,
        uint uid, uint gid, ushort permissions, bool ancestor = false)
    {
        internal SafeFileHandle Handle { get; } = handle;
        internal DirectoryNode? Parent { get; } = parent;
        internal string Name { get; } = name;
        internal uint Uid { get; } = uid;
        internal uint Gid { get; } = gid;
        internal ushort Permissions { get; } = permissions;
        internal bool Ancestor { get; } = ancestor;
        internal bool AllowMount { get; } = ancestor && name == "run";
        internal LinuxProtectedMetadata Metadata { get; set; }
        internal LinuxProtectedMetadata? SealedMetadata { get; set; }
    }

    private sealed class SealEntry(SafeFileHandle handle, SafeFileHandle parent, string name,
        LinuxProtectedMetadata original, bool directory, bool ownsHandle)
    {
        internal SafeFileHandle Handle { get; } = handle;
        internal SafeFileHandle Parent { get; } = parent;
        internal string Name { get; } = name;
        internal LinuxProtectedMetadata Original { get; } = original;
        internal bool Directory { get; } = directory;
        internal bool OwnsHandle { get; } = ownsHandle;
        internal LinuxProtectedMetadata? Terminal { get; set; }
    }

    [DllImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static extern int Fchown(SafeFileHandle handle, uint uid, uint gid);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(SafeFileHandle handle, uint permissions);
}
