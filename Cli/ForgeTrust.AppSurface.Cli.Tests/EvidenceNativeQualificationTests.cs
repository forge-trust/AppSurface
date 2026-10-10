using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Build-selection and original callback controls; no native owner, lease or accepted proof.</summary>
public sealed class EvidenceNativeQualificationTests
{
    [Fact]
    public async Task CompiledImageOwnsOnlyItsSelectedCheckpoint()
    {
#if EVIDENCE_PRIVATE_N04
        var expected = EvidenceNativeQualificationKind.PeerReplacement;
#elif EVIDENCE_PRIVATE_N08
        var expected = EvidenceNativeQualificationKind.CancellationBeforeAllocation;
#elif EVIDENCE_PRIVATE_N09
        var expected = EvidenceNativeQualificationKind.CancellationBeforeActivation;
#elif EVIDENCE_PRIVATE_N11
        var expected = EvidenceNativeQualificationKind.SynchronousWorkerStall;
#elif EVIDENCE_PRIVATE_N12
        var expected = EvidenceNativeQualificationKind.LeaderExitWithDescendant;
#else
        var expected = EvidenceNativeQualificationKind.None;
#endif
        Assert.Equal(expected, EvidenceNativeQualification.Current);
        Assert.Equal(expected == EvidenceNativeQualificationKind.PeerReplacement,
            EvidenceNativeQualification.PeerReplacementEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.LeaderExitWithDescendant,
            EvidenceNativeQualification.DescendantEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.SynchronousWorkerStall,
            EvidenceNativeQualification.WorkerStallEnabled);
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
