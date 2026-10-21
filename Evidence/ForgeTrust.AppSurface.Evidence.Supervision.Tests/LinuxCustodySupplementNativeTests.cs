using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Supplementary real root descriptor/name controls, independent of the original 39-case receipt.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCustodySupplementNativeTests
{
    [RootCustodyFact]
    public void NestedExactInventoryReadsEachOriginalFileAndClosesAllReaders()
    {
        using var fixture = new Fixture();
        fixture.File("a/b/report.xml", [1, 2, 3]);
        fixture.File("a/empty.xml", []);
        using var reports = fixture.Open(["a/empty.xml", "a/b/report.xml"]);
        Assert.Equal(new[] { "a/b/report.xml", "a/empty.xml" }, reports.ListReports().Select(item => item.RelativePath));
        Assert.Equal(new byte[] { 1, 2, 3 }, reports.ReadChunk("a/b/report.xml", 0, 3));
        Assert.Empty(reports.ReadChunk("a/empty.xml", 0, 1));
        reports.Dispose();
        reports.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reports.ReadChunk("a/b/report.xml", 0, 1));
    }

    [RootCustodyTheory]
    [InlineData(null)]
    [InlineData("unselected.xml")]
    public void UnselectedReadPoisonsAnOtherwiseGenuineRetainedTree(string? name)
    {
        using var fixture = new Fixture();
        fixture.File("report.xml", [1]);
        using var reports = fixture.Open(["report.xml"]);
        Assert.Single(reports.ListReports());
        Assert.Throws<IOException>(() => reports.ReadChunk(name!, 0, 1));
        Assert.Throws<IOException>(() => reports.ReadChunk("report.xml", 0, 1));
        Assert.Throws<IOException>(() => reports.ListReports());
    }

    [RootCustodyTheory]
    [InlineData("device-major")]
    [InlineData("device-minor")]
    [InlineData("inode")]
    public void ActualFactoryRejectsASelectedRootPinForAnotherObject(string field)
    {
        using var fixture = new Fixture();
        fixture.File("report.xml", [1]);
        var selected = fixture.Metadata();
        using (var accepted = fixture.Open(["report.xml"], selected)) Assert.Single(accepted.ListReports());
        var changed = field switch
        {
            "device-major" => selected with { DeviceMajor = selected.DeviceMajor + 1 },
            "device-minor" => selected with { DeviceMinor = selected.DeviceMinor + 1 },
            "inode" => selected with { Inode = selected.Inode + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Assert.Throws<IOException>(() => fixture.Open(["report.xml"], changed));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(fixture.Results + "/report.xml"));
    }

    [RootCustodyTheory]
    [InlineData("missing-file")]
    [InlineData("replaced-directory")]
    public void NestedNameMutationRejectsBeforeReturningTheRetainedBytes(string fault)
    {
        using var fixture = new Fixture();
        fixture.File("nested/report.xml", [1]);
        using var reports = fixture.Open(["nested/report.xml"]);
        Assert.Single(reports.ListReports());
        if (fault == "missing-file") File.Delete(fixture.Results + "/nested/report.xml");
        else
        {
            Directory.Move(fixture.Results + "/nested", fixture.Root + "/original");
            fixture.File("nested/report.xml", [2]);
        }
        Assert.Throws<IOException>(() => reports.ReadChunk("nested/report.xml", 0, 1));
        Assert.Throws<IOException>(() => reports.ListReports());
        if (fault == "replaced-directory")
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(fixture.Root + "/original/report.xml"));
    }

    [RootCustodyFact]
    public void CancellationBeforeAChunkPoisonsTheSetAndCannotRenewItsReadPosition()
    {
        using var fixture = new Fixture();
        fixture.File("report.xml", [1, 2]);
        using var reports = fixture.Open(["report.xml"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => reports.ReadChunk("report.xml", 0, 1, cancellation.Token));
        Assert.Throws<IOException>(() => reports.ReadChunk("report.xml", 0, 1));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(fixture.Results + "/report.xml"));
    }

    private sealed class Fixture : IDisposable
    {
        private const uint Uid = 12345;
        private const uint Gid = 12346;
        internal string Root { get; } = "/run/issue779-custody-supplement-" + Guid.NewGuid().ToString("N");
        internal string Results => Root + "/results";
        internal Fixture()
        {
            Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Results);
            OwnDirectory(Results);
        }
        internal void File(string relativePath, byte[] bytes)
        {
            var parts = relativePath.Split('/');
            var parent = Results;
            foreach (var part in parts[..^1])
            {
                parent += "/" + part;
                Directory.CreateDirectory(parent);
                OwnDirectory(parent);
            }
            var path = parent + "/" + parts[^1];
            System.IO.File.WriteAllBytes(path, bytes);
            Assert.Equal(0, Chown(path, Uid, Gid));
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        private static void OwnDirectory(string path)
        {
            Assert.Equal(0, Chown(path, Uid, Gid));
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        internal LinuxProtectedMetadata Metadata()
        {
            Assert.Equal(0, Statx(-100, Results, 0x100, 0x7ff, out var value));
            return new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Uid, value.Gid, value.Mode,
                value.Nlink, value.Size, value.ChangeSeconds, value.ChangeNanoseconds, value.ModifySeconds, value.ModifyNanoseconds);
        }
        internal LinuxRetainedReportSet Open(string[] paths, LinuxProtectedMetadata? expected = null) =>
            LinuxRetainedReportSet.OpenActual(Results, expected ?? Metadata(), paths, Uid, Gid,
                new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30)));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class RootCustodyFactAttribute : FactAttribute
    {
        public RootCustodyFactAttribute() { if (!Available()) Skip = "Requires actual Linux x64 root file custody."; }
    }
    private sealed class RootCustodyTheoryAttribute : TheoryAttribute
    {
        public RootCustodyTheoryAttribute() { if (!Available()) Skip = "Requires actual Linux x64 root file custody."; }
    }
    private static bool Available() => OperatingSystem.IsLinux()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64 && GetEffectiveUid() == 0;
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
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
}
