using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using static ForgeTrust.AppSurface.Durable.PostgreSql.PostgreSqlDurableProtocolCodec;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Locked opt-in claim, lease, permit and one-use invocation admission transitions.</summary>
internal partial class PostgreSqlDurableWorkStore
{
    private async ValueTask<PostgreSqlDurableWorkClaim?> ClaimExecutionPolicyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PostgreSqlDispatchCandidate candidate, string workerId,
        PostgreSqlWorkExecutionRow row,
        Func<NpgsqlTransaction, DurableWorkState, string, CancellationToken, ValueTask>? onTransitionApplied,
        CancellationToken cancellationToken)
    {
        await using var scope = new NpgsqlCommand("SELECT generation FROM appsurface_durable.scope WHERE scope_id=@scope_id AND state='active';", connection, transaction);
        scope.Parameters.AddWithValue("scope_id", row.ScopeId.Value);
        if (await scope.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long scopeGeneration
            || scopeGeneration != row.ScopeGeneration)
        {
            RecordExecutionRefusal("claim", "lease_lost");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (row.DispatchId != candidate.DispatchId || row.Revision != candidate.ExpectedRevision)
        {
            RecordExecutionRefusal("claim", "claim_lost");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (row.RuntimeEpoch != _runtimeEpoch)
        {
            RecordExecutionRefusal("claim", "epoch_mismatch");
            if (row.State is not ("pending" or "retry_wait" or "leased" or "effect_permitted" or "cancel_pending"))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            // Recovery requires an audited release. Persist the suspension and its
            // projections so a payload-free dispatch hint cannot rediscover it forever.
            await ApplyExecutionStateAsync(connection, transaction, row, "suspended_manual_resolution",
                "runtime_epoch_mismatch", "epoch_mismatch", row.DueAtUtc, null, cancellationToken).ConfigureAwait(false);
            if (onTransitionApplied is not null)
                await onTransitionApplied(transaction, DurableWorkState.Suspended, "runtime_epoch_mismatch", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var fresh = row.State is "pending" or "retry_wait";
        var expired = row.State is "leased" or "effect_permitted" or "cancel_pending"
            && row.LeaseExpiresAtUtc <= row.NowUtc;
        var reason = row.AdmissionReason(consumesNextAttempt: fresh || expired);
        if (reason is not null && (fresh || expired || row.State is "leased" or "effect_permitted" or "cancel_pending"))
        {
            await CloseExecutionAdmissionAsync(connection, transaction, row, reason, "claim", cancellationToken).ConfigureAwait(false);
            if (!expired && !fresh && row.InvocationAdmittedAtUtc is not null && reason != "deadline_elapsed")
            {
                await UpdateExecutionDispatchAsync(connection, transaction, row, row.State, row.DueAtUtc,
                    row.LeaseExpiresAtUtc, row.Revision, true, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            var uncertain = row.HasUncertainEffect;
            var state = uncertain ? ExecutionAmbiguousState(row.Safety)
                : row.CancellationRequested ? "canceled_before_effect" : "failed";
            var code = uncertain ? DurableProblemCodes.AmbiguousExternalOutcome
                : row.CancellationRequested ? "canceled_before_effect" : ExecutionTimingCode(row, reason, false);
            await ApplyExecutionStateAsync(connection, transaction, row, state, code, reason,
                row.DueAtUtc, null, cancellationToken).ConfigureAwait(false);
            if (onTransitionApplied is not null)
                await onTransitionApplied(transaction, ParseWorkState(state), code, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (!fresh && !expired)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (expired && row.HasUncertainEffect
            && (row.Safety is DurableProviderSafety.ReconcileBeforeRetry or DurableProviderSafety.ManualResolution || row.CancellationRequested))
        {
            var state = ExecutionAmbiguousState(row.Safety);
            await ApplyExecutionStateAsync(connection, transaction, row, state, DurableProblemCodes.AmbiguousExternalOutcome,
                "ambiguous_effect", row.DueAtUtc, null, cancellationToken).ConfigureAwait(false);
            if (onTransitionApplied is not null)
                await onTransitionApplied(transaction, DurableWorkState.Suspended, DurableProblemCodes.AmbiguousExternalOutcome, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var dueAt = row.Policy.AttemptPlan is not null ? row.NextEligibilityUtc : row.DueAtUtc;
        if (row.NowUtc < dueAt)
        {
            if (expired)
            {
                // An expired pre-effect attempt consumed its slot. Persist the next eligibility
                // so an early recovery candidate cannot spin on the old lease hint.
                await ApplyExecutionStateAsync(connection, transaction, row, "retry_wait", "not_due",
                    "not_due", dueAt, null, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (row.PermitId is not null && row.InvocationAdmittedAtUtc is null)
            await MarkUnadmittedPermitNoEffectAsync(connection, transaction, row, cancellationToken).ConfigureAwait(false);
        var leaseEnd = AddExecutionTimeCapped(row.NowUtc, row.Policy.RetryPolicy.LeaseDuration,
            row.Deadline?.NotAfterUtc);
        await using var command = new NpgsqlCommand("""
            UPDATE appsurface_durable.work SET state = 'leased', attempt_number = attempt_number + 1,
                lease_generation = lease_generation + 1, lease_owner = @owner,
                lease_started_at = @now, lease_expires_at = @expiry, due_at = @due,
                updated_at = @now, revision = revision + 1
            WHERE scope_id = @scope_id AND work_id = @work_id AND revision = @revision
            RETURNING work_name, work_version, contract_id, payload_schema_version, payload_classification,
                payload, payload_sha256, payload_retention, activity_id, attempt_number, lease_generation,
                revision, lease_renewal_cadence;
            """, connection, transaction);
        AddExecutionRowParameters(command, row);
        command.Parameters.AddWithValue("owner", workerId);
        command.Parameters.AddWithValue("expiry", leaseEnd.UtcDateTime);
        command.Parameters.AddWithValue("due", dueAt.UtcDateTime);
        PostgreSqlDurableWorkClaim claim;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("Locked opted-in claim lost its revision.");
            var payload = new DurableEncodedPayload(reader.GetString(2), reader.GetString(3), ParseClassification(reader.GetString(4)),
                reader.GetFieldValue<byte[]>(5), reader.GetString(7));
            if (!reader.GetFieldValue<byte[]>(6).AsSpan().SequenceEqual(Convert.FromHexString(payload.Sha256)))
                throw new InvalidDataException("The durable payload hash does not match its authoritative bytes.");
            claim = new(row.DispatchId, row.ScopeId, row.WorkId, reader.GetString(0), reader.GetString(1), payload,
                row.Safety, workerId, reader.GetString(8), reader.GetInt32(9), reader.GetInt64(10), row.ScopeGeneration,
                row.RuntimeEpoch, row.NowUtc, leaseEnd, reader.GetInt64(11), row.CancellationRequested, reader.GetFieldValue<TimeSpan>(12))
            { Execution = CreateExecutionSnapshot(row, dueAt) };
        }
        await UpdateExecutionDispatchAsync(connection, transaction, row, "leased", dueAt, leaseEnd, claim.Revision,
            admitted: false, cancellationToken).ConfigureAwait(false);
        await InsertHistoryAsync(connection, transaction, claim, "claimed", false, "{}", cancellationToken, row.NowUtc).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claim;
    }

    private static DurableWorkExecutionSnapshot CreateExecutionSnapshot(PostgreSqlWorkExecutionRow row, DateTimeOffset? eligibility) =>
        new(row.Policy, row.Deadline, row.AcceptedAtUtc, eligibility, row.CutoffUtc);

    private static void AddExecutionRowParameters(NpgsqlCommand command, PostgreSqlWorkExecutionRow row)
    {
        command.Parameters.AddWithValue("scope_id", row.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", row.WorkId.Value);
        command.Parameters.AddWithValue("revision", row.Revision);
        command.Parameters.AddWithValue("now", row.NowUtc.UtcDateTime);
    }

    private static async ValueTask CloseExecutionAdmissionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PostgreSqlWorkExecutionRow row, string reason, string boundary, CancellationToken cancellationToken)
    {
        await RecordAdmissionClosureAsync(connection, transaction, row, reason, cancellationToken).ConfigureAwait(false);
        RecordExecutionRefusal(boundary, reason);
    }

    private static async ValueTask MarkUnadmittedPermitNoEffectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PostgreSqlWorkExecutionRow row, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE appsurface_durable.effect_permit SET status = 'proven_no_effect', observed_at = @now
            WHERE permit_id = @permit_id AND invocation_admitted_at IS NULL AND status IN ('granted', 'ambiguous');
            """, connection, transaction);
        command.Parameters.AddWithValue("permit_id", row.PermitId!.Value);
        command.Parameters.AddWithValue("now", row.NowUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maintains eligibility separately from payload-free discovery; closed projections are excluded.</summary>
    internal static async ValueTask UpdateExecutionDispatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PostgreSqlWorkExecutionRow row, string state, DateTimeOffset dueAt, DateTimeOffset? expiry, long revision,
        bool admitted, CancellationToken cancellationToken)
    {
        var dispatchState = state is "pending" or "retry_wait" ? "available"
            : state is "leased" or "effect_permitted" or "cancel_pending" ? "leased"
            : state.StartsWith("suspended_", StringComparison.Ordinal) || state == "reconciling" ? "suspended" : "terminal";
        DateTimeOffset? hint = dispatchState == "available" ? MinTime(dueAt, row.CutoffUtc)
            : dispatchState == "leased" && expiry is { } end
                ? admitted ? row.Deadline is { } deadline ? MinTime(end, deadline.NotAfterUtc) : end : MinTime(end, row.CutoffUtc)
                : null;
        await using var command = new NpgsqlCommand("""
            UPDATE appsurface_durable.dispatch SET state = @state, due_at = @due,
                execution_discovery_at = @hint, expected_revision = @revision, updated_at = @now
            WHERE dispatch_id = @dispatch_id AND scope_id = @scope_id AND aggregate_kind = 'work' AND aggregate_id = @work_id;
            """, connection, transaction);
        AddExecutionRowParameters(command, row with { Revision = revision });
        command.Parameters.AddWithValue("state", dispatchState);
        command.Parameters.AddWithValue("due", dueAt.UtcDateTime);
        command.Parameters.AddWithValue("dispatch_id", row.DispatchId);
        command.Parameters.Add(new NpgsqlParameter("hint", NpgsqlDbType.TimestampTz) { Value = hint is { } h ? h.UtcDateTime : DBNull.Value });
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Opted-in Work lost its locked dispatch projection.");
    }

    private static async ValueTask<long> ApplyExecutionStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PostgreSqlWorkExecutionRow row, string state, string code, string reason, DateTimeOffset dueAt,
        DurableEncodedPayload? result, CancellationToken cancellationToken)
    {
        if (row.PermitId is not null && row.InvocationAdmittedAtUtc is null)
            await MarkUnadmittedPermitNoEffectAsync(connection, transaction, row, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            UPDATE appsurface_durable.work SET state = @state, terminal_code = @code, due_at = @due,
                lease_owner = NULL, lease_started_at = NULL, lease_expires_at = NULL,
                terminal_at = CASE WHEN @state IN ('failed','canceled_before_effect','succeeded','succeeded_after_cancel_requested') THEN @now ELSE NULL END,
                updated_at = @now, revision = revision + 1,
                result_contract_id = @result_contract_id, result_schema_version = @result_schema_version,
                result_codec_id = @result_codec_id, result_classification = @result_classification,
                result_retention_policy_id = @result_retention_policy_id, result_payload = @result_payload, result_sha256 = @result_sha256
            WHERE scope_id = @scope_id AND work_id = @work_id AND revision = @revision RETURNING revision;
            """, connection, transaction);
        AddExecutionRowParameters(command, row);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("due", dueAt.UtcDateTime);
        AddResultParameters(command, result);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not long revision) throw new InvalidDataException("Locked opted-in transition lost its revision.");
        await UpdateExecutionDispatchAsync(connection, transaction, row, state, dueAt, null, revision, false, cancellationToken).ConfigureAwait(false);
        await using var history = new NpgsqlCommand("""
            INSERT INTO appsurface_durable.work_history (scope_id,work_id,aggregate_revision,event_type,
                attempt_number,lease_generation,scope_generation,runtime_epoch,details,observed_at)
            VALUES (@scope_id,@work_id,@revision,'execution_transition',@attempt,@lease,@generation,@epoch,@details::jsonb,@now);
            """, connection, transaction);
        AddExecutionRowParameters(history, row with { Revision = revision });
        history.Parameters.AddWithValue("attempt", row.AttemptNumber);
        history.Parameters.AddWithValue("lease", row.LeaseGeneration);
        history.Parameters.AddWithValue("generation", row.ScopeGeneration);
        history.Parameters.AddWithValue("epoch", row.RuntimeEpoch);
        history.Parameters.AddWithValue("details", JsonSerializer.Serialize(new { code, reason, lifecycle = state, plan_version = row.Policy.AttemptPlan?.Version }));
        await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var parsed = ParseWorkState(state);
        if (IsTerminal(parsed) || parsed == DurableWorkState.Suspended)
            await PostgreSqlDurableFlowActivityProjector.ProjectAsync(transaction, row.ScopeId, row.WorkId, parsed, cancellationToken).ConfigureAwait(false);
        if (IsTerminal(parsed))
            await PostgreSqlDurableScheduleWorkProjector.RequeuePendingOccurrenceAsync(transaction, row.ScopeId, row.WorkId, cancellationToken).ConfigureAwait(false);
        return revision;
    }
}
