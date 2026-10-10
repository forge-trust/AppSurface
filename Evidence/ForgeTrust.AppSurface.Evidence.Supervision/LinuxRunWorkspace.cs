using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>One fixed directory policy, as data only.</summary>
/// <param name="Name">Fixed child name; empty selects the generation root.</param>
/// <param name="ParentName">Fixed parent policy name; null selects the retained /run ancestor.</param>
/// <param name="Uid">Required owner.</param>
/// <param name="Gid">Required group.</param>
/// <param name="Permissions">Exact permission bits, including absence of extra special bits.</param>
internal sealed record LinuxWorkspaceDirectoryPolicy(string Name, string? ParentName,
    uint Uid, uint Gid, ushort Permissions);

/// <summary>Closed generation layout and metadata guards. None of these data APIs creates a workspace or account.</summary>
internal sealed class LinuxWorkspaceLayout
{
    /// <summary>Maximum descriptor bytes, counted before copying or native creation.</summary>
    internal const int MaximumDescriptorBytes = 64 * 1024;
    /// <summary>Fixed descriptor filename.</summary>
    internal const string DescriptorName = "worker-control.json";
    /// <summary>Fixed worker-owned exclusive artifact allocation slot; workspace creation does not allocate it.</summary>
    internal const string ArtifactSlot = "evidence";

    private LinuxWorkspaceLayout(Guid id, LinuxRunAccountSnapshot accounts)
    {
        AccountData = accounts with { };
        Name = "appsurface-evidence-" + id.ToString("N");
        Root = "/run/" + Name;
        GenerationDirectory = new(string.Empty, null, 0, accounts.WorkerGid, 0x1e8); // 0750.
        ControlDirectory = new("worker", string.Empty, 0, accounts.WorkerGid, 0x1c8); // 0710.
        BrokerDirectory = new("broker", "worker", 0, accounts.WorkerGid, 0x1c8); // 0710.
        OutputDirectory = new("output", string.Empty, accounts.WorkerUid, accounts.WorkerGid, 0x1c0); // 0700.
        RawResultsDirectory = new("raw-results", string.Empty, accounts.SubjectUid, accounts.ResultsGid, 0x1c8);
        Directories = Array.AsReadOnly(new[]
        {
            GenerationDirectory, ControlDirectory, BrokerDirectory, OutputDirectory, RawResultsDirectory,
        });
        WorkerGid = accounts.WorkerGid;
        if (Encoding.UTF8.GetByteCount(ControlSocket) > 100) throw Invalid();
    }

    /// <summary>Gets the fixed generation component.</summary>
    internal string Name { get; }
    /// <summary>Gets the root-selected absolute generation path.</summary>
    internal string Root { get; }
    /// <summary>Gets the fixed control root, disjoint from the worker-owned output parent.</summary>
    internal string ControlRoot => Root + "/worker";
    /// <summary>Gets the bounded listener path; this does not bind a socket.</summary>
    internal string ControlSocket => ControlRoot + "/broker/control.sock";
    /// <summary>Gets the fixed descriptor path.</summary>
    internal string DescriptorPath => ControlRoot + "/" + DescriptorName;
    /// <summary>Gets the worker's exclusively prepared 0700 artifact parent.</summary>
    internal string OutputParent => Root + "/output";
    /// <summary>Gets the subject-owned results directory, not a claim of subject traversal through the outer root.</summary>
    internal string RawResultsRoot => Root + "/raw-results";
    /// <summary>Gets the copied selected worker group.</summary>
    internal uint WorkerGid { get; }
    /// <summary>Gets the complete copied account comparison data, including the subject's separate primary group.</summary>
    /// <remarks>Directory policies do not contain every group. This immutable value issues no actual account holder.</remarks>
    internal LinuxRunAccountSnapshot AccountData { get; }
    /// <summary>Gets the fixed generation policy beneath retained /run.</summary>
    internal LinuxWorkspaceDirectoryPolicy GenerationDirectory { get; }
    /// <summary>Gets the fixed root-owned worker control policy beneath the generation.</summary>
    internal LinuxWorkspaceDirectoryPolicy ControlDirectory { get; }
    /// <summary>Gets the fixed broker policy beneath worker control, not beneath output.</summary>
    internal LinuxWorkspaceDirectoryPolicy BrokerDirectory { get; }
    /// <summary>Gets the fixed worker-owned output policy beneath the generation.</summary>
    internal LinuxWorkspaceDirectoryPolicy OutputDirectory { get; }
    /// <summary>Gets the fixed subject-owned raw-result policy beneath the generation.</summary>
    internal LinuxWorkspaceDirectoryPolicy RawResultsDirectory { get; }
    /// <summary>Gets a detached read-only list of exact directory policies.</summary>
    internal ReadOnlyCollection<LinuxWorkspaceDirectoryPolicy> Directories { get; }

