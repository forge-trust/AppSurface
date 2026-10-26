using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual retained-file custody controls, not producer, account or systemd acceptance.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxRetainedReportNativeTests
{
    [RootReportFact]
    public void RealInventoryHashesAndReadsOnlyOriginalMonotonicChunks()
    {
        using var fixture = new Fixture();
        var bytes = Enumerable.Range(0, 65553).Select(i => (byte)(i % 251)).ToArray();
        fixture.File("coverage.xml", bytes);
        using var reports = fixture.Open(["coverage.xml"]);
        var report = Assert.Single(reports.ListReports());
        Assert.Equal(bytes.LongLength, report.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), report.Sha256);
        Assert.Equal(bytes[..65536], reports.ReadChunk("coverage.xml", 0, 65536));
        Assert.Equal(bytes[65536..], reports.ReadChunk("coverage.xml", 65536, 65536));
        Assert.Throws<IOException>(() => reports.ReadChunk("coverage.xml", bytes.Length, 1));
        Assert.Throws<IOException>(() => reports.ListReports());
    }

    [RootReportFact]
    public void EmptyTreeAndSingleEmptyReportAreDataNotSuccessfulCoverage()
    {
        using var fixture = new Fixture();
        using (var empty = fixture.Open([])) Assert.Empty(empty.ListReports());
        fixture.File("empty.xml", []);
        using var reports = fixture.Open(["empty.xml"]);
        Assert.Equal(0, Assert.Single(reports.ListReports()).Length);
        Assert.Empty(reports.ReadChunk("empty.xml", 0, 1));
        Assert.Throws<IOException>(() => reports.ReadChunk("empty.xml", 0, 1));
    }

    [RootReportFact]
    public void OriginalRootAndFileNameSubstitutionCannotBeAdopted()
    {
        using var fixture = new Fixture();
        fixture.File("coverage.xml", "original bytes"u8.ToArray());
        using var reports = fixture.Open(["coverage.xml"]);
        Directory.Move(fixture.Results, fixture.Results + "-retained");
        Directory.CreateDirectory(fixture.Results);
        fixture.SetResultOwner();
        fixture.File("coverage.xml", "replacement bytes"u8.ToArray());
        Assert.Throws<IOException>(() => reports.ListReports());
        Assert.Throws<IOException>(() => reports.ReadChunk("coverage.xml", 0, 1));
        Assert.Equal("original bytes", System.IO.File.ReadAllText(fixture.Results + "-retained/coverage.xml"));
        Assert.Equal("replacement bytes", System.IO.File.ReadAllText(fixture.Results + "/coverage.xml"));
    }

    [RootReportFact]
    public void RealByteMutationPoisonsTheRetainedSetAndCannotRetry()
    {
        using var fixture = new Fixture();
        fixture.File("coverage.xml", "original bytes"u8.ToArray());
        using var reports = fixture.Open(["coverage.xml"]);
        using (var write = System.IO.File.OpenHandle(fixture.Results + "/coverage.xml", FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            RandomAccess.Write(write, "changed!"u8, 0);
        Assert.Throws<IOException>(() => reports.ReadChunk("coverage.xml", 0, 1));
        Assert.Throws<IOException>(() => reports.ListReports());
    }

    [RootReportFact]
    public void SafeModeChangePoisonsTheRetainedSetEvenAfterMetadataIsRestored()
    {
        using var fixture = new Fixture();
        var original = "unchanged report bytes"u8.ToArray();
        fixture.File("coverage.xml", original);
        var outside = fixture.Root + "/outside-sentinel";
        System.IO.File.WriteAllText(outside, "outside sentinel remains unchanged");
        System.IO.File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var reports = fixture.Open(["coverage.xml"]);
        Assert.Equal("coverage.xml", Assert.Single(reports.ListReports()).RelativePath);

        var reportPath = fixture.Results + "/coverage.xml";
        System.IO.File.SetUnixFileMode(reportPath, UnixFileMode.UserRead);
        Assert.Throws<IOException>(() => reports.ListReports());
        Assert.Equal(original, System.IO.File.ReadAllBytes(reportPath));

        System.IO.File.SetUnixFileMode(reportPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Throws<IOException>(() => reports.ListReports());
        Assert.Equal(original, System.IO.File.ReadAllBytes(reportPath));
        Assert.Equal("outside sentinel remains unchanged", System.IO.File.ReadAllText(outside));
    }

    [RootReportFact]
    public void SelectedReportGroupChangePoisonsTheRetainedSetWithoutChangingBytes()
    {
        using var fixture = new Fixture();
        var original = "unchanged selected report"u8.ToArray();
        fixture.File("coverage.xml", original);
        var reportPath = fixture.Results + "/coverage.xml";
        using var held = System.IO.File.OpenHandle(reportPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var outside = fixture.Root + "/outside-sentinel";
        System.IO.File.WriteAllText(outside, "outside sentinel remains unchanged");
        System.IO.File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var reports = fixture.Open(["coverage.xml"]);
        Assert.Equal("coverage.xml", Assert.Single(reports.ListReports()).RelativePath);

        Assert.Equal(0, Chown(reportPath, Fixture.Uid, Fixture.Gid + 1));
        Assert.Throws<IOException>(() => reports.ListReports());
        Assert.Equal(original, ReadHeldBytes(held, original.Length));

        Assert.Equal(0, Chown(reportPath, Fixture.Uid, Fixture.Gid));
        Assert.Throws<IOException>(() => reports.ListReports());
        Assert.Equal(original, ReadHeldBytes(held, original.Length));
        Assert.Equal(original, System.IO.File.ReadAllBytes(reportPath));
        Assert.Equal("outside sentinel remains unchanged", System.IO.File.ReadAllText(outside));
    }

    [RootReportFact]
    public void ExtraFileIsNotPartOfTheSelectedInventory()
    {
        using var fixture = new Fixture();
        fixture.File("coverage.xml", "coverage bytes"u8.ToArray());
        using var reports = fixture.Open(["coverage.xml"]);
        fixture.File("extra.xml", "extra"u8.ToArray());
        Assert.Throws<IOException>(() => reports.ListReports());
        Assert.Throws<IOException>(() => reports.ListReports());
    }

    [RootReportTheory]
    [InlineData("symlink")]
    [InlineData("hardlink")]
    [InlineData("fifo")]
    [InlineData("mode")]
    [InlineData("owner")]
    [InlineData("group")]
    public void UnsafeActualNodeRejectsWithoutReadingOrChangingOutsideSentinel(string kind)
    {
        using var fixture = new Fixture();
        var sentinel = fixture.Root + "/sentinel";
        System.IO.File.WriteAllText(sentinel, "outside bytes");
        System.IO.File.SetUnixFileMode(sentinel, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var path = fixture.Results + "/coverage.xml";
        switch (kind)
        {
            case "symlink": System.IO.File.CreateSymbolicLink(path, sentinel); break;
            case "hardlink": fixture.Own(sentinel); Assert.Equal(0, Link(sentinel, path)); break;
            case "fifo": Assert.Equal(0, MkFifo(path, 0x180)); fixture.Own(path); break;
            default:
                fixture.File("coverage.xml", "selected bytes"u8.ToArray());
                if (kind == "mode") System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
                if (kind == "owner") Assert.Equal(0, Chown(path, Fixture.Uid + 1, Fixture.Gid));
                if (kind == "group") Assert.Equal(0, Chown(path, Fixture.Uid, Fixture.Gid + 1));
                break;
        }
        Assert.ThrowsAny<IOException>(() => fixture.Open(["coverage.xml"]));
        Assert.Equal("outside bytes", System.IO.File.ReadAllText(sentinel));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(sentinel));
    }

    [RootReportFact]
    public void CancelledReadPoisonsTheSetAndDisposalStaysClosed()
    {
        using var fixture = new Fixture();
        fixture.File("coverage.xml", "bytes"u8.ToArray());
        var reports = fixture.Open(["coverage.xml"]);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => reports.ListReports(cancel.Token));
        Assert.Throws<IOException>(() => reports.ListReports());
        reports.Dispose(); reports.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reports.ListReports());
    }

    private sealed class RootReportFactAttribute : FactAttribute
    {
        public RootReportFactAttribute() { if (!Available()) Skip = "Requires actual Linux x64 root filesystem access."; }
    }
    private sealed class RootReportTheoryAttribute : TheoryAttribute
    {
        public RootReportTheoryAttribute() { if (!Available()) Skip = "Requires actual Linux x64 root filesystem access."; }
    }
    private static bool Available() => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 && GetEffectiveUid() == 0;

    private static byte[] ReadHeldBytes(SafeFileHandle handle, int length)
    {
        var bytes = new byte[length];
        Assert.Equal(length, RandomAccess.Read(handle, bytes, 0));
        return bytes;
    }

    private sealed class Fixture : IDisposable
    {
        internal const uint Uid = 12345;
        internal const uint Gid = 12346;
        internal string Root { get; } = "/run/issue779-report-test-" + Guid.NewGuid().ToString("N");
        internal string Results => Root + "/results";
        internal Fixture()
        {
            Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Results);
            SetResultOwner();
        }
        internal void SetResultOwner()
        {
            Own(Results);
            System.IO.File.SetUnixFileMode(Results, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        internal void Own(string path) => Assert.Equal(0, Chown(path, Uid, Gid));
        internal void File(string name, byte[] bytes)
        {
            var path = Results + "/" + name;
            System.IO.File.WriteAllBytes(path, bytes); Own(path);
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        internal LinuxRetainedReportSet Open(string[] paths)
        {
            Assert.Equal(0, Statx(-100, Results, 0x100, 0x7ff, out var value));
            var metadata = new LinuxProtectedMetadata(value.DeviceMajor, value.DeviceMinor, value.Inode,
                value.Uid, value.Gid, value.Mode, value.Nlink, value.Size, value.ChangeSeconds,
                value.ChangeNanoseconds, value.ModifySeconds, value.ModifyNanoseconds);
            return LinuxRetainedReportSet.OpenActual(Results, metadata, paths, Uid, Gid,
                new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30)));
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
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

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)] private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)] private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
