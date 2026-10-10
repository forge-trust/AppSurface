using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed N09 metadata projection; serialized data never conveys native authority.</summary>
internal readonly record struct LinuxN09DirectoryIdentity(uint DeviceMajor, uint DeviceMinor,
    ulong Inode, uint Uid, uint Gid, ushort Mode)
{
    internal bool SameObject(LinuxN09DirectoryIdentity other) =>
        DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor && Inode == other.Inode
        && Uid == other.Uid && Gid == other.Gid && Mode == other.Mode;

    internal static LinuxN09DirectoryIdentity From(LinuxProtectedMetadata value) =>
        new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Uid, value.Gid, value.Mode);
}

/// <summary>Bounded post-join data from the original workspace's retained N09 allocation descriptors.</summary>
/// <remarks>Detached construction verifies shape only. Production data is created by the privately held workspace.</remarks>
internal static class LinuxN09AllocationObservation
{
    internal const int MaximumJsonBytes = 2048;

    internal static byte[] EncodeDetached(Guid generation, LinuxN09DirectoryIdentity parentBeforeSignal,
        LinuxN09DirectoryIdentity parentAfterJoin, LinuxN09DirectoryIdentity slotBeforeSignal,
        LinuxN09DirectoryIdentity slotAfterJoin, bool slotEmptyBeforeSignal, bool slotEmptyAfterWorkerJoin,
        bool parentHandleClosed, bool slotHandleClosed)
    {
        if (generation == Guid.Empty || parentBeforeSignal.Inode == 0 || slotBeforeSignal.Inode == 0
            || !parentBeforeSignal.SameObject(parentAfterJoin) || !slotBeforeSignal.SameObject(slotAfterJoin)
            || parentBeforeSignal.DeviceMajor != slotBeforeSignal.DeviceMajor
            || parentBeforeSignal.DeviceMinor != slotBeforeSignal.DeviceMinor
            || parentBeforeSignal.Inode == slotBeforeSignal.Inode
            || parentBeforeSignal.Uid == 0 || parentBeforeSignal.Gid == 0
            || slotBeforeSignal.Uid != parentBeforeSignal.Uid || slotBeforeSignal.Gid != parentBeforeSignal.Gid
            || parentBeforeSignal.Mode != (0x4000 | 0x1c0) || slotBeforeSignal.Mode != (0x4000 | 0x1c0)
            || !slotEmptyBeforeSignal || !slotEmptyAfterWorkerJoin || !parentHandleClosed || !slotHandleClosed)
            throw LinuxProtectedDeployment.InvalidDeployment();

        var bytes = ForgeTrust.AppSurface.Evidence.Contracts.EvidenceCanonicalJson.Serialize(new
        {
            schema = "issue779-n09-allocation-slot-observation-v1",
            generation = generation.ToString("N"),
            output_parent_before_signal = parentBeforeSignal,
            output_parent_after_join = parentAfterJoin,
            allocated_slot_before_signal = slotBeforeSignal,
            allocated_slot_after_join = slotAfterJoin,
            slot_empty_before_signal = true,
            slot_empty_after_worker_join = true,
            parent_handle_closed = true,
            slot_handle_closed = true,
            observation_only = true,
            native_authority = false,
            native_acceptance = false
        });
        if (bytes.Length is 0 or > MaximumJsonBytes) throw LinuxProtectedDeployment.InvalidDeployment();
        return bytes;
    }
}

internal sealed partial class LinuxRunWorkspace
{
    private N09AllocationSlot? _n09AllocationSlot;

    internal bool HasN09AllocationSlot { get { lock (_gate) return _n09AllocationSlot is not null; } }

    /// <summary>Retains and validates the real allocated slot before the original N09 signal is sent.</summary>
    /// <remarks>Only the compile-selected N09 control path calls this on the actual privately owned workspace.</remarks>
    internal void RetainN09AllocationSlot(CancellationToken token)
    {
        lock (_gate)
        {
            Check(token);
            if (_n09AllocationSlot is not null || _outputParent is null) throw LinuxWorkspaceLayout.Invalid();
            RecheckDirectories(token);
            SafeFileHandle? parent = null;
            SafeFileHandle? slot = null;
            try
            {
                parent = OpenAt2(Fd(_outputParent.Handle), ".", DirectoryFlags, 0, ChildResolution);
                var parentMetadata = LinuxProtectedMetadata.From(StatFd(parent));
                if (!parentMetadata.SameAncestorAs(_outputParent.Metadata)) throw LinuxWorkspaceLayout.Invalid();
                slot = OpenAt2(Fd(parent), LinuxWorkspaceLayout.ArtifactSlot, DirectoryFlags, 0, ChildResolution);
                var slotMetadata = LinuxProtectedMetadata.From(StatFd(slot));
                LinuxWorkspaceLayout.RequireDirectory(slotMetadata, _layout.OutputDirectory);
                var retained = new N09AllocationSlot(parent, slot, parentMetadata, slotMetadata);
                parent = null;
                slot = null;
                _n09AllocationSlot = retained;
                _ = VerifyN09AllocationSlot(retained, token, InspectionPurpose.Work);
                retained.BeforeSignalVerified = true;
            }
            catch (Exception error) when (Recoverable(error))
            {
                _quarantined = true;
                throw LinuxWorkspaceLayout.Invalid();
            }
            finally
            {
                parent?.Dispose();
                slot?.Dispose();
            }
        }
    }

