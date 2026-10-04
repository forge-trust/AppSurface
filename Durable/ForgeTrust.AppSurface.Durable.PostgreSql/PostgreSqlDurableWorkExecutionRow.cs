using System.Diagnostics.Metrics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using static ForgeTrust.AppSurface.Durable.PostgreSql.PostgreSqlDurableProtocolCodec;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Closed persisted opt-in facts read under the canonical scope/Work/dispatch/permit locks.</summary>
/// <remarks>NowUtc is sampled after every row lock, never before a wait. This row grants no capability.</remarks>
internal sealed record PostgreSqlWorkExecutionRow(
    DurableScopeId ScopeId, DurableWorkId WorkId, Guid DispatchId,
    DurableWorkExecutionPolicy Policy, DurableExecutionDeadline? Deadline,
    DateTimeOffset AcceptedAtUtc, DateTimeOffset DueAtUtc, DateTimeOffset NowUtc,
    DateTimeOffset? AdmissionClosedAtUtc, string? AdmissionClosedReason,
    string State, long Revision, int AttemptNumber, long LeaseGeneration, long ScopeGeneration,
    Guid RuntimeEpoch, string? LeaseOwner, DateTimeOffset? LeaseStartedAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc, bool CancellationRequested, Guid? PermitId,
    DateTimeOffset? InvocationAdmittedAtUtc, string? PermitStatus, bool HasUncertainEffect,
    DurableProviderSafety Safety)
{
    /// <summary>A permanently observed absolute deadline, independent of the first admission closure reason.</summary>
    internal DateTimeOffset? DeadlineReachedAtUtc { get; init; }

    /// <summary>Unresolved admitted effects from earlier attempts cannot be cleared by a current no-effect proof.</summary>
    internal bool HasUncertainPriorEffect { get; init; }

    /// <summary>Descriptive persisted timing facts for inspection; no authorization is conveyed.</summary>
    internal DurableWorkExecutionSnapshot ExecutionSnapshot => new(Policy, Deadline, AcceptedAtUtc,
        Policy.AttemptPlan is { } plan && AttemptNumber >= plan.ElapsedOffsets.Count ? null : NextEligibilityUtc, CutoffUtc);

    /// <summary>Frozen cutoff; an elapsed horizon closes admission, not a current admitted success.</summary>
    internal DateTimeOffset CutoffUtc => DurableWorkTimingEvaluator.GetAdmissionCutoff(Policy, AcceptedAtUtc, Deadline);

    /// <summary>Next acceptance-relative eligibility; consumed attempts are never rebased or skipped.</summary>
    internal DateTimeOffset NextEligibilityUtc => DurableWorkTimingEvaluator.Evaluate(Policy, AcceptedAtUtc,
        AttemptNumber == int.MaxValue ? int.MaxValue : AttemptNumber + 1, Deadline, NowUtc).NextEligibilityAtUtc ?? DueAtUtc;

    /// <summary>Safe permanent admission refusal, or null; current-slot admission does not consume a new slot.</summary>
    internal string? AdmissionReason(bool consumesNextAttempt)
    {
        if (DeadlineReachedAtUtc is not null) return "deadline_elapsed";
        var attempt = consumesNextAttempt && AttemptNumber < int.MaxValue ? AttemptNumber + 1 : Math.Max(1, AttemptNumber);
        var evaluation = DurableWorkTimingEvaluator.Evaluate(Policy, AcceptedAtUtc, attempt, Deadline, NowUtc);
        var reason = evaluation.Reason switch
        {
            DurableWorkTimingReason.DeadlineElapsed => "deadline_elapsed",
            DurableWorkTimingReason.CircuitElapsed => "circuit_elapsed",
            DurableWorkTimingReason.SlotsExhausted => "slots_exhausted",
            DurableWorkTimingReason.ElapsedExhausted => "elapsed_exhausted",
            _ => null,
        };
        if (reason is not null) return reason;
        if (AdmissionClosedAtUtc is not null) return AdmissionClosedReason;
        return consumesNextAttempt && AttemptNumber >= Policy.RetryPolicy.MaximumAttempts ? "slots_exhausted" : null;
    }

    /// <summary>Checks the exact current attempt and lease without trusting the caller's snapshot or clock.</summary>
    internal bool Matches(PostgreSqlDurableWorkClaim claim) =>
        AttemptNumber == claim.AttemptNumber && LeaseGeneration == claim.LeaseGeneration
        && ScopeGeneration == claim.ScopeGeneration && RuntimeEpoch == claim.RuntimeEpoch
        && string.Equals(LeaseOwner, claim.LeaseOwner, StringComparison.Ordinal);

}

