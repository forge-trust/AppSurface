using System.Collections;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure custody comparisons. Samples and inventories establish no native holder, lease or proof.</summary>
public sealed class LinuxCustodyDataTests
{
    private static readonly LinuxRunAccountSnapshot Accounts = new(65010, 65011, 65012, 65013, 65014);

    [Theory]
    [InlineData((int)LinuxCustodyNodeKind.Generation)]
    [InlineData((int)LinuxCustodyNodeKind.Control)]
    [InlineData((int)LinuxCustodyNodeKind.Broker)]
    [InlineData((int)LinuxCustodyNodeKind.Output)]
    [InlineData((int)LinuxCustodyNodeKind.RawResults)]
    [InlineData((int)LinuxCustodyNodeKind.Slot)]
    [InlineData((int)LinuxCustodyNodeKind.Descriptor)]
    [InlineData((int)LinuxCustodyNodeKind.Socket)]
    [InlineData((int)LinuxCustodyNodeKind.Plan)]
    [InlineData((int)LinuxCustodyNodeKind.Manifest)]
    [InlineData((int)LinuxCustodyNodeKind.Summary)]
    public void OriginalRoleHasAValidNeighborForEachSingleFieldRejection(int kindValue)
    {
        var kind = (LinuxCustodyNodeKind)kindValue;
        var value = Original(kind);
        LinuxCustodyData.RequireOriginal(kind, value, Accounts);
        foreach (var invalid in new[]
        {
            value with { Uid = value.Uid + 1 }, value with { Gid = value.Gid + 1 },
            value with { Mode = (ushort)(value.Mode ^ 1) },
            value with { Mode = (ushort)((value.Mode & 0xfff) | 0xa000) },
            value with { Inode = 0 }, value with { LinkCount = 0 },
            value with { ChangeNanoseconds = 1_000_000_000 }, value with { ModifyNanoseconds = 1_000_000_000 },
        }) Reject(() => LinuxCustodyData.RequireOriginal(kind, invalid, Accounts));
    }

    [Theory]
    [InlineData((int)LinuxCustodyNodeKind.Generation)]
    [InlineData((int)LinuxCustodyNodeKind.Control)]
    [InlineData((int)LinuxCustodyNodeKind.Broker)]
    [InlineData((int)LinuxCustodyNodeKind.Output)]
    [InlineData((int)LinuxCustodyNodeKind.RawResults)]
    [InlineData((int)LinuxCustodyNodeKind.Slot)]
    [InlineData((int)LinuxCustodyNodeKind.Descriptor)]
    [InlineData((int)LinuxCustodyNodeKind.Socket)]
    [InlineData((int)LinuxCustodyNodeKind.Plan)]
    [InlineData((int)LinuxCustodyNodeKind.Manifest)]
    [InlineData((int)LinuxCustodyNodeKind.Summary)]
    public void TerminalAllowsOnlyTheIntentionalOwnershipPermissionsAndCtimeChanges(int kindValue)
    {
        var kind = (LinuxCustodyNodeKind)kindValue;
        var before = Original(kind);
        LinuxCustodyData.RequireOriginal(kind, before, Accounts);
        var after = Terminal(kind, before);
        LinuxCustodyData.RequireTerminal(kind, before, after);
        foreach (var invalid in new[]
        {
            after with { DeviceMajor = after.DeviceMajor + 1 }, after with { DeviceMinor = after.DeviceMinor + 1 },
            after with { Inode = after.Inode + 1 }, after with { LinkCount = after.LinkCount + 1 },
            after with { Length = after.Length + 1 }, after with { ModifySeconds = after.ModifySeconds + 1 },
            after with { ModifyNanoseconds = after.ModifyNanoseconds + 1 },
            after with { Uid = Accounts.WorkerUid }, after with { Gid = Accounts.WorkerGid },
            after with { Mode = (ushort)(after.Mode | 0x20) },
            after with { Mode = (ushort)((after.Mode & 0xfff) | 0xa000) },
        }) Reject(() => LinuxCustodyData.RequireTerminal(kind, before, invalid));
    }

