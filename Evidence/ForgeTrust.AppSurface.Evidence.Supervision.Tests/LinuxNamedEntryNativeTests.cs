using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Real unprivileged Linux name/descriptor checks; these issue no protected workspace or admission.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxNamedEntryNativeTests
{
    private const ulong ChildResolution = 0x02 | 0x04 | 0x08 | 0x01;
    private const ulong ParentResolution = 0x02 | 0x04 | 0x08;
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [LinuxNameFact]
    public void ActualDirectoryNameAllowsChildChurnButRejectsChangedAccessPolicyAndReplacement()
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor("selected");
        Directory.CreateDirectory(path, PrivateDirectory);
        var original = Sample(path);
        using var parent = fixture.OpenParent();
        Assert.True(LinuxNamedEntry.Matches(Descriptor(parent), "selected", original, ChildResolution));

        File.WriteAllBytes(path + "/child", [1]);
        Assert.True(LinuxNamedEntry.Matches(Descriptor(parent), "selected", original, ChildResolution));
        File.SetUnixFileMode(path, PrivateDirectory | UnixFileMode.GroupExecute);
        Assert.False(LinuxNamedEntry.Matches(Descriptor(parent), "selected", original, ChildResolution));
        File.SetUnixFileMode(path, PrivateDirectory);
        Assert.True(LinuxNamedEntry.Matches(Descriptor(parent), "selected", original, ChildResolution));

        Directory.Move(path, fixture.PathFor("retained"));
        Directory.CreateDirectory(path, PrivateDirectory);
        Assert.False(LinuxNamedEntry.Matches(Descriptor(parent), "selected", original, ChildResolution));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(fixture.PathFor("retained/child")));
        Assert.False(parent.IsClosed);
    }

    [LinuxNameFact]
    public void ActualFileNameRequiresExactMetadataAfterARealWriteAndAfterReplacement()
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor("selected");
        File.WriteAllBytes(path, [1]);
        var original = Sample(path);
        using var parent = fixture.OpenParent();
        Assert.True(LinuxNamedEntry.Matches(Descriptor(parent), "selected", original, ChildResolution));

        File.AppendAllText(path, "changed");
        Assert.False(LinuxNamedEntry.Matches(Descriptor(parent), "selected", original, ChildResolution));
        var afterWrite = Sample(path);
        Assert.True(LinuxNamedEntry.Matches(Descriptor(parent), "selected", afterWrite, ChildResolution));

        File.Move(path, fixture.PathFor("retained"));
        File.WriteAllBytes(path, File.ReadAllBytes(fixture.PathFor("retained")));
        Assert.False(LinuxNamedEntry.Matches(Descriptor(parent), "selected", afterWrite, ChildResolution));
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(fixture.PathFor("retained")));
        Assert.False(parent.IsClosed);
    }

    [LinuxNameFact]
    public void MissingSymlinkAndEscapingNamesKeepNativeRejectionAndNeverFollowTheSentinel()
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor("selected");
        File.WriteAllBytes(path, [1]);
        var original = Sample(path);
        using var parent = fixture.OpenParent();
        File.CreateSymbolicLink(fixture.PathFor("link"), path);

        var missing = Assert.Throws<IOException>(() => LinuxNamedEntry.Matches(Descriptor(parent), "missing", original, ChildResolution));
        Assert.Equal(2, Assert.IsType<Win32Exception>(missing.InnerException).NativeErrorCode);
        var linked = Assert.Throws<IOException>(() => LinuxNamedEntry.Matches(Descriptor(parent), "link", original, ChildResolution));
        Assert.Equal(40, Assert.IsType<Win32Exception>(linked.InnerException).NativeErrorCode);
        var escaped = Assert.Throws<IOException>(() => LinuxNamedEntry.Matches(Descriptor(parent), "../selected", original, ChildResolution));
        Assert.Equal(18, Assert.IsType<Win32Exception>(escaped.InnerException).NativeErrorCode);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path));
        Assert.Equal(path, new FileInfo(fixture.PathFor("link")).LinkTarget);
        Assert.False(parent.IsClosed);
    }

    [LinuxNameFact]
    public void CallerResolutionAndAtFdcwdRemainExactAndTemporaryNameHandlesNeverCloseTheParent()
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor("selected");
        File.WriteAllBytes(path, [1]);
        var original = Sample(path);
        using var parent = fixture.OpenParent();
        Assert.True(LinuxNamedEntry.Matches(Descriptor(parent), "selected", original, ParentResolution));
        Assert.True(LinuxNamedEntry.Matches(-100, path, original, 0));
        Assert.True(LinuxNamedEntry.Matches(Descriptor(parent), "selected", original, ChildResolution));
        Assert.False(parent.IsClosed);
        Assert.Equal(Sample(fixture.Root), SampleDescriptor(parent));
    }

    private static int Descriptor(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());

    private static LinuxProtectedMetadata Sample(string path)
    {
        Assert.Equal(0, Statx(-100, path, 0x100, 0x7ff, out var value));
        return value.Metadata();
    }

    private static LinuxProtectedMetadata SampleDescriptor(SafeFileHandle handle)
    {
        Assert.Equal(0, Statx(Descriptor(handle), string.Empty, 0x1000, 0x7ff, out var value));
        return value.Metadata();
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = "/tmp/issue779-named-entry-" + Guid.NewGuid().ToString("N");
        internal Fixture() => Directory.CreateDirectory(Root, PrivateDirectory);
        internal string PathFor(string name) => Root + "/" + name;
        internal SafeFileHandle OpenParent()
        {
            var descriptor = Open(Root, 0x200000 | 0x10000 | 0x80000);
            Assert.True(descriptor >= 0);
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class LinuxNameFactAttribute : FactAttribute
    {
        public LinuxNameFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                Skip = "Requires actual Linux x64 openat2/statx. No root, workspace or acceptance authority is exercised.";
        }
    }

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
        internal readonly LinuxProtectedMetadata Metadata()
        {
            Assert.Equal(0x7ffu, Mask & 0x7ffu);
            return new(DeviceMajor, DeviceMinor, Inode, Uid, Gid, Mode, Nlink, Size,
                ChangeSeconds, ChangeNanoseconds, ModifySeconds, ModifyNanoseconds);
        }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxData value);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
