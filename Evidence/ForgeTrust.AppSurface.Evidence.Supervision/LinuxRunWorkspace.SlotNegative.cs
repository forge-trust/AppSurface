using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

#if EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06
internal sealed partial class LinuxRunWorkspace
{
    private const string SlotSentinelName = "allocation-sentinel";
    private const string SlotSymlinkTarget = "../raw-results";
    private static ReadOnlySpan<byte> SlotSentinelBytes => "issue779-owned-slot-sentinel\n"u8;
    private SlotNegativeFixture? _slotNegativeFixture;

    /// <summary>Exclusively seeds the real worker slot before launch, only in the compile-selected N05/N06 image.</summary>
    /// <remarks>No existing node is adopted or removed. The actual allocator and custody rules remain unchanged.</remarks>
    internal byte[] PrepareSlotNegativeFixture(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        lock (_gate)
        {
            RequireOwnedBy(owner, accounts, token);
            if (!EvidenceNativeQualification.SlotNegativeEnabled || _slotNegativeFixture is not null || _outputParent is null)
                throw LinuxWorkspaceLayout.Invalid();
            SafeFileHandle? occupied = null;
            SafeFileHandle? sentinel = null;
            try
            {
                var parent = _outputParent;
                var link = EvidenceNativeQualification.Current == EvidenceNativeQualificationKind.SymlinkWorkerOutputSlot;
                DirectoryNode? target = null;
                if (link)
                {
                    target = _nodes.Single(node => node.Policy == _layout.RawResultsDirectory);
                    // The fixed sibling is already owned by this generation; no caller path selects a target.
                    sentinel = CreateSlotSentinel(target.Handle, 0, 0, token);
                    if (SymlinkAt(SlotSymlinkTarget, Fd(parent.Handle), LinuxWorkspaceLayout.ArtifactSlot) != 0)
                        throw LinuxWorkspaceLayout.Invalid();
                    occupied = OpenAt2(Fd(parent.Handle), LinuxWorkspaceLayout.ArtifactSlot,
                        PathFlags | 0x20000, 0, ChildResolution);
                }
                else
                {
                    if (MkdirAt(Fd(parent.Handle), LinuxWorkspaceLayout.ArtifactSlot, 0x1c0) != 0)
                        throw LinuxWorkspaceLayout.Invalid();
                    occupied = OpenAt2(Fd(parent.Handle), LinuxWorkspaceLayout.ArtifactSlot, DirectoryFlags, 0, ChildResolution);
                    if (Fchown(occupied, accounts.WorkerUid, accounts.WorkerGid) != 0 || Fchmod(occupied, 0x1c0) != 0)
                        throw LinuxWorkspaceLayout.Invalid();
                    sentinel = CreateSlotSentinel(occupied, accounts.WorkerUid, accounts.WorkerGid, token);
                }
                var fixture = new SlotNegativeFixture(link, occupied, sentinel, target,
                    LinuxProtectedMetadata.From(StatFd(occupied)), LinuxProtectedMetadata.From(StatFd(sentinel)));
                // Store every retained native handle before validation or emission can fail.
                _slotNegativeFixture = fixture;
                occupied = sentinel = null;
                var bytes = VerifySlotNegativeFixture(fixture, "setup", token, InspectionPurpose.Work);
                fixture.SetupVerified = true;
                return bytes;
            }
            catch { _quarantined = true; throw; }
            finally { occupied?.Dispose(); sentinel?.Dispose(); }
        }
    }

    /// <summary>Rechecks fixture names, pinned bytes and identities after original worker joins; always closes its inspection handles.</summary>
    /// <remarks>Execution must authenticate original native joins first. This emits data, not custody or account release.</remarks>
    internal byte[] CloseAndCaptureSlotNegativeFixture(CancellationToken token)
    {
        lock (_gate)
        {
            var fixture = _slotNegativeFixture ?? throw LinuxWorkspaceLayout.Invalid();
            byte[]? result = null;
            Exception? failure = null;
            try
            {
                if (!fixture.SetupVerified) throw LinuxWorkspaceLayout.Invalid();
                result = VerifySlotNegativeFixture(fixture, "post_join", token, InspectionPurpose.Control);
            }
            catch (Exception error) when (Recoverable(error)) { failure = error; _quarantined = true; }
            finally
            {
                _slotNegativeFixture = null;
                if (!Close([fixture.Sentinel, fixture.Occupied]) || !fixture.Sentinel.IsClosed || !fixture.Occupied.IsClosed)
                { _quarantined = true; failure ??= LinuxWorkspaceLayout.Invalid(); }
            }
            if (failure is not null || result is null) throw LinuxWorkspaceLayout.Invalid();
            return result;
        }
    }

