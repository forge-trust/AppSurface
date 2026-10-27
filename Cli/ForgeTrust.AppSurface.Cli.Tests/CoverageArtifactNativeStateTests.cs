using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Coverage;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Coverage.CoverageFileSystemInterop;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Decision tests for native observations; these do not attest to another OS's ABI.</summary>
public sealed class CoverageArtifactNativeStateTests
{
    [Theory]
    [InlineData(true, Architecture.Arm64, 96, 48, 64)]
    [InlineData(false, Architecture.X64, 48, 88, 104)]
    [InlineData(false, Architecture.Arm64, 48, 88, 104)]
    public void UnixLayoutsPreserveLengthAndBothMutationTimestamps(bool mac, Architecture architecture,
        int lengthOffset, int modifiedOffset, int changedOffset)
    {
        using var handle = new SafeFileHandle(123, ownsHandle: false);
        var calls = Unix(mac, architecture, (_, buffer) =>
        {
            Marshal.WriteInt64(buffer, lengthOffset, 9876);
            Marshal.WriteInt64(buffer, modifiedOffset, 1234);
            Marshal.WriteInt64(buffer, modifiedOffset + 8, 5678);
            Marshal.WriteInt64(buffer, changedOffset, 9012);
            Marshal.WriteInt64(buffer, changedOffset + 8, 3456);
            return 0;
        });

        var state = CoverageRunOutputLease.GetArtifactObjectState(handle, calls);

        Assert.Equal(new CoverageArtifactObjectState("retained", 9876, 1234, 5678, 9012, 3456), state);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnixFailureAndNegativeSizeNeverReturnAUsableState(bool nativeFailure)
    {
        using var handle = new SafeFileHandle(123, ownsHandle: false);
        var calls = Unix(true, Architecture.Arm64, (_, buffer) =>
        {
            Marshal.WriteInt64(buffer, 96, -1);
            return nativeFailure ? -1 : 0;
        });
        Assert.Throws<IOException>(() => CoverageRunOutputLease.GetArtifactObjectState(handle, calls));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnsupportedHostOrUnixArchitectureFailsClosed(bool unsupportedHost)
    {
        using var handle = new SafeFileHandle(123, ownsHandle: false);
        var calls = new CoverageArtifactStateCalls
        {
            IsWindows = false,
            IsMacOS = false,
            IsLinux = !unsupportedHost,
            Architecture = Architecture.X86,
            UnixStat = (_, _) => 0,
            Identity = _ => throw new InvalidOperationException("Identity must not be read.")
        };
        Assert.Throws<PlatformNotSupportedException>(() => CoverageRunOutputLease.GetArtifactObjectState(handle, calls));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void WindowsNativeFailuresAndOverflowNeverReturnAUsableState(int failure)
    {
        using var handle = new SafeFileHandle(123, ownsHandle: false);
        var calls = Windows(failure);
        Assert.Throws<IOException>(() => CoverageRunOutputLease.GetArtifactObjectState(handle, calls));
    }

    [Fact]
    public void WindowsDecodesLargeLengthAndFileTimeSubsecondsWithoutTruncating()
    {
        using var handle = new SafeFileHandle(123, ownsHandle: false);
        var state = CoverageRunOutputLease.GetArtifactObjectState(handle, Windows(-1));
        Assert.Equal(new CoverageArtifactObjectState("retained", 4_294_967_301, 2, 3, 4, 5), state);
    }

    [Fact]
    public void UnavailableHandlesAreRejectedBeforeNativeObservation()
    {
        Assert.Throws<ArgumentNullException>(() => CoverageRunOutputLease.GetArtifactObjectState(null!));
        using var invalid = new SafeFileHandle(-1, ownsHandle: false);
        Assert.Throws<IOException>(() => CoverageRunOutputLease.GetArtifactObjectState(invalid));
        var closed = new SafeFileHandle(123, ownsHandle: false);
        closed.Dispose();
        Assert.Throws<IOException>(() => CoverageRunOutputLease.GetArtifactObjectState(closed));
    }

    private static CoverageArtifactStateCalls Unix(bool mac, Architecture architecture, Func<int, nint, int> stat) => new()
    {
        IsWindows = false,
        IsMacOS = mac,
        IsLinux = !mac,
        Architecture = architecture,
        UnixStat = stat,
        Identity = _ => "retained"
    };

    private static CoverageArtifactStateCalls Windows(int failure) => new()
    {
        IsWindows = true,
        WindowsBasic = (SafeFileHandle _, int _, out WindowsFileBasicInformation basic, uint _) =>
        {
            basic = new() { LastWriteTime = 20_000_003, ChangeTime = 40_000_005 };
            return failure != 0;
        },
        WindowsInformation = (SafeFileHandle _, out WindowsByHandleFileInformation information) =>
        {
            information = new() { FileSizeHigh = failure == 2 ? uint.MaxValue : 1, FileSizeLow = 5 };
            return failure != 1;
        },
        Identity = _ => "retained"
    };
}
