using System.Collections.Immutable;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Copied producer facts only; these tests create no owner, process, unit, admission or native receipt.</summary>
public sealed class LinuxProducerSettlementTests
{
    [Fact]
    public void ReportReadinessAcceptsCompleteFactsWithoutInspectingAnExitCode()
    {
        LinuxProducerSettlementPolicy.RequireReportPrefix(true, true, true, true, Output());
        LinuxProducerSettlementPolicy.RequireReportLatches(false, false, true, true);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("physical")]
    [InlineData("normal-terminal")]
    [InlineData("stopped-terminal")]
    [InlineData("output")]
    [InlineData("output-null")]
    [InlineData("discarded")]
    [InlineData("failed")]
    [InlineData("cleanup")]
    [InlineData("group")]
    [InlineData("pump")]
    public void ReportReadinessRejectsOneChangedFact(string field)
    {
        if (field is "run" or "physical" or "normal-terminal" or "stopped-terminal")
            AssertInvalid(() => LinuxProducerSettlementPolicy.RequireReportPrefix(
                field != "run", field != "physical", field != "normal-terminal", field != "stopped-terminal", Output()));
        else if (field is "output" or "output-null" or "discarded")
            AssertInvalid(() => LinuxProducerSettlementPolicy.RequireReportPrefix(true, true, true, true,
                field == "output-null" ? null : field == "output" ? Output(failed: true) : Output(discarded: true)));
        else
            AssertInvalid(() => LinuxProducerSettlementPolicy.RequireReportLatches(
                field == "failed", field == "cleanup", field != "group", field != "pump"));
    }

    [Fact]
    public void PhysicalSettlementAcceptsNoDispatchFailedStartCleanup()
    {
        LinuxProducerSettlementPolicy.RequirePhysicalSettlement(CleanupFacts() with
        {
            Pending = Pending(true, false, true, true, true, SupervisionPendingStartFailure.StartFailed),
            PhysicallySettled = true,
            DispatchAttempted = false,
            StoppedTerminalObserved = false,
            GroupEmptyTwice = false,
            PumpJoined = false,
            OutputDrained = false,
            CollectionPending = true,
        });
    }

    [Fact]
    public void AnUnreservedStartNeedsNoStartJoinWhenTheOriginalStopAndClosureAreComplete()
    {
        LinuxProducerSettlementPolicy.RequirePhysicalSettlement(CleanupFacts() with
        {
            Pending = Pending(false, false, false, true, true, SupervisionPendingStartFailure.None),
            DispatchAttempted = false,
            StoppedTerminalObserved = false,
            GroupEmptyTwice = false,
            PumpJoined = false,
            OutputDrained = false,
        });
    }

    [Fact]
    public void PhysicalSettlementAcceptsAJoinedFailedStartWithCompleteCleanup()
    {
        LinuxProducerSettlementPolicy.RequirePhysicalSettlement(CleanupFacts() with
        {
            Pending = Pending(true, false, true, true, true, SupervisionPendingStartFailure.StartFailed),
            DispatchAttempted = true,
        });
    }

    [Theory]
    [InlineData("stop-failed")]
    [InlineData("stop-cancelled")]
    [InlineData("not-closed")]
    [InlineData("not-stop-joined")]
    [InlineData("not-start-joined")]
    [InlineData("cleanup")]
    [InlineData("physical")]
    [InlineData("terminal")]
    [InlineData("group")]
    [InlineData("pump")]
    [InlineData("drained")]
    [InlineData("collection")]
    public void PhysicalSettlementRejectsOneChangedFact(string field)
    {
        var facts = CleanupFacts() with
        {
            Pending = Pending(true, false, field != "not-start-joined", field != "not-closed",
                field != "not-stop-joined", field switch
                {
                    "stop-failed" => SupervisionPendingStartFailure.StopFailed,
                    "stop-cancelled" => SupervisionPendingStartFailure.StopCancelled,
                    _ => SupervisionPendingStartFailure.None,
                }),
            PhysicallySettled = field != "physical",
            CleanupFailed = field == "cleanup",
            StoppedTerminalObserved = field != "terminal",
            GroupEmptyTwice = field != "group",
            PumpJoined = field != "pump",
            OutputDrained = field != "drained",
            CollectionPending = field == "collection",
        };
        AssertInvalid(() => LinuxProducerSettlementPolicy.RequirePhysicalSettlement(facts));
    }

    private static SupervisionOutputReceipt Output(bool failed = false, bool discarded = false)
    {
        var failure = failed ? SupervisionOutputFailure.ReadFailed : SupervisionOutputFailure.None;
        var prefix = discarded ? ImmutableArray.Create<byte>(1) : ImmutableArray<byte>.Empty;
        var stream = new SupervisionOutputStreamReceipt(discarded ? 2 : 0, prefix, true, failure);
        return new(stream, stream, discarded ? 4 : 0, 1024, failure, false, false);
    }

    private static LinuxProducerCleanupFacts CleanupFacts() => new(
        Pending(true, false, true, true, true, SupervisionPendingStartFailure.None),
        DispatchAttempted: true, PhysicallySettled: true, CleanupFailed: false,
        StoppedTerminalObserved: true, GroupEmptyTwice: true, PumpJoined: true,
        OutputDrained: true, CollectionPending: false);

    private static SupervisionPendingStartSnapshot Pending(bool reserved, bool started, bool startJoined,
        bool closed, bool stopJoined, SupervisionPendingStartFailure failure) =>
        new(reserved, started, startJoined, closed, stopJoined, failure);

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }
}
