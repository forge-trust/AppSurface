using System.Text.Json;
using Npgsql;
using static ForgeTrust.AppSurface.Durable.PostgreSql.PostgreSqlDurableProtocolCodec;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Classifies local observations under the exact locked fence without treating time as effect evidence.</summary>
internal partial class PostgreSqlDurableWorkStore
{
    private static async ValueTask<PostgreSqlWorkCompletionResult> CompleteExecutionPolicyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSqlDurableWorkClaim claim,
        PostgreSqlWorkCompletion completion, PostgreSqlWorkExecutionRow row,
        Func<NpgsqlTransaction, DurableWorkState, CancellationToken, ValueTask>? onProjectionApplied,
        CancellationToken cancellationToken)
    {
        var reachedDeadline = row.DeadlineReachedAtUtc is not null || row.AdmissionClosedReason == "deadline_elapsed"
            || row.Deadline is { } deadline && row.NowUtc >= deadline.NotAfterUtc;
        var current = row.Matches(claim) && row.State is ("leased" or "effect_permitted" or "cancel_pending");
        var deadlineCappedLease = reachedDeadline && row.Deadline is { } cap
            && row.LeaseExpiresAtUtc >= cap.NotAfterUtc;
        if (!current || (!deadlineCappedLease && row.LeaseExpiresAtUtc <= row.NowUtc)
            || (completion.Kind == PostgreSqlWorkCompletionKind.Succeeded
                && (row.PermitId is null || row.InvocationAdmittedAtUtc is null)))
        {
            RecordExecutionRefusal("completion", current ? "lease_lost" : "claim_lost");
            await InsertExecutionObservationAsync(connection, transaction, claim, completion,
                "stale_completion", "claim_lost", row.NowUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var state = ParseWorkState(row.State);
            return new(IsTerminal(state) || state == DurableWorkState.Suspended
                ? PostgreSqlWorkObservationOutcome.AlreadyTerminal : PostgreSqlWorkObservationOutcome.StaleObservation,
                state, row.Revision, null);
        }

        // A current no-effect fact clears only its own permit. Earlier admitted attempts may remain unknown.
        var explicitNoEffect = completion.Kind == PostgreSqlWorkCompletionKind.ProvenNoEffect;
        var currentUnknown = row.InvocationAdmittedAtUtc is not null && row.PermitStatus is ("granted" or "ambiguous");
        var uncertain = row.HasUncertainPriorEffect || (currentUnknown && !explicitNoEffect);
        var reason = completion.Kind == PostgreSqlWorkCompletionKind.Succeeded && !reachedDeadline
            ? row.AdmissionReason(false) : row.AdmissionReason(true);
        if (reason is not null)
            await CloseExecutionAdmissionAsync(connection, transaction, row, reason, "completion", cancellationToken).ConfigureAwait(false);

        string nextState;
        var code = completion.Code;
        var decisionReason = reason ?? "local_observation";
        var due = row.DueAtUtc;
        DurableEncodedPayload? result = null;
        string? permitStatus = null;

        if (completion.Kind == PostgreSqlWorkCompletionKind.Succeeded && !reachedDeadline)
        {
            // A circuit closes new admission but does not censor a current, already admitted success.
            nextState = row.CancellationRequested ? "succeeded_after_cancel_requested" : "succeeded";
            result = completion.Result;
            permitStatus = "known_succeeded";
        }
        else if (completion.Kind == PostgreSqlWorkCompletionKind.Succeeded)
        {
            await InsertExecutionObservationAsync(connection, transaction, claim, completion,
                "late_completion", "deadline_elapsed", row.NowUtc, cancellationToken).ConfigureAwait(false);
            nextState = ExecutionAmbiguousState(row.Safety);
            code = DurableProblemCodes.AmbiguousExternalOutcome;
            permitStatus = "ambiguous";
        }
        else if (uncertain && (reason is not null || row.CancellationRequested
            || completion.Kind is PostgreSqlWorkCompletionKind.FailedTerminal or PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome
            || row.Safety is DurableProviderSafety.ReconcileBeforeRetry or DurableProviderSafety.ManualResolution))
        {
            nextState = ExecutionAmbiguousState(row.Safety);
            code = DurableProblemCodes.AmbiguousExternalOutcome;
            permitStatus = explicitNoEffect ? "proven_no_effect" : currentUnknown ? "ambiguous" : null;
        }
        else if (row.CancellationRequested)
        {
            nextState = "canceled_before_effect";
            code = "canceled_before_effect";
            permitStatus = explicitNoEffect ? "proven_no_effect" : null;
        }
        else if (reason is not null)
        {
            nextState = "failed";
            code = ExecutionTimingCode(row, reason, false);
            permitStatus = explicitNoEffect ? "proven_no_effect" : null;
        }
        else if (completion.Kind == PostgreSqlWorkCompletionKind.ContractUnavailable)
        {
            nextState = "suspended_contract_unavailable";
        }
        else if (completion.Kind == PostgreSqlWorkCompletionKind.FailedTerminal)
        {
            nextState = "failed";
        }
        else
        {
            nextState = "retry_wait";
            due = row.Policy.AttemptPlan is not null ? row.NextEligibilityUtc
                : AddExecutionTimeCapped(row.NowUtc, PostgreSqlDurableRetryDelayCalculator.Calculate(row.Policy.RetryPolicy.BackoffAlgorithm,
                    row.AttemptNumber, row.Policy.RetryPolicy.InitialRetryDelay, row.Policy.RetryPolicy.MaximumRetryDelay), row.CutoffUtc);
            permitStatus = explicitNoEffect ? "proven_no_effect" : currentUnknown ? "ambiguous" : null;
        }
        if (reachedDeadline && completion.Kind != PostgreSqlWorkCompletionKind.Succeeded)
            await InsertExecutionObservationAsync(connection, transaction, claim, completion,
                "late_completion", "deadline_elapsed", row.NowUtc, cancellationToken).ConfigureAwait(false);
        if (permitStatus is not null && row.PermitId is { } permitId)
        {
            await using var permit = new NpgsqlCommand("""
                UPDATE appsurface_durable.effect_permit SET status=@status,observed_at=@now,details=@details::jsonb
                WHERE permit_id=@permit_id;
                """, connection, transaction);
            permit.Parameters.AddWithValue("status", permitStatus);
            permit.Parameters.AddWithValue("now", row.NowUtc.UtcDateTime);
            permit.Parameters.AddWithValue("details", completion.DetailsJson);
            permit.Parameters.AddWithValue("permit_id", permitId);
            await permit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var revision = await ApplyExecutionStateAsync(connection, transaction, row, nextState, code, decisionReason,
            due, result, cancellationToken).ConfigureAwait(false);
        var parsedState = ParseWorkState(nextState);
        if (onProjectionApplied is not null && (IsTerminal(parsedState) || parsedState == DurableWorkState.Suspended))
            await onProjectionApplied(transaction, parsedState, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(PostgreSqlWorkObservationOutcome.Applied, parsedState, revision, nextState == "retry_wait" ? due : null);
    }

    /// <summary>Appends exact old-fence evidence once, retaining codec/digest/classification/retention metadata.</summary>
    /// <remarks>The owning Work lock serializes duplicate observations; winning truth and normal results stay untouched.</remarks>
    private static async ValueTask InsertExecutionObservationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim, PostgreSqlWorkCompletion completion, string eventPrefix,
        string reason, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        var eventType = $"{eventPrefix}_{FormatCompletionKind(completion.Kind)}";
        await using var exists = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM appsurface_durable.work_history
                WHERE scope_id=@scope_id AND work_id=@work_id AND attempt_number=@attempt_number
                    AND lease_generation=@lease_generation AND scope_generation=@scope_generation AND runtime_epoch=@runtime_epoch
                    AND event_type IN (@late_event, @stale_event) AND observation_sha256 IS NOT DISTINCT FROM @observation_sha256
                    AND observation_contract_id IS NOT DISTINCT FROM @observation_contract_id
                    AND observation_schema_version IS NOT DISTINCT FROM @observation_schema_version
                    AND observation_retention_policy_id IS NOT DISTINCT FROM @observation_retention_policy_id);
            """, connection, transaction);
        AddClaimIdentityParameters(exists, claim);
        exists.Parameters.AddWithValue("late_event", $"late_completion_{FormatCompletionKind(completion.Kind)}");
        exists.Parameters.AddWithValue("stale_event", $"stale_completion_{FormatCompletionKind(completion.Kind)}");
        AddObservationParameters(exists, completion.Result);
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) return;
        // Keep quarantine evidence bounded independently of caller details; the closed reason and code suffice.
        var details = JsonSerializer.Serialize(new { code = completion.Code, reason });
        await InsertStaleCompletionHistoryAsync(connection, transaction, claim, eventType,
            completion with { DetailsJson = details }, cancellationToken, observedAtUtc).ConfigureAwait(false);
    }
}