    /// <summary>Rechecks the original slot after worker joins, closes both retained FDs, then returns data only.</summary>
    /// <remarks>The returned record contains no success, authority, account-release or artifact-publication claim.</remarks>
    internal byte[]? CloseAndCaptureN09AllocationSlot(CancellationToken token)
    {
        lock (_gate)
        {
            var retained = _n09AllocationSlot;
            if (retained is null) return null;
            byte[]? result = null;
            Exception? failure = null;
            try
            {
                if (!retained.BeforeSignalVerified) throw LinuxWorkspaceLayout.Invalid();
                var (finalParentMetadata, finalSlotMetadata) = VerifyN09AllocationSlot(
                    retained, token, InspectionPurpose.Control);
                result = LinuxN09AllocationObservation.EncodeDetached(_owner.RunId,
                    LinuxN09DirectoryIdentity.From(retained.ParentMetadata),
                    LinuxN09DirectoryIdentity.From(finalParentMetadata),
                    LinuxN09DirectoryIdentity.From(retained.SlotMetadata),
                    LinuxN09DirectoryIdentity.From(finalSlotMetadata),
                    slotEmptyBeforeSignal: true, slotEmptyAfterWorkerJoin: true,
                    parentHandleClosed: true, slotHandleClosed: true);
            }
            catch (Exception error) when (Recoverable(error))
            {
                failure = error;
                _quarantined = true;
            }
            finally
            {
                _n09AllocationSlot = null;
                if (!Close(new[] { retained.Slot, retained.Parent })
                    || !retained.Slot.IsClosed || !retained.Parent.IsClosed)
                {
                    _quarantined = true;
                    failure ??= LinuxWorkspaceLayout.Invalid();
                }
            }

            if (failure is not null || result is null) throw LinuxWorkspaceLayout.Invalid();
            return result;
        }
    }

    internal void CloseN09AllocationSlotWithoutCapture()
    {
        lock (_gate)
        {
            var retained = _n09AllocationSlot;
            _n09AllocationSlot = null;
            if (retained is not null && (!Close(new[] { retained.Slot, retained.Parent })
                || !retained.Slot.IsClosed || !retained.Parent.IsClosed))
            {
                _quarantined = true;
                throw LinuxWorkspaceLayout.Invalid();
            }
        }
    }

    private (LinuxProtectedMetadata Parent, LinuxProtectedMetadata Slot) VerifyN09AllocationSlot(N09AllocationSlot retained,
        CancellationToken token, InspectionPurpose purpose)
    {
        Check(token, purpose);
        RecheckDirectories(token, purpose);
        var parentMetadata = LinuxProtectedMetadata.From(StatFd(retained.Parent));
        if (!parentMetadata.SameAncestorAs(retained.ParentMetadata)
            || _outputParent is null || !parentMetadata.SameAncestorAs(_outputParent.Metadata))
            throw LinuxWorkspaceLayout.Invalid();
        var slotMetadata = LinuxProtectedMetadata.From(StatFd(retained.Slot));
        LinuxWorkspaceLayout.RequireDirectory(slotMetadata, _layout.OutputDirectory);
        RequireSameNamed(retained.Parent, LinuxWorkspaceLayout.ArtifactSlot, retained.SlotMetadata);
        if (!slotMetadata.SameAncestorAs(retained.SlotMetadata)) throw LinuxWorkspaceLayout.Invalid();
        var parentNames = ReadN09Names(retained.Parent);
        var slotNames = ReadN09Names(retained.Slot);
        if (parentNames.Count != 1 || parentNames[0] != LinuxWorkspaceLayout.ArtifactSlot || slotNames.Count != 0)
            throw LinuxWorkspaceLayout.Invalid();
        Check(token, purpose);
        return (parentMetadata, slotMetadata);
    }

    private static IReadOnlyList<string> ReadN09Names(SafeFileHandle directory)
    {
        using var fresh = OpenAt2(Fd(directory), ".", DirectoryFlags, 0, ChildResolution);
        var buffer = new byte[4096];
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var count = ReadDirectory(fresh, buffer);
            if (count == 0) break;
            foreach (var name in LinuxDirectoryData.Parse(buffer.AsSpan(0, count)))
                if (names.Count >= 4 || !names.Add(name)) throw LinuxWorkspaceLayout.Invalid();
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class N09AllocationSlot(SafeFileHandle parent, SafeFileHandle slot,
        LinuxProtectedMetadata parentMetadata, LinuxProtectedMetadata slotMetadata)
    {
        internal SafeFileHandle Parent { get; } = parent;
        internal SafeFileHandle Slot { get; } = slot;
        internal LinuxProtectedMetadata ParentMetadata { get; } = parentMetadata;
        internal LinuxProtectedMetadata SlotMetadata { get; } = slotMetadata;
        internal bool BeforeSignalVerified { get; set; }
    }
}
