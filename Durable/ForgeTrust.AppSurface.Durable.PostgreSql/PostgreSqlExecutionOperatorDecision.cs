using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>A closed, side-effect-free operator truth decision under already validated identity and locks.</summary>
internal sealed record PostgreSqlExecutionOperatorDecision(string State, string Code, string? TimingReason,
    DateTimeOffset DueAtUtc, bool Terminal)
{
    /// <summary>Checks immutable retry eligibility without granting authority or executing a projection.</summary>
    /// <remarks>Applied establishes existing effect truth; NotApplied clears only its exact permit; Unknown never expires into absence.</remarks>
    internal static PostgreSqlExecutionOperatorDecision Evaluate(PostgreSqlWorkExecutionRow row, string requestedState,
        DurableEffectReconciliationKind? proof)
    {
        var reason = row.AdmissionReason(consumesNextAttempt: requestedState == "retry_wait");
        if (proof == DurableEffectReconciliationKind.Applied)
            return new(row.CancellationRequested ? "succeeded_after_cancel_requested" : "succeeded",
                "reconciled_applied", reason, row.DueAtUtc, true);

        var uncertain = proof == DurableEffectReconciliationKind.NotApplied ? row.HasUncertainPriorEffect : row.HasUncertainEffect;
        if (proof == DurableEffectReconciliationKind.Unknown)
            return new(PostgreSqlDurableWorkStore.ExecutionAmbiguousState(row.Safety), DurableProblemCodes.AmbiguousExternalOutcome,
                reason, row.DueAtUtc, false);
        if (proof == DurableEffectReconciliationKind.NotApplied && row.HasUncertainPriorEffect)
            return new(PostgreSqlDurableWorkStore.ExecutionAmbiguousState(row.Safety), DurableProblemCodes.AmbiguousExternalOutcome,
                reason, row.DueAtUtc, false);
        if (requestedState != "retry_wait")
            return new(requestedState, uncertain ? DurableProblemCodes.AmbiguousExternalOutcome : "operator_transition",
                reason, row.DueAtUtc, requestedState is "succeeded" or "succeeded_after_cancel_requested" or "failed" or "canceled_before_effect");
        if (uncertain && (reason is not null || proof == DurableEffectReconciliationKind.NotApplied))
            return new(PostgreSqlDurableWorkStore.ExecutionAmbiguousState(row.Safety), DurableProblemCodes.AmbiguousExternalOutcome,
                reason, row.DueAtUtc, false);
        if (row.CancellationRequested && proof == DurableEffectReconciliationKind.NotApplied)
            return new("canceled_before_effect", "canceled_before_effect", reason, row.DueAtUtc, true);
        if (reason is not null)
            return new("failed", PostgreSqlDurableWorkStore.ExecutionTimingCode(row, reason, false), reason, row.DueAtUtc, true);
        return new("retry_wait", "operator_retry", null,
            row.Policy.AttemptPlan is null ? row.NowUtc : row.NextEligibilityUtc, false);
    }
}
