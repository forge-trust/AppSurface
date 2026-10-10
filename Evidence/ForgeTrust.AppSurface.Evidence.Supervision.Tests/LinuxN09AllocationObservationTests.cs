using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached schema controls only; these values are not a native observation.</summary>
public sealed class LinuxN09AllocationObservationTests
{
    private static readonly Guid Generation = Guid.Parse("4523b7bf-72a3-4fc7-a367-86857556e574");
    private static readonly LinuxN09DirectoryIdentity Parent = new(8, 1, 100, 65010, 65011, 0x41c0);
    private static readonly LinuxN09DirectoryIdentity Slot = new(8, 1, 101, 65010, 65011, 0x41c0);

    [Fact]
    public void DetachedEncodingIsBoundedAndNeverClaimsAuthority()
    {
        var bytes = LinuxN09AllocationObservation.EncodeDetached(Generation, Parent, Parent, Slot, Slot,
            slotEmptyBeforeSignal: true, slotEmptyAfterWorkerJoin: true,
            parentHandleClosed: true, slotHandleClosed: true);

        Assert.InRange(bytes.Length, 1, LinuxN09AllocationObservation.MaximumJsonBytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal("issue779-n09-allocation-slot-observation-v1", root.GetProperty("schema").GetString());
        Assert.Equal(Generation.ToString("N"), root.GetProperty("generation").GetString());
        Assert.Equal((ulong)100, root.GetProperty("output_parent_before_signal").GetProperty("Inode").GetUInt64());
        Assert.Equal((ulong)100, root.GetProperty("output_parent_after_join").GetProperty("Inode").GetUInt64());
        Assert.Equal((ulong)101, root.GetProperty("allocated_slot_before_signal").GetProperty("Inode").GetUInt64());
        Assert.Equal((ulong)101, root.GetProperty("allocated_slot_after_join").GetProperty("Inode").GetUInt64());
        Assert.True(root.GetProperty("slot_empty_before_signal").GetBoolean());
        Assert.True(root.GetProperty("slot_empty_after_worker_join").GetBoolean());
        Assert.True(root.GetProperty("parent_handle_closed").GetBoolean());
        Assert.True(root.GetProperty("slot_handle_closed").GetBoolean());
        Assert.True(root.GetProperty("observation_only").GetBoolean());
        Assert.False(root.GetProperty("native_authority").GetBoolean());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void DetachedEncodingRejectsMissingLifecycleOrDescriptorCloseFact(
        bool beforeSignalEmpty, bool afterJoinEmpty, bool parentClosed, bool slotClosed)
    {
        Assert.Throws<EvidenceAdmissionException>(() => LinuxN09AllocationObservation.EncodeDetached(Generation,
            Parent, Parent, Slot, Slot, beforeSignalEmpty, afterJoinEmpty, parentClosed, slotClosed));
    }

    [Fact]
    public void DetachedEncodingRejectsCrossDeviceOrWrongDirectoryPolicy()
    {
        var crossDevice = Slot with { DeviceMajor = 9 };
        var wrongOwner = Slot with { Uid = 0 };
        var wrongMode = Slot with { Mode = 0x41e0 };
        var changedSlot = Slot with { Inode = 102 };

        Assert.Throws<EvidenceAdmissionException>(() => LinuxN09AllocationObservation.EncodeDetached(Generation,
            Parent, Parent, crossDevice, crossDevice, true, true, true, true));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxN09AllocationObservation.EncodeDetached(Generation,
            Parent, Parent, wrongOwner, wrongOwner, true, true, true, true));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxN09AllocationObservation.EncodeDetached(Generation,
            Parent, Parent, wrongMode, wrongMode, true, true, true, true));
        Assert.Throws<EvidenceAdmissionException>(() => LinuxN09AllocationObservation.EncodeDetached(Generation,
            Parent, Parent, Slot, changedSlot, true, true, true, true));
    }
}