/// <summary>Post-lock clock and persistence helpers shared by the opted-in store and audited operator protocol.</summary>
internal partial class PostgreSqlDurableWorkStore
{
    private static readonly Meter ExecutionMeter = new("ForgeTrust.AppSurface.Durable");
    private static readonly Counter<long> ExecutionFencedCounter = ExecutionMeter.CreateCounter<long>("durable.work.execution_fenced");

    /// <summary>Records only closed values after authoritative refusal; listener failure cannot change truth.</summary>
    internal static void RecordExecutionRefusal(string boundary, string reason)
    {
        if (boundary is not ("acceptance" or "claim" or "permit" or "invocation" or "renewal" or "completion" or "recovery" or "operator_release"))
            throw new ArgumentOutOfRangeException(nameof(boundary));
        if (reason is not ("deadline_elapsed" or "circuit_elapsed" or "slots_exhausted" or "elapsed_exhausted" or "claim_lost" or "lease_lost" or "scope_disabled" or "epoch_mismatch" or "validation_failed"))
            throw new ArgumentOutOfRangeException(nameof(reason));
        try
        {
            ExecutionFencedCounter.Add(1, new KeyValuePair<string, object?>("boundary", boundary), new KeyValuePair<string, object?>("reason", reason));
        }
        catch (Exception)
        {
            // A consumer MeterListener is observational and must not undo or prevent the authoritative transition.
        }
    }

