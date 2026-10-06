using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Sampled socket/worker policy controls; none binds a listener or creates a kernel identity.</summary>
public sealed class LinuxControlListenerTests
{
    [Fact]
    public void ConnectionGenerationComparisonRejectsAnotherLiveGenerationBeforePeerExposure()
    {
        var worker = LinuxUnitName.Create(LinuxUnitRole.Worker, Id);
        LinuxControlListenerPolicy.RequireWorkerGeneration(Id, worker, LinuxProcessSamplingRole.Worker);
        Reject(() => LinuxControlListenerPolicy.RequireWorkerGeneration(Guid.NewGuid(), worker, LinuxProcessSamplingRole.Worker));
        Reject(() => LinuxControlListenerPolicy.RequireWorkerGeneration(Guid.Empty, worker, LinuxProcessSamplingRole.Worker));
        Reject(() => LinuxControlListenerPolicy.RequireWorkerGeneration(Id,
            LinuxUnitName.Create(LinuxUnitRole.Owner, Id), LinuxProcessSamplingRole.Worker));
        Reject(() => LinuxControlListenerPolicy.RequireWorkerGeneration(Id, worker, LinuxProcessSamplingRole.Owner));
        Reject(() => LinuxControlListenerPolicy.RequireWorkerGeneration(Id, null!, LinuxProcessSamplingRole.Worker));
    }

    private static readonly Guid Id = Guid.Parse("a9371394-1a0b-4351-8767-8fa7d846d5d8");
    private const uint WorkerUid = 65010;
    private const uint WorkerGid = 65011;
    private static readonly LinuxProtectedMetadata Created = new(0, 41, 91, 0, 0, 0xc1ff, 1, 0, 10, 20, 11, 21);
    private static readonly LinuxProtectedMetadata Sealed = Created with { Gid = WorkerGid, Mode = 0xc1b0 };

    [Fact]
    public void FreshRootSocketMayHaveAnyUmaskButCannotAlreadyCarryAnotherOwnerOrLink()
    {
        foreach (var permissions in new ushort[] { 0x000, 0x100, 0x180, 0x1b0, 0x1ff })
            LinuxControlListenerPolicy.RequireCreated(Created with { Mode = (ushort)(0xc000 | permissions) });
        foreach (var invalid in new[]
        {
            Created with { Uid = WorkerUid }, Created with { Gid = WorkerGid },
            Created with { Inode = 0 }, Created with { LinkCount = 0 }, Created with { LinkCount = 2 },
            Created with { Length = 1 }, Created with { Mode = 0x81ff }, Created with { Mode = 0xa1ff },
            Created with { Mode = 0x41ff }, Created with { Mode = 0x11ff },
            Created with { Mode = 0xc3ff }, Created with { Mode = 0xc5ff }, Created with { Mode = 0xc9ff },
        }) Reject(() => LinuxControlListenerPolicy.RequireCreated(invalid));
    }

