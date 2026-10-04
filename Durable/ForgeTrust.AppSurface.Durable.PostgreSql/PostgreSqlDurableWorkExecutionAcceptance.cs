using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Opt-in acceptance freezes one post-scope-lock timestamp and preserves duplicate identity after expiry.</summary>
internal partial class PostgreSqlDurableWorkStore
{
    private static async ValueTask<DurableOperationResult<DurableWorkAcceptance>> AcceptExecutionPolicyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DurableWorkRequest request, Guid runtimeEpoch,
        long scopeGeneration, string? derivedActivityId, bool sendWakeNotification, CancellationToken cancellationToken)
    {
        // A scoped advisory lock serializes opted-in identities without upgrading the shared scope lifecycle lock.
        await using (var scopeLock = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@scope_id, 765));", connection, transaction))
        {
            scopeLock.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            await scopeLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var duplicate = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM appsurface_durable.work WHERE scope_id = @scope_id AND (command_id = @command_id OR idempotency_key = @key));", connection, transaction))
        {
            duplicate.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            duplicate.Parameters.AddWithValue("command_id", request.CommandId.Value);
            duplicate.Parameters.AddWithValue("key", request.IdempotencyKey);
            if (await duplicate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                return await ReadDuplicateAcceptanceAsync(connection, transaction, request, request.Fingerprint, cancellationToken).ConfigureAwait(false);
        }
        if (request.ExecutionPolicy.AttemptPlan is { Version: not "attempt-plan-v1" })
            return ExecutionAcceptanceFailure(request, DurableProblemCodes.ValidationFailed, "The execution plan version is not supported by this provider.", "validation_failed");
        var acceptedAt = await SampleExecutionTimeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (request.ExecutionDeadline is { } deadline && acceptedAt >= deadline.NotAfterUtc)
            return ExecutionAcceptanceFailure(request, DurableProblemCodes.ExecutionDeadlineReached, "The execution deadline was reached before new acceptance.", "deadline_elapsed");
        DateTimeOffset cutoff;
        try
        {
            cutoff = DurableWorkTimingEvaluator.GetAdmissionCutoff(request.ExecutionPolicy, acceptedAt, request.ExecutionDeadline);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ExecutionAcceptanceFailure(request, DurableProblemCodes.ValidationFailed, "The execution timing exceeds the supported timestamp range.", "validation_failed");
        }
        var workId = DurableWorkId.New();
        var dispatchId = Guid.NewGuid();
        const string sql = """
            WITH accepted AS
            (
                INSERT INTO appsurface_durable.work
                (
                    scope_id, work_id, activity_id, command_id, idempotency_key, work_name, work_version,
                    contract_id, payload_schema_version, codec_id, payload, payload_sha256,
                    payload_classification, payload_retention, request_fingerprint_schema, request_fingerprint_sha256,
                    state, provider_safety, accepted_at, due_at, updated_at, scope_generation, runtime_epoch,
                    execution_policy_schema, attempt_plan_version, attempt_plan_offsets,
                    maximum_circuit_microseconds, execution_not_after,
                    maximum_attempts, maximum_elapsed, backoff_algorithm,
                    initial_retry_delay, maximum_retry_delay, lease_duration, lease_renewal_cadence,
                    maximum_lease_lifetime
                )
                VALUES
                (
                    @scope_id, @work_id, @activity_id, @command_id, @idempotency_key, @work_name, @work_version,
                    @contract_id, @payload_schema_version, @codec_id, @payload, @payload_sha256,
                    @payload_classification, @payload_retention, @request_fingerprint_schema, @request_fingerprint_sha256,
                    'pending', @provider_safety, @accepted_at, COALESCE(@due_at, @accepted_at), @accepted_at,
                    @scope_generation, @runtime_epoch,
                    'work-execution-v1', @attempt_plan_version, @attempt_plan_offsets,
                    @maximum_circuit_microseconds, @execution_not_after,
                    @maximum_attempts, @maximum_elapsed, @backoff_algorithm,
                    @initial_retry_delay, @maximum_retry_delay, @lease_duration, @lease_renewal_cadence,
                    @maximum_lease_lifetime
                )
                ON CONFLICT DO NOTHING
                RETURNING work_id, command_id, revision, accepted_at, due_at
            ),
            dispatched AS
            (
                INSERT INTO appsurface_durable.dispatch
                    (dispatch_id, scope_id, aggregate_kind, aggregate_id, due_at, state, expected_revision, execution_discovery_at, updated_at)
                SELECT @dispatch_id, @scope_id, 'work', work_id, due_at, 'available', revision, LEAST(due_at, @cutoff), @accepted_at
                FROM accepted
                RETURNING aggregate_id
            ),
            historied AS
            (
                INSERT INTO appsurface_durable.work_history
                    (scope_id, work_id, aggregate_revision, event_type, command_id,
                     attempt_number, lease_generation, scope_generation, runtime_epoch, details, observed_at)
                SELECT @scope_id, work_id, revision, 'accepted', command_id,
                       0, 0, @scope_generation, @runtime_epoch,
                       jsonb_build_object('work_name', @work_name, 'work_version', @work_version), @accepted_at
                FROM accepted
                RETURNING work_id
            )
            SELECT work_id, command_id, revision, accepted_at
            FROM accepted;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddAcceptanceParameters(command, request, runtimeEpoch, scopeGeneration, workId, dispatchId, request.Fingerprint, derivedActivityId);
        var attemptPlan = request.ExecutionPolicy.AttemptPlan;
        command.Parameters.AddWithValue("accepted_at", acceptedAt.UtcDateTime);
        command.Parameters.AddWithValue("cutoff", cutoff.UtcDateTime);
        command.Parameters.Add(new NpgsqlParameter("attempt_plan_version", NpgsqlDbType.Text) { Value = attemptPlan?.Version ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("attempt_plan_offsets", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        { Value = attemptPlan is null ? DBNull.Value : attemptPlan.ElapsedOffsets.Select(static value => value.Ticks / 10).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("maximum_circuit_microseconds", NpgsqlDbType.Bigint)
        { Value = attemptPlan is null ? DBNull.Value : attemptPlan.MaximumCircuitDuration.Ticks / 10 });
        command.Parameters.Add(new NpgsqlParameter("execution_not_after", NpgsqlDbType.TimestampTz)
        { Value = request.ExecutionDeadline is { } absoluteDeadline ? absoluteDeadline.NotAfterUtc.UtcDateTime : DBNull.Value });
        DurableWorkAcceptance? acceptance = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                acceptance = new(new DurableWorkId(reader.GetString(0)), new DurableCommandId(reader.GetString(1)),
                    DurableWorkAcceptanceKind.Accepted, reader.GetInt64(2), ReadUtc(reader, 3));
        }
        if (acceptance is null)
            return await ReadDuplicateAcceptanceAsync(connection, transaction, request, request.Fingerprint, cancellationToken).ConfigureAwait(false);
        if (sendWakeNotification) await SendWakeNotificationAsync(connection, transaction, dispatchId, cancellationToken).ConfigureAwait(false);
        return DurableOperationResult<DurableWorkAcceptance>.Success(acceptance);
    }

    private static DateTimeOffset MinTime(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>Adds a positive duration only after bounding it by a representable UTC cap.</summary>
    /// <remarks>Comparing the remaining interval first avoids overflow even when the uncapped duration is valid.</remarks>
    internal static DateTimeOffset AddExecutionTimeCapped(DateTimeOffset start, TimeSpan duration, DateTimeOffset? cap = null)
    {
        var limit = cap ?? DateTimeOffset.MaxValue.AddTicks(-(DateTimeOffset.MaxValue.Ticks % TimeSpan.TicksPerMicrosecond));
        return duration >= limit - start ? limit : start + duration;
    }

    private static DurableOperationResult<DurableWorkAcceptance> ExecutionAcceptanceFailure(
        DurableWorkRequest request, string code, string cause, string reason)
    {
        RecordExecutionRefusal("acceptance", reason);
        return DurableOperationResult<DurableWorkAcceptance>.Failure(new DurableProblem(code,
            "The opted-in Work request was not accepted.", cause,
            "Use a supported new Work version with a valid future window; do not alter an accepted request or retry an ambiguous effect.",
            WorkDocumentation, request.CommandId.Value));
    }
}