    /// <summary>Returns timing facts only for an opt-in row, locking rows in separate commands before sampling.</summary>
    internal static async ValueTask<PostgreSqlWorkExecutionRow?> ReadExecutionRowLockedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DurableScopeId scopeId,
        DurableWorkId workId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT work.execution_policy_schema, work.attempt_plan_version, work.attempt_plan_offsets,
                   work.maximum_circuit_microseconds, work.execution_not_after,
                   work.maximum_attempts, work.maximum_elapsed, work.initial_retry_delay, work.maximum_retry_delay,
                   work.lease_duration, work.lease_renewal_cadence, work.maximum_lease_lifetime, work.backoff_algorithm,
                   work.accepted_at, work.due_at, work.execution_admission_closed_at, work.execution_admission_closed_reason,
                   work.state, work.revision, work.attempt_number, work.lease_generation, work.scope_generation,
                   work.runtime_epoch, work.lease_owner, work.lease_started_at, work.lease_expires_at,
                   work.cancellation_requested_at IS NOT NULL, work.provider_safety, work.execution_deadline_reached_at
            FROM appsurface_durable.work AS work
            WHERE work.scope_id = @scope_id AND work.work_id = @work_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        PostgreSqlWorkExecutionRow row;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0)) return null;
            if (reader.GetString(0) != "work-execution-v1") throw new InvalidDataException("Unsupported accepted execution policy schema.");
            var retry = new DurableWorkRetryPolicy(reader.GetInt32(5), reader.GetFieldValue<TimeSpan>(6),
                reader.GetFieldValue<TimeSpan>(7), reader.GetFieldValue<TimeSpan>(8), reader.GetFieldValue<TimeSpan>(9),
                reader.GetFieldValue<TimeSpan>(10), reader.GetFieldValue<TimeSpan>(11), reader.GetString(12));
            if (retry.BackoffAlgorithm != "exponential-v1") throw new InvalidDataException("Unsupported accepted retry policy.");
            DurableWorkExecutionPolicy policy;
            if (!reader.IsDBNull(1))
            {
                if (reader.GetString(1) != "attempt-plan-v1") throw new InvalidDataException("Unsupported accepted attempt plan version.");
                var offsets = reader.GetFieldValue<long[]>(2).Select(static value => TimeSpan.FromTicks(checked(value * 10)));
                policy = DurableWorkExecutionPolicy.ForAttemptPlan(retry,
                    new DurableAttemptPlan(reader.GetString(1), offsets, TimeSpan.FromTicks(checked(reader.GetInt64(3) * 10))));
            }
            else policy = DurableWorkExecutionPolicy.FromRetryPolicy(retry);
            row = new(scopeId, workId, Guid.Empty, policy,
                reader.IsDBNull(4) ? null : new DurableExecutionDeadline(ReadUtc(reader, 4)),
                ReadUtc(reader, 13), ReadUtc(reader, 14), default,
                NullableUtc(reader, 15), reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.GetString(17), reader.GetInt64(18), reader.GetInt32(19), reader.GetInt64(20), reader.GetInt64(21),
                reader.GetGuid(22), reader.IsDBNull(23) ? null : reader.GetString(23), NullableUtc(reader, 24),
                NullableUtc(reader, 25), reader.GetBoolean(26), null, null, null, false, ParseProviderSafety(reader.GetString(27)))
            {
                DeadlineReachedAtUtc = NullableUtc(reader, 28),
            };
        }
        await using var dispatch = new NpgsqlCommand("""
            SELECT dispatch_id FROM appsurface_durable.dispatch
            WHERE scope_id = @scope_id AND aggregate_kind = 'work' AND aggregate_id = @work_id FOR UPDATE;
            """, connection, transaction);
        dispatch.Parameters.AddWithValue("scope_id", scopeId.Value);
        dispatch.Parameters.AddWithValue("work_id", workId.Value);
        var dispatchId = await dispatch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (dispatchId is not Guid id) throw new InvalidDataException("Opted-in Work has no dispatch projection.");
        await using var permits = new NpgsqlCommand("""
            SELECT permit_id, attempt_number, lease_generation, scope_generation, runtime_epoch,
                   invocation_admitted_at, status
            FROM appsurface_durable.effect_permit WHERE scope_id = @scope_id AND work_id = @work_id
            ORDER BY attempt_number, lease_generation, permit_id FOR UPDATE;
            """, connection, transaction);
        permits.Parameters.AddWithValue("scope_id", scopeId.Value);
        permits.Parameters.AddWithValue("work_id", workId.Value);
        await using (var reader = await permits.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var admitted = NullableUtc(reader, 5);
                var status = reader.GetString(6);
                var current = reader.GetInt32(1) == row.AttemptNumber && reader.GetInt64(2) == row.LeaseGeneration
                    && reader.GetInt64(3) == row.ScopeGeneration && reader.GetGuid(4) == row.RuntimeEpoch;
                if (admitted is not null && status is ("granted" or "ambiguous"))
                    row = row with { HasUncertainEffect = true, HasUncertainPriorEffect = row.HasUncertainPriorEffect || !current };
                if (current)
                    row = row with { PermitId = reader.GetGuid(0), InvocationAdmittedAtUtc = admitted, PermitStatus = status };
            }
        }
        return row with { DispatchId = id, NowUtc = await SampleExecutionTimeAsync(connection, transaction, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>Samples the volatile authoritative clock only after callers acquire required locks.</summary>
    internal static async ValueTask<DateTimeOffset> SampleExecutionTimeAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT appsurface_durable.work_execution_now();", connection, transaction);
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The execution clock returned null.")), TimeSpan.Zero);
    }

    /// <summary>Persists first closure without clearing it or altering a command's revision projection.</summary>
    internal static async ValueTask RecordAdmissionClosureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PostgreSqlWorkExecutionRow row, string reason, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE appsurface_durable.work SET execution_admission_closed_at = COALESCE(execution_admission_closed_at, @now),
                execution_admission_closed_reason = COALESCE(execution_admission_closed_reason, @reason),
                execution_deadline_reached_at = CASE WHEN @reason = 'deadline_elapsed'
                    THEN COALESCE(execution_deadline_reached_at, @now) ELSE execution_deadline_reached_at END
            WHERE scope_id = @scope_id AND work_id = @work_id AND execution_policy_schema IS NOT NULL;
            """, connection, transaction);
        command.Parameters.AddWithValue("now", row.NowUtc.UtcDateTime);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("scope_id", row.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", row.WorkId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects closed public problem precedence; ambiguous effects take precedence over timing.</summary>
    internal static string ExecutionTimingCode(PostgreSqlWorkExecutionRow row, string reason, bool uncertain) => uncertain
        ? DurableProblemCodes.AmbiguousExternalOutcome
        : reason == "deadline_elapsed" ? DurableProblemCodes.ExecutionDeadlineReached
        : row.Policy.AttemptPlan is not null ? DurableProblemCodes.AttemptPlanExhausted : "retry_policy_exhausted";

    /// <summary>Closed safety-specific suspension for an admitted outcome that remains unknown.</summary>
    internal static string ExecutionAmbiguousState(DurableProviderSafety safety) => safety switch
    {
        DurableProviderSafety.ReconcileBeforeRetry => "suspended_reconciliation_required",
        DurableProviderSafety.ManualResolution => "suspended_manual_resolution",
        _ => "suspended_ambiguous_external_outcome",
    };

    private static DateTimeOffset? NullableUtc(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ReadUtc(reader, ordinal);
}
