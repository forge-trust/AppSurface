using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlExecutionOperatorDecisionTests
{
    private static readonly DateTimeOffset Accepted = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ExactProofTruthTable_PreservesPriorAmbiguityAndNeverReopensClosedAdmission()
    {
        foreach (var safety in Enum.GetValues<DurableProviderSafety>())
            foreach (var proof in Enum.GetValues<DurableEffectReconciliationKind>())
                foreach (var cutoff in new[] { "open", "deadline", "circuit", "slots", "witness" })
                    foreach (var prior in new[] { false, true })
                        foreach (var canceled in new[] { false, true })
                        {
                            var row = Row(safety, cutoff, uncertain: true, prior, canceled);
                            var requested = proof == DurableEffectReconciliationKind.Applied ? "succeeded"
                                : proof == DurableEffectReconciliationKind.Unknown ? row.State
                                : canceled ? "canceled_before_effect" : "retry_wait";
                            var decision = PostgreSqlExecutionOperatorDecision.Evaluate(row, requested, proof);
                            if (proof == DurableEffectReconciliationKind.Applied)
                            {
                                Assert.True(decision.Terminal);
                                Assert.Equal(canceled ? "succeeded_after_cancel_requested" : "succeeded", decision.State);
                            }
                            else if (proof == DurableEffectReconciliationKind.Unknown || prior)
                            {
                                Assert.False(decision.Terminal);
                                Assert.Equal(PostgreSqlDurableWorkStore.ExecutionAmbiguousState(safety), decision.State);
                                Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, decision.Code);
                            }
                            else if (canceled)
                            {
                                Assert.True(decision.Terminal);
                                Assert.Equal("canceled_before_effect", decision.State);
                            }
                            else if (cutoff != "open")
                            {
                                Assert.True(decision.Terminal);
                                Assert.Equal("failed", decision.State);
                                Assert.Equal(cutoff == "deadline" ? DurableProblemCodes.ExecutionDeadlineReached : DurableProblemCodes.AttemptPlanExhausted, decision.Code);
                            }
                            else
                            {
                                Assert.False(decision.Terminal);
                                Assert.Equal("retry_wait", decision.State);
                                Assert.Equal(Accepted.AddMinutes(5), decision.DueAtUtc);
                            }
                        }
    }

    [Fact]
    public void RetryAndRecoveryTruthTable_PreservesSafeRepeatOnlyWhileAdmissionIsOpen()
    {
        foreach (var safety in Enum.GetValues<DurableProviderSafety>())
            foreach (var cutoff in new[] { "open", "deadline", "circuit", "slots", "witness" })
                foreach (var uncertain in new[] { false, true })
                {
                    var row = Row(safety, cutoff, uncertain, prior: false, canceled: false);
                    var decision = PostgreSqlExecutionOperatorDecision.Evaluate(row, "retry_wait", null);
                    if (cutoff == "open")
                    {
                        Assert.Equal("retry_wait", decision.State);
                        Assert.Equal(Accepted.AddMinutes(5), decision.DueAtUtc);
                        Assert.Null(decision.TimingReason);
                    }
                    else if (uncertain)
                    {
                        Assert.Equal(PostgreSqlDurableWorkStore.ExecutionAmbiguousState(safety), decision.State);
                        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, decision.Code);
                        Assert.False(decision.Terminal);
                    }
                    else
                    {
                        Assert.Equal("failed", decision.State);
                        Assert.True(decision.Terminal);
                    }
                }
        var legacyTiming = Row(DurableProviderSafety.Idempotent, "open", false, false, false) with
        { Policy = DurableWorkExecutionPolicy.FromRetryPolicy(Retry()), NowUtc = Accepted.AddMinutes(1) };
        Assert.Equal(legacyTiming.NowUtc, PostgreSqlExecutionOperatorDecision.Evaluate(legacyTiming, "retry_wait", null).DueAtUtc);
        var suspended = PostgreSqlExecutionOperatorDecision.Evaluate(legacyTiming, "suspended_contract_unavailable", null);
        Assert.Equal("suspended_contract_unavailable", suspended.State);
        Assert.False(suspended.Terminal);
    }

    private static PostgreSqlWorkExecutionRow Row(DurableProviderSafety safety, string cutoff, bool uncertain, bool prior, bool canceled)
    {
        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(Retry(), new("attempt-plan-v1",
            new[] { TimeSpan.Zero, TimeSpan.FromMinutes(5) }, TimeSpan.FromMinutes(10)));
        return new(new("scope"), new("work"), Guid.NewGuid(), policy,
            cutoff == "deadline" ? new(Accepted.AddMinutes(1)) : null, Accepted, Accepted,
            cutoff == "circuit" ? Accepted.AddMinutes(10) : Accepted.AddMinutes(1),
            cutoff == "witness" ? Accepted.AddSeconds(1) : null, cutoff == "witness" ? "circuit_elapsed" : null,
            PostgreSqlDurableWorkStore.ExecutionAmbiguousState(safety), 3, cutoff == "slots" ? 2 : 1, 1, 1,
            Guid.NewGuid(), null, null, null, canceled, Guid.NewGuid(), uncertain ? Accepted : null,
            uncertain ? "ambiguous" : "proven_no_effect", uncertain, safety)
        { HasUncertainPriorEffect = prior };
    }

    private static DurableWorkRetryPolicy Retry() => new(2, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), "exponential-v1");
}
