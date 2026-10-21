using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Supplementary actual root marker controls; these never construct admission or an accepted consumer proof.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxOwnerGuardNativeTests
{
    [RootMarkerFact]
    public void SuccessfulConsumptionIsOneWayAndDisposalCannotRearmTheGeneration()
    {
        using var fixture = new MarkerFixture();
        using var guard = LinuxOwnerGuard.Consume(fixture.Generation, CancellationToken.None);
        Assert.False(File.Exists(fixture.Armed));
        Assert.Equal(fixture.Bytes, File.ReadAllBytes(fixture.Consumed));
        guard.Recheck(CancellationToken.None);
        AssertClosed(() => LinuxOwnerGuard.Consume(fixture.Generation, CancellationToken.None));
        guard.Recheck(CancellationToken.None);
        guard.Dispose();
        guard.Dispose();
        AssertClosed(() => guard.Recheck(CancellationToken.None));
        Assert.False(File.Exists(fixture.Armed));
        Assert.Equal(fixture.Bytes, File.ReadAllBytes(fixture.Consumed));
    }

    [RootMarkerTheory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("wrong-bytes")]
    [InlineData("public-mode")]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    [InlineData("consumed-exists")]
    [InlineData("parent-mode")]
    public void InvalidActualMarkerCannotBeConsumedOrReplaceTheOutsideSentinel(string fault)
    {
        using var fixture = new MarkerFixture();
        var sentinel = fixture.Directory + "/sentinel";
        File.WriteAllText(sentinel, "unchanged outside marker");
        File.SetUnixFileMode(sentinel, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        switch (fault)
        {
            case "missing": File.Delete(fixture.Armed); break;
            case "empty": File.WriteAllBytes(fixture.Armed, []); break;
            case "short": File.WriteAllBytes(fixture.Armed, fixture.Bytes[..32]); break;
            case "long": File.WriteAllBytes(fixture.Armed, [.. fixture.Bytes, 0]); break;
            case "wrong-bytes": File.WriteAllBytes(fixture.Armed, new byte[33]); break;
            case "public-mode": File.SetUnixFileMode(fixture.Armed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead); break;
            case "owner": Assert.Equal(0, Chown(fixture.Armed, 12345, 0)); break;
            case "group": Assert.Equal(0, Chown(fixture.Armed, 0, 12346)); break;
            case "hardlink": Assert.Equal(0, Link(fixture.Armed, fixture.Directory + "/alias")); break;
            case "symlink": File.Delete(fixture.Armed); File.CreateSymbolicLink(fixture.Armed, sentinel); break;
            case "consumed-exists": File.WriteAllText(fixture.Consumed, "preexisting consumed"); break;
            case "parent-mode": File.SetUnixFileMode(fixture.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute); break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        AssertClosed(() => LinuxOwnerGuard.Consume(fixture.Generation, CancellationToken.None));
        Assert.Equal("unchanged outside marker", File.ReadAllText(sentinel));
        if (fault == "consumed-exists") Assert.Equal("preexisting consumed", File.ReadAllText(fixture.Consumed));
        else Assert.False(File.Exists(fixture.Consumed));
    }

    [RootMarkerTheory]
    [InlineData("rearmed")]
    [InlineData("consumed-replaced")]
    [InlineData("bytes")]
    [InlineData("mode")]
    [InlineData("parent-mode")]
    public void RecheckRejectsActualMutationAfterTheOriginalAtomicConsumption(string fault)
    {
        using var fixture = new MarkerFixture();
        using var guard = LinuxOwnerGuard.Consume(fixture.Generation, CancellationToken.None);
        switch (fault)
        {
            case "rearmed": File.WriteAllBytes(fixture.Armed, fixture.Bytes); break;
            case "consumed-replaced":
                File.Move(fixture.Consumed, fixture.Consumed + "-original");
                File.WriteAllBytes(fixture.Consumed, fixture.Bytes);
                File.SetUnixFileMode(fixture.Consumed, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                break;
            case "bytes": File.WriteAllBytes(fixture.Consumed, new byte[33]); break;
            case "mode": File.SetUnixFileMode(fixture.Consumed, UnixFileMode.UserRead); break;
            case "parent-mode": File.SetUnixFileMode(fixture.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite); break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        AssertClosed(() => guard.Recheck(CancellationToken.None));
    }

    [RootMarkerFact]
    public void CancelledAcquisitionLeavesArmedAndCancelledRecheckDoesNotUndoConsumption()
    {
        using var fixture = new MarkerFixture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => LinuxOwnerGuard.Consume(fixture.Generation, cancel.Token));
        Assert.Equal(fixture.Bytes, File.ReadAllBytes(fixture.Armed));
        Assert.False(File.Exists(fixture.Consumed));
        using var guard = LinuxOwnerGuard.Consume(fixture.Generation, CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => guard.Recheck(cancel.Token));
        guard.Recheck(CancellationToken.None);
        Assert.False(File.Exists(fixture.Armed));
        AssertClosed(() => LinuxOwnerGuard.Consume(Guid.Empty, CancellationToken.None));
    }

    private static void AssertClosed(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
    }

    private sealed class MarkerFixture : IDisposable
    {
        internal Guid Generation { get; } = Guid.NewGuid();
        internal string Directory => "/run/appsurface-evidence-owners/" + Generation.ToString("N");
        internal string Armed => Directory + "/armed";
        internal string Consumed => Directory + "/consumed";
        internal byte[] Bytes => System.Text.Encoding.ASCII.GetBytes(Generation.ToString("N") + "\n");

        internal MarkerFixture()
        {
            System.IO.Directory.CreateDirectory("/run/appsurface-evidence-owners",
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            System.IO.Directory.CreateDirectory(Directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var stream = new FileStream(Armed, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(Bytes);
            File.SetUnixFileMode(Armed, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class RootMarkerFactAttribute : FactAttribute
    {
        public RootMarkerFactAttribute() { if (!Available()) Skip = "Requires actual Linux x64 root marker custody."; }
    }

    private sealed class RootMarkerTheoryAttribute : TheoryAttribute
    {
        public RootMarkerTheoryAttribute() { if (!Available()) Skip = "Requires actual Linux x64 root marker custody."; }
    }

    private static bool Available() => OperatingSystem.IsLinux()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64 && GetEffectiveUid() == 0;

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
