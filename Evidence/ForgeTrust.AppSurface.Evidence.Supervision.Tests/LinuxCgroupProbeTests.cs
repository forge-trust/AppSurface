using System.ComponentModel;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure record/metadata controls only; no fixture constructs a retained kernel cgroup.</summary>
public sealed class LinuxCgroupProbeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BothClosedValuesAndEitherKeyOrderProduceSampledData(bool populated, bool frozen)
    {
        var p = populated ? 1 : 0;
        var f = frozen ? 1 : 0;
        var forward = LinuxCgroupProbe.ParseEvents(Encoding.ASCII.GetBytes($"populated {p}\nfrozen {f}\n"));
        var reverse = LinuxCgroupProbe.ParseEvents(Encoding.ASCII.GetBytes($"frozen {f}\npopulated {p}\n"));
        Assert.Equal(new LinuxCgroupEvents(populated, frozen), forward);
        Assert.Equal(forward, reverse);
    }

    [Theory]
    [InlineData("")]
    [InlineData("populated 0\n")]
    [InlineData("frozen 0\n")]
    [InlineData("populated 0\nfrozen 0")]
    [InlineData("populated 0\npopulated 1\nfrozen 0\n")]
    [InlineData("populated 0\nfrozen 0\nfrozen 1\n")]
    [InlineData("populated 0\nfrozen 0\nextra 0\n")]
    [InlineData("populated 0\nfrozen 0\n\n")]
    [InlineData("populated 0\r\nfrozen 0\n")]
    [InlineData("populated\t0\nfrozen 0\n")]
    [InlineData(" populated 0\nfrozen 0\n")]
    [InlineData("populated 0 \nfrozen 0\n")]
    [InlineData("populated 00\nfrozen 0\n")]
    [InlineData("populated -1\nfrozen 0\n")]
    [InlineData("populated 2\nfrozen 0\n")]
    [InlineData("populated true\nfrozen 0\n")]
    [InlineData("populated 0\nfrozen 2\n")]
    [InlineData("populated 0\nfrozen \n")]
    [InlineData("populated 0\nfrozen 0\0\n")]
    [InlineData("private-cgroup-canary\n")]
    public void MalformedMissingDuplicateOrUnknownRowsFailClosedWithoutEcho(string text) =>
        AssertClosed(() => LinuxCgroupProbe.ParseEvents(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void ByteLimitAndNonAsciiDataRejectBeforeAnyNativeAcquisition()
    {
        var maximum = new byte[LinuxCgroupProbe.MaximumEventsBytes];
        Array.Fill(maximum, (byte)'x');
        AssertClosed(() => LinuxCgroupProbe.ParseEvents(maximum));
        AssertClosed(() => LinuxCgroupProbe.ParseEvents(new byte[maximum.Length + 1]));
        AssertClosed(() => LinuxCgroupProbe.ParseEvents(new byte[] { 0xff, (byte)'\n' }));
        var valid = LinuxCgroupProbe.ParseEvents("populated 0\nfrozen 0\n"u8);
        Assert.False(valid.Populated);
        Assert.False(valid.Frozen);
    }

    [Fact]
    public void KernelDirectoryAndZeroSizedPseudoFileMetadataRemainDataOnly()
    {
        LinuxCgroupProbe.RequireMetadata(Directory(), LinuxCgroupProbe.Cgroup2Magic, true);
        LinuxCgroupProbe.RequireMetadata(File(), LinuxCgroupProbe.Cgroup2Magic, false);
        LinuxCgroupProbe.RequireMetadata(File() with { Length = LinuxCgroupProbe.MaximumEventsBytes },
            LinuxCgroupProbe.Cgroup2Magic, false);
        AssertClosed(() => LinuxCgroupProbe.RequireMetadata(File() with { Length = LinuxCgroupProbe.MaximumEventsBytes + 1 },
            LinuxCgroupProbe.Cgroup2Magic, false));
    }

    [Theory]
    [InlineData("filesystem")]
    [InlineData("inode")]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("type")]
    [InlineData("group-write")]
    [InlineData("other-write")]
    [InlineData("setid")]
    [InlineData("owner-read")]
    [InlineData("owner-search")]
    [InlineData("pruned")]
    [InlineData("links")]
    public void DirectoryFactsRejectNonKernelUnsafeOrPrunedObjects(string field)
    {
        var value = Directory();
        value = field switch
        {
            "inode" => value with { Inode = 0 },
            "uid" => value with { Uid = 1 },
            "gid" => value with { Gid = 1 },
            "type" => value with { Mode = 0xa1ed },
            "group-write" => value with { Mode = 0x41fd },
            "other-write" => value with { Mode = 0x41ef },
            "setid" => value with { Mode = 0x49ed },
            "owner-read" => value with { Mode = 0x40ed },
            "owner-search" => value with { Mode = 0x41ad },
            "pruned" => value with { LinkCount = 0 },
            "links" => value with { LinkCount = 1 },
            _ => value
        };
        AssertClosed(() => LinuxCgroupProbe.RequireMetadata(value,
            field == "filesystem" ? 0x62656572 : LinuxCgroupProbe.Cgroup2Magic, true));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("symlink")]
    [InlineData("fifo")]
    [InlineData("device")]
    [InlineData("links")]
    [InlineData("pruned")]
    [InlineData("read")]
    [InlineData("write")]
    public void EventsFactsRejectLinksSpecialFilesOrUnsafeAccess(string field)
    {
        var value = File();
        value = field switch
        {
            "directory" => value with { Mode = 0x41ed },
            "symlink" => value with { Mode = 0xa124 },
            "fifo" => value with { Mode = 0x1124 },
            "device" => value with { Mode = 0x2124 },
            "links" => value with { LinkCount = 2 },
            "pruned" => value with { LinkCount = 0 },
            "read" => value with { Mode = 0x8024 },
            _ => value with { Mode = 0x8134 }
        };
        AssertClosed(() => LinuxCgroupProbe.RequireMetadata(value, LinuxCgroupProbe.Cgroup2Magic, false));
    }

    [Theory]
    [InlineData("inode")]
    [InlineData("major")]
    [InlineData("minor")]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("mode")]
    public void NamedSubstitutionOrAccessChangeCannotMatchRetainedIdentity(string field)
    {
        var original = Directory();
        var replacement = field switch
        {
            "inode" => original with { Inode = original.Inode + 1 },
            "major" => original with { DeviceMajor = 99 },
            "minor" => original with { DeviceMinor = 99 },
            "owner" => original with { Uid = 99 },
            "group" => original with { Gid = 99 },
            _ => original with { Mode = 0x41c0 }
        };
        Assert.False(LinuxCgroupProbe.SameObject(original, replacement));
        Assert.True(LinuxCgroupProbe.SameObject(original, original));
    }

    [Fact]
    public void ChildAndEventChurnDoesNotInventReplacementButPrunedMetadataStillRejects()
    {
        var original = Directory();
        var churn = original with { LinkCount = 4, Length = 8192, ChangeSeconds = 99, ModifySeconds = 99 };
        Assert.True(LinuxCgroupProbe.SameObject(original, churn));
        LinuxCgroupProbe.RequireMetadata(churn, LinuxCgroupProbe.Cgroup2Magic, true);
        var pruned = original with { LinkCount = 0 };
        Assert.True(LinuxCgroupProbe.SameObject(original, pruned)); // Identity is distinct from validity.
        AssertClosed(() => LinuxCgroupProbe.RequireMetadata(pruned, LinuxCgroupProbe.Cgroup2Magic, true));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(13, false)]
    [InlineData(20, false)]
    [InlineData(40, false)]
    [InlineData(116, false)]
    public void OnlyExactNativeEnoentCanSupplyAbsenceDataAfterParentValidation(int error, bool missing)
    {
        Assert.Equal(missing, LinuxCgroupProbe.IsAbsentError(new IOException("private-cgroup-canary", new Win32Exception(error))));
        Assert.False(LinuxCgroupProbe.IsAbsentError(new IOException("No such file: private-cgroup-canary")));
        Assert.False(LinuxCgroupProbe.IsAbsentError(new UnauthorizedAccessException("private-cgroup-canary")));
        Assert.False(LinuxCgroupProbe.IsAbsentError(new PlatformNotSupportedException("private-cgroup-canary", new Win32Exception(2))));
    }

    [Fact]
    public void MissingSelectedUnitRejectsBeforeAnyNativeAcquisition()
    {
        Assert.Throws<ArgumentNullException>(() => LinuxCgroupProbe.Read(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("acquisition")]
    [InlineData("inspection")]
    [InlineData("named-recheck")]
    public void InitialAbsenceAndPostAcquisitionPruningHaveDifferentProcedureOutcomes(string failureStage)
    {
        // Procedure markers are data only: no handle or retained kernel object is fabricated.
        var error = new IOException("private-cgroup-canary", new Win32Exception(2));
        var steps = new List<string>();
        Action acquire = () =>
        {
            steps.Add("acquire");
            if (failureStage == "acquisition") throw error;
        };
        Action validate = () =>
        {
            steps.Add("register");
            steps.Add("inspection");
            if (failureStage == "inspection") throw error;
            steps.Add("named-recheck");
            throw error;
        };
        if (failureStage == "acquisition")
        {
            Assert.False(LinuxCgroupProbe.AcquireThenValidateLeaf(acquire, validate));
            Assert.Equal(new[] { "acquire" }, steps);
        }
        else
        {
            Assert.Same(error, Assert.Throws<IOException>(() => LinuxCgroupProbe.AcquireThenValidateLeaf(acquire, validate)));
            Assert.Equal(failureStage == "inspection"
                ? new[] { "acquire", "register", "inspection" }
                : new[] { "acquire", "register", "inspection", "named-recheck" }, steps);
        }
        steps.Clear();
        Assert.True(LinuxCgroupProbe.AcquireThenValidateLeaf(() => steps.Add("acquire"), () => steps.Add("validate")));
        Assert.Equal(new[] { "acquire", "validate" }, steps);
    }

    [Fact]
    public void OtherAcquisitionFailuresAndCancellationNeverBecomeAbsenceOrDispatchValidation()
    {
        var validations = 0;
        var denied = new IOException("private-cgroup-canary", new Win32Exception(13));
        Assert.Same(denied, Assert.Throws<IOException>(() =>
            LinuxCgroupProbe.AcquireThenValidateLeaf(() => throw denied, () => validations++)));
        var cancelled = new OperationCanceledException("private-cgroup-canary");
        Assert.Same(cancelled, Assert.Throws<OperationCanceledException>(() =>
            LinuxCgroupProbe.AcquireThenValidateLeaf(() => throw cancelled, () => validations++)));
        Assert.Equal(0, validations);
    }

    private static LinuxProtectedMetadata Directory() => new(0, 29, 123, 0, 0, 0x41ed, 2, 0, 1, 0, 1, 0);
    private static LinuxProtectedMetadata File() => new(0, 29, 456, 0, 0, 0x8124, 1, 0, 1, 0, 1, 0);
    private static void AssertClosed(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-cgroup-canary", error.ToString(), StringComparison.Ordinal);
    }
}