    private SafeFileHandle CreateSlotSentinel(SafeFileHandle parent, uint uid, uint gid, CancellationToken token)
    {
        Check(token);
        var file = OpenAt2(Fd(parent), SlotSentinelName, 0x02 | 0x40 | 0x80 | 0x20000 | 0x80000, 0x180, ChildResolution);
        try
        {
            var initial = LinuxProtectedMetadata.From(StatFd(file));
            if (initial.Uid != 0 || initial.Inode == 0 || initial.Mode >> 12 != 8 || initial.LinkCount != 1 || initial.Length != 0)
                throw LinuxWorkspaceLayout.Invalid();
            if (Fchown(file, uid, gid) != 0 || Fchmod(file, 0x180) != 0) throw LinuxWorkspaceLayout.Invalid();
            RandomAccess.Write(file, SlotSentinelBytes, 0);
            if (Fsync(file) != 0) throw LinuxWorkspaceLayout.Invalid();
            Check(token);
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private byte[] VerifySlotNegativeFixture(SlotNegativeFixture fixture, string phase, CancellationToken token, InspectionPurpose purpose)
    {
        Check(token, purpose);
        RecheckDirectories(token, purpose);
        var parent = _outputParent ?? throw LinuxWorkspaceLayout.Invalid();
        var parentMetadata = LinuxProtectedMetadata.From(StatFd(parent.Handle));
        if (fixture.Link)
        {
            using var namedLink = OpenAt2(Fd(parent.Handle), LinuxWorkspaceLayout.ArtifactSlot,
                PathFlags | 0x20000, 0, ChildResolution);
            if (LinuxProtectedMetadata.From(StatFd(namedLink)) != fixture.OccupiedMetadata)
                throw LinuxWorkspaceLayout.Invalid();
        }
        else RequireSameNamed(parent.Handle, LinuxWorkspaceLayout.ArtifactSlot, fixture.OccupiedMetadata);
        var occupiedMetadata = LinuxProtectedMetadata.From(StatFd(fixture.Occupied));
        if (fixture.Link ? occupiedMetadata != fixture.OccupiedMetadata : !occupiedMetadata.SameAncestorAs(fixture.OccupiedMetadata))
            throw LinuxWorkspaceLayout.Invalid();
        var sentinelParent = fixture.Link ? fixture.Target!.Handle : fixture.Occupied;
        RequireSameNamed(sentinelParent, SlotSentinelName, fixture.SentinelMetadata);
        if (LinuxProtectedMetadata.From(StatFd(fixture.Sentinel)) != fixture.SentinelMetadata)
            throw LinuxWorkspaceLayout.Invalid();
        var bytes = new byte[SlotSentinelBytes.Length + 1];
        if (RandomAccess.Read(fixture.Sentinel, bytes, 0) != SlotSentinelBytes.Length
            || !bytes.AsSpan(0, SlotSentinelBytes.Length).SequenceEqual(SlotSentinelBytes)
            || LinuxProtectedMetadata.From(StatFd(fixture.Sentinel)) != fixture.SentinelMetadata)
            throw LinuxWorkspaceLayout.Invalid();
        if (ReadN09Names(parent.Handle) is not [LinuxWorkspaceLayout.ArtifactSlot]
            || ReadN09Names(sentinelParent) is not [SlotSentinelName]) throw LinuxWorkspaceLayout.Invalid();
        LinuxProtectedMetadata? target = null;
        if (fixture.Link)
        {
            target = LinuxProtectedMetadata.From(StatFd(fixture.Target!.Handle));
            if (!target.Value.SameAncestorAs(fixture.Target.Metadata)) throw LinuxWorkspaceLayout.Invalid();
            var linkBytes = new byte[64];
            var count = ReadLinkAt(Fd(parent.Handle), LinuxWorkspaceLayout.ArtifactSlot, linkBytes, (nuint)linkBytes.Length);
            if (count != 14 || !linkBytes.AsSpan(0, 14).SequenceEqual(Encoding.UTF8.GetBytes(SlotSymlinkTarget)))
                throw LinuxWorkspaceLayout.Invalid();
        }
        Check(token, purpose);
        return LinuxSlotNegativeObservation.EncodeInspection(fixture.Link ? "N06" : "N05", phase,
            parentMetadata, occupiedMetadata, fixture.SentinelMetadata,
            Convert.ToHexStringLower(SHA256.HashData(SlotSentinelBytes)), target);
    }

    private sealed class SlotNegativeFixture(bool link, SafeFileHandle occupied, SafeFileHandle sentinel, DirectoryNode? target,
        LinuxProtectedMetadata occupiedMetadata, LinuxProtectedMetadata sentinelMetadata)
    {
        internal bool Link { get; } = link;
        internal SafeFileHandle Occupied { get; } = occupied;
        internal SafeFileHandle Sentinel { get; } = sentinel;
        internal DirectoryNode? Target { get; } = target;
        internal LinuxProtectedMetadata OccupiedMetadata { get; } = occupiedMetadata;
        internal LinuxProtectedMetadata SentinelMetadata { get; } = sentinelMetadata;
        internal bool SetupVerified { get; set; }
    }

    [DllImport("libc", EntryPoint = "symlinkat", SetLastError = true)]
    private static extern int SymlinkAt([MarshalAs(UnmanagedType.LPUTF8Str)] string target, int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    private static extern long ReadLinkAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [Out] byte[] buffer, nuint length);
}

#endif