    [Fact]
    public void SealedSocketHasExactlyRootWorker0660WithoutOtherAccessOrExecute()
    {
        LinuxControlListenerPolicy.RequireSealed(Sealed, WorkerGid);
        Assert.Equal(0x1b0, Sealed.Mode & 0x0fff);
        Assert.Equal(0, Sealed.Mode & 0x7);
        foreach (var invalid in new[]
        {
            Sealed with { Uid = WorkerUid }, Sealed with { Gid = WorkerGid + 1 },
            Sealed with { Inode = 0 }, Sealed with { LinkCount = 0 }, Sealed with { LinkCount = 2 },
            Sealed with { Length = 1 }, Sealed with { Mode = 0x81b0 }, Sealed with { Mode = 0xa1b0 },
            Sealed with { Mode = 0x41b0 }, Sealed with { Mode = 0x11b0 },
        }) Reject(() => LinuxControlListenerPolicy.RequireSealed(invalid, WorkerGid));
        Reject(() => LinuxControlListenerPolicy.RequireSealed(Sealed, 0));
        Reject(() => LinuxControlListenerPolicy.RequireSealed(Sealed, uint.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void EveryAccessOrSpecialPermissionBitChangeRejects(int bit)
    {
        LinuxControlListenerPolicy.RequireSealed(Sealed, WorkerGid);
        var changed = Sealed with { Mode = (ushort)(Sealed.Mode ^ (1 << bit)) };
        Reject(() => LinuxControlListenerPolicy.RequireSealed(changed, WorkerGid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void NamedSocketRequiresSameDeviceInodeAndBothTimestamps(int field)
    {
        LinuxControlListenerPolicy.RequireSameSocket(Sealed, Sealed, WorkerGid);
        var replacement = field switch
        {
            0 => Sealed with { DeviceMajor = Sealed.DeviceMajor + 1 },
            1 => Sealed with { DeviceMinor = Sealed.DeviceMinor + 1 },
            2 => Sealed with { Inode = Sealed.Inode + 1 },
            3 => Sealed with { ChangeSeconds = Sealed.ChangeSeconds + 1 },
            4 => Sealed with { ChangeNanoseconds = Sealed.ChangeNanoseconds + 1 },
            5 => Sealed with { ModifySeconds = Sealed.ModifySeconds + 1 },
            _ => Sealed with { ModifyNanoseconds = Sealed.ModifyNanoseconds + 1 },
        };
        Reject(() => LinuxControlListenerPolicy.RequireSameSocket(replacement, Sealed, WorkerGid));
        Reject(() => LinuxControlListenerPolicy.RequireSameSocket(Sealed, replacement, WorkerGid));
        Reject(() => LinuxControlListenerPolicy.RequireSameSocket(Sealed, Created, WorkerGid));
    }

    [Fact]
    public void OnlyThisGenerationWorkerRoleAndActualAccountTupleCanBeSelected()
    {
        var worker = LinuxUnitName.Create(LinuxUnitRole.Worker, Id);
        void Select(Guid id, uint pid, uint uid, uint gid, LinuxUnitName unit, LinuxProcessSamplingRole role,
            uint selectedUid = WorkerUid, uint selectedGid = WorkerGid) =>
            LinuxControlListenerPolicy.RequireWorkerSelection(id, pid, uid, gid, unit, role, selectedUid, selectedGid);
        Select(Id, 1234, WorkerUid, WorkerGid, worker, LinuxProcessSamplingRole.Worker);
        Select(Id, int.MaxValue, WorkerUid, WorkerGid, worker, LinuxProcessSamplingRole.Worker);
        Reject(() => Select(Guid.Empty, 1234, WorkerUid, WorkerGid, worker, LinuxProcessSamplingRole.Worker));
        Reject(() => Select(Guid.NewGuid(), 1234, WorkerUid, WorkerGid, worker, LinuxProcessSamplingRole.Worker));
        foreach (var pid in new[] { 0u, (uint)int.MaxValue + 1, uint.MaxValue })
            Reject(() => Select(Id, pid, WorkerUid, WorkerGid, worker, LinuxProcessSamplingRole.Worker));
        foreach (var uid in new[] { 0u, WorkerUid + 1, uint.MaxValue })
            Reject(() => Select(Id, 1234, uid, WorkerGid, worker, LinuxProcessSamplingRole.Worker));
        foreach (var gid in new[] { 0u, WorkerGid + 1, uint.MaxValue })
            Reject(() => Select(Id, 1234, WorkerUid, gid, worker, LinuxProcessSamplingRole.Worker));
        foreach (var role in new[] { LinuxProcessSamplingRole.Owner, (LinuxProcessSamplingRole)123 })
            Reject(() => Select(Id, 1234, WorkerUid, WorkerGid, worker, role));
        Reject(() => Select(Id, 1234, WorkerUid, WorkerGid, null!, LinuxProcessSamplingRole.Worker));
        foreach (var unitRole in new[] { LinuxUnitRole.Owner, LinuxUnitRole.Producer, LinuxUnitRole.Application,
            LinuxUnitRole.AccountUtility })
            Reject(() => Select(Id, 1234, WorkerUid, WorkerGid, LinuxUnitName.Create(unitRole, Id),
                LinuxProcessSamplingRole.Worker));
        Reject(() => Select(Id, 1234, WorkerUid, WorkerGid, worker, LinuxProcessSamplingRole.Worker, 0));
        Reject(() => Select(Id, 1234, WorkerUid, WorkerGid, worker, LinuxProcessSamplingRole.Worker, uint.MaxValue));
        Reject(() => Select(Id, 1234, WorkerUid, WorkerGid, worker, LinuxProcessSamplingRole.Worker, WorkerUid, 0));
        Reject(() => Select(Id, 1234, WorkerUid, WorkerGid, worker, LinuxProcessSamplingRole.Worker, WorkerUid, uint.MaxValue));
    }

    [Fact]
    public void ClosedDiagnosticContainsNoPathPeerOrExceptionAndBoundsRemainFixed()
    {
        var error = LinuxControlListenerPolicy.Invalid();
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("/run/", error.Message);
        Assert.DoesNotContain("65010", error.Message);
        Assert.Equal("control.sock", LinuxControlListenerPolicy.SocketName);
        Assert.Equal(32, LinuxControlListenerPolicy.MaximumConnections);
    }

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
    }
}
