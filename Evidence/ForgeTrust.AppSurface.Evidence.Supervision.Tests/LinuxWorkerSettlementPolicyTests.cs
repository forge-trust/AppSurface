using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Truth-table checks for copied worker settlement booleans; no owner or native sample is fabricated.</summary>
public sealed class LinuxWorkerSettlementPolicyTests
{
    [Fact]
    public void PrefixAcceptsCompleteFacts()
    {
        LinuxWorkerSettlementPolicy.RequirePrefix(true, true, true, false, true, true);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("physical")]
    [InlineData("pending-stop")]
    [InlineData("pending-failed")]
    [InlineData("start-reservation")]
    [InlineData("exit")]
    public void PrefixRejectsOneChangedFact(string field)
    {
        AssertInvalid(() => LinuxWorkerSettlementPolicy.RequirePrefix(
            field != "stop", field != "physical", field != "pending-stop", field == "pending-failed",
            field != "start-reservation", field != "exit"));
    }

    [Fact]
    public void OutputDrainAcceptsDrainedOutputWithoutRunBudget()
    {
        LinuxWorkerSettlementPolicy.RequireOutputDrain(true);
    }

    [Fact]
    public void OutputDrainRejectsUndrainedOutput()
    {
        AssertInvalid(() => LinuxWorkerSettlementPolicy.RequireOutputDrain(false));
    }

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }
}
