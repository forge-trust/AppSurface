using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Coverage;
using CoverageNative = ForgeTrust.AppSurface.Evidence.Coverage.CoverageFileSystemInterop;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies Darwin filesystem-stat ABI dispatch and the native call's structure layout.</summary>
public sealed class CoverageArtifactDarwinInteropTests
{
    /// <summary>Routes Intel Darwin through the inode64 entry point and other supported lanes through unsuffixed fstat.</summary>
    [Theory]
    [InlineData("macos", Architecture.X64, 1)]
    [InlineData("macos", Architecture.Arm64, 2)]
    [InlineData("linux", Architecture.X64, 2)]
    public void UnixFStatDispatch_UsesAbiSpecificCall(string platform, Architecture architecture, int expectedCall)
    {
        var standardCalls = 0;
        var intelCalls = 0;
        var result = CoverageNative.InvokeUnixFStat(platform, architecture, 17, 23,
            (descriptor, buffer) => { Assert.Equal(17, descriptor); Assert.Equal((nint)23, buffer); standardCalls++; return 31; },
            (descriptor, buffer) => { Assert.Equal(17, descriptor); Assert.Equal((nint)23, buffer); intelCalls++; return 47; });

        Assert.Equal(expectedCall == 1 ? 0 : 1, standardCalls);
        Assert.Equal(expectedCall == 1 ? 1 : 0, intelCalls);
        Assert.Equal(expectedCall == 1 ? 47 : 31, result);
    }

    /// <summary>Rejects missing injected fstat implementations before dispatch.</summary>
    [Fact]
    public void UnixFStatDispatch_RequiresBothNativeImplementations()
    {
        Func<int, nint, int> implementation = (_, _) => 0;

        Assert.Throws<ArgumentNullException>(() => CoverageNative.InvokeUnixFStat(
            "macos", Architecture.Arm64, 17, 23, null!, implementation));
        Assert.Throws<ArgumentNullException>(() => CoverageNative.InvokeUnixFStat(
            "macos", Architecture.Arm64, 17, 23, implementation, null!));
    }

    /// <summary>Routes Intel Darwin fstatfs through its inode64 symbol while arm64 and Linux keep the standard symbol.</summary>
    [Theory]
    [InlineData("macos", Architecture.X64, 1)]
    [InlineData("macos", Architecture.Arm64, 2)]
    [InlineData("linux", Architecture.Arm64, 2)]
    public void UnixFStatFsDispatch_UsesAbiSpecificCall(string platform, Architecture architecture, int expectedCall)
    {
        var standardCalls = 0;
        var intelCalls = 0;
        var result = CoverageArtifactCapability.InvokeUnixFStatFs(platform, architecture, 17, 23,
            (_, _) => { standardCalls++; return 31; },
            (_, _) => { intelCalls++; return 47; });

        Assert.Equal(expectedCall == 1 ? 0 : 1, standardCalls);
        Assert.Equal(expectedCall == 1 ? 1 : 0, intelCalls);
        Assert.Equal(expectedCall == 1 ? 47 : 31, result);
    }

    /// <summary>Rejects missing injected fstatfs implementations before dispatch.</summary>
    [Fact]
    public void UnixFStatFsDispatch_RequiresBothNativeImplementations()
    {
        Func<int, nint, int> implementation = (_, _) => 0;

        Assert.Throws<ArgumentNullException>(() => CoverageArtifactCapability.InvokeUnixFStatFs(
            "macos", Architecture.Arm64, 17, 23, null!, implementation));
        Assert.Throws<ArgumentNullException>(() => CoverageArtifactCapability.InvokeUnixFStatFs(
            "macos", Architecture.Arm64, 17, 23, implementation, null!));
    }

    /// <summary>Calls the host fstat on a real descriptor and validates fields from its native stat layout.</summary>
    [Fact]
    public void UnixFStat_OnHost_WritesExpectedStatLayout()
    {
        if (OperatingSystem.IsWindows())
            return;

        var assemblyPath = typeof(CoverageArtifactDarwinInteropTests).Assembly.Location;
        using var file = File.OpenRead(assemblyPath);
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            for (var index = 0; index < 512; index++) Marshal.WriteByte(buffer, index, 0);
            Assert.Equal(0, CoverageNative.UnixFStat(file.SafeFileHandle.DangerousGetHandle().ToInt32(), buffer));
            var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            var mode = OperatingSystem.IsMacOS()
                ? unchecked((ushort)Marshal.ReadInt16(buffer, 4))
                : RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? unchecked((uint)Marshal.ReadInt32(buffer, 24))
                    : unchecked((uint)Marshal.ReadInt32(buffer, 16));
            var fileLengthOffset = OperatingSystem.IsMacOS() ? 96 : 48;
            var length = Marshal.ReadInt64(buffer, fileLengthOffset);
            Assert.NotEqual(0UL, inode);
            Assert.NotEqual(0U, mode);
            Assert.Equal(0x8000U, mode & 0xF000U);
            Assert.Equal(new FileInfo(assemblyPath).Length, length);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
