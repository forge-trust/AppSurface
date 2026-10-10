using System.Text;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure compile-owned phase metadata controls; no peer, owner, admission or positive run is fabricated.</summary>
public sealed class LinuxOwnerDeathPhaseFrameTests
{
    [Fact]
    public void OwnerDeathVariantIsExclusiveAndFrameIsExactBoundedData()
    {
#if EVIDENCE_PRIVATE_N13
        const string expected = "NATIVE_PRIVATE_PHASE:N13:ACCEPTED_BLOCKED_WORK\n";
#elif EVIDENCE_PRIVATE_N14
        const string expected = "NATIVE_PRIVATE_PHASE:N14:ACCEPTED_BLOCKED_WORK\n";
#else
        Assert.Null(LinuxOwnerDeathPhaseFrame.Create());
#endif
#if EVIDENCE_PRIVATE_N13 || EVIDENCE_PRIVATE_N14
        var frame = Assert.IsType<byte[]>(LinuxOwnerDeathPhaseFrame.Create());
        Assert.Equal(expected, Encoding.ASCII.GetString(frame));
        Assert.InRange(frame.Length, 1, 96);
        Assert.True(LinuxOwnerDeathPhaseFrame.IsValid(frame));
        Assert.False(LinuxOwnerDeathPhaseFrame.IsValid(frame.AsSpan(0, frame.Length - 1)));
        var altered = frame.ToArray(); altered[0] ^= 1;
        Assert.False(LinuxOwnerDeathPhaseFrame.IsValid(altered));
#endif
    }
}
