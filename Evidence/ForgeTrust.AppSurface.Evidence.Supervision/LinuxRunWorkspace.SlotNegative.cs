using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

// PRIVATE FULL-IMAGE N05 VARIANT ONLY. Do not include this template in the ordinary build.
internal sealed partial class LinuxRunWorkspace
{
    private const string NegativeSentinelName = "occupied-sentinel";
    private readonly SupervisionSingleAttempt _negativeSlotCreation = new();
    private static ReadOnlySpan<byte> NegativeSentinelBytes => "issue779-n05-occupied-slot-v1\n"u8;

    /// <summary>Creates the fixed N05 collision under this actual workspace, before listener/worker creation.</summary>
    /// <param name="owner">The reference-equal actual owner already retained by this workspace.</param>
    /// <param name="accounts">The reference-equal actual account holder; no identity data substitutes for it.</param>
    /// <param name="token">Original work cancellation; every native stage checks the original owner deadline.</param>
    /// <returns>A privately constructed FD owner for subsequent unchanged-object comparison, not artifact custody.</returns>
    /// <remarks>
    /// The call is fixed at WorkspaceCreate in the private full-image variant. There is no path, case, UID,
    /// environment or callback selector. One attempt is claimed before mutation. Existing names reject and
    /// are never adopted, removed or chmodded. Partial setup quarantines the workspace and preserves paths
    /// and accounts. This does not publish a descriptor, activate a writer or issue admission/custody.
    /// </remarks>
    internal OccupiedSlotInspection PrepareOccupiedSlotNegative(LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, CancellationToken token)
        => OccupiedSlotInspection.PrepareActual(this, owner, accounts, token);