    [Theory]
    [InlineData((int)LinuxCustodyNodeKind.Descriptor, 65536UL)]
    [InlineData((int)LinuxCustodyNodeKind.Plan, 20971520UL)]
    [InlineData((int)LinuxCustodyNodeKind.Manifest, 20971520UL)]
    [InlineData((int)LinuxCustodyNodeKind.Summary, 20971520UL)]
    public void RegularFileBoundsAndSingleLinkAreRequiredBeforeAndAfterCustody(int kindValue, ulong maximum)
    {
        var kind = (LinuxCustodyNodeKind)kindValue;
        foreach (var length in new[] { 1UL, maximum })
        {
            var before = Original(kind) with { Length = length };
            LinuxCustodyData.RequireOriginal(kind, before, Accounts);
            LinuxCustodyData.RequireTerminal(kind, before, Terminal(kind, before));
        }
        foreach (var invalid in new[]
        {
            Original(kind) with { Length = 0 }, Original(kind) with { Length = maximum + 1 },
            Original(kind) with { LinkCount = 2 },
        })
        {
            Reject(() => LinuxCustodyData.RequireOriginal(kind, invalid, Accounts));
            Reject(() => LinuxCustodyData.RequireTerminal(kind, invalid, Terminal(kind, invalid)));
        }
    }

