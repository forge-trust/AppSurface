using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached control-server state checks; no server, owner, worker, callback or native entry is created.</summary>
public sealed class LinuxControlServerStatePolicyTests
{
    [Fact]
    public void CustodyAcceptsTheAdjacentCompleteState()
    {
        LinuxControlServerStatePolicy.RequireLocalCustody(Custody());
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("io")]
    [InlineData("owners")]
    [InlineData("close")]
    public void CustodyRejectsEachRequiredStateField(string field)
    {
        AssertRejected(() => LinuxControlServerStatePolicy.RequireLocalCustody(Change(field, Custody())));
    }

    [Fact]
    public void NegativeReadyAcceptsAndRejectsItsSingleMissingField()
    {
        LinuxControlServerStatePolicy.RequireNegativeReady(new(true, true));
        AssertRejected(() => LinuxControlServerStatePolicy.RequireNegativeReady(new(false, true)));
        AssertRejected(() => LinuxControlServerStatePolicy.RequireNegativeReady(new(true, false)));
    }

    private static LinuxControlServerCustodyFacts Custody() => new(true, true, true, true);

    private static LinuxControlServerCustodyFacts Change(string field, LinuxControlServerCustodyFacts facts) => field switch
    {
        "execution" => facts with { ExecutionCompleted = false },
        "io" => facts with { IoJoined = false },
        "owners" => facts with { LocalOwnersClosed = false },
        "close" => facts with { ConnectionCloseHealthy = false },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    private static void AssertRejected(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.StartsWith("ASEVD410: The protected empty Observation control operation was rejected.", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }
}