    /// <summary>Validates distinct nonroot identity data and derives only the fixed /run layout.</summary>
    internal static LinuxWorkspaceLayout Create(Guid id, LinuxRunAccountSnapshot accounts)
    {
        if (id == Guid.Empty || accounts is null || accounts.WorkerUid is 0 or uint.MaxValue
            || accounts.SubjectUid is 0 or uint.MaxValue || accounts.WorkerUid == accounts.SubjectUid
            || accounts.WorkerGid is 0 or uint.MaxValue || accounts.SubjectGid is 0 or uint.MaxValue
            || accounts.ResultsGid is 0 or uint.MaxValue || accounts.WorkerGid == accounts.SubjectGid
            || accounts.WorkerGid == accounts.ResultsGid || accounts.SubjectGid == accounts.ResultsGid)
            throw Invalid();
        return new(id, accounts);
    }

    /// <summary>Requires an exact retained directory policy, ignoring child-list metadata only.</summary>
    internal static void RequireDirectory(LinuxProtectedMetadata value, LinuxWorkspaceDirectoryPolicy policy)
    {
        if (policy is null || value.Inode == 0 || value.Uid != policy.Uid || value.Gid != policy.Gid
            || value.Mode != (0x4000 | policy.Permissions)) throw Invalid();
    }

    /// <summary>Checks the fixed policy-parent relation as data only, without opening or selecting a path.</summary>
    internal static void RequireParent(LinuxWorkspaceDirectoryPolicy policy, LinuxWorkspaceDirectoryPolicy? parent)
    {
        if (policy is null || (policy.ParentName is null ? parent is not null : parent?.Name != policy.ParentName))
            throw Invalid();
    }

    /// <summary>Requires root-owned searchable / and /run ancestors without group/other write.</summary>
    internal static void RequireAncestor(LinuxProtectedMetadata value)
    {
        if (value.Uid != 0 || value.Gid != 0) throw Invalid();
        value.RequireAncestor(sharedTraversal: true);
    }

    /// <summary>Requires the sealed descriptor's root owner, worker read group, single link and exact size.</summary>
    internal static void RequireDescriptor(LinuxProtectedMetadata value, uint workerGid, int bytes)
    {
        if (bytes is < 1 or > MaximumDescriptorBytes || workerGid is 0 or uint.MaxValue
            || value.Inode == 0 || value.Uid != 0 || value.Gid != workerGid || value.Mode != 0x8120
            || value.LinkCount != 1 || value.Length != (ulong)bytes) throw Invalid(); // Regular 0440.
    }

    /// <summary>Copies bounded root-selected bytes. JSON grammar and admission are separate caller obligations.</summary>
    internal static byte[] CopyDescriptor(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 1 or > MaximumDescriptorBytes) throw Invalid();
        return bytes.ToArray();
    }

    /// <summary>Creates a fixed failure with no selected path, account name, bytes or inner exception.</summary>
    internal static EvidenceAdmissionException Invalid() =>
        new("ASEVD402", "The protected run workspace is unavailable or changed.");
}