    [Fact]
    public void SocketMustBeAnEmptySingleLinkedSocketNotARegularFileOrDirectory()
    {
        var value = Original(LinuxCustodyNodeKind.Socket);
        LinuxCustodyData.RequireOriginal(LinuxCustodyNodeKind.Socket, value, Accounts);
        LinuxCustodyData.RequireTerminal(LinuxCustodyNodeKind.Socket, value, Terminal(LinuxCustodyNodeKind.Socket, value));
        foreach (var invalid in new[]
        {
            value with { Length = 1 }, value with { LinkCount = 2 },
            value with { Mode = 0x81b0 }, value with { Mode = 0x41b0 },
        }) Reject(() => LinuxCustodyData.RequireOriginal(LinuxCustodyNodeKind.Socket, invalid, Accounts));
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
    [InlineData(12)]
    [InlineData(13)]
    public void CompleteAccountComparisonRejectsRootSentinelAndOverlappingRoleNamespaces(int variant)
    {
        var invalid = variant switch
        {
            0 => Accounts with { WorkerUid = 0 }, 1 => Accounts with { SubjectUid = uint.MaxValue },
            2 => Accounts with { WorkerGid = 0 }, 3 => Accounts with { SubjectGid = uint.MaxValue },
            4 => Accounts with { ResultsGid = 0 }, 5 => Accounts with { SubjectUid = Accounts.WorkerUid },
            6 => Accounts with { SubjectGid = Accounts.WorkerGid }, 7 => Accounts with { ResultsGid = Accounts.SubjectGid },
            8 => Accounts with { WorkerUid = uint.MaxValue }, 9 => Accounts with { SubjectUid = 0 },
            10 => Accounts with { WorkerGid = uint.MaxValue }, 11 => Accounts with { SubjectGid = 0 },
            12 => Accounts with { ResultsGid = uint.MaxValue }, _ => Accounts with { ResultsGid = Accounts.WorkerGid },
        };
        Reject(() => LinuxCustodyData.RequireOriginal(LinuxCustodyNodeKind.Generation,
            Original(LinuxCustodyNodeKind.Generation), invalid));
    }

    [Fact]
    public void DirectoryInventoriesAreClosedAndOrderIndependentWithPartialOutputAllowed()
    {
        LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Generation, ["raw-results", "output", "worker"]);
        LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Control, ["worker-control.json", "broker"]);
        LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Broker, ["control.sock"]);
        LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.RawResults, []);
        LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Output, []);
        LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Output, ["evidence"]);
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Generation, ["worker", "output"]));
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Control, ["broker"]));
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Broker, []));
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.RawResults, ["evidence"]));
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Output, ["evidence", "CANARY"]));
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
    public void EveryCancelledSlotSubsetIsComparisonDataNotSuccessfulEvidence(int mask)
    {
        var all = new[] { "evidence-plan.json", "evidence-manifest.json", "evidence-summary.json" };
        var subset = all.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
        LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Slot, subset);
    }

    [Theory]
    [InlineData("CANARY")]
    [InlineData("Evidence-plan.json")]
    [InlineData("../evidence-plan.json")]
    [InlineData("sub/evidence-plan.json")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownAliasesTraversalAndNullNamesRejectWithoutEcho(string? name)
    {
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Slot, [name!]));
    }

    [Fact]
    public void DuplicateAndOverLimitInventoriesRejectBeforeRetainingNames()
    {
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Slot,
            ["evidence-plan.json", "evidence-plan.json"]));
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Slot, new OverLimitNames()));
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Slot, null!));
    }

    [Fact]
    public void EnumerationCannotEvadeTheThreeNameBoundByMisreportingCount()
    {
        var names = new MisreportedNames();
        Reject(() => LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Slot, names));
        Assert.Equal(4, names.Observed);
    }

    [Fact]
    public void FileRolesAndUnknownKindsCannotSupplyDirectoryInventoriesOrValidMetadata()
    {
        foreach (var kind in new[] { LinuxCustodyNodeKind.Descriptor, LinuxCustodyNodeKind.Socket,
            LinuxCustodyNodeKind.Plan, LinuxCustodyNodeKind.Manifest, LinuxCustodyNodeKind.Summary })
            Reject(() => LinuxCustodyData.RequireInventory(kind, []));
        var unknown = (LinuxCustodyNodeKind)999;
        Reject(() => LinuxCustodyData.RequireOriginal(unknown, Original(LinuxCustodyNodeKind.Plan), Accounts));
        Reject(() => LinuxCustodyData.RequireTerminal(unknown, Original(LinuxCustodyNodeKind.Plan),
            Terminal(LinuxCustodyNodeKind.Plan, Original(LinuxCustodyNodeKind.Plan))));
        Reject(() => LinuxCustodyData.RequireInventory(unknown, []));
        Reject(() => LinuxCustodyData.RequireOriginal(LinuxCustodyNodeKind.Plan, Original(LinuxCustodyNodeKind.Plan), null!));
    }

    private static LinuxProtectedMetadata Original(LinuxCustodyNodeKind kind)
    {
        var directory = (int)kind <= (int)LinuxCustodyNodeKind.Slot;
        var type = directory ? 0x4000 : kind == LinuxCustodyNodeKind.Socket ? 0xc000 : 0x8000;
        var permissions = kind switch
        {
            LinuxCustodyNodeKind.Generation => 0x1e8,
            LinuxCustodyNodeKind.Control or LinuxCustodyNodeKind.Broker or LinuxCustodyNodeKind.RawResults => 0x1c8,
            LinuxCustodyNodeKind.Output or LinuxCustodyNodeKind.Slot => 0x1c0,
            LinuxCustodyNodeKind.Descriptor => 0x120,
            LinuxCustodyNodeKind.Socket => 0x1b0,
            _ => 0x180,
        };
        var uid = kind is LinuxCustodyNodeKind.Generation or LinuxCustodyNodeKind.Control
            or LinuxCustodyNodeKind.Broker or LinuxCustodyNodeKind.Descriptor or LinuxCustodyNodeKind.Socket
            ? 0u : kind == LinuxCustodyNodeKind.RawResults ? Accounts.SubjectUid : Accounts.WorkerUid;
        return new(0, 42, 700, uid, kind == LinuxCustodyNodeKind.RawResults ? Accounts.ResultsGid : Accounts.WorkerGid,
            (ushort)(type | permissions), directory ? 2u : 1u, kind == LinuxCustodyNodeKind.Socket ? 0UL : 128UL,
            10, 20, 30, 40);
    }

    private static LinuxProtectedMetadata Terminal(LinuxCustodyNodeKind kind, LinuxProtectedMetadata before) => before with
    {
        Uid = 0, Gid = 0, ChangeSeconds = before.ChangeSeconds + 10, ChangeNanoseconds = 900,
        Mode = (ushort)((before.Mode & 0xf000) | ((int)kind <= (int)LinuxCustodyNodeKind.Slot ? 0x1c0
            : kind == LinuxCustodyNodeKind.Socket ? 0x180 : 0x100)),
    };

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.StartsWith("ASEVD402: The protected custody metadata or inventory was rejected.", error.Message);
        Assert.DoesNotContain("CANARY", error.Message);
        Assert.Null(error.InnerException);
    }

    private sealed class OverLimitNames : IReadOnlyList<string>
    {
        public int Count => 4;
        public string this[int index] => throw new InvalidOperationException("Unexpected inventory access.");
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("Unexpected inventory enumeration.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class MisreportedNames : IReadOnlyList<string>
    {
        public int Count => 3;
        internal int Observed { get; private set; }
        public string this[int index] => throw new InvalidOperationException("Unexpected indexed access.");
        public IEnumerator<string> GetEnumerator()
        {
            foreach (var name in new[] { "evidence-plan.json", "evidence-manifest.json", "evidence-summary.json", "CANARY" })
            {
                Observed++;
                yield return name;
            }
            throw new InvalidOperationException("Enumeration continued beyond the custody bound.");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
