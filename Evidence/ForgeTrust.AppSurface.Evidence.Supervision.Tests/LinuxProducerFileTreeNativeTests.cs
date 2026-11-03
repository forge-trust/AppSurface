using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Supplementary root copy controls using audited sources and borrowed destination descriptors.</summary>
/// <remarks>Numeric ownership is filesystem fixture metadata, not a reserved account or workspace authority.
/// These controls do not construct an owner, admission, producer, worker or consumer proof.</remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxProducerFileTreeNativeTests
{
    private const uint SubjectUid = 12345;
    private const uint SubjectGid = 12346;
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute;
    private const UnixFileMode ReadOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode ReadOnlyFile = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    [RootCopyFact]
    public void AnExistingRealDirectoryIsNotAdoptedEvenWhenItsMetadataAlreadyMatches()
    {
        using var fixture = Fixture.Create("nested/payload.bin");
        using var source = LinuxProtectedDeployment.Open(fixture.Source, default);
        fixture.RequirePositiveCopy(source, "control", "nested/payload.bin");
        using var destination = fixture.Destination("candidate");
        var existing = fixture.DestinationPath("candidate") + "/nested";
        Directory.CreateDirectory(existing, PrivateDirectory);
        Assert.Equal(0, Chown(existing, SubjectUid, SubjectGid));
        File.SetUnixFileMode(existing, PrivateDirectory);
        var sentinel = existing + "/sentinel";
        File.WriteAllBytes(sentinel, "kept"u8.ToArray());
        var before = SamplePath(existing);
        Assert.Equal(SubjectUid, before.Uid);
        Assert.Equal(SubjectGid, before.Gid);
        Assert.Equal((ushort)0x41c0, before.Mode);

        var budget = new LinuxProducerCopyBudget();
        RequireClosed(() => Copy(source, destination, budget, fixture.Deadline));
        Assert.Equal(2, budget.Nodes);
        Assert.Equal(0L, budget.Bytes);
        Assert.Equal(before, SamplePath(existing));
        Assert.Equal("kept"u8.ToArray(), File.ReadAllBytes(sentinel));
        Assert.False(File.Exists(existing + "/payload.bin"));
        RequireBorrowedDirectory(destination);
        source.Recheck(default);
    }

    [RootCopyTheory]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    [InlineData("directory")]
    [InlineData("fifo")]
    public void ExclusiveFileOpenRejectsEveryPreexistingNodeWithoutFollowingOrReplacingIt(string kind)
    {
        using var fixture = Fixture.Create("payload.bin");
        using var source = LinuxProtectedDeployment.Open(fixture.Source, default);
        fixture.RequirePositiveCopy(source, "control", "payload.bin");
        using var destination = fixture.Destination("candidate");
        var selected = fixture.DestinationPath("candidate") + "/payload.bin";
        var sentinel = fixture.Private + "/sentinel";
        File.WriteAllBytes(sentinel, "outside selected leaf"u8.ToArray());
        switch (kind)
        {
            case "hardlink": Assert.Equal(0, Link(sentinel, selected)); break;
            case "symlink": File.CreateSymbolicLink(selected, sentinel); break;
            case "directory":
                Directory.CreateDirectory(selected, PrivateDirectory);
                File.WriteAllBytes(selected + "/kept", "directory sentinel"u8.ToArray());
                break;
            case "fifo": Assert.Equal(0, MakeFifo(selected, 0x180)); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var selectedBefore = SamplePath(selected);
        var sentinelBefore = SamplePath(sentinel);
        var budget = new LinuxProducerCopyBudget();
        var error = Assert.Throws<IOException>(() => Copy(source, destination, budget, fixture.Deadline));
        // EEXIST identifies rejection of the preexisting destination node.
        Assert.Equal(17, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        Assert.Equal(2, budget.Nodes);
        Assert.Equal((long)Fixture.Payload.Length, budget.Bytes);
        Assert.Equal(selectedBefore, SamplePath(selected));
        Assert.Equal(sentinelBefore, SamplePath(sentinel));
        Assert.Equal("outside selected leaf"u8.ToArray(), File.ReadAllBytes(sentinel));
        if (kind == "directory") Assert.Equal("directory sentinel"u8.ToArray(), File.ReadAllBytes(selected + "/kept"));
        if (kind == "symlink") Assert.Equal(sentinel, new FileInfo(selected).LinkTarget);
        RequireBorrowedDirectory(destination);
        source.Recheck(default);
    }

    [RootCopyTheory]
    [InlineData("same-length-bytes")]
    [InlineData("mode")]
    [InlineData("hardlink")]
    public void ChangedAuditedSourceRejectsBeforeAnyDestinationFileOrByteReservation(string mutation)
    {
        using var fixture = Fixture.Create("payload.bin");
        using var source = LinuxProtectedDeployment.Open(fixture.Source, default);
        fixture.RequirePositiveCopy(source, "control", "payload.bin");
        using var destination = fixture.Destination("candidate");
        var selected = fixture.Source + "/payload.bin";
        var before = SamplePath(selected);
        var previousWriteTime = File.GetLastWriteTimeUtc(selected);
        switch (mutation)
        {
            case "same-length-bytes":
                File.SetUnixFileMode(selected, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.WriteAllBytes(selected, "changed"u8.ToArray());
                File.SetUnixFileMode(selected, ReadOnlyFile);
                File.SetLastWriteTimeUtc(selected, previousWriteTime.AddDays(-1));
                Assert.Equal(Fixture.Payload.Length, File.ReadAllBytes(selected).Length);
                break;
            case "mode": File.SetUnixFileMode(selected, ReadOnlyFile | UnixFileMode.UserWrite); break;
            case "hardlink": Assert.Equal(0, Link(selected, fixture.Private + "/source-alias")); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.NotEqual(before, SamplePath(selected));

        var budget = new LinuxProducerCopyBudget();
        RequireClosed(() => Copy(source, destination, budget, fixture.Deadline));
        Assert.Equal(1, budget.Nodes);
        Assert.Equal(0L, budget.Bytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.DestinationPath("candidate")));
        Assert.Equal(Fixture.Payload, File.ReadAllBytes(fixture.DestinationPath("control") + "/payload.bin"));
        RequireBorrowedDirectory(destination);
    }

    [RootCopyFact]
    public void DisposedActualDeploymentCannotBeCopiedOrRenewItsRetainedFileOperations()
    {
        using var fixture = Fixture.Create("payload.bin");
        using var source = LinuxProtectedDeployment.Open(fixture.Source, default);
        fixture.RequirePositiveCopy(source, "control", "payload.bin");
        using var destination = fixture.Destination("candidate");
        var sentinel = fixture.Private + "/sentinel";
        File.WriteAllBytes(sentinel, "not overwritten"u8.ToArray());
        using var writer = File.OpenHandle(sentinel, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        source.Dispose();
        source.Dispose();

        var budget = new LinuxProducerCopyBudget();
        RequireClosed(() => Copy(source, destination, budget, fixture.Deadline));
        RequireClosed(() => source.RequireFileLength("payload.bin", default, fixture.Deadline));
        RequireClosed(() => source.CopyFile("payload.bin", writer, default, fixture.Deadline));
        Assert.Equal(1, budget.Nodes); // FileNames is detached data; the first retained file check still rejects.
        Assert.Equal(0L, budget.Bytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.DestinationPath("candidate")));
        Assert.Equal("not overwritten"u8.ToArray(), File.ReadAllBytes(sentinel));
        Assert.False(writer.IsClosed);
        RequireBorrowedDirectory(destination);
    }

    [RootCopyFact]
    public void AlreadyExpiredDeadlineRejectsBeforeTheFirstReservationOrNativeDestinationWrite()
    {
        using var fixture = Fixture.Create("payload.bin");
        using var source = LinuxProtectedDeployment.Open(fixture.Source, default);
        fixture.RequirePositiveCopy(source, "control", "payload.bin");
        using var destination = fixture.Destination("candidate");
        var expired = new LinuxInputDeadline(
            TimeProvider.System.GetTimestamp() - TimeProvider.System.TimestampFrequency,
            TimeSpan.FromMilliseconds(1));
        Assert.True(TimeProvider.System.GetElapsedTime(expired.StartedAt) >= expired.Allowance);
        var budget = new LinuxProducerCopyBudget();
        RequireClosed(() => Copy(source, destination, budget, expired));
        Assert.Equal(0, budget.Nodes);
        Assert.Equal(0L, budget.Bytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.DestinationPath("candidate")));
        RequireBorrowedDirectory(destination);
        source.Recheck(default);
    }

    [RootCopyFact]
    public void CancelledRetainedFileCopyLeavesTheBorrowedWritableSentinelAndSourceIntact()
    {
        using var fixture = Fixture.Create("payload.bin");
        using var source = LinuxProtectedDeployment.Open(fixture.Source, default);
        fixture.RequirePositiveCopy(source, "control", "payload.bin");
        var sentinel = fixture.Private + "/sentinel";
        File.WriteAllBytes(sentinel, "not overwritten"u8.ToArray());
        using var writer = File.OpenHandle(sentinel, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() =>
            source.CopyFile("payload.bin", writer, cancelled.Token, fixture.Deadline));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Equal("not overwritten"u8.ToArray(), File.ReadAllBytes(sentinel));
        Assert.False(writer.IsClosed);
        source.Recheck(default);
        // A separate destination checks retained-file usability, not retry or adoption by a workspace owner.
        fixture.RequirePositiveCopy(source, "independent", "payload.bin");
    }

    [RootCopyTheory]
    [InlineData("missing.bin")]
    [InlineData("../payload.bin")]
    public void RetainedCopyCannotSelectAnUninventoriedNameOrWriteTheBorrowedDestination(string name)
    {
        using var fixture = Fixture.Create("payload.bin");
        using var source = LinuxProtectedDeployment.Open(fixture.Source, default);
        fixture.RequirePositiveCopy(source, "control", "payload.bin");
        var sentinel = fixture.Private + "/sentinel";
        File.WriteAllBytes(sentinel, "not overwritten"u8.ToArray());
        using var writer = File.OpenHandle(sentinel, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        RequireClosed(() => source.CopyFile(name, writer, default, fixture.Deadline));
        Assert.Equal("not overwritten"u8.ToArray(), File.ReadAllBytes(sentinel));
        Assert.False(writer.IsClosed);
        source.Recheck(default);
    }

    private static void Copy(LinuxProtectedDeployment source, SafeFileHandle destination,
        LinuxProducerCopyBudget budget, LinuxInputDeadline deadline) =>
        LinuxProducerFileTree.Copy(source, destination, SubjectUid, SubjectGid, budget, deadline, default);

    private static void RequireClosed(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
    }

    private static void RequireBorrowedDirectory(SafeFileHandle handle)
    {
        Assert.False(handle.IsClosed);
        var value = SampleHandle(handle);
        Assert.Equal(SubjectUid, value.Uid);
        Assert.Equal(SubjectGid, value.Gid);
        Assert.Equal((ushort)0x41c0, value.Mode);
    }

    private static LinuxProtectedMetadata SamplePath(string path)
    {
        // AT_SYMLINK_NOFOLLOW also samples FIFO metadata without opening it for data.
        Assert.Equal(0, Statx(-100, path, 0x100, 0x7ff, out var value));
        return Metadata(value);
    }

    private static LinuxProtectedMetadata SampleHandle(SafeFileHandle handle)
    {
        var held = false;
        try
        {
            handle.DangerousAddRef(ref held);
            Assert.Equal(0, Statx(handle.DangerousGetHandle().ToInt32(), string.Empty, 0x1000, 0x7ff, out var value));
            return Metadata(value);
        }
        finally { if (held) handle.DangerousRelease(); }
    }

    private static LinuxProtectedMetadata Metadata(StatxData value)
    {
        Assert.Equal(0x7ffu, value.Mask & 0x7ffu);
        return new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Uid, value.Gid,
            value.Mode, value.Nlink, value.Size, value.ChangeSeconds, value.ChangeNanoseconds,
            value.ModifySeconds, value.ModifyNanoseconds);
    }

    private sealed class Fixture : IDisposable
    {
        internal static byte[] Payload => "payload"u8.ToArray();
        internal string Root { get; } = "/run/issue779-file-tree-" + Guid.NewGuid().ToString("N");
        internal string Source => Root + "/source";
        internal string Private => Root + "/private";
        internal LinuxInputDeadline Deadline { get; } =
            new(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(30));

        private Fixture() { }

        internal static Fixture Create(string relative)
        {
            var fixture = new Fixture();
            try
            {
                Directory.CreateDirectory(fixture.Root, PrivateDirectory);
                File.SetUnixFileMode(fixture.Root,
                    PrivateDirectory | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                Directory.CreateDirectory(fixture.Source, PrivateDirectory);
                Directory.CreateDirectory(fixture.Private, PrivateDirectory);
                var path = fixture.Source + "/" + relative;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!, PrivateDirectory);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(Payload);
                File.SetUnixFileMode(path, ReadOnlyFile);
                foreach (var directory in Directory.EnumerateDirectories(fixture.Source, "*",
                    SearchOption.AllDirectories))
                    File.SetUnixFileMode(directory, ReadOnlyDirectory);
                File.SetUnixFileMode(fixture.Source, ReadOnlyDirectory);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        internal string DestinationPath(string name) => Private + "/" + name;

        internal SafeFileHandle Destination(string name)
        {
            var path = DestinationPath(name);
            Directory.CreateDirectory(path, PrivateDirectory);
            Assert.Equal(0, Chown(path, SubjectUid, SubjectGid));
            File.SetUnixFileMode(path, PrivateDirectory);
            var fd = OpenDirectory(path, 0x10000 | 0x80000);
            Assert.True(fd >= 0);
            return new((nint)fd, ownsHandle: true);
        }

        internal void RequirePositiveCopy(LinuxProtectedDeployment source, string name, string relative)
        {
            using var destination = Destination(name);
            var budget = new LinuxProducerCopyBudget();
            Copy(source, destination, budget, Deadline);
            Assert.Equal(Payload, File.ReadAllBytes(DestinationPath(name) + "/" + relative));
            Assert.Equal((long)Payload.Length, budget.Bytes);
            Assert.Equal(relative.Count(static character => character == '/') + 2, budget.Nodes);
            RequireBorrowedDirectory(destination);
            source.Recheck(default);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) DeleteTree(Root);
        }

        private static void DeleteTree(string path)
        {
            File.SetUnixFileMode(path, PrivateDirectory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
                    DeleteTree(entry);
                else File.Delete(entry);
            }
            Directory.Delete(path);
        }
    }

    private sealed class RootCopyFactAttribute : FactAttribute
    {
        public RootCopyFactAttribute()
        {
            if (!Available()) Skip = "Requires actual Linux x64 root; skipping is not native proof.";
        }
    }

    private sealed class RootCopyTheoryAttribute : TheoryAttribute
    {
        public RootCopyTheoryAttribute()
        {
            if (!Available()) Skip = "Requires actual Linux x64 root; skipping is not native proof.";
        }
    }

    private static bool Available() => OperatingSystem.IsLinux()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64 && GetEffectiveUid() == 0;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxData
    {
        [FieldOffset(0)] internal uint Mask;
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
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxData value);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string created);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
}
