using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual unprivileged Linux getdents checks against owned temporary nodes; no protected owner is created.</summary>
[SupportedOSPlatform("linux")]
public sealed class SupervisionDirectoryReadNativeTests
{
    [DirectoryReadFact]
    public void ActualKernelDirectoryBlocksReturnOnlyTheExactNamesAndLeaveTheBorrowedHandleOpen()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Root + "/z", [1]);
        File.WriteAllBytes(fixture.Root + "/é", [2]);
        Directory.CreateDirectory(fixture.Root + "/a");
        using var directory = fixture.OpenDirectory();
        var result = Read(directory);
        Assert.Equal(["a", "z", "é"], result);
        Assert.False(directory.IsClosed);
        Assert.Empty(Read(directory)); // This exact open-file description remains at EOF.
        using var independent = fixture.OpenDirectory();
        Assert.Equal(result, Read(independent));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(fixture.Root + "/z"));
    }

    [DirectoryReadFact]
    public void AnOrdinaryFileDescriptorReturnsNativeNotDirectoryWithoutChangingTheFile()
    {
        using var fixture = new Fixture();
        var path = fixture.Root + "/file";
        File.WriteAllBytes(path, [1, 2, 3]);
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        var error = Assert.Throws<IOException>(() => Read(file));
        Assert.Equal(20, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        Assert.False(file.IsClosed);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [DirectoryReadFact]
    public void ExactInventoryAndNameBudgetRejectActualUnexpectedEntriesWithoutModifyingThem()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Root + "/kept", [1]);
        using (var valid = fixture.OpenDirectory()) Assert.Equal(["kept"], Read(valid, 1, name => name == "kept"));
        using (var count = fixture.OpenDirectory()) Assert.Throws<IOException>(() => Read(count, 0));
        using (var extra = fixture.OpenDirectory()) Assert.Throws<IOException>(() => Read(extra, 1, name => name == "other"));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(fixture.Root + "/kept"));
    }

    [DirectoryReadFact]
    public void OriginalCancellationRunsBeforeAReadEvenWhenTheBorrowedDescriptorIsClosed()
    {
        using var fixture = new Fixture();
        using var directory = fixture.OpenDirectory();
        directory.Dispose();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => SupervisionDirectoryRead.ReadActual(
            directory, new byte[4096], () => cancelled.Token.ThrowIfCancellationRequested(),
            static () => { }, 4, 4, null, Invalid));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    private static string[] Read(SafeFileHandle handle, int maximum = 4, Func<string, bool>? accept = null) =>
        SupervisionDirectoryRead.ReadActual(handle, new byte[4096], static () => { }, static () => { },
            maximum, 4, accept, Invalid);
    private static IOException Invalid() => new("Fixed directory inventory rejection.");

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = "/tmp/issue779-directory-read-" + Guid.NewGuid().ToString("N");
        internal Fixture() => Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        internal SafeFileHandle OpenDirectory()
        {
            var descriptor = Open(Root, 0x10000 | 0x80000);
            Assert.True(descriptor >= 0);
            return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class DirectoryReadFactAttribute : FactAttribute
    {
        public DirectoryReadFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                Skip = "Requires actual Linux x64 getdents. No root or protected runtime authority is exercised.";
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