    private static void RequireNegativeCreated(LinuxProtectedMetadata value, bool directory)
    {
        var permissions = directory ? 0x1c0 : 0x180;
        if (value.Inode == 0 || value.Uid != 0 || value.Gid != 0
            || (value.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
            || (value.Mode & 0x0fff & ~permissions) != 0
            || (directory ? value.LinkCount != 2 : value.LinkCount != 1 || value.Length != 0))
            throw LinuxWorkspaceLayout.Invalid();
    }

    private static void RequireNegativeOwned(LinuxProtectedMetadata value, bool directory,
        LinuxRunAccountSnapshot accounts)
    {
        if (value.Inode == 0 || value.Uid != accounts.WorkerUid || value.Gid != accounts.WorkerGid
            || value.Mode != (directory ? 0x41c0 : 0x8180)
            || (directory ? value.LinkCount != 2
                : value.LinkCount != 1 || value.Length != (ulong)NegativeSentinelBytes.Length))
            throw LinuxWorkspaceLayout.Invalid();
    }

    /// <summary>Immutable actual setup samples and expected fixed bytes; this data grants no native authority.</summary>
    /// <param name="Parent">Output-parent sample after the intentional slot insertion.</param>
    /// <param name="Slot">Worker-owned occupied directory sample after fixed sentinel insertion.</param>
    /// <param name="Sentinel">Single-link regular sentinel sample after write/fsync.</param>
    /// <param name="Sha256">Expected and actually re-read fixed sentinel SHA256.</param>
    internal sealed record OccupiedSlotSnapshot(LinuxProtectedMetadata Parent, LinuxProtectedMetadata Slot,
        LinuxProtectedMetadata Sentinel, string Sha256);

    /// <summary>Privately constructed ownership of actual comparison FDs; never an artifact/custody capability.</summary>
    /// <remarks>
    /// The root execution retains this owner until the original worker/server joins and the postjoin check,
    /// and closes it before workspace disposal. Dispose attempts every FD and asserts no process exit,
    /// cleanup or account release. No entry is removed. Caller ordering remains mandatory.
    /// </remarks>
    internal sealed class OccupiedSlotInspection : IDisposable
    {
        private readonly LinuxRunWorkspace _workspace;
        private readonly SafeFileHandle _parent;
        private readonly SafeFileHandle _slot;
        private readonly SafeFileHandle _sentinel;
        private bool _disposed;
        private bool _setupEvidenceAttempted;
        private bool _setupEvidenceComplete;
        private bool _postJoinEvidenceAttempted;

        private OccupiedSlotInspection(LinuxRunWorkspace workspace, SafeFileHandle parent,
            SafeFileHandle slot, SafeFileHandle sentinel, OccupiedSlotSnapshot data)
        { _workspace = workspace; _parent = parent; _slot = slot; _sentinel = sentinel; PreparedData = data; }

        /// <summary>Acquires only the fixed native objects from actual reference-equal owners.</summary>
        /// <remarks>No path, sampled metadata or supplied FD can construct this private comparison owner.</remarks>
        internal static OccupiedSlotInspection PrepareActual(LinuxRunWorkspace workspace,
            LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            lock (workspace._gate)
            {
                SafeFileHandle? parent = null;
                SafeFileHandle? slot = null;
                SafeFileHandle? writer = null;
                SafeFileHandle? reader = null;
                var failed = false;
                try
                {
                    if (!ReferenceEquals(owner, workspace._owner) || !ReferenceEquals(accounts, workspace._accounts)
                        || workspace._descriptorAttempted || workspace._descriptor is not null) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    workspace._negativeSlotCreation.Claim();
                    workspace.RecheckDirectories(token);
                    var originalParent = workspace._outputParent ?? throw LinuxWorkspaceLayout.Invalid();
                    var generation = originalParent.Parent ?? throw LinuxWorkspaceLayout.Invalid();
                    // Register every newly acquired FD immediately, before inspection or ownership changes.
                    parent = OpenAt2(Fd(generation.Handle), originalParent.Name, DirectoryFlags, 0, ChildResolution);
                    var openedParent = LinuxProtectedMetadata.From(StatFd(parent));
                    LinuxWorkspaceLayout.RequireDirectory(openedParent, workspace._layout.OutputDirectory);
                    if (!openedParent.SameAncestorAs(originalParent.Metadata)) throw LinuxWorkspaceLayout.Invalid();
                    RequireSameNamed(generation.Handle, originalParent.Name, openedParent);
                    workspace.Check(token);
                    if (MkdirAt(Fd(parent), LinuxWorkspaceLayout.ArtifactSlot, 0x1c0) != 0)
                        throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    slot = OpenAt2(Fd(parent), LinuxWorkspaceLayout.ArtifactSlot, DirectoryFlags, 0, ChildResolution);
                    var createdSlot = LinuxProtectedMetadata.From(StatFd(slot));
                    RequireNegativeCreated(createdSlot, directory: true);
                    RequireSameNamed(parent, LinuxWorkspaceLayout.ArtifactSlot, createdSlot);
                    workspace.Check(token);
                    if (Fchown(slot, workspace._layout.AccountData.WorkerUid, workspace._layout.AccountData.WorkerGid) != 0
                        || Fchmod(slot, 0x1c0) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    var ownedSlot = LinuxProtectedMetadata.From(StatFd(slot));
                    RequireNegativeOwned(ownedSlot, directory: true, workspace._layout.AccountData);
                    RequireSameNamed(parent, LinuxWorkspaceLayout.ArtifactSlot, ownedSlot);
                    workspace.Check(token);
                    // O_WRONLY|O_CREAT|O_EXCL|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC; fixed child beneath retained FD.
                    writer = OpenAt2(Fd(slot), NegativeSentinelName,
                        0x01 | 0x40 | 0x80 | 0x800 | 0x20000 | 0x80000, 0x180, ChildResolution);
                    var createdFile = LinuxProtectedMetadata.From(StatFd(writer));
                    RequireNegativeCreated(createdFile, directory: false);
                    RequireSameNamed(slot, NegativeSentinelName, createdFile);
                    workspace.Check(token);
                    if (Fchown(writer, workspace._layout.AccountData.WorkerUid, workspace._layout.AccountData.WorkerGid) != 0
                        || Fchmod(writer, 0x180) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    RandomAccess.Write(writer, NegativeSentinelBytes, 0);
                    workspace.Check(token);
                    if (Fsync(writer) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    var fileMetadata = LinuxProtectedMetadata.From(StatFd(writer));
                    RequireNegativeOwned(fileMetadata, directory: false, workspace._layout.AccountData);
                    RequireSameNamed(slot, NegativeSentinelName, fileMetadata);
                    reader = OpenAt2(Fd(slot), NegativeSentinelName, 0x800 | 0x20000 | 0x80000, 0, ChildResolution);
                    if (LinuxProtectedMetadata.From(StatFd(reader)) != fileMetadata) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    writer.Dispose(); writer = null;
                    if (Fsync(slot) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    if (Fsync(parent) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    var snapshot = new OccupiedSlotSnapshot(LinuxProtectedMetadata.From(StatFd(parent)),
                        LinuxProtectedMetadata.From(StatFd(slot)), fileMetadata,
                        Convert.ToHexStringLower(SHA256.HashData(NegativeSentinelBytes)));
                    var result = new OccupiedSlotInspection(workspace, parent, slot, reader, snapshot);
                    result.Recheck(token, InspectionPurpose.Work);
                    workspace.Check(token);
                    parent = slot = reader = null; // Ownership transfers only after every check succeeds.
                    return result;
                }
                catch
                {
                    failed = true;
                    workspace._quarantined = true;
                    throw; // Preserve the original setup/cancellation failure.
                }
                finally
                {
                    if (!Close(new[] { parent, slot, writer, reader }.OfType<SafeFileHandle>()))
                    {
                        workspace._quarantined = true;
                        if (!failed) throw LinuxWorkspaceLayout.Invalid();
                    }
                }
            }
        }

        /// <summary>Gets captured immutable data only; metadata cannot reconstruct this private FD owner.</summary>
        internal OccupiedSlotSnapshot PreparedData { get; }

        /// <summary>Rechecks and writes one fixed bounded private setup line before any worker launch.</summary>
        /// <remarks>Publication failure rejects preparation; it cannot be silently treated as setup success.</remarks>
        internal void WriteSetupEvidence(CancellationToken token)
        {
            lock (_workspace._gate)
            {
                try
                {
                    if (_setupEvidenceAttempted) throw LinuxWorkspaceLayout.Invalid();
                    _setupEvidenceAttempted = true; // Claim before checks or stderr I/O; failed publication cannot retry.
                    Recheck(token, InspectionPurpose.Work);
                    WriteEvidence(postJoin: false);
                    Recheck(token, InspectionPurpose.Work);
                    _setupEvidenceComplete = true;
                }
                catch { _workspace._quarantined = true; throw; }
            }
        }

        /// <summary>Checks unchanged retained objects only after the actual worker/server physical-ownership guard.</summary>
        /// <param name="input">Original actual protected input, not reconstructed metadata.</param>
        /// <param name="worker">Original actual worker holder whose shared StopAndJoin completed.</param>
        /// <param name="server">Original actual server whose handlers and I/O joined.</param>
        /// <param name="token">Original root teardown token; never a replacement cleanup allowance.</param>
        /// <remarks>
        /// Uses the same native pre-custody guard, including pending starts, exact group emptiness and pump
        /// joins. That guard and unchanged samples still issue no filesystem custody or successful run.
        /// A failed guard emits no postjoin-positive line; uncertainty remains failure/quarantine.
        /// </remarks>
        internal void WritePostJoinEvidence(EvidenceProtectedLaunchInput input, LinuxWorkerProcess worker,
            LinuxEmptyObservationControlServer server, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(worker);
            ArgumentNullException.ThrowIfNull(server);
            lock (_workspace._gate)
            {
                if (_disposed || !_setupEvidenceComplete || _postJoinEvidenceAttempted)
                    throw LinuxWorkspaceLayout.Invalid();
                _postJoinEvidenceAttempted = true; // No retry even if the original physical guard fails.
            }
            // Original native guard may perform I/O; no workspace lock is held across its composition.
            var kernel = worker.CaptureNegativeObservation(input, _workspace._owner, _workspace._accounts, _workspace, server, token);
            lock (_workspace._gate)
            {
                try
                {
                    // Data only; reached only after the original native custody guard above.
                    var projected = N05JoinedWorkerOutput.Parse(worker.Output, token);
                    Recheck(token, InspectionPurpose.Control);
                    WriteEvidence(postJoin: true);
                    Recheck(token, InspectionPurpose.Control);
                    var diagnostic = N05JoinedWorkerOutput.Serialize(projected, token);
                    Console.Error.WriteLine(diagnostic);
                    Console.Error.Flush();
                    Recheck(token, InspectionPurpose.Control);
                    token.ThrowIfCancellationRequested();
                    Console.Error.WriteLine(Encoding.UTF8.GetString(kernel.Bytes));
                    Console.Error.Flush();
                    Recheck(token, InspectionPurpose.Control);
                }
                catch { _workspace._quarantined = true; throw; }
            }
        }

        private void Recheck(CancellationToken token, InspectionPurpose purpose)
        {
            if (_disposed) throw LinuxWorkspaceLayout.Invalid();
            _workspace.RecheckCore(token, purpose);
            var parentNode = _workspace._outputParent ?? throw LinuxWorkspaceLayout.Invalid();
            var generation = parentNode.Parent ?? throw LinuxWorkspaceLayout.Invalid();
            LinuxWorkspaceLayout.RequireDirectory(PreparedData.Parent, _workspace._layout.OutputDirectory);
            if (!PreparedData.Parent.SameAncestorAs(parentNode.Metadata)) throw LinuxWorkspaceLayout.Invalid();
            _workspace.Check(token, purpose);
            if (LinuxProtectedMetadata.From(StatFd(_parent)) != PreparedData.Parent)
                throw LinuxWorkspaceLayout.Invalid();
            RequireSameNamed(generation.Handle, parentNode.Name, PreparedData.Parent);
            _workspace.Check(token, purpose);
            RequireNegativeOwned(PreparedData.Slot, directory: true, _workspace._layout.AccountData);
            if (LinuxProtectedMetadata.From(StatFd(_slot)) != PreparedData.Slot)
                throw LinuxWorkspaceLayout.Invalid();
            RequireSameNamed(_parent, LinuxWorkspaceLayout.ArtifactSlot, PreparedData.Slot);
            _workspace.Check(token, purpose);
            RequireNegativeOwned(PreparedData.Sentinel, directory: false, _workspace._layout.AccountData);
            RequireSameNamed(_slot, NegativeSentinelName, PreparedData.Sentinel);
            if (_workspace.HashDescriptor(_sentinel, PreparedData.Sentinel, token, purpose) != PreparedData.Sha256)
                throw LinuxWorkspaceLayout.Invalid();
            RequireSameNamed(_slot, NegativeSentinelName, PreparedData.Sentinel);
            RequireSameNamed(_parent, LinuxWorkspaceLayout.ArtifactSlot, PreparedData.Slot);
            RequireSameNamed(generation.Handle, parentNode.Name, PreparedData.Parent);
            if (LinuxProtectedMetadata.From(StatFd(_slot)) != PreparedData.Slot
                || LinuxProtectedMetadata.From(StatFd(_parent)) != PreparedData.Parent)
                throw LinuxWorkspaceLayout.Invalid();
            _workspace.Check(token, purpose);
        }

        private void WriteEvidence(bool postJoin)
        {
            static object Identity(LinuxProtectedMetadata value) => new
            {
                device_major = value.DeviceMajor, device_minor = value.DeviceMinor, inode = value.Inode,
                uid = value.Uid, gid = value.Gid, mode = value.Mode, links = value.LinkCount, length = value.Length,
            };
            var line = JsonSerializer.Serialize(new
            {
                schema = "issue779-n05-slot-inspection-v1", phase = postJoin ? "post_join" : "setup",
                parent = Identity(PreparedData.Parent), slot = Identity(PreparedData.Slot),
                sentinel = Identity(PreparedData.Sentinel), sha256 = PreparedData.Sha256,
            });
            if (Encoding.UTF8.GetByteCount(line) + 1 > 1024) throw LinuxWorkspaceLayout.Invalid();
            Console.Error.WriteLine(line); // Existing private root stderr, never a public success summary.
            Console.Error.Flush();
        }

        /// <summary>Attempts all owned closes once, preserves paths, and never claims physical settlement.</summary>
        public void Dispose()
        {
            lock (_workspace._gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (!Close(new[] { _parent, _slot, _sentinel }))
                { _workspace._quarantined = true; throw LinuxWorkspaceLayout.Invalid(); }
            }
        }
    }
}
