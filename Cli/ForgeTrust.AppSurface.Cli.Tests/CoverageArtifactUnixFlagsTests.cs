using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies constant selection; native no-follow behavior is separately exercised on each host.</summary>
public sealed class CoverageArtifactUnixFlagsTests
{
    [Theory]
    [InlineData(true, Architecture.Arm64, 0x00100000, 0x00000100)]
    [InlineData(true, Architecture.X64, 0x00100000, 0x00000100)]
    [InlineData(false, Architecture.X64, 0x00010000, 0x00020000)]
    [InlineData(false, Architecture.Arm64, 0x00004000, 0x00008000)]
    public void SupportedTraversalFlagsMatchNativePlatform(bool macos, Architecture architecture, int directory, int noFollow)
        => Assert.Equal((directory, noFollow), CoverageFileSystemInterop.UnixTraversalFlags(macos, architecture));

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void UnverifiedLinuxArchitectureFailsBeforeOpeningAnyPath(Architecture architecture)
        => Assert.Throws<PlatformNotSupportedException>(() => CoverageFileSystemInterop.UnixTraversalFlags(false, architecture));

    [Fact]
    public void NativeTraversalRefusesLinkedArtifactParentWithoutTouchingItsTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = TestDirectory.Create();
        var real = temp.CreateDirectory("real");
        var sentinel = Path.Join(real, "sentinel");
        File.WriteAllText(sentinel, "preserve");
        var link = Path.Join(temp.Path, "alias");
        Directory.CreateSymbolicLink(link, real);
        try
        {
            Assert.Throws<IOException>(() => CoverageRunOutputLease.AcquireArtifactDirectory(link, false, false));
            Assert.Equal("preserve", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(link); }
    }
}
