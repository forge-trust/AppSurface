using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Coverage;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Coverage.CoverageFileSystemInterop;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Checks native-observation decisions without claiming target-OS filesystem acceptance.</summary>
public sealed class CoverageArtifactCandidateDecisionTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("special")]
    [InlineData("metadata")]
    [InlineData("directory")]
    [InlineData("link")]
    [InlineData("overflow")]
    public void WindowsCandidateRejectsNonregularOrUnrepresentableFactsAndDisposesHandle(string condition)
    {
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "candidate");
        File.WriteAllBytes(path, [1, 2, 3]);
        SafeFileHandle? handle = null;
        var calls = new CoverageArtifactCandidateCalls
        {
            IsWindows = true,
            WindowsOpen = _ => handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read),
            WindowsType = _ => condition == "special" ? 0u : 1u,
            WindowsInformation = (SafeFileHandle _, out WindowsByHandleFileInformation info) =>
            {
                info = new()
                {
                    FileAttributes = condition == "directory" ? 0x10u : condition == "link" ? 0x400u : 0,
                    FileSizeHigh = condition == "overflow" ? uint.MaxValue : 0,
                    FileSizeLow = 3
                };
                return condition != "metadata";
            }
        };
        var accepted = CoverageArtifactInspector.TryOpenRegularFile(path, out var stream, out var length, calls);
        Assert.Equal(condition == "success", accepted);
        if (accepted)
        {
            Assert.Equal(3, length);
            Assert.NotNull(stream);
            Assert.Equal(1, stream.ReadByte());
            stream.Dispose();
        }
        else Assert.Null(stream);
        Assert.True(handle!.IsClosed);
    }

    [Fact]
    public void WindowsInvalidHandleFailsWithoutReturningAStream()
    {
        var handle = new SafeFileHandle(-1, false);
        Assert.Throws<IOException>(() => CoverageArtifactInspector.TryOpenRegularFile("unused", out _, out _,
            new CoverageArtifactCandidateCalls { IsWindows = true, WindowsOpen = _ => handle }));
        Assert.True(handle.IsClosed);
    }

    [Theory]
    [InlineData(true, Architecture.Arm64, "success")]
    [InlineData(false, Architecture.X64, "success")]
    [InlineData(false, Architecture.Arm64, "success")]
    [InlineData(false, Architecture.X86, "unsupported")]
    [InlineData(true, Architecture.Arm64, "stat")]
    [InlineData(true, Architecture.Arm64, "special")]
    [InlineData(true, Architecture.Arm64, "negative")]
    public void UnixCandidateChecksRegularKindLayoutAndLength(bool mac, Architecture architecture, string condition)
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        var path = Path.Join(temp.Path, "candidate");
        File.WriteAllBytes(path, [1, 2, 3]);
        var descriptor = -1;
        var calls = new CoverageArtifactCandidateCalls
        {
            IsWindows = false,
            IsMacOS = mac,
            IsLinux = !mac,
            Architecture = architecture,
            UnixOpen = (name, _) =>
            {
                descriptor = ForgeTrust.AppSurface.Evidence.Coverage.CoverageFileSystemInterop.UnixOpen(name, 0);
                return descriptor;
            },
            UnixStat = (_, buffer) =>
            {
                var mode = condition == "special" ? 0x4000 : 0x8000;
                if (mac) Marshal.WriteInt16(buffer, 4, unchecked((short)mode));
                else Marshal.WriteInt32(buffer, architecture == Architecture.X64 ? 24 : 16, mode);
                Marshal.WriteInt64(buffer, mac ? 96 : 48, condition == "negative" ? -1 : 3);
                return condition == "stat" ? -1 : 0;
            }
        };
        if (condition == "stat")
            Assert.Throws<IOException>(() => CoverageArtifactInspector.TryOpenRegularFile(path, out _, out _, calls));
        else
        {
            var accepted = CoverageArtifactInspector.TryOpenRegularFile(path, out var stream, out var length, calls);
            Assert.Equal(condition == "success", accepted);
            if (accepted)
            {
                Assert.Equal(3, length);
                Assert.NotNull(stream);
                Assert.Equal(1, stream.ReadByte());
                stream.Dispose();
            }
            else Assert.Null(stream);
        }
        Assert.True(descriptor >= 0);
        var nativeState = Marshal.AllocHGlobal(512);
        try { Assert.Equal(-1, ForgeTrust.AppSurface.Evidence.Coverage.CoverageFileSystemInterop.UnixFStat(descriptor, nativeState)); }
        finally { Marshal.FreeHGlobal(nativeState); }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void UnsupportedHostAndFailedUnixOpenReturnNoCandidate()
    {
        var unavailable = new CoverageArtifactCandidateCalls { IsWindows = false, IsLinux = false, IsMacOS = false };
        Assert.False(CoverageArtifactInspector.TryOpenRegularFile("unused", out var stream, out _, unavailable));
        Assert.Null(stream);
        var failed = new CoverageArtifactCandidateCalls { IsWindows = false, IsLinux = true, IsMacOS = false, UnixOpen = (_, _) => -1 };
        Assert.Throws<IOException>(() => CoverageArtifactInspector.TryOpenRegularFile("unused", out _, out _, failed));
    }
}
