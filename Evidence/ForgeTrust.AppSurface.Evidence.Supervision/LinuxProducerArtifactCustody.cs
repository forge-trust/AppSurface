using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

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
    private const int MaximumDiscoveryNodes = 4096;
    private const uint DirectoryMode = 0x41c0; // 0700.
    private const uint ResultsParentMode = 0x41c9; // 0711.
    private const ulong PathFlags = 0x200000 | 0x80000; // O_PATH | O_CLOEXEC.
    private const ulong DirectoryFlags = 0x10000 | 0x80000; // O_DIRECTORY | O_CLOEXEC.
    private const ulong AncestorResolution = 0x02 | 0x04 | 0x08;
    private const ulong ChildResolution = AncestorResolution | 0x01;
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
            using var discovery = SelectedResultsRoot.Open(selected.Path, selected.Metadata, uid, gid,
                input.Deadline, token);
            var paths = DiscoverReports(discovery.Root, uid, gid, input.Deadline, token);
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
                if (!SameInventory(current, _inventory)) throw Invalid();
                CheckWork(token);
                return _inventory;
            }
            catch { _failed = true; throw; }
        }
    }

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
                if (report.Length == 0)
                {
                    var empty = reports.ReadChunk(relativePath, 0,
                        Math.Min(maximumBytes, LinuxRetainedReportSet.MaximumChunkBytes), token);
                    CheckWork(token);
                    return empty;
                }

                var length = checked((int)Math.Min(maximumBytes, report.Length - offset));
                var result = new byte[length];
                var copied = 0;
                while (copied < result.Length)
                {
                    CheckWork(token);
                    var requested = Math.Min(LinuxRetainedReportSet.MaximumChunkBytes, result.Length - copied);
                    var piece = reports.ReadChunk(relativePath, offset + copied, requested, token);
                    if (piece.Length != requested) throw Invalid();
                    piece.CopyTo(result, copied);
                    copied += piece.Length;
                }
                CheckWork(token);
                return result;
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

    private static string[] DiscoverReports(SafeFileHandle root, uint uid, uint gid,
        LinuxInputDeadline deadline, CancellationToken token)
    {
        var paths = new List<string>();
        long total = 0;
        var nodes = 1;
        Visit(root, string.Empty, 0, uid, gid, deadline, token, paths, ref nodes, ref total);
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static void Visit(SafeFileHandle directory, string prefix, int depth, uint uid, uint gid,
        LinuxInputDeadline deadline, CancellationToken token, List<string> paths, ref int nodes, ref long total)
    {
        deadline.Check(token);
        var before = LinuxProtectedMetadata.From(StatFd(directory));
        LinuxRetainedReportSet.RequireMetadata(before, true, uid, gid);
        var names = ReadNames(directory, before, deadline, token);
        foreach (var name in names)
        {
            deadline.Check(token);
            if (++nodes > MaximumDiscoveryNodes) throw Invalid();
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            if (path.Split('/').Length > 32 || Encoding.UTF8.GetByteCount(path) > 4095) throw Invalid();

            using var probe = OpenAt2(Fd(directory), name, PathFlags, 0, ChildResolution);
            var metadata = LinuxProtectedMetadata.From(StatFd(probe));
            deadline.Check(token);
            var type = metadata.Mode & 0xf000;
            if (type == 0x4000)
            {
                LinuxRetainedReportSet.RequireMetadata(metadata, true, uid, gid);
                using var child = OpenAt2(Fd(directory), name, DirectoryFlags, 0, ChildResolution);
                var opened = LinuxProtectedMetadata.From(StatFd(child));
                deadline.Check(token);
                if (!opened.SameAncestorAs(metadata)) throw Invalid();
                Visit(child, path, depth + 1, uid, gid, deadline, token, paths, ref nodes, ref total);
                RequireNamed(directory, name, metadata, directory: true, uid, gid, deadline, token);
                continue;
            }

            if (type != 0x8000 || !StringComparer.Ordinal.Equals(name, ReportName)) throw Invalid();
            LinuxRetainedReportSet.RequireMetadata(metadata, false, uid, gid);
            if (paths.Count >= LinuxRetainedReportSet.MaximumFiles) throw Invalid();
            total = LinuxRetainedReportSet.AddLength(total, metadata.Length);
            paths.Add(path);
            RequireNamed(directory, name, metadata, directory: false, uid, gid, deadline, token);
        }

        var after = LinuxProtectedMetadata.From(StatFd(directory));
        LinuxRetainedReportSet.RequireMetadata(after, true, uid, gid);
        if (!after.SameAncestorAs(before)
            || !ReadNames(directory, after, deadline, token).SequenceEqual(names, StringComparer.Ordinal)) throw Invalid();
        if (depth > 0 && names.Length == 0) throw Invalid();
        deadline.Check(token);
    }

    private static string[] ReadNames(SafeFileHandle directory, LinuxProtectedMetadata expected,
        LinuxInputDeadline deadline, CancellationToken token)
    {
        deadline.Check(token);
        using var fresh = OpenAt2(Fd(directory), ".", DirectoryFlags, 0, ChildResolution);
        var opened = LinuxProtectedMetadata.From(StatFd(fresh));
        if (!opened.SameAncestorAs(expected)) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new byte[32 * 1024];
        var blocks = 0;
        while (true)
        {
            deadline.Check(token);
            if (++blocks > MaximumDiscoveryNodes) throw Invalid();
            var count = ReadDirectory(fresh, buffer);
            deadline.Check(token);
            if (count == 0) break;
            foreach (var name in LinuxDirectoryData.Parse(buffer.AsSpan(0, count)))
                if (names.Count >= MaximumDiscoveryNodes || !names.Add(name)) throw Invalid();
        }
        deadline.Check(token);
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    private static void RequireNamed(SafeFileHandle parent, string name, LinuxProtectedMetadata expected,
        bool directory, uint uid, uint gid, LinuxInputDeadline deadline, CancellationToken token)
    {
        deadline.Check(token);
        using var named = OpenAt2(Fd(parent), name, PathFlags, 0, ChildResolution);
        var current = LinuxProtectedMetadata.From(StatFd(named));
        deadline.Check(token);
        if (directory)
        {
            LinuxRetainedReportSet.RequireMetadata(current, true, uid, gid);
            if (!current.SameAncestorAs(expected)) throw Invalid();
        }
        else if (!SameFileSample(current, expected)) throw Invalid();
    }

    private static bool SameFileSample(LinuxProtectedMetadata left, LinuxProtectedMetadata right) =>
        left.DeviceMajor == right.DeviceMajor && left.DeviceMinor == right.DeviceMinor && left.Inode == right.Inode
        && left.Uid == right.Uid && left.Gid == right.Gid && left.Mode == right.Mode
        && left.LinkCount == right.LinkCount && left.Length == right.Length;

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

    private static bool SameInventory(IReadOnlyList<LinuxRetainedReportSet.ReportInfo> left,
        IReadOnlyList<LinuxRetainedReportSet.ReportInfo> right) =>
        left.Count == right.Count && left.SequenceEqual(right);

    private static void RequireResultsParent(LinuxProtectedMetadata metadata)
    {
        metadata.RequireAncestor(false);
        if (metadata.Gid != 0 || metadata.Mode != ResultsParentMode || metadata.LinkCount == 0) throw Invalid();
    }

    private static void RequireSelectedRoot(LinuxProtectedMetadata metadata, LinuxProtectedMetadata expected,
        uint uid, uint gid)
    {
        LinuxRetainedReportSet.RequireRootIdentity(metadata, expected, uid, gid);
        if (metadata.Mode != DirectoryMode) throw Invalid();
    }

    private static IOException Invalid() => new("The producer report set is unavailable or changed.");
    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private sealed class SelectedResultsRoot : IDisposable
    {
        private readonly List<DirectoryIdentity> _chain;
        private readonly uint _uid;
        private readonly uint _gid;
        private bool _closed;

        private SelectedResultsRoot(List<DirectoryIdentity> chain, uint uid, uint gid)
        { _chain = chain; _uid = uid; _gid = gid; }

        internal SafeFileHandle Root => _chain[^1].Handle;
        internal LinuxProtectedMetadata Metadata => _chain[^1].Metadata;

        internal static SelectedResultsRoot Open(string path, LinuxProtectedMetadata expectedRoot,
            uint uid, uint gid, LinuxInputDeadline deadline, CancellationToken token)
        {
            LinuxProtectedDeployment.ValidateAbsolute(path);
            LinuxRetainedReportSet.RequireRootIdentity(expectedRoot, expectedRoot, uid, gid);
            if (expectedRoot.Mode != DirectoryMode) throw Invalid();
            var chain = new List<DirectoryIdentity>();
            SafeFileHandle? pending = null;
            try
            {
                deadline.Check(token);
                pending = OpenAt2(-100, "/", DirectoryFlags, 0, 0);
                var rootMetadata = LinuxProtectedMetadata.From(StatFd(pending));
                deadline.Check(token);
                rootMetadata.RequireAncestor(false);
                chain.Add(new(pending, null, "/", rootMetadata, IsSelectedRoot: false));
                pending = null;
                var parts = path[1..].Split('/');
                if (parts.Length < 2) throw Invalid();
                for (var index = 0; index < parts.Length; index++)
                {
                    deadline.Check(token);
                    var parent = chain[^1];
                    pending = OpenAt2(Fd(parent.Handle), parts[index], DirectoryFlags, 0, AncestorResolution);
                    var metadata = LinuxProtectedMetadata.From(StatFd(pending));
                    deadline.Check(token);
                    if (index == parts.Length - 1)
                    {
                        RequireSelectedRoot(metadata, expectedRoot, uid, gid);
                    }
                    else if (index == parts.Length - 2)
                    {
                        RequireResultsParent(metadata);
                    }
                    else
                    {
                        metadata.RequireAncestor(false);
                    }
                    chain.Add(new(pending, parent, parts[index], metadata, index == parts.Length - 1));
                    pending = null;
                }
                var result = new SelectedResultsRoot(chain, uid, gid);
                result.Recheck(deadline, token);
                return result;
            }
            catch
            {
                pending?.Dispose();
                foreach (var node in chain.AsEnumerable().Reverse()) node.Handle.Dispose();
                throw;
            }
        }

        internal void Recheck(LinuxInputDeadline deadline, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            foreach (var node in _chain)
            {
                deadline.Check(token);
                var current = LinuxProtectedMetadata.From(StatFd(node.Handle));
                deadline.Check(token);
                if (node.IsSelectedRoot)
                {
                    RequireSelectedRoot(current, node.Metadata, _uid, _gid);
                }
                else
                {
                    current.RequireAncestor(false);
                    if (!current.SameAncestorAs(node.Metadata)) throw Invalid();
                }
                if (node.Parent is not null)
                {
                    using var named = OpenAt2(Fd(node.Parent.Handle), node.Name, PathFlags, 0, AncestorResolution);
                    var namedMetadata = LinuxProtectedMetadata.From(StatFd(named));
                    deadline.Check(token);
                    if (!namedMetadata.SameAncestorAs(node.Metadata)) throw Invalid();
                }
            }
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            foreach (var node in _chain.AsEnumerable().Reverse()) node.Handle.Dispose();
        }

        private sealed record DirectoryIdentity(SafeFileHandle Handle, DirectoryIdentity? Parent,
            string Name, LinuxProtectedMetadata Metadata, bool IsSelectedRoot);
    }
}
