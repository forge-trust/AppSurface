using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Checks the actual retained producer-result descriptor chain, separately from producer admission.</summary>
/// <remarks>Real root Linux filesystem objects are required. No test constructs an owner or producer lease.</remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxSelectedProducerResultsRootNativeTests
{
    [ResultsRootFact]
    public void ActualSelectedDirectoryRechecksAndRepeatedDisposeClosesTheRetainedDescriptor()
    {
        using var fixture = new Fixture();
        var selected = fixture.Open();
        var handle = selected.Root;
        Assert.Equal(fixture.Metadata(), selected.Metadata);
        selected.Recheck(fixture.Deadline, default);
        Assert.False(handle.IsClosed);
        selected.Dispose();
        selected.Dispose();
        Assert.True(handle.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => selected.Recheck(fixture.Deadline, default));
        fixture.AssertSentinel();
    }

    [ResultsRootFact]
    public void ASelectedRootWithAnotherKernelInodeRejectsBeforeAdoptingTheActualDirectory()
    {
        using var fixture = new Fixture();
        var actual = fixture.Metadata();
        Assert.Throws<IOException>(() => fixture.Open(actual with { Inode = actual.Inode + 1 }));
        fixture.AssertSentinel();
    }

    [ResultsRootFact]
    public void SelectedDirectoryRequiresExactPrivateModeEvenWhenTheSampleMatchesTheKernel()
    {
        using var fixture = new Fixture();
        File.SetUnixFileMode(fixture.Selected, Fixture.Searchable);
        Assert.Throws<IOException>(() => fixture.Open());
        fixture.AssertSentinel();
    }

    [ResultsRootFact]
    public void SelectedDirectoryRequiresTheOriginalSubjectOwner()
    {
        using var fixture = new Fixture();
        fixture.ChownSelected(Fixture.Uid + 1, Fixture.Gid);
        Assert.Throws<IOException>(() => fixture.Open());
        fixture.AssertSentinel();
    }

    [ResultsRootFact]
    public void ResultsParentRequiresTheExactRootOwnedSearchableMode()
    {
        using var fixture = new Fixture();
        using (var valid = fixture.Open()) valid.Recheck(fixture.Deadline, default);
        File.SetUnixFileMode(fixture.Results, Fixture.Private);
        Assert.Throws<IOException>(() => fixture.Open());
        File.SetUnixFileMode(fixture.Results, Fixture.Searchable);
        try
        {
            fixture.ChownResults(0, Fixture.Gid);
            Assert.Throws<IOException>(() => fixture.Open());
            fixture.ChownResults(Fixture.Uid, Fixture.Gid);
            var error = Assert.Throws<EvidenceAdmissionException>(() => fixture.Open());
            Assert.Equal("ASEVD402", error.Code);
        }
        finally { fixture.ChownResults(0, 0); }
        using (var restored = fixture.Open()) restored.Recheck(fixture.Deadline, default);
        fixture.AssertSentinel();
    }

    [ResultsRootFact]
    public void WritableAncestorRejectsWithoutOpeningAReplacementOrChangingItsBytes()
    {
        using var fixture = new Fixture();
        File.SetUnixFileMode(fixture.Root, Fixture.Private | UnixFileMode.GroupWrite);
        var error = Assert.Throws<EvidenceAdmissionException>(() => fixture.Open());
        Assert.Equal("ASEVD402", error.Code);
        fixture.AssertSentinel();
    }

    [ResultsRootFact]
    public void RenamedSelectedDirectoryCannotBeReplacedBehindItsRetainedDescriptor()
    {
        using var fixture = new Fixture();
        using var selected = fixture.Open();
        var retained = fixture.Selected + "-retained";
        Directory.Move(fixture.Selected, retained);
        fixture.RecreateSelected("replacement"u8.ToArray());

        Assert.Throws<IOException>(() => selected.Recheck(fixture.Deadline, default));
        Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(TestPathUtils.PathUnder(retained, "sentinel")));
        Assert.Equal("replacement"u8.ToArray(), File.ReadAllBytes(fixture.Sentinel));
    }

    [ResultsRootFact]
    public void RenamedResultsParentCannotBeReplacedBehindTheRetainedChain()
    {
        using var fixture = new Fixture();
        using var selected = fixture.Open();
        var retained = fixture.Results + "-retained";
        Directory.Move(fixture.Results, retained);
        Directory.CreateDirectory(fixture.Results, Fixture.Searchable);
        fixture.RecreateSelected("replacement"u8.ToArray());

        Assert.Throws<IOException>(() => selected.Recheck(fixture.Deadline, default));
        Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(
            TestPathUtils.PathUnder(retained, "selected", "sentinel")));
        Assert.Equal("replacement"u8.ToArray(), File.ReadAllBytes(fixture.Sentinel));
    }

    [ResultsRootFact]
    public void ChangedSelectedOwnerAfterOpenRejectsTheNextKernelRecheck()
    {
        using var fixture = new Fixture();
        using var selected = fixture.Open();
        fixture.ChownSelected(Fixture.Uid + 1, Fixture.Gid);

        Assert.Throws<IOException>(() => selected.Recheck(fixture.Deadline, default));
        fixture.AssertSentinel();
    }

    [ResultsRootFact]
    public void SelectedSymlinkCannotAdoptAnOtherwiseValidOutsideDirectory()
    {
        using var fixture = new Fixture();
        var expected = fixture.Metadata();
        var retained = fixture.Selected + "-retained";
        Directory.Move(fixture.Selected, retained);
        Directory.CreateSymbolicLink(fixture.Selected, retained);

        Assert.Throws<IOException>(() => fixture.Open(expected));
        Assert.Equal(retained, new DirectoryInfo(fixture.Selected).LinkTarget);
        Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(TestPathUtils.PathUnder(retained, "sentinel")));
    }

    [ResultsRootFact]
    public void PreCancelledOpenAndRecheckKeepTheOriginalTokenAndNeverChangeTheTree()
    {
        using var fixture = new Fixture();
        using var selected = fixture.Open();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var opening = Assert.Throws<OperationCanceledException>(() => fixture.Open(token: cancel.Token));
        Assert.Equal(cancel.Token, opening.CancellationToken);
        var checking = Assert.Throws<OperationCanceledException>(() => selected.Recheck(fixture.Deadline, cancel.Token));
        Assert.Equal(cancel.Token, checking.CancellationToken);
        fixture.AssertSentinel();
    }

    [ResultsRootFact]
    public void ExpiredOriginalDeadlineRejectsOpenAndRecheckWithoutRenewingTheAllowance()
    {
        using var fixture = new Fixture();
        using var selected = fixture.Open();
        var expired = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.Zero);
        var opening = Assert.Throws<EvidenceAdmissionException>(() => fixture.Open(deadline: expired));
        Assert.Equal("ASEVD402", opening.Code);
        var checking = Assert.Throws<EvidenceAdmissionException>(() => selected.Recheck(expired, default));
        Assert.Equal("ASEVD402", checking.Code);
        fixture.AssertSentinel();
    }

    private sealed class Fixture : IDisposable
    {
        internal const uint Uid = 12345;
        internal const uint Gid = 12346;
        internal const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        internal const UnixFileMode Searchable = Private | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        internal string Root { get; }
        internal string Results => TestPathUtils.PathUnder(Root, "results");
        internal string Selected => TestPathUtils.PathUnder(Results, "selected");
        internal string Sentinel => TestPathUtils.PathUnder(Selected, "sentinel");
        internal LinuxInputDeadline Deadline { get; } = new(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30));

        internal Fixture()
        {
            Root = TestPathUtils.PathUnder("/run", "issue779-results-root-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(0, MakeDirectory(Root, 0x1c0));
            try
            {
                Directory.CreateDirectory(Results, Searchable);
                File.SetUnixFileMode(Results, Searchable);
                RecreateSelected("original"u8.ToArray());
            }
            catch { Dispose(); throw; }
        }

        internal void RecreateSelected(byte[] bytes)
        {
            Directory.CreateDirectory(Selected, Private);
            File.SetUnixFileMode(Selected, Private);
            ChownSelected(Uid, Gid);
            File.WriteAllBytes(Sentinel, bytes);
            File.SetUnixFileMode(Sentinel, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        internal void ChownSelected(uint uid, uint gid) => Assert.Equal(0, Chown(Selected, uid, gid));
        internal void ChownResults(uint uid, uint gid) => Assert.Equal(0, Chown(Results, uid, gid));
        internal void AssertSentinel() => Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(Sentinel));
        internal LinuxProtectedMetadata Metadata()
        {
            Assert.Equal(0, Statx(-100, Selected, 0x100, 0x7ff, out var value));
            return new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Uid, value.Gid, value.Mode,
                value.Nlink, value.Size, value.ChangeSeconds, value.ChangeNanoseconds, value.ModifySeconds, value.ModifyNanoseconds);
        }
        internal LinuxSelectedProducerResultsRoot Open(LinuxProtectedMetadata? expected = null,
            CancellationToken token = default, LinuxInputDeadline? deadline = null) =>
            LinuxSelectedProducerResultsRoot.Open(Selected, expected ?? Metadata(), Uid, Gid, deadline ?? Deadline, token);
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            File.SetUnixFileMode(Root, Private);
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ResultsRootFactAttribute : FactAttribute
    {
        public ResultsRootFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || GetEffectiveUid() != 0)
                Skip = "Requires actual Linux x64 root filesystem descriptors; skip is not native evidence.";
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxData
    {
        [FieldOffset(16)] internal uint Nlink;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(24)] internal uint Gid;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(96)] internal long ChangeSeconds;
        [FieldOffset(104)] internal uint ChangeNanoseconds;
        [FieldOffset(112)] internal long ModifySeconds;
        [FieldOffset(120)] internal uint ModifyNanoseconds;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxData value);
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
}