/// <summary>Fresh retained /run workspace owned by an actual armed owner and its actual accounts.</summary>
/// <remarks>
/// Never adopts an existing generation or child, and never removes worker artifacts. Creation/descriptor failure
/// preserves partial paths for quarantine; the caller must preserve account ownership and custody too. Creation
/// requires the account holder's actual RequireOwnedBy validation. No caller pathname selects a directory.
/// Descriptor publication is one attempt, even after failure. Publish after actual worker PID capture and before ready authentication.
/// The descriptor and broker are beneath the root-owned worker control directory, disjoint from output as
/// required by the existing worker descriptor grammar. Fixed parent policies select no caller-controlled path.
/// Readers, listeners and consumers must physically join before Dispose; closing FDs proves no account/group exit.
/// The subject cannot traverse the 0750 root with its separate groups. Future subject access to raw-results needs
/// genuine retained-descriptor/namespace composition, not relaxed modes or a new shared worker write permission.
/// Socket binding and root:worker 0660 sealing remain a separate owned-listener composition step.
/// </remarks>
internal sealed partial class LinuxRunWorkspace : IDisposable
{
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong PathFlags = 0x200000 | 0x80000;
    private const ulong ParentResolution = 0x02 | 0x04 | 0x08;
    private const ulong ChildResolution = ParentResolution | 0x01;
    private readonly object _gate = new();
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxWorkspaceLayout _layout;
    private readonly List<DirectoryNode> _nodes;
    private readonly SupervisionSingleAttempt _listenerCreation = new();
    private Task<RootCustody>? _custodyExecution;
    private EvidenceProtectedLaunchInput? _custodyInput;
    private LinuxWorkerProcess? _custodyWorker;
    private LinuxEmptyObservationControlServer? _custodyServer;
    private RootCustody? _activeCustody;
    private bool _custodyStarted;
    private DirectoryNode? _controlRoot;
    private DirectoryNode? _brokerDirectory;
    private DirectoryNode? _outputParent;
    private SafeFileHandle? _descriptor;
    private LinuxProtectedMetadata _descriptorMetadata;
    private string? _descriptorHash;
    private bool _descriptorAttempted;
    private bool _quarantined;
    private bool _closed;

    private LinuxRunWorkspace(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        LinuxWorkspaceLayout layout, List<DirectoryNode> nodes)
    { _owner = owner; _accounts = accounts; _layout = layout; _nodes = nodes; }

