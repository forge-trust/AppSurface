using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class DurableFlowRepairContractTests
{
    private static readonly DurableScopeId Scope = new("scope");
    private static readonly DurableFlowInstanceId Flow = new("flow");
    private static readonly DurableWorkId Work = new("work");
    private static readonly DurableCommandId Command = new("command");
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Completed_and_not_applied_factories_are_closed_and_versioned()
    {
        var completed = DurableFlowRepairRequest.AssertChildEffectCompleted(
            Scope, Flow, Command, 4, Digest, Work, 7, 11, Digest, "operator", "repair");
        var notApplied = DurableFlowRepairRequest.AssertChildEffectNotApplied(
            Scope, Flow, Command, 4, Digest, Work, 7, 12, new DurableCommandId("proof"), "operator", "repair");

        Assert.Equal(DurableFlowRepairAction.AssertChildEffectCompleted, completed.Action);
        Assert.Equal("appsurface.durable.flow.repair.completed.v1", completed.Fingerprint.SchemaId);
        Assert.Equal(Digest, completed.Evidence.ExpectedChildResultSha256);
        Assert.Null(completed.Evidence.RequiredWorkOperatorCommandId);
        Assert.Equal(DurableFlowRepairAction.AssertChildEffectNotApplied, notApplied.Action);
        Assert.Equal("appsurface.durable.flow.repair.not-applied.v1", notApplied.Fingerprint.SchemaId);
        Assert.Null(notApplied.Evidence.ExpectedChildResultSha256);
        Assert.Equal(new DurableCommandId("proof"), notApplied.Evidence.RequiredWorkOperatorCommandId);
        Assert.Equal(
            DurableCommandFingerprintMatch.Exact,
            completed.Fingerprint.Compare(DurableFlowRepairRequest.AssertChildEffectCompleted(
                Scope, Flow, new DurableCommandId("retry"), 4, Digest, Work, 7, 11, Digest, "operator", "repair").Fingerprint));
        Assert.Equal(
            DurableCommandFingerprintMatch.Conflict,
            completed.Fingerprint.Compare(DurableFlowRepairRequest.AssertChildEffectCompleted(
                Scope, Flow, new DurableCommandId("retry"), 4, Digest, Work, 8, 11, Digest, "operator", "repair").Fingerprint));
    }

    [Fact]
    public void Evidence_and_result_reject_ambiguous_or_invalid_shapes()
    {
        Assert.Throws<ArgumentException>(() => DurableFlowRepairEvidenceReference.Completed(Work, 1, 1, "upper"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowRepairResult(
            (DurableFlowRepairOutcome)999, null, null, null, null));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairResult(
            DurableFlowRepairOutcome.Applied, null, null, DurableFlowState.Ready, 2));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairAssessment(
            Flow, DurableFlowState.Suspended, 1, "schema", null, null, null, null, []));

        var noEffectEvidence = DurableFlowRepairEvidenceReference.ProvenNotApplied(
            Work,
            1,
            1,
            new DurableCommandId("proof"));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairCandidate(
            DurableFlowRepairAction.AssertChildEffectCompleted,
            noEffectEvidence));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairReceipt(
            Scope,
            Flow,
            Command,
            DurableFlowRepairAction.AssertChildEffectNotApplied,
            new DurableCommandFingerprint("appsurface.durable.flow.repair.not-applied.v1", Digest),
            Digest,
            DurableFlowRepairEvidenceReference.Completed(Work, 1, 1, Digest),
            "operator",
            "repair",
            DurableFlowState.Suspended,
            1,
            DurableFlowState.WaitingForActivity,
            2,
            3,
            DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairAssessment(
            Flow,
            DurableFlowState.Suspended,
            1,
            null,
            null,
            null,
            null,
            null,
            [null!]));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairResult(
            DurableFlowRepairOutcome.Refused,
            null,
            null,
            DurableFlowState.Suspended,
            1));

        var completed = DurableFlowRepairRequest.AssertChildEffectCompleted(
            Scope, Flow, Command, 4, Digest, Work, 7, 11, Digest, "operator", "repair");
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairReceipt(
            Scope,
            Flow,
            Command,
            completed.Action,
            new DurableCommandFingerprint("appsurface.durable.flow.repair.not-applied.v1", Digest),
            Digest,
            completed.Evidence,
            completed.ActorId,
            completed.ReasonCode,
            DurableFlowState.Suspended,
            4,
            DurableFlowState.Ready,
            5,
            19,
            DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairReceipt(
            Scope,
            Flow,
            Command,
            completed.Action,
            completed.Fingerprint,
            Digest,
            completed.Evidence,
            completed.ActorId,
            completed.ReasonCode,
            DurableFlowState.Ready,
            4,
            DurableFlowState.Ready,
            5,
            19,
            DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairReceipt(
            Scope,
            Flow,
            Command,
            completed.Action,
            completed.Fingerprint,
            Digest,
            completed.Evidence,
            completed.ActorId,
            completed.ReasonCode,
            DurableFlowState.Suspended,
            4,
            DurableFlowState.WaitingForActivity,
            5,
            19,
            DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairReceipt(
            Scope,
            Flow,
            Command,
            completed.Action,
            completed.Fingerprint,
            Digest,
            completed.Evidence,
            completed.ActorId,
            completed.ReasonCode,
            DurableFlowState.Suspended,
            4,
            DurableFlowState.Ready,
            6,
            19,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Receipt_binds_action_specific_evidence_and_normalizes_timestamp()
    {
        var request = DurableFlowRepairRequest.AssertChildEffectCompleted(
            Scope, Flow, Command, 4, Digest, Work, 7, 11, Digest, "operator", "repair");
        var timestamp = new DateTimeOffset(2026, 8, 6, 3, 4, 5, TimeSpan.FromHours(-4)).AddTicks(7);
        var receipt = new DurableFlowRepairReceipt(
            Scope,
            Flow,
            Command,
            request.Action,
            request.Fingerprint,
            Digest,
            request.Evidence,
            request.ActorId,
            request.ReasonCode,
            DurableFlowState.Suspended,
            4,
            DurableFlowState.Ready,
            5,
            19,
            timestamp);
        var sameMicrosecondReceipt = new DurableFlowRepairReceipt(
            Scope,
            Flow,
            Command,
            request.Action,
            request.Fingerprint,
            Digest,
            request.Evidence,
            request.ActorId,
            request.ReasonCode,
            DurableFlowState.Suspended,
            4,
            DurableFlowState.Ready,
            5,
            19,
            timestamp.AddTicks(2));
        var changedEvidence = DurableFlowRepairRequest.AssertChildEffectCompleted(
            Scope, Flow, Command, 4, Digest, Work, 7, 11,
            "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210", "operator", "repair");
        var changedReceipt = new DurableFlowRepairReceipt(
            Scope,
            Flow,
            Command,
            changedEvidence.Action,
            changedEvidence.Fingerprint,
            Digest,
            changedEvidence.Evidence,
            changedEvidence.ActorId,
            changedEvidence.ReasonCode,
            DurableFlowState.Suspended,
            4,
            DurableFlowState.Ready,
            5,
            19,
            timestamp);

        Assert.Equal(TimeSpan.Zero, receipt.AcceptedAtUtc.Offset);
        Assert.Equal(0, receipt.AcceptedAtUtc.Ticks % 10);
        Assert.Equal(receipt.AcceptedAtUtc, sameMicrosecondReceipt.AcceptedAtUtc);
        Assert.Equal(receipt.ReceiptSha256, sameMicrosecondReceipt.ReceiptSha256);
        Assert.Equal(64, receipt.ReceiptSha256.Length);
        Assert.NotEqual(receipt.ReceiptSha256, changedReceipt.ReceiptSha256);
        Assert.Equal(DurableFlowRepairOutcome.Applied, new DurableFlowRepairResult(
            DurableFlowRepairOutcome.Applied, receipt, null, DurableFlowState.Ready, 5).Outcome);
    }

    [Fact]
    public void Assessment_defensively_copies_candidates()
    {
        var source = new List<DurableFlowRepairCandidate>
        {
            new(
                DurableFlowRepairAction.AssertChildEffectNotApplied,
                DurableFlowRepairEvidenceReference.ProvenNotApplied(Work, 3, 4, new DurableCommandId("proof"))),
        };
        var assessment = new DurableFlowRepairAssessment(
            Flow,
            DurableFlowState.Suspended,
            2,
            "appsurface.durable.flow.child-suspension.v1",
            Digest,
            Guid.NewGuid(),
            Work,
            3,
            source);
        source.Clear();

        Assert.Single(assessment.Candidates);
        Assert.False(assessment.Candidates is DurableFlowRepairCandidate[]);
        Assert.Throws<NotSupportedException>(() => ((IList<DurableFlowRepairCandidate>)assessment.Candidates).Clear());
    }

    [Fact]
    public void Contracts_preserve_valid_values_and_reject_invalid_state_shapes()
    {
        var completed = DurableFlowRepairEvidenceReference.Completed(Work, 3, 4, Digest);
        var request = DurableFlowRepairRequest.AssertChildEffectCompleted(
            Scope, Flow, Command, 2, Digest, Work, 3, 4, Digest, "operator", "repair");
        var receipt = new DurableFlowRepairReceipt(
            Scope,
            Flow,
            Command,
            request.Action,
            request.Fingerprint,
            Digest,
            completed,
            "operator",
            "repair",
            DurableFlowState.Suspended,
            2,
            DurableFlowState.Ready,
            3,
            5,
            DateTimeOffset.UtcNow);
        var problem = new DurableProblem(
            "repair_problem",
            "Repair was refused.",
            "The retained evidence changed.",
            "Reload the repair assessment.",
            new Uri("https://example.test/durable"),
            "repair-correlation");
        var accepted = new DurableFlowRepairResult(
            DurableFlowRepairOutcome.Duplicate,
            receipt,
            null,
            DurableFlowState.Ready,
            3);
        var refused = new DurableFlowRepairResult(
            DurableFlowRepairOutcome.Refused,
            null,
            problem,
            DurableFlowState.Suspended,
            2);
        var assessmentRequest = new DurableFlowRepairAssessmentRequest(Scope, Flow);
        var candidate = new DurableFlowRepairCandidate(DurableFlowRepairAction.AssertChildEffectCompleted, completed, problem);
        var assessment = new DurableFlowRepairAssessment(
            Flow,
            DurableFlowState.Suspended,
            2,
            "appsurface.durable.flow.child-suspension.v1",
            Digest,
            Guid.NewGuid(),
            Work,
            3,
            [candidate]);

        Assert.Equal(Scope, assessmentRequest.ScopeId);
        Assert.Equal(Flow, assessmentRequest.InstanceId);
        Assert.Equal(DurableFlowRepairAction.AssertChildEffectCompleted, candidate.Action);
        Assert.Same(completed, candidate.Evidence);
        Assert.Same(problem, candidate.RefusalProblem);
        Assert.Equal(Flow, assessment.InstanceId);
        Assert.Equal(DurableFlowState.Suspended, assessment.State);
        Assert.Equal(2, assessment.Revision);
        Assert.Equal("appsurface.durable.flow.child-suspension.v1", assessment.SuspensionDescriptorSchema);
        Assert.Equal(Digest, assessment.SuspensionDescriptorSha256);
        Assert.NotNull(assessment.ActivityWaitId);
        Assert.Equal(Work, assessment.ChildWorkId);
        Assert.Equal(3, assessment.ChildWorkRevision);
        Assert.Single(assessment.Candidates);
        Assert.Same(receipt, accepted.Receipt);
        Assert.Null(accepted.Problem);
        Assert.Equal(DurableFlowState.Ready, accepted.ObservedFlowState);
        Assert.Equal(3, accepted.ObservedFlowRevision);
        Assert.Null(refused.Receipt);
        Assert.Same(problem, refused.Problem);
        Assert.Equal(DurableFlowState.Suspended, refused.ObservedFlowState);
        Assert.Equal(2, refused.ObservedFlowRevision);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowRepairCandidate((DurableFlowRepairAction)99, completed));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowRepairAssessment(
            Flow, (DurableFlowState)99, 1, null, null, null, null, null, []));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairAssessment(
            Flow, DurableFlowState.Suspended, 1, null, null, Guid.NewGuid(), Work, null, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowRepairResult(
            DurableFlowRepairOutcome.Refused, null, problem, (DurableFlowState)99, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowRepairResult(
            DurableFlowRepairOutcome.Refused, null, problem, DurableFlowState.Suspended, 0));
        Assert.Throws<ArgumentException>(() => new DurableFlowRepairResult(
            DurableFlowRepairOutcome.Duplicate, receipt, problem, DurableFlowState.Ready, 3));
    }
}
