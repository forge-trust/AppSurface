using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

        using (var invalidZero = fixture.Open(["report.xml"]))
        {
            Assert.Throws<IOException>(() => invalidZero.ReadChunk("report.xml", 0, 0));
            Assert.Throws<IOException>(() => invalidZero.ReadChunk("report.xml", 0, 1));
        }
        using (var invalidLarge = fixture.Open(["report.xml"]))
        {
            Assert.Throws<IOException>(() => invalidLarge.ReadChunk("report.xml", 0, 65_537));
            Assert.Throws<IOException>(() => invalidLarge.ReadChunk("report.xml", 0, 1));
        }
        using (var invalidOffset = fixture.Open(["report.xml"]))
        {
            Assert.Throws<IOException>(() => invalidOffset.ReadChunk("report.xml", 1, 1));
            Assert.Throws<IOException>(() => invalidOffset.ReadChunk("report.xml", 0, 1));
        }
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

        using var openingCancellation = new CancellationTokenSource();
        openingCancellation.Cancel();
        var openingError = Assert.Throws<OperationCanceledException>(() =>
            fixture.Open(["report.xml"], token: openingCancellation.Token));
        Assert.Equal(openingCancellation.Token, openingError.CancellationToken);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(fixture.Results + "/report.xml"));

        using var reports = fixture.Open(["report.xml"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => reports.ReadChunk("report.xml", 0, 1, cancellation.Token));
        Assert.Throws<IOException>(() => reports.ReadChunk("report.xml", 0, 1));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(fixture.Results + "/report.xml"));
    }

    [RootCustodyFact]
    public void NestedDirectorySymlinkCannotSelectAnOutsideReport()
    {
        using var fixture = new Fixture();
        var outside = fixture.Root + "/outside";
        Directory.CreateDirectory(outside);
        fixture.Own(outside);
        System.IO.File.SetUnixFileMode(outside,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sentinel = outside + "/coverage.xml";
        System.IO.File.WriteAllBytes(sentinel, "outside report"u8.ToArray());
        fixture.Own(sentinel);
        System.IO.File.SetUnixFileMode(sentinel, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateSymbolicLink(fixture.Results + "/nested", outside);

        Assert.ThrowsAny<IOException>(() => fixture.Open(["nested/coverage.xml"]));
        Assert.Equal("outside report"u8.ToArray(), System.IO.File.ReadAllBytes(sentinel));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(sentinel));
        Assert.Equal(outside, new DirectoryInfo(fixture.Results + "/nested").LinkTarget);
    }

    [RootCustodyFact]
    public void ActualReportAtTheByteLimitReadsExactEofAndTheNextByteRejectsBeforeRetention()
    {
        using var fixture = new Fixture();
        var bytes = new byte[LinuxRetainedReportSet.MaximumFileBytes];
        bytes[0] = 1;
        bytes[^1] = 2;
        fixture.File("coverage.xml", bytes);
        using (var reports = fixture.Open(["coverage.xml"]))
        {
            var selected = Assert.Single(reports.ListReports());
            Assert.Equal(bytes.LongLength, selected.Length);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), selected.Sha256);
            using var collected = new MemoryStream();
            long offset = 0;
            while (offset < selected.Length)
            {
                var chunk = reports.ReadChunk("coverage.xml", offset, LinuxRetainedReportSet.MaximumChunkBytes);
                Assert.InRange(chunk.Length, 1, LinuxRetainedReportSet.MaximumChunkBytes);
                collected.Write(chunk);
                offset += chunk.Length;
            }
            Assert.Equal(bytes, collected.ToArray());
            Assert.Throws<IOException>(() => reports.ReadChunk("coverage.xml", offset, 1));
        }
        var path = fixture.Results + "/coverage.xml";
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Position = writer.Length;
            writer.WriteByte(3);
        }
        Assert.Equal(bytes.LongLength + 1, new FileInfo(path).Length);
        Assert.Throws<IOException>(() => fixture.Open(["coverage.xml"]));
        using var unchanged = new FileStream(path, FileMode.Open, FileAccess.Read);
        Assert.Equal(1, unchanged.ReadByte());
        unchanged.Position = bytes.LongLength - 1;
        Assert.Equal(2, unchanged.ReadByte());
        Assert.Equal(3, unchanged.ReadByte());
        Assert.Equal(-1, unchanged.ReadByte());
    }

    [RootCustodyFact]
    public void ReplacedParentOfTheSelectedRootPoisonsTheRetainedSet()
    {
        using var fixture = new Fixture();
        var originalBytes = "original bytes"u8.ToArray();
        var replacementBytes = "replacement bytes"u8.ToArray();
        fixture.File("coverage.xml", originalBytes);
        var reportsForList = fixture.Open(["coverage.xml"]);
        LinuxRetainedReportSet? reportsForChunk = null;
        var retainedRoot = fixture.Root + "-retained";
        try
        {
            reportsForChunk = fixture.Open(["coverage.xml"]);
            Assert.Single(reportsForList.ListReports());
            Assert.Single(reportsForChunk.ListReports());

            Directory.Move(fixture.Root, retainedRoot);
            Directory.CreateDirectory(fixture.Root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(fixture.Results);
            fixture.SetResultOwner();
            fixture.File("coverage.xml", replacementBytes);

            Assert.Throws<IOException>(() => reportsForList.ListReports());
            Assert.Throws<IOException>(() => reportsForChunk.ReadChunk("coverage.xml", 0, 1));
            Assert.Throws<IOException>(() => reportsForList.ReadChunk("coverage.xml", 0, 1));
            Assert.Throws<IOException>(() => reportsForChunk.ListReports());
            Assert.Equal(originalBytes, System.IO.File.ReadAllBytes(retainedRoot + "/results/coverage.xml"));
            Assert.Equal(replacementBytes, System.IO.File.ReadAllBytes(fixture.Results + "/coverage.xml"));
        }
        finally
        {
            try { reportsForList.Dispose(); }
            finally
            {
                try { reportsForChunk?.Dispose(); }
                finally
                {
                    if (Directory.Exists(retainedRoot)) Directory.Delete(retainedRoot, recursive: true);
                }
            }
        }
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
        internal void Own(string path) => Assert.Equal(0, Chown(path, Uid, Gid));
        internal void SetResultOwner() => OwnDirectory(Results);
        internal LinuxProtectedMetadata Metadata()
        {
            Assert.Equal(0, Statx(-100, Results, 0x100, 0x7ff, out var value));
            return new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Uid, value.Gid, value.Mode,
                value.Nlink, value.Size, value.ChangeSeconds, value.ChangeNanoseconds, value.ModifySeconds, value.ModifyNanoseconds);
        }
        internal LinuxRetainedReportSet Open(string[] paths, LinuxProtectedMetadata? expected = null,
            CancellationToken token = default) =>
            LinuxRetainedReportSet.OpenActual(Results, expected ?? Metadata(), paths, Uid, Gid,
                new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30)), token);
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
