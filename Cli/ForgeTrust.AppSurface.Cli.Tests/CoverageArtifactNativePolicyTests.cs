using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Coverage.CoverageFileSystemInterop;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Tests managed policy from scripted native observations; native acceptance runs separately.</summary>
public sealed class CoverageArtifactNativePolicyTests
{
    [Theory]
    [InlineData(true, Architecture.Arm64, false)]
    [InlineData(false, Architecture.X64, false)]
    [InlineData(false, Architecture.Arm64, false)]
    [InlineData(true, Architecture.Arm64, true)]
    [InlineData(false, Architecture.X64, true)]
    [InlineData(false, Architecture.Arm64, true)]
    public void PrivateUnixPolicyAcceptsExactOwnerAndMode(bool mac, Architecture architecture, bool directory)
    {
        using var handle = new SafeFileHandle(123, false);
        var calls = Metadata(mac, architecture, directory ? 0x1c0u : 0x180u, 42, directory ? 2 : 1);
        CoverageRunOutputLease.ValidateArtifactUnixMetadata(handle, directory, true, calls, 42);
    }

    [Theory]
    [InlineData(0x184u, 42u, 1)]
    [InlineData(0x180u, 43u, 1)]
    [InlineData(0x180u, 42u, 2)]
    [InlineData(0x180u, 42u, 0)]
    public void PrivateUnixPolicyRejectsBroadModeForeignOwnerOrMultipleLinks(uint mode, uint owner, int links)
    {
        using var handle = new SafeFileHandle(123, false);
        Assert.Throws<IOException>(() => CoverageRunOutputLease.ValidateArtifactUnixMetadata(handle,
            false, true, Metadata(true, Architecture.Arm64, mode, owner, links), 42));
    }

    [Fact]
    public void NonprivateReadStillRequiresSingleLinkButAllowsDifferentOwnerAndMode()
    {
        using var handle = new SafeFileHandle(123, false);
        CoverageRunOutputLease.ValidateArtifactUnixMetadata(handle, false, false,
            Metadata(false, Architecture.Arm64, 0x1ff, 99, 1), 42);
        Assert.Throws<IOException>(() => CoverageRunOutputLease.ValidateArtifactUnixMetadata(handle, false, false,
            Metadata(false, Architecture.X64, 0x1ff, 99, 2), 42));
    }

    [Fact]
    public void UnixMetadataNativeFailureAndUnsupportedLayoutFailClosed()
    {
        using var handle = new SafeFileHandle(123, false);
        Assert.Throws<IOException>(() => CoverageRunOutputLease.ValidateArtifactUnixMetadata(handle, false, true,
            new CoverageArtifactStateCalls { UnixStat = (_, _) => -1 }, 42));
        Assert.Throws<PlatformNotSupportedException>(() => CoverageRunOutputLease.ValidateArtifactUnixMetadata(handle, false, true,
            Metadata(false, Architecture.X86, 0x180, 42, 1), 42));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsRenameEncodesDestinationAndReplaceAuthority(bool replace)
    {
        using var source = new SafeFileHandle(123, false);
        using var destination = new SafeFileHandle(456, false);
        var called = 0;
        CoverageRunOutputLease.RenameArtifactWindows(source, destination, "coverage-λ.xml", replace, (actualSource, buffer, size) =>
        {
            called++;
            Assert.Same(source, actualSource);
            Assert.Equal(replace ? 1 : 0, Marshal.ReadByte(buffer));
            var root = IntPtr.Size;
            var length = 2 * IntPtr.Size;
            var name = length + sizeof(uint);
            Assert.Equal(destination.DangerousGetHandle(), Marshal.ReadIntPtr(buffer, root));
            Assert.Equal(Encoding.Unicode.GetByteCount("coverage-λ.xml"), Marshal.ReadInt32(buffer, length));
            Assert.Equal("coverage-λ.xml", Marshal.PtrToStringUni(buffer + name, Marshal.ReadInt32(buffer, length) / 2));
            Assert.True(size >= name + Marshal.ReadInt32(buffer, length));
            return 0;
        });
        Assert.Equal(1, called);
    }

    [Fact]
    public void WindowsRenameMapsNativeFailureWithoutClaimingPublication()
    {
        using var source = new SafeFileHandle(123, false);
        using var destination = new SafeFileHandle(456, false);
        var exception = Assert.Throws<IOException>(() => CoverageRunOutputLease.RenameArtifactWindows(source, destination,
            "coverage.xml", false, (_, _, _) => -1, status =>
            {
                Assert.Equal(-1, status);
                return 5;
            }));
        Assert.Equal(5, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
    }

    [Fact]
    public void NewPrivateDirectoryAndFilePermissionFailuresLeaveExistingObjectsUnchanged()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "private");
        Assert.Throws<IOException>(() => CoverageRunOutputLease.AcquireArtifactDirectory(path, true, true,
            unixFChmod: (_, _) => -1));
        using var root = CoverageRunOutputLease.AcquireArtifactDirectory(path, false, true, unixFChmod: (_, _) => -1)!;
        Assert.Throws<IOException>(() => root.CreateArtifactDirectory("child"));
        Assert.Throws<IOException>(() => root.OpenArtifactFile("payload", true));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(root.ArtifactDirectoryPath).Count());
        // The failed permission call can leave a newly-created file masked to mode 000.
        // Repair only fixture-owned entries so the test can dispose its temporary tree.
        File.SetUnixFileMode(Path.Join(path, "payload"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(Path.Join(path, "child"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static CoverageArtifactStateCalls Metadata(bool mac, Architecture architecture, uint mode, uint owner, int links) => new()
    {
        IsWindows = false,
        IsMacOS = mac,
        IsLinux = !mac,
        Architecture = architecture,
        UnixStat = (_, buffer) =>
        {
            var x64 = architecture == Architecture.X64;
            if (mac)
            {
                Marshal.WriteInt16(buffer, 4, (short)mode);
                Marshal.WriteInt16(buffer, 6, (short)links);
            }
            else
            {
                Marshal.WriteInt32(buffer, x64 ? 24 : 16, (int)mode);
                if (x64) Marshal.WriteInt64(buffer, 16, links);
                else Marshal.WriteInt32(buffer, 20, links);
            }
            Marshal.WriteInt32(buffer, mac ? 16 : x64 ? 28 : 24, (int)owner);
            return 0;
        }
    };
}
