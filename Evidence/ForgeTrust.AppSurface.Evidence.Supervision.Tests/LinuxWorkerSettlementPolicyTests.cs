using System.Collections.Immutable;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Truth-table checks for copied worker settlement booleans; no owner or native sample is fabricated.</summary>
public sealed class LinuxWorkerSettlementPolicyTests
{
    [Fact]
    public void SuccessfulCompletionDataAcceptsTheOriginalAdjacentTerminalAndOutputValues()
    {
        Assert.True(LinuxWorkerSettlementPolicy.HasSuccessfulTerminalAndOutput(
            new(Terminal(), Output())));
    }

    [Theory]
    [InlineData("missing-terminal")]
    [InlineData("nonzero-exit")]
    [InlineData("signal-exit")]
    [InlineData("missing-output")]
    [InlineData("unsuccessful-output")]
    public void SuccessfulCompletionDataRejectsNullAndAdjacentNonzeroOutcomes(string change)
    {
        var terminal = Terminal();
        var output = Output();
        var facts = change switch
        {
            "missing-terminal" => new LinuxWorkerCompletionFacts(null, output),
            "nonzero-exit" => new LinuxWorkerCompletionFacts(Terminal(status: 1), output),
            "signal-exit" => new LinuxWorkerCompletionFacts(Terminal(code: 2, status: 1), output),
            "missing-output" => new LinuxWorkerCompletionFacts(terminal, null),
            "unsuccessful-output" => new LinuxWorkerCompletionFacts(terminal, Output(successful: false)),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
        };

        Assert.False(LinuxWorkerSettlementPolicy.HasSuccessfulTerminalAndOutput(facts));
    }

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

    [Fact]
    public void FailedCaptureReadinessAcceptsTheCompleteCopiedPrerequisiteNeighbor()
    {
        LinuxWorkerSettlementPolicy.RequireFailedCaptureReadiness(ReadyForFailedCapture());
    }

    [Theory]
    [InlineData("lifetime-start")]
    [InlineData("lifetime-stop")]
    [InlineData("pending-reserved")]
    [InlineData("pending-start")]
    [InlineData("pending-closed")]
    [InlineData("pending-stop")]
    [InlineData("exit")]
    public void FailedCaptureReadinessRejectsEachMissingCopiedPrerequisite(string field)
    {
        var facts = ReadyForFailedCapture() with
        {
            LifetimeStartJoined = field != "lifetime-start",
            LifetimeStopJoined = field != "lifetime-stop",
            PendingStartReserved = field != "pending-reserved",
            PendingStartJoined = field != "pending-start",
            PendingClosed = field != "pending-closed",
            PendingStopJoined = field != "pending-stop",
            ExitCompleted = field != "exit",
        };

        AssertInvalid(() => LinuxWorkerSettlementPolicy.RequireFailedCaptureReadiness(facts));
        LinuxWorkerSettlementPolicy.RequireFailedCaptureReadiness(ReadyForFailedCapture());
    }

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }

    private static LinuxFailedCaptureReadiness ReadyForFailedCapture() =>
        new(true, true, true, true, true, true, true);

    private static LinuxUnitProperties Terminal(int code = 1, int status = 0) =>
        new("appsurface-evidence-worker-00000000000000000000000000000001.service", "loaded", "inactive", "dead",
            "/system.slice/appsurface-evidence-worker-00000000000000000000000000000001.service",
            0, 1234, code, status, "65010", "65010", "exec", "control-group", true, 60_000_000, 5_000_000);

    private static SupervisionOutputReceipt Output(bool successful = true)
    {
        var stdout = new SupervisionOutputStreamReceipt(0, ImmutableArray<byte>.Empty, true,
            successful ? SupervisionOutputFailure.None : SupervisionOutputFailure.ReadFailed);
        var stderr = new SupervisionOutputStreamReceipt(0, ImmutableArray<byte>.Empty, true, SupervisionOutputFailure.None);
        return new(stdout, stderr, 0, 1, successful ? SupervisionOutputFailure.None : SupervisionOutputFailure.ReadFailed,
            false, false);
    }
}