    /// <summary>Gets the selected generation path; it grants no authority to reconstruct this owner.</summary>
    internal string RootPath => _layout.Root;
    /// <summary>Gets the fixed bounded UNIX socket path, without creating or adopting a listener.</summary>
    internal string ControlSocket => _layout.ControlSocket;
    /// <summary>Gets the fixed descriptor filename path; publication status must be checked separately.</summary>
    internal string DescriptorPath => _layout.DescriptorPath;
    /// <summary>Gets the actually prepared worker-owned 0700 artifact parent path.</summary>
    internal string OutputParent => _layout.OutputParent;
    /// <summary>Gets the fixed allocation component, not an allocated artifact-root lease.</summary>
    internal string OutputSlot => LinuxWorkspaceLayout.ArtifactSlot;
    /// <summary>Gets the prepared raw result path; no subject traversal is inferred.</summary>
    internal string RawResultsRoot => _layout.RawResultsRoot;
    /// <summary>Gets immutable layout comparison data; it cannot reconstruct native workspace ownership.</summary>
    internal LinuxWorkspaceLayout Layout => _layout;
    /// <summary>Gets the measured output-parent identity for the existing exclusive allocator.</summary>
    internal EvidenceLinuxArtifactIdentity OutputParentIdentity
    {
        get
        {
            var value = (_outputParent ?? throw LinuxWorkspaceLayout.Invalid()).Metadata;
            return new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Uid, value.Gid);
        }
    }
    /// <summary>Gets whether a single descriptor attempt completed all sealing and byte rechecks.</summary>
    internal bool DescriptorWritten { get { lock (_gate) return !_closed && !_quarantined && _descriptor is not null; } }
    /// <summary>Gets the actual sealed byte digest as data, or null before successful publication.</summary>
    internal string? DescriptorSha256 { get { lock (_gate) return _descriptorHash; } }
    /// <summary>Gets whether use has irreversibly failed; preserved paths may not be recursively deleted.</summary>
    internal bool IsQuarantined { get { lock (_gate) return _quarantined; } }

    /// <summary>Exclusively creates this generation, its three children and the nested broker under protected /run.</summary>
    /// <remarks>Any failure closes all obtained FDs and preserves partial generation paths; it never chmods a collision.</remarks>
    internal static LinuxRunWorkspace Create(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts);
        var nodes = new List<DirectoryNode>();
        try
        {
            LinuxProtectedDeployment.RequirePlatform();
            owner.RequireActive(token);
            accounts.RequireOwnedBy(owner, token);
            var layout = LinuxWorkspaceLayout.Create(owner.RunId, new(accounts.WorkerUid, accounts.WorkerGid,
                accounts.SubjectUid, accounts.SubjectGid, accounts.ResultsGid));
            var result = new LinuxRunWorkspace(owner, accounts, layout, nodes);
            accounts.RetainWorkspaceForCustody(result, owner); // Retain before the first directory mutation.
            var slash = result.AddAncestor(null, "/", token);
            var run = result.AddAncestor(slash, "run", token);
            var generation = result.CreateDirectory(run, layout.GenerationDirectory, token);
            var control = result.CreateDirectory(generation, layout.ControlDirectory, token);
            var broker = result.CreateDirectory(control, layout.BrokerDirectory, token);
            var output = result.CreateDirectory(generation, layout.OutputDirectory, token);
            result.CreateDirectory(generation, layout.RawResultsDirectory, token);
            result._controlRoot = control;
            result._brokerDirectory = broker;
            result._outputParent = output;
            result.Recheck(token);
            nodes = []; // Ownership transfers only after all fixed policies and names recheck.
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (Recoverable(error)) { throw LinuxWorkspaceLayout.Invalid(); }
        finally { Close(nodes.Select(static node => node.Handle)); }
    }

    /// <summary>Checks only the private original references before accounts reserve this pending workspace.</summary>
    /// <remarks>No I/O, directory creation, custody transfer or account release is established by this check.</remarks>
    internal void RequireAccountOwner(LinuxRunAccounts accounts, LinuxOwnerActivation owner)
    {
        if (!ReferenceEquals(accounts, _accounts) || !ReferenceEquals(owner, _owner)) throw LinuxWorkspaceLayout.Invalid();
    }

    /// <summary>Claims the one listener attempt and opens its retained, fixed broker parent for the actual owner.</summary>
    /// <remarks>
    /// Claims before opening; failure prevents retry. Only the same authenticated owner and account holder may
    /// obtain this handle. The listener must own and close it after its actual accepts and connection closes join.
    /// This does not bind a socket, publish a descriptor, or admit the worker. A collision is never unlinked.
    /// </remarks>
    internal SafeFileHandle ClaimListenerParent(LinuxOwnerActivation owner, LinuxRunAccounts accounts,
        CancellationToken token)
    {
        lock (_gate)
        {
            SafeFileHandle? handle = null;
            try
            {
                RequireOwnedBy(owner, accounts, token);
                _listenerCreation.Claim();
                var parent = _controlRoot ?? throw LinuxWorkspaceLayout.Invalid();
                var broker = _brokerDirectory ?? throw LinuxWorkspaceLayout.Invalid();
                handle = OpenAt2(Fd(parent.Handle), broker.Name, DirectoryFlags, 0, ChildResolution);
                var current = LinuxProtectedMetadata.From(StatFd(handle));
                LinuxWorkspaceLayout.RequireDirectory(current, _layout.BrokerDirectory);
                if (!current.SameAncestorAs(broker.Metadata)) throw LinuxWorkspaceLayout.Invalid();
                RequireOwnedBy(owner, accounts, token);
                var result = handle; handle = null;
                return result;
            }
            catch { _quarantined = true; throw; }
            finally { handle?.Dispose(); }
        }
    }

    /// <summary>Rechecks this actual workspace and its original owner/accounts; data cannot reconstruct this binding.</summary>
    /// <remarks>Use before and after listener binding and every worker-sensitive operation; cleanup custody is separate.</remarks>
    internal void RequireOwnedBy(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(accounts, _accounts))
                throw LinuxWorkspaceLayout.Invalid();
            Recheck(token);
        }
    }

    /// <summary>Rechecks the same native workspace for transport continuity after work admission closes.</summary>
    /// <remarks>
    /// Does not clear quarantine, alter permissions, release custody or publish a descriptor. An I/O
    /// cancellation supplies no successful check; native checks use the original owner's deadline.
    /// </remarks>
    internal void RequireControlOwnedBy(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(accounts, _accounts))
                throw LinuxWorkspaceLayout.Invalid();
            try { RecheckCore(default, InspectionPurpose.Control); }
            catch (Exception error) when (Recoverable(error))
            { _quarantined = true; throw LinuxWorkspaceLayout.Invalid(); }
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Exclusively writes root-selected descriptor bytes as 0640, fsyncs, seals 0440 and retains a reader.</summary>
    /// <remarks>The attempt is latched before native dispatch. Failure quarantines and forbids retry/worker launch.</remarks>
    internal void WriteDescriptor(byte[] bytes, CancellationToken token)
    {
        lock (_gate)
        {
            SafeFileHandle? writer = null;
            SafeFileHandle? reader = null;
            try
            {
                Check(token);
                var snapshot = LinuxWorkspaceLayout.CopyDescriptor(bytes);
                if (_descriptorAttempted) throw LinuxWorkspaceLayout.Invalid();
                _descriptorAttempted = true;
                RecheckDirectories(token);
                var parent = _controlRoot ?? throw LinuxWorkspaceLayout.Invalid();
                writer = OpenAt2(Fd(parent.Handle), LinuxWorkspaceLayout.DescriptorName,
                    0x01 | 0x40 | 0x80 | 0x800 | 0x20000 | 0x80000, 0x1a0, ChildResolution);
                var created = LinuxProtectedMetadata.From(StatFd(writer));
                if (created.Uid != 0 || created.Gid != 0 || created.Inode == 0 || created.Mode >> 12 != 8
                    || created.LinkCount != 1 || created.Length != 0) throw LinuxWorkspaceLayout.Invalid();
                RequireSameNamed(parent.Handle, LinuxWorkspaceLayout.DescriptorName, created);
                Check(token);
                if (Fchown(writer, 0, _layout.WorkerGid) != 0 || Fchmod(writer, 0x1a0) != 0)
                    throw LinuxWorkspaceLayout.Invalid();
                for (var offset = 0; offset < snapshot.Length; offset += 4096)
                {
                    Check(token);
                    RandomAccess.Write(writer, snapshot.AsSpan(offset, Math.Min(4096, snapshot.Length - offset)), offset);
                    Check(token);
                }
                if (Fsync(writer) != 0) throw LinuxWorkspaceLayout.Invalid();
                Check(token);
                if (Fchmod(writer, 0x120) != 0) throw LinuxWorkspaceLayout.Invalid();
                var sealedMetadata = LinuxProtectedMetadata.From(StatFd(writer));
                LinuxWorkspaceLayout.RequireDescriptor(sealedMetadata, _layout.WorkerGid, snapshot.Length);
                RequireSameNamed(parent.Handle, LinuxWorkspaceLayout.DescriptorName, sealedMetadata);
                reader = OpenAt2(Fd(parent.Handle), LinuxWorkspaceLayout.DescriptorName,
                    0x80000 | 0x800, 0, ChildResolution);
                if (LinuxProtectedMetadata.From(StatFd(reader)) != sealedMetadata) throw LinuxWorkspaceLayout.Invalid();
                var hash = HashDescriptor(reader, sealedMetadata, token);
                if (hash != Convert.ToHexStringLower(SHA256.HashData(snapshot))) throw LinuxWorkspaceLayout.Invalid();
                writer.Dispose(); writer = null;
                RecheckDirectories(token);
                RequireSameNamed(parent.Handle, LinuxWorkspaceLayout.DescriptorName, sealedMetadata);
                Check(token);
                _descriptorMetadata = sealedMetadata; _descriptorHash = hash; _descriptor = reader; reader = null;
            }
            catch (OperationCanceledException) { _quarantined = true; throw; }
            catch (Exception error) when (Recoverable(error))
            { _quarantined = true; throw LinuxWorkspaceLayout.Invalid(); }
            finally
            {
                var closed = Close(new[] { writer, reader }.OfType<SafeFileHandle>());
                if (!closed) _quarantined = true;
            }
        }
    }

    /// <summary>Rechecks actual directory/name policy and sealed descriptor bytes against the original owner deadline.</summary>
    internal void Recheck(CancellationToken token)
    {
        lock (_gate)
        {
            try
            {
                RecheckCore(token, InspectionPurpose.Work);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _owner.CloseWorkAdmission();
                throw;
            }
            catch (Exception error) when (Recoverable(error))
            { _quarantined = true; throw LinuxWorkspaceLayout.Invalid(); }
        }
    }

    private void RecheckCore(CancellationToken token, InspectionPurpose purpose)
    {
        Check(token, purpose);
        RecheckDirectories(token, purpose);
        if (_descriptor is not null)
        {
            LinuxWorkspaceLayout.RequireDescriptor(_descriptorMetadata, _layout.WorkerGid,
                checked((int)_descriptorMetadata.Length));
            var parent = _controlRoot ?? throw LinuxWorkspaceLayout.Invalid();
            RequireSameNamed(parent.Handle, LinuxWorkspaceLayout.DescriptorName, _descriptorMetadata);
            if (HashDescriptor(_descriptor, _descriptorMetadata, token, purpose) != _descriptorHash)
                throw LinuxWorkspaceLayout.Invalid();
            RequireSameNamed(parent.Handle, LinuxWorkspaceLayout.DescriptorName, _descriptorMetadata);
        }
        RecheckDirectories(token, purpose);
        Check(token, purpose);
    }

    /// <summary>Irreversibly closes workspace use without deleting any directory, descriptor or artifact.</summary>
    internal void Quarantine() { lock (_gate) _quarantined = true; }

    /// <summary>Closes all retained FDs after users join; keeps paths and artifacts for root custody.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_custodyStarted && _custodyExecution?.IsCompleted != true)
                throw LinuxWorkspaceLayout.Invalid();
            CloseOriginalHandles();
        }
    }

    private void CloseOriginalForCustody(RootCustody custody)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(custody, _activeCustody)) throw LinuxWorkspaceLayout.Invalid();
            CloseOriginalHandles();
        }
    }

    // Caller owns the workspace bookkeeping lock. The native transfer alone may close these
    // original handles while its independently retained tree remains pending.
    private void CloseOriginalHandles()
    {
            if (_closed) return;
            _closed = true;
            var handles = _nodes.Select(static node => node.Handle).ToList();
            if (_descriptor is not null) handles.Add(_descriptor);
            if (_n09AllocationSlot is not null)
            {
                // Defensive owner-lifetime fallback. The N09 execution normally closes these
                // after joins and before custody; custody still attempts them before account release.
                handles.Add(_n09AllocationSlot.Slot);
                handles.Add(_n09AllocationSlot.Parent);
                _n09AllocationSlot = null;
            }
            if (!Close(handles)) { _quarantined = true; throw LinuxWorkspaceLayout.Invalid(); }
    }

    private DirectoryNode AddAncestor(DirectoryNode? parent, string name, CancellationToken token)
    {
        Check(token);
        var handle = OpenAt2(parent is null ? -100 : Fd(parent.Handle), name,
            DirectoryFlags, 0, parent is null ? 0 : ParentResolution);
        var node = new DirectoryNode(handle, parent, name, null);
        _nodes.Add(node); // Retain before stat or policy failure.
        node.Metadata = LinuxProtectedMetadata.From(StatFd(handle));
        LinuxWorkspaceLayout.RequireAncestor(node.Metadata);
        Check(token);
        return node;
    }

    private DirectoryNode CreateDirectory(DirectoryNode parent, LinuxWorkspaceDirectoryPolicy policy,
        CancellationToken token)
    {
        Check(token); RecheckDirectories(token);
        LinuxWorkspaceLayout.RequireParent(policy, parent.Policy);
        if (policy.ParentName is null && (parent.Name != "run" || parent.Parent?.Name != "/"))
            throw LinuxWorkspaceLayout.Invalid();
        var name = policy.ParentName is null ? _layout.Name : policy.Name;
        if (MkdirAt(Fd(parent.Handle), name, 0x1c0) != 0) throw LinuxWorkspaceLayout.Invalid();
        Check(token);
        var handle = OpenAt2(Fd(parent.Handle), name, DirectoryFlags, 0, ChildResolution);
        var node = new DirectoryNode(handle, parent, name, policy);
        _nodes.Add(node);
        var created = LinuxProtectedMetadata.From(StatFd(handle));
        if (created.Inode == 0 || created.Uid != 0 || created.Gid != 0 || created.Mode >> 12 != 4
            || (created.Mode & 0x0fff & ~0x1c0) != 0) throw LinuxWorkspaceLayout.Invalid();
        RequireSameNamed(parent.Handle, name, created);
        Check(token);
        if (Fchown(handle, policy.Uid, policy.Gid) != 0 || Fchmod(handle, policy.Permissions) != 0)
            throw LinuxWorkspaceLayout.Invalid();
        node.Metadata = LinuxProtectedMetadata.From(StatFd(handle));
        LinuxWorkspaceLayout.RequireDirectory(node.Metadata, policy);
        RequireSameNamed(parent.Handle, name, node.Metadata);
        Check(token);
        return node;
    }

    private void RecheckDirectories(CancellationToken token, InspectionPurpose purpose = InspectionPurpose.Work)
    {
        foreach (var node in _nodes)
        {
            Check(token, purpose);
            var current = LinuxProtectedMetadata.From(StatFd(node.Handle));
            if (node.Policy is null) LinuxWorkspaceLayout.RequireAncestor(current);
            else LinuxWorkspaceLayout.RequireDirectory(current, node.Policy);
            if (!current.SameAncestorAs(node.Metadata)) throw LinuxWorkspaceLayout.Invalid();
            if (node.Parent is null)
            {
                using var named = OpenAt2(-100, "/", PathFlags, 0, 0);
                if (!LinuxProtectedMetadata.From(StatFd(named)).SameAncestorAs(node.Metadata))
                    throw LinuxWorkspaceLayout.Invalid();
            }
            else RequireSameNamed(node.Parent.Handle, node.Name, node.Metadata, node.Policy is null);
            Check(token, purpose);
        }
    }

    private static void RequireSameNamed(SafeFileHandle parent, string name,
        LinuxProtectedMetadata expected, bool allowMount = false)
    {
        using var named = OpenAt2(Fd(parent), name, PathFlags, 0, allowMount ? ParentResolution : ChildResolution);
        var current = LinuxProtectedMetadata.From(StatFd(named));
        if ((expected.Mode & 0xf000) == 0x4000
            ? !current.SameAncestorAs(expected) : current != expected) throw LinuxWorkspaceLayout.Invalid();
    }

    private string HashDescriptor(SafeFileHandle file, LinuxProtectedMetadata expected, CancellationToken token,
        InspectionPurpose purpose = InspectionPurpose.Work)
    {
        Check(token, purpose);
        if (LinuxProtectedMetadata.From(StatFd(file)) != expected) throw LinuxWorkspaceLayout.Invalid();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4096];
        long offset = 0;
        while ((ulong)offset < expected.Length)
        {
            Check(token, purpose);
            var count = RandomAccess.Read(file, buffer.AsSpan(0,
                (int)Math.Min((ulong)buffer.Length, expected.Length - (ulong)offset)), offset);
            if (count == 0) throw LinuxWorkspaceLayout.Invalid();
            hash.AppendData(buffer.AsSpan(0, count)); offset += count;
            Check(token, purpose);
        }
        if (RandomAccess.Read(file, buffer.AsSpan(0, 1), offset) != 0
            || LinuxProtectedMetadata.From(StatFd(file)) != expected) throw LinuxWorkspaceLayout.Invalid();
        Check(token, purpose);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private void Check(CancellationToken token, InspectionPurpose purpose = InspectionPurpose.Work)
    {
        if (_closed || _quarantined || _custodyStarted) throw LinuxWorkspaceLayout.Invalid();
        token.ThrowIfCancellationRequested();
        if (purpose == InspectionPurpose.Work)
        {
            _owner.RequireActive(token);
            _accounts.RequireOwnedBy(_owner, token);
        }
        else
        {
            _owner.RequireControlIdentity(token);
            _accounts.RequireControlOwnedBy(_owner, token);
        }
        token.ThrowIfCancellationRequested();
    }

    private enum InspectionPurpose { Work, Control }

    private static bool Close(IEnumerable<SafeFileHandle> handles)
    {
        var success = true;
        foreach (var handle in handles.Reverse())
        {
            try { handle.Dispose(); }
            catch (Exception error) when (Recoverable(error)) { success = false; }
        }
        return success;
    }
    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private sealed class DirectoryNode(SafeFileHandle handle, DirectoryNode? parent, string name,
        LinuxWorkspaceDirectoryPolicy? policy)
    {
        internal SafeFileHandle Handle { get; } = handle;
        internal DirectoryNode? Parent { get; } = parent;
        internal string Name { get; } = name;
        internal LinuxWorkspaceDirectoryPolicy? Policy { get; } = policy;
        internal LinuxProtectedMetadata Metadata { get; set; }
    }
    [DllImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static extern int Fchown(SafeFileHandle handle, uint uid, uint gid);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(SafeFileHandle handle, uint permissions);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(SafeFileHandle handle);
}
