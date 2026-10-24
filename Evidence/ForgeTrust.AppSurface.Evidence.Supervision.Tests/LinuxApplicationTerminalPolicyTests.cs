using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Terminal scalar policy tests only; these rows create no process, group, or exit authority.</summary>
public sealed class LinuxApplicationTerminalPolicyTests
{
    [Fact]
    public void StableTerminalScalarsAcceptEqualNonzeroOutcomeAndOptionalPidBindings()
    {
        var facts = Facts(41, 2, 9);

        LinuxApplicationTerminalPolicy.RequireStableMain(facts, facts with { }, null);
        LinuxApplicationTerminalPolicy.RequireStableMain(facts, facts with { }, 41);
    }

    [Theory]
    [InlineData(42, 2, 9, null)]
    [InlineData(41, 3, 9, null)]
    [InlineData(41, 2, 10, null)]
    [InlineData(41, 2, 9, 40U)]
    public void OneTerminalScalarOrOptionalPidMismatchRejects(uint pid, int code, int status, uint? appHostPid)
    {
        var first = Facts(41, 2, 9);
        var second = Facts(pid, code, status);

        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxApplicationTerminalPolicy.RequireStableMain(first, second, appHostPid));
        Assert.Equal("ASEVD410", error.Code);
        Assert.StartsWith("ASEVD410: Restricted application ownership or independent resource readiness was not confirmed.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void OptionalPidNullDoesNotInventAnApplicationBinding()
    {
        var first = Facts(41, 1, 0);
        var second = Facts(41, 1, 0);

        LinuxApplicationTerminalPolicy.RequireStableMain(first, second, null);
    }

    [Theory]
    [InlineData(true, "first")]
    [InlineData(false, "second")]
    public void NullTerminalFactReportsTheExactPureApiParameter(bool nullFirst, string parameterName)
    {
        var first = nullFirst ? null : Facts(41, 1, 0);
        var second = nullFirst ? Facts(41, 1, 0) : null;

        var error = Assert.Throws<ArgumentNullException>(() =>
            LinuxApplicationTerminalPolicy.RequireStableMain(first!, second!, null));

        Assert.Equal(parameterName, error.ParamName);
    }

    private static LinuxUnitProperties Facts(uint pid, int code, int status) => new(
        "appsurface-evidence-application-test.service", "loaded", "inactive", "dead", "/system.slice/test",
        0, pid, code, status, "61001", "61002", "exec", "control-group", true, 1_000_000, 5_000_000);
}
