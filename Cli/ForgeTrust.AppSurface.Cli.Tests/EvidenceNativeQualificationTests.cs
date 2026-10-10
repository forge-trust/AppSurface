using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Build-selection and original callback controls; no native owner, lease or accepted proof.</summary>
public sealed class EvidenceNativeQualificationTests
{
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
    [Fact]
    public void PrivateAcceptancePhaseIsOnlyDetachedDataWithAnExactClosedShape()
    {
        using var frame = System.Text.Json.JsonDocument.Parse("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":true}");
        EvidenceLinuxWorkerSupervisor.ValidateAcceptedBlockedWorkPhase(frame.RootElement);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"phase\":\"work-accepted\",\"body_blocked\":true}")]
    [InlineData("{\"ok\":true,\"phase\":\"canary-accepted\",\"body_blocked\":true}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":false}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":\"canary\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":true,\"callback\":\"canary\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":true,\"body_blocked\":true}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"Body_blocked\":true}")]
    public void PrivateAcceptancePhaseRejectsIncompleteUnclosedAndUnblockedData(string input)
    {
        using var frame = System.Text.Json.JsonDocument.Parse(input);
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceLinuxWorkerSupervisor.ValidateAcceptedBlockedWorkPhase(frame.RootElement));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message);
    }
#endif

    [Fact]
    public async Task CompiledImageOwnsOnlyItsSelectedCheckpoint()
    {
#if EVIDENCE_PRIVATE_N04
        var expected = EvidenceNativeQualificationKind.PeerReplacement;
#elif EVIDENCE_PRIVATE_N07
        var expected = EvidenceNativeQualificationKind.ParentIdentitySubstitution;
#elif EVIDENCE_PRIVATE_N08
        var expected = EvidenceNativeQualificationKind.CancellationBeforeAllocation;
#elif EVIDENCE_PRIVATE_N09
        var expected = EvidenceNativeQualificationKind.CancellationBeforeActivation;
#elif EVIDENCE_PRIVATE_N10
        var expected = EvidenceNativeQualificationKind.PendingStartRace;
#elif EVIDENCE_PRIVATE_N11
        var expected = EvidenceNativeQualificationKind.SynchronousWorkerStall;
#elif EVIDENCE_PRIVATE_N12
        var expected = EvidenceNativeQualificationKind.LeaderExitWithDescendant;
#elif EVIDENCE_PRIVATE_N15
        var expected = EvidenceNativeQualificationKind.PendingStartOwnerDeath;
#elif EVIDENCE_PRIVATE_N16
        var expected = EvidenceNativeQualificationKind.AcceptedBlockedWork;
#elif EVIDENCE_PRIVATE_N13
        var expected = EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork;
#elif EVIDENCE_PRIVATE_N14
        var expected = EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork;
#else
        var expected = EvidenceNativeQualificationKind.None;
#endif
        Assert.Equal(expected, EvidenceNativeQualification.Current);
        Assert.Equal(expected == EvidenceNativeQualificationKind.PeerReplacement,
            EvidenceNativeQualification.PeerReplacementEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.ParentIdentitySubstitution,
            EvidenceNativeQualification.ParentReplacementEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.LeaderExitWithDescendant,
            EvidenceNativeQualification.DescendantEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.PendingStartRace,
            EvidenceNativeQualification.PendingStartRaceEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.SynchronousWorkerStall,
            EvidenceNativeQualification.WorkerStallEnabled);
        Assert.Equal(expected is EvidenceNativeQualificationKind.AcceptedBlockedWork
                or EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork
                or EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork,
            EvidenceNativeQualification.AcceptedBlockedWorkEnabled);
        Assert.Equal(expected is EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork
                or EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork,
            EvidenceNativeQualification.OwnerDeathAcceptedWorkEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.AcceptedBlockedWork,
            EvidenceNativeQualification.N16StopWaitEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.PendingStartOwnerDeath,
            EvidenceNativeQualification.PendingStartOwnerDeathEnabled);
        Assert.Equal(EvidenceNativeQualification.WorkerStallEnabled,
            EvidenceFixedSynchronousInputFactory.CreateForProtectedRole() is not null);
        var checkpoint = EvidenceNativeQualification.CreateCancellationCheckpoint();
        if (expected is not (EvidenceNativeQualificationKind.CancellationBeforeAllocation
            or EvidenceNativeQualificationKind.CancellationBeforeActivation))
        {
            Assert.False(EvidenceNativeQualification.CancellationEnabled);
            Assert.Null(checkpoint); // Ordinary worker creates no wait, marker or signal prerequisite.
            return;
        }

        Assert.True(EvidenceNativeQualification.CancellationEnabled);
        Assert.NotNull(checkpoint);
        var phase = expected == EvidenceNativeQualificationKind.CancellationBeforeAllocation
            ? EvidenceOriginalCancellationPhase.BeforeAllocation : EvidenceOriginalCancellationPhase.BeforeActivation;
        Assert.Equal(EvidenceOriginalCancellationCheckpoint.SelectedPhase, phase);
        using var caller = new CancellationTokenSource();
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        using var error = new MemoryStream();
        var callback = checkpoint.WaitAtAsync(phase, error, stage.Token, caller.Token).AsTask();
        try
        {
            Assert.False(callback.IsCompleted);
            Assert.Equal(EvidenceOriginalCancellationCheckpoint.Frame, error.ToArray());
            caller.Cancel();
        }
        finally
        {
            caller.Cancel();
            await callback.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.True(caller.IsCancellationRequested);
    }
}
