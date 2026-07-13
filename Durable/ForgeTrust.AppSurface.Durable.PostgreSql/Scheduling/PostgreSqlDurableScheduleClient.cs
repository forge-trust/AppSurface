using System.Data;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Persists and queries durable schedules through scoped PostgreSQL transactions.
/// </summary>
/// <remarks>
/// This client mutates schedule definitions and lifecycle only. Due occurrence processing is performed by
/// <see cref="PostgreSqlDurableScheduleProcessor"/> through the same authoritative schedule rows and dispatch protocol.
/// No method performs schema creation or holds a database connection while application code executes.
/// </remarks>
public sealed class PostgreSqlDurableScheduleClient : IDurableScheduleClient
{
    private static readonly Uri ScheduleDocumentation = new("https://appsurface.dev/docs/durable/scheduling");
    private readonly NpgsqlDataSource dataSource;
    private readonly IDurablePayloadCodecRegistry payloadCodecs;
    private readonly IDurableWorkRegistry workRegistry;
    private readonly IDurableFlowRegistry flowRegistry;
    private readonly IDurableRuntimeSchemaManager schemaManager;
    private readonly Guid runtimeEpoch;

    /// <summary>
    /// Initializes a PostgreSQL durable schedule client.
    /// </summary>
    /// <param name="dataSource">Runtime-role PostgreSQL data source.</param>
    /// <param name="payloadCodecs">Allowlisted durable payload codecs.</param>
    /// <param name="workRegistry">Registered immutable work contracts.</param>
    /// <param name="flowRegistry">Registered immutable Flow definitions.</param>
    /// <param name="runtimeEpoch">Out-of-band non-empty restore epoch.</param>
    /// <param name="schemaManager">Optional shared schema compatibility gate.</param>
    public PostgreSqlDurableScheduleClient(
        NpgsqlDataSource dataSource,
        IDurablePayloadCodecRegistry payloadCodecs,
        IDurableWorkRegistry workRegistry,
        IDurableFlowRegistry flowRegistry,
        Guid runtimeEpoch,
        IDurableRuntimeSchemaManager? schemaManager = null)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.payloadCodecs = payloadCodecs ?? throw new ArgumentNullException(nameof(payloadCodecs));
        this.workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        this.flowRegistry = flowRegistry ?? throw new ArgumentNullException(nameof(flowRegistry));
        this.schemaManager = schemaManager ?? new PostgreSqlDurableRuntimeSchemaManager(dataSource);
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        this.runtimeEpoch = runtimeEpoch;
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableScheduleMutationResult>> CreateAsync(
        DurableScheduleCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var encodedTarget = EncodeTarget(request.Target);
        var fingerprint = DurableScheduleRequestFingerprint.ComputeCreate(request, encodedTarget.Input);
        await schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var duplicate = await ReadDuplicateAsync(
                connection,
                transaction,
                request.ScopeId,
                request.CommandId,
                request.IdempotencyKey,
                request.ScheduleId,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            var scopeGeneration = await PostgreSqlScheduleStorage.EnsureActiveScopeAsync(
                connection,
                transaction,
                request.ScopeId,
                createIfMissing: true,
                cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.AccessDenied,
                    "The schedule was not created because its durable scope is disabled.",
                    "The scope lifecycle changed before schedule acceptance.",
                    "Use a currently authorized active scope.",
                    request.CommandId.Value);
            }

            if (await ScheduleExistsAsync(
                    connection, transaction, request.ScopeId, request.ScheduleId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return CommandConflict<DurableScheduleMutationResult>(request.CommandId.Value);
            }

            var acceptedAt = await PostgreSqlScheduleStorage.ReadTransactionTimeAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            PostgreSqlResolvedSchedule schedule;
            try
            {
                schedule = PostgreSqlScheduleStorage.Resolve(request.ScheduleId, request.Schedule, acceptedAt);
            }
            catch (ScheduleDefinitionException exception)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return InvalidSchedule<DurableScheduleMutationResult>(exception, request.CommandId.Value);
            }

            await InsertScheduleAsync(
                connection,
                transaction,
                request,
                schedule,
                encodedTarget,
                scopeGeneration.Value,
                fingerprint,
                acceptedAt,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableScheduleMutationResult>.Success(new DurableScheduleMutationResult(
                request.ScheduleId,
                request.CommandId,
                DurableScheduleMutationCode.Created,
                generation: 1,
                revision: 1,
                acceptedAt));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableScheduleMutationResult>> UpdateAsync(
        DurableScheduleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var encodedTarget = EncodeTarget(request.Target);
        var fingerprint = DurableScheduleRequestFingerprint.ComputeUpdate(request, encodedTarget.Input);
        await schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var duplicate = await ReadDuplicateAsync(
                connection,
                transaction,
                request.ScopeId,
                request.CommandId,
                idempotencyKey: null,
                request.ScheduleId,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            if (await PostgreSqlScheduleStorage.EnsureActiveScopeAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    createIfMissing: false,
                    cancellationToken).ConfigureAwait(false) is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.AccessDenied,
                    "The schedule was not updated because its durable scope is disabled.",
                    "The scope lifecycle changed before this command acquired its mutation lock.",
                    "Use a currently authorized active scope.",
                    request.CommandId.Value);
            }

            var current = await LockStateAsync(
                connection, transaction, request.ScopeId, request.ScheduleId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return NotFound<DurableScheduleMutationResult>(request.CommandId.Value);
            }

            var transition = ScheduleStateTransitions.Update(current, request.ExpectedRevision);
            var rejection = TransitionFailure<DurableScheduleMutationResult>(transition, request.CommandId.Value);
            if (rejection is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return rejection;
            }

            var acceptedAt = await PostgreSqlScheduleStorage.ReadTransactionTimeAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            PostgreSqlResolvedSchedule schedule;
            try
            {
                schedule = PostgreSqlScheduleStorage.Resolve(request.ScheduleId, request.Schedule, acceptedAt);
            }
            catch (ScheduleDefinitionException exception)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return InvalidSchedule<DurableScheduleMutationResult>(exception, request.CommandId.Value);
            }

            await UpdateScheduleAsync(
                connection,
                transaction,
                request,
                schedule,
                encodedTarget,
                transition.State,
                fingerprint,
                acceptedAt,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableScheduleMutationResult>.Success(new DurableScheduleMutationResult(
                request.ScheduleId,
                request.CommandId,
                DurableScheduleMutationCode.Updated,
                transition.State.Generation,
                transition.State.Revision,
                acceptedAt));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleMutationResult>> PauseAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken = default) =>
        ChangeLifecycleAsync("pause", command, ScheduleStateTransitions.Pause, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleMutationResult>> ResumeAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken = default) =>
        ChangeLifecycleAsync("resume", command, ScheduleStateTransitions.Resume, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleMutationResult>> DeleteAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken = default) =>
        ChangeLifecycleAsync("delete", command, ScheduleStateTransitions.Delete, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableScheduleMutationResult>> ReleaseAfterRecoveryAsync(
        DurableScheduleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fingerprint = DurableScheduleRequestFingerprint.ComputeCommand("recovery_release", command);
        await schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, command.ScopeId, cancellationToken).ConfigureAwait(false);
            var duplicate = await ReadDuplicateAsync(
                connection,
                transaction,
                command.ScopeId,
                command.CommandId,
                idempotencyKey: null,
                command.ScheduleId,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            var scopeGeneration = await PostgreSqlScheduleStorage.EnsureActiveScopeAsync(
                connection,
                transaction,
                command.ScopeId,
                createIfMissing: false,
                cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.AccessDenied,
                    "The schedule recovery release was rejected because its durable scope is disabled.",
                    "Recovery release cannot re-enable an aggregate whose owning scope is inactive.",
                    "Re-enable the scope through its authorized lifecycle before retrying recovery release.",
                    command.CommandId.Value);
            }

            var current = await LockRecoveryStateAsync(
                connection, transaction, command.ScopeId, command.ScheduleId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.State == DurableScheduleState.Deleted)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return NotFound<DurableScheduleMutationResult>(command.CommandId.Value);
            }

            if (current.Revision != command.ExpectedRevision)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.RevisionConflict,
                    "The schedule changed before recovery release could be committed.",
                    "The expected revision does not match authoritative state.",
                    "Reload the schedule and retry only after confirming that recovery release is still required.",
                    command.CommandId.Value);
            }

            var releaseState = ResolveRecoveryReleaseState(current);
            if (releaseState is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.ScheduleInvalid,
                    "The schedule is not eligible for recovery release.",
                    "Only an old-epoch active or paused schedule, or a schedule suspended by ASDUR108, may be released.",
                    "Repair the underlying compatibility issue or use the ordinary lifecycle command appropriate to the current state.",
                    command.CommandId.Value);
            }

            var committedAt = await PostgreSqlScheduleStorage.ReadTransactionTimeAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var revision = checked(current.Revision + 1);
            const string updateSql = """
                UPDATE appsurface_durable.schedule_current
                SET state = @state,
                    revision = @revision,
                    suspended_from_state = NULL,
                    suspension_code = NULL,
                    scope_generation = @scope_generation,
                    runtime_epoch = @runtime_epoch,
                    updated_at = @committed_at
                WHERE scope_id = @scope_id
                  AND schedule_id = @schedule_id
                  AND revision = @expected_revision;
                """;
            await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
            {
                AddIdentity(update, command.ScopeId, command.ScheduleId);
                update.Parameters.AddWithValue("state", PostgreSqlScheduleStorage.FormatState(releaseState.Value));
                update.Parameters.AddWithValue("revision", revision);
                update.Parameters.AddWithValue("scope_generation", scopeGeneration.Value);
                update.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
                update.Parameters.AddWithValue("committed_at", committedAt);
                update.Parameters.AddWithValue("expected_revision", current.Revision);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new DBConcurrencyException("The schedule changed while recovery release was being applied.");
                }
            }

            await InsertHistoryAsync(
                connection,
                transaction,
                command.ScopeId,
                command.ScheduleId,
                current.Generation,
                revision,
                "recovery_released",
                command.CommandId,
                nominalDueUtc: null,
                command.ActorId,
                command.ReasonCode,
                cancellationToken).ConfigureAwait(false);
            await InsertCommandAsync(
                connection,
                transaction,
                command.ScopeId,
                command.ScheduleId,
                command.CommandId,
                idempotencyKey: null,
                "recovery_release",
                fingerprint,
                DurableScheduleMutationCode.RecoveryReleased,
                current.Generation,
                revision,
                committedAt,
                command.ActorId,
                command.ReasonCode,
                cancellationToken).ConfigureAwait(false);
            const string dispatchSql = """
                UPDATE appsurface_durable.dispatch
                SET state = @state,
                    expected_revision = @revision,
                    updated_at = @committed_at
                WHERE scope_id = @scope_id
                  AND aggregate_kind = 'schedule'
                  AND aggregate_id = @schedule_id;
                """;
            await using (var dispatch = new NpgsqlCommand(dispatchSql, connection, transaction))
            {
                AddIdentity(dispatch, command.ScopeId, command.ScheduleId);
                dispatch.Parameters.AddWithValue(
                    "state",
                    releaseState == DurableScheduleState.Active ? "available" : "suspended");
                dispatch.Parameters.AddWithValue("revision", revision);
                dispatch.Parameters.AddWithValue("committed_at", committedAt);
                if (await dispatch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidDataException(
                        "The recovery-fenced schedule has no authoritative dispatch row; recovery release was refused.");
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableScheduleMutationResult>.Success(new DurableScheduleMutationResult(
                command.ScheduleId,
                command.CommandId,
                DurableScheduleMutationCode.RecoveryReleased,
                current.Generation,
                revision,
                committedAt));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableScheduleSnapshot>> GetAsync(
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        await schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadSnapshotAsync(
                connection, transaction, scopeId, scheduleId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshot is null
                ? NotFound<DurableScheduleSnapshot>(scheduleId.Value)
                : DurableOperationResult<DurableScheduleSnapshot>.Success(snapshot);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableScheduleListResult>> ListAsync(
        DurableScheduleListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            const string sql = """
                SELECT current.schedule_id,
                       current.display_name,
                       current.state,
                       current.generation,
                       current.revision,
                       current.schedule_kind,
                       current.overlap_kind,
                       current.overlap_maximum,
                       current.misfire_kind,
                       current.misfire_maximum,
                       current.target_kind,
                       current.target_name,
                       current.target_version,
                       current.target_provider_safety,
                       current.next_nominal_due_utc,
                       current.runtime_epoch <> @runtime_epoch AND current.state <> 'deleted'
                           AS requires_recovery_release
                FROM appsurface_durable.schedule_current AS current
                WHERE current.scope_id = @scope_id
                  AND (@continuation_token IS NULL OR current.schedule_id > @continuation_token)
                  AND (@state_filter IS NULL OR current.state = @state_filter)
                  AND
                  (
                      @requires_recovery_release IS NULL
                      OR (current.runtime_epoch <> @runtime_epoch AND current.state <> 'deleted')
                          = @requires_recovery_release
                  )
                ORDER BY current.schedule_id
                LIMIT @query_size;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            PostgreSqlScheduleStorage.AddNullable(
                command, "continuation_token", NpgsqlTypes.NpgsqlDbType.Text, request.ContinuationToken);
            PostgreSqlScheduleStorage.AddNullable(
                command,
                "state_filter",
                NpgsqlTypes.NpgsqlDbType.Text,
                request.State is { } state ? PostgreSqlScheduleStorage.FormatState(state) : null);
            PostgreSqlScheduleStorage.AddNullable(
                command,
                "requires_recovery_release",
                NpgsqlTypes.NpgsqlDbType.Boolean,
                request.RequiresRecoveryRelease);
            command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
            command.Parameters.AddWithValue("query_size", request.PageSize + 1);
            var schedules = new List<DurableScheduleListItem>(request.PageSize + 1);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    schedules.Add(new DurableScheduleListItem(
                        new DurableScheduleId(reader.GetString(0)),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        PostgreSqlScheduleStorage.ParseState(reader.GetString(2)),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        PostgreSqlScheduleStorage.ParseScheduleKind(reader.GetString(5)),
                        PostgreSqlScheduleStorage.ParseOverlap(reader.GetString(6), reader.GetInt32(7)),
                        PostgreSqlScheduleStorage.ParseMisfire(reader.GetString(8), reader.GetInt32(9)),
                        PostgreSqlScheduleStorage.ParseTargetKind(reader.GetString(10)),
                        reader.GetString(11),
                        reader.GetString(12),
                        reader.IsDBNull(13)
                            ? null
                            : PostgreSqlScheduleStorage.ParseProviderSafety(reader.GetString(13)),
                        PostgreSqlScheduleStorage.ReadNullableUtc(reader, 14),
                        reader.GetBoolean(15)));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            string? continuation = null;
            if (schedules.Count > request.PageSize)
            {
                schedules.RemoveAt(schedules.Count - 1);
                continuation = schedules[^1].ScheduleId.Value;
            }

            return DurableOperationResult<DurableScheduleListResult>.Success(
                new DurableScheduleListResult(schedules, continuation));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableScheduleExplanation>> ExplainNextOccurrencesAsync(
        DurableScheduleExplainRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(DurableOperationResult<DurableScheduleExplanation>.Success(
                ScheduleExplanationCalculator.Explain(request)));
        }
        catch (ScheduleDefinitionException exception)
        {
            return ValueTask.FromResult(InvalidSchedule<DurableScheduleExplanation>(
                exception,
                request.ScheduleId.Value));
        }
    }

    private async ValueTask<DurableOperationResult<DurableScheduleMutationResult>> ChangeLifecycleAsync(
        string commandType,
        DurableScheduleCommand command,
        Func<ScheduleStateModel, long, ScheduleStateTransitionResult> transitionFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fingerprint = DurableScheduleRequestFingerprint.ComputeCommand(commandType, command);
        await schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection, transaction, runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await PostgreSqlScheduleStorage.SetScopeAsync(
                connection, transaction, command.ScopeId, cancellationToken).ConfigureAwait(false);
            var duplicate = await ReadDuplicateAsync(
                connection,
                transaction,
                command.ScopeId,
                command.CommandId,
                idempotencyKey: null,
                command.ScheduleId,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            if (await PostgreSqlScheduleStorage.EnsureActiveScopeAsync(
                    connection,
                    transaction,
                    command.ScopeId,
                    createIfMissing: false,
                    cancellationToken).ConfigureAwait(false) is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableScheduleMutationResult>(
                    DurableScheduleProblemCodes.AccessDenied,
                    "The schedule lifecycle was not changed because its durable scope is disabled.",
                    "The scope lifecycle changed before this command acquired its mutation lock.",
                    "Use a currently authorized active scope.",
                    command.CommandId.Value);
            }

            var current = await LockStateAsync(
                connection, transaction, command.ScopeId, command.ScheduleId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return NotFound<DurableScheduleMutationResult>(command.CommandId.Value);
            }

            var transition = transitionFactory(current, command.ExpectedRevision);
            var rejection = TransitionFailure<DurableScheduleMutationResult>(transition, command.CommandId.Value);
            if (rejection is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return rejection;
            }

            var committedAt = await PostgreSqlScheduleStorage.ReadTransactionTimeAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var resultCode = transition.Code == ScheduleStateTransitionCode.Unchanged
                ? DurableScheduleMutationCode.Unchanged
                : commandType switch
                {
                    "pause" => DurableScheduleMutationCode.Paused,
                    "resume" => DurableScheduleMutationCode.Resumed,
                    "delete" => DurableScheduleMutationCode.Deleted,
                    _ => throw new ArgumentOutOfRangeException(nameof(commandType)),
                };
            await PersistLifecycleAsync(
                connection,
                transaction,
                commandType,
                command,
                transition,
                resultCode,
                fingerprint,
                committedAt,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableScheduleMutationResult>.Success(new DurableScheduleMutationResult(
                command.ScheduleId,
                command.CommandId,
                resultCode,
                transition.State.Generation,
                transition.State.Revision,
                committedAt));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private PostgreSqlEncodedScheduleTarget EncodeTarget(DurableScheduleTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            DurableScheduleTargetKind.Work => EncodeWorkTarget(target),
            DurableScheduleTargetKind.Flow => EncodeFlowTarget(target),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    private PostgreSqlEncodedScheduleTarget EncodeWorkTarget(DurableScheduleTarget target)
    {
        var registration = workRegistry.GetRequired(target.RegisteredName, target.RegisteredVersion);
        if (registration.WorkCodec.PayloadType != target.InputType)
        {
            throw new InvalidOperationException("The schedule work target does not match its registered durable codec.");
        }

        var codec = payloadCodecs.GetRequired(
            target.InputType,
            registration.WorkCodec.ContractName,
            registration.WorkCodec.ContractVersion);
        if (!ReferenceEquals(codec, registration.WorkCodec))
        {
            throw new InvalidOperationException(
                "The schedule work target must use the exact codec instance owned by its immutable work registration.");
        }

        var input = codec.EncodeObject(target.InputValue);
        return new PostgreSqlEncodedScheduleTarget(
            target.Kind,
            target.RegisteredName,
            target.RegisteredVersion,
            registration.ProviderSafety,
            input);
    }

    private PostgreSqlEncodedScheduleTarget EncodeFlowTarget(DurableScheduleTarget target)
    {
        var registration = flowRegistry.GetRequired(target.RegisteredName, target.RegisteredVersion);
        if (registration.ContextCodec.PayloadType != target.InputType)
        {
            throw new InvalidOperationException("The schedule Flow target does not match its registered durable codec.");
        }

        var codec = payloadCodecs.GetRequired(
            target.InputType,
            registration.ContextCodec.ContractName,
            registration.ContextCodec.ContractVersion);
        if (!ReferenceEquals(codec, registration.ContextCodec))
        {
            throw new InvalidOperationException(
                "The schedule Flow target must use the exact codec instance owned by its immutable Flow registration.");
        }

        var input = codec.EncodeObject(target.InputValue);
        return new PostgreSqlEncodedScheduleTarget(
            target.Kind,
            target.RegisteredName,
            target.RegisteredVersion,
            null,
            input);
    }

    private async ValueTask InsertScheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScheduleCreateRequest request,
        PostgreSqlResolvedSchedule schedule,
        PostgreSqlEncodedScheduleTarget target,
        long scopeGeneration,
        byte[] fingerprint,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        const string rootSql = """
            INSERT INTO appsurface_durable.schedule (scope_id, schedule_id, created_at)
            VALUES (@scope_id, @schedule_id, @accepted_at);
            """;
        await using (var root = new NpgsqlCommand(rootSql, connection, transaction))
        {
            AddIdentity(root, request.ScopeId, request.ScheduleId);
            root.Parameters.AddWithValue("accepted_at", acceptedAt);
            await root.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string currentSql = """
            INSERT INTO appsurface_durable.schedule_current
                (scope_id, schedule_id, display_name, state, generation, revision,
                 schedule_kind, at_utc, delay, every_interval, anchor_utc,
                 overlap_kind, overlap_maximum, misfire_kind, misfire_maximum,
                 cron_expression, cron_dialect, cron_grammar, iana_time_zone_id,
                 cron_evaluator_version, cron_jitter_seed, time_zone_rules_fingerprint,
                 target_kind, target_name, target_version, target_provider_safety,
                 target_contract_name, target_contract_version, target_classification, target_retention_policy_id,
                 target_payload, target_payload_sha256, accepted_at, next_nominal_due_utc,
                 scope_generation, runtime_epoch, updated_at)
            VALUES
                (@scope_id, @schedule_id, @display_name, 'active', 1, 1,
                 @schedule_kind, @at_utc, @delay, @every_interval, @anchor_utc,
                 @overlap_kind, @overlap_maximum, @misfire_kind, @misfire_maximum,
                 @cron_expression, @cron_dialect, @cron_grammar, @iana_time_zone_id,
                 @cron_evaluator_version, @cron_jitter_seed, @time_zone_rules_fingerprint,
                 @target_kind, @target_name, @target_version, @target_provider_safety,
                 @target_contract_name, @target_contract_version, @target_classification, @target_retention_policy_id,
                 @target_payload, @target_payload_sha256, @accepted_at, @next_nominal_due_utc,
                 @scope_generation, @runtime_epoch, @accepted_at);
            """;
        await using (var current = new NpgsqlCommand(currentSql, connection, transaction))
        {
            AddIdentity(current, request.ScopeId, request.ScheduleId);
            PostgreSqlScheduleStorage.AddNullable(
                current, "display_name", NpgsqlTypes.NpgsqlDbType.Text, request.DisplayName);
            PostgreSqlScheduleStorage.AddDefinitionParameters(current, schedule, target);
            current.Parameters.AddWithValue("accepted_at", acceptedAt);
            current.Parameters.AddWithValue("scope_generation", scopeGeneration);
            current.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
            await current.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertHistoryAsync(
            connection, transaction, request.ScopeId, request.ScheduleId, 1, 1, "created",
            request.CommandId,
            nominalDueUtc: null,
            actorId: null,
            reasonCode: null,
            cancellationToken).ConfigureAwait(false);
        await InsertCommandAsync(
            connection,
            transaction,
            request.ScopeId,
            request.ScheduleId,
            request.CommandId,
            request.IdempotencyKey,
            "create",
            fingerprint,
            DurableScheduleMutationCode.Created,
            generation: 1,
            revision: 1,
            acceptedAt,
            actorId: null,
            reasonCode: null,
            cancellationToken).ConfigureAwait(false);
        await UpsertDispatchAsync(
            connection,
            transaction,
            request.ScopeId,
            request.ScheduleId,
            schedule.NextNominalDueUtc,
            "active",
            expectedRevision: 1,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UpdateScheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScheduleUpdateRequest request,
        PostgreSqlResolvedSchedule schedule,
        PostgreSqlEncodedScheduleTarget target,
        ScheduleStateModel state,
        byte[] fingerprint,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.schedule_current
            SET display_name = @display_name,
                generation = @generation,
                revision = @revision,
                schedule_kind = @schedule_kind,
                at_utc = @at_utc,
                delay = @delay,
                every_interval = @every_interval,
                anchor_utc = @anchor_utc,
                overlap_kind = @overlap_kind,
                overlap_maximum = @overlap_maximum,
                misfire_kind = @misfire_kind,
                misfire_maximum = @misfire_maximum,
                cron_expression = @cron_expression,
                cron_dialect = @cron_dialect,
                cron_grammar = @cron_grammar,
                iana_time_zone_id = @iana_time_zone_id,
                cron_evaluator_version = @cron_evaluator_version,
                cron_jitter_seed = @cron_jitter_seed,
                time_zone_rules_fingerprint = @time_zone_rules_fingerprint,
                target_kind = @target_kind,
                target_name = @target_name,
                target_version = @target_version,
                target_provider_safety = @target_provider_safety,
                target_contract_name = @target_contract_name,
                target_contract_version = @target_contract_version,
                target_classification = @target_classification,
                target_retention_policy_id = @target_retention_policy_id,
                target_payload = @target_payload,
                target_payload_sha256 = @target_payload_sha256,
                accepted_at = @accepted_at,
                next_nominal_due_utc = @next_nominal_due_utc,
                pending_generation = NULL,
                pending_nominal_due_utc = NULL,
                pending_covered_through_utc = NULL,
                pending_covered_occurrence_count = NULL,
                catch_up_remaining = NULL,
                runtime_epoch = @runtime_epoch,
                updated_at = @accepted_at
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND revision = @expected_revision;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, request.ScopeId, request.ScheduleId);
        command.Parameters.AddWithValue("expected_revision", request.ExpectedRevision);
        command.Parameters.AddWithValue("generation", state.Generation);
        command.Parameters.AddWithValue("revision", state.Revision);
        PostgreSqlScheduleStorage.AddNullable(
            command, "display_name", NpgsqlTypes.NpgsqlDbType.Text, request.DisplayName);
        PostgreSqlScheduleStorage.AddDefinitionParameters(command, schedule, target);
        command.Parameters.AddWithValue("accepted_at", acceptedAt);
        command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException("The schedule revision changed while applying an update.");
        }

        const string invalidateSql = """
            UPDATE appsurface_durable.schedule_occurrence
            SET state = 'invalidated', resolved_at = @accepted_at
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND schedule_generation < @generation
              AND state IN ('ready', 'queued', 'coalesced');
            """;
        await using (var invalidate = new NpgsqlCommand(invalidateSql, connection, transaction))
        {
            AddIdentity(invalidate, request.ScopeId, request.ScheduleId);
            invalidate.Parameters.AddWithValue("generation", state.Generation);
            invalidate.Parameters.AddWithValue("accepted_at", acceptedAt);
            await invalidate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertHistoryAsync(
            connection,
            transaction,
            request.ScopeId,
            request.ScheduleId,
            state.Generation,
            state.Revision,
            "updated",
            request.CommandId,
            nominalDueUtc: null,
            actorId: null,
            reasonCode: null,
            cancellationToken).ConfigureAwait(false);
        await InsertCommandAsync(
            connection,
            transaction,
            request.ScopeId,
            request.ScheduleId,
            request.CommandId,
            idempotencyKey: null,
            "update",
            fingerprint,
            DurableScheduleMutationCode.Updated,
            state.Generation,
            state.Revision,
            acceptedAt,
            actorId: null,
            reasonCode: null,
            cancellationToken).ConfigureAwait(false);
        await UpsertDispatchAsync(
            connection,
            transaction,
            request.ScopeId,
            request.ScheduleId,
            schedule.NextNominalDueUtc,
            state.State == DurableScheduleState.Active ? "active" : "paused",
            state.Revision,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PersistLifecycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandType,
        DurableScheduleCommand command,
        ScheduleStateTransitionResult transition,
        DurableScheduleMutationCode resultCode,
        byte[] fingerprint,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken)
    {
        var clearPending = transition.InvalidatePendingOccurrences;
        const string updateSql = """
            UPDATE appsurface_durable.schedule_current
            SET state = @state,
                revision = @revision,
                pending_generation = CASE WHEN @clear_pending THEN NULL ELSE pending_generation END,
                pending_nominal_due_utc = CASE WHEN @clear_pending THEN NULL ELSE pending_nominal_due_utc END,
                pending_covered_through_utc = CASE WHEN @clear_pending THEN NULL ELSE pending_covered_through_utc END,
                pending_covered_occurrence_count = CASE WHEN @clear_pending THEN NULL ELSE pending_covered_occurrence_count END,
                catch_up_remaining = CASE WHEN @clear_pending THEN NULL ELSE catch_up_remaining END,
                deleted_at = CASE WHEN @state = 'deleted' THEN @committed_at ELSE deleted_at END,
                updated_at = @committed_at
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
              AND revision = @expected_revision;
            """;
        await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
        {
            AddIdentity(update, command.ScopeId, command.ScheduleId);
            update.Parameters.AddWithValue("state", PostgreSqlScheduleStorage.FormatState(transition.State.State));
            update.Parameters.AddWithValue("revision", transition.State.Revision);
            update.Parameters.AddWithValue("clear_pending", clearPending);
            update.Parameters.AddWithValue("committed_at", committedAt);
            update.Parameters.AddWithValue("expected_revision", command.ExpectedRevision);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new DBConcurrencyException("The schedule revision changed while applying a lifecycle command.");
            }
        }

        if (clearPending)
        {
            const string invalidateSql = """
                UPDATE appsurface_durable.schedule_occurrence
                SET state = 'invalidated', resolved_at = @committed_at
                WHERE scope_id = @scope_id
                  AND schedule_id = @schedule_id
                  AND state IN ('ready', 'queued', 'coalesced');
                """;
            await using var invalidate = new NpgsqlCommand(invalidateSql, connection, transaction);
            AddIdentity(invalidate, command.ScopeId, command.ScheduleId);
            invalidate.Parameters.AddWithValue("committed_at", committedAt);
            await invalidate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertHistoryAsync(
            connection,
            transaction,
            command.ScopeId,
            command.ScheduleId,
            transition.State.Generation,
            transition.State.Revision,
            commandType == "delete" ? "deleted" : resultCode == DurableScheduleMutationCode.Unchanged ? "unchanged" : commandType + "d",
            command.CommandId,
            nominalDueUtc: null,
            command.ActorId,
            command.ReasonCode,
            cancellationToken).ConfigureAwait(false);
        await InsertCommandAsync(
            connection,
            transaction,
            command.ScopeId,
            command.ScheduleId,
            command.CommandId,
            idempotencyKey: null,
            commandType,
            fingerprint,
            resultCode,
            transition.State.Generation,
            transition.State.Revision,
            committedAt,
            command.ActorId,
            command.ReasonCode,
            cancellationToken).ConfigureAwait(false);

        string dispatchState;
        DateTimeOffset? dueOverride = null;
        if (transition.State.State == DurableScheduleState.Deleted)
        {
            dispatchState = "deleted";
        }
        else if (transition.State.State == DurableScheduleState.Paused)
        {
            dispatchState = "paused";
        }
        else
        {
            dispatchState = "active";
            if (transition.PendingOccurrenceBecameEligible)
            {
                dueOverride = committedAt;
            }
        }

        await UpsertDispatchAsync(
            connection,
            transaction,
            command.ScopeId,
            command.ScheduleId,
            dueOverride,
            dispatchState,
            transition.State.Revision,
            cancellationToken,
            preserveExistingDueWhenNull: true).ConfigureAwait(false);
    }

    private static async ValueTask<ScheduleStateModel?> LockStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state, generation, revision
            FROM appsurface_durable.schedule_current
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, scopeId, scheduleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ScheduleStateModel(
                PostgreSqlScheduleStorage.ParseState(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt64(2))
            : null;
    }

    private static async ValueTask<ScheduleRecoveryState?> LockRecoveryStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state, generation, revision, suspended_from_state, suspension_code, runtime_epoch
            FROM appsurface_durable.schedule_current
            WHERE scope_id = @scope_id
              AND schedule_id = @schedule_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, scopeId, scheduleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ScheduleRecoveryState(
            PostgreSqlScheduleStorage.ParseState(reader.GetString(0)),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : PostgreSqlScheduleStorage.ParseState(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetGuid(5));
    }

    private DurableScheduleState? ResolveRecoveryReleaseState(ScheduleRecoveryState current)
    {
        if (current.State == DurableScheduleState.Suspended)
        {
            return current.RuntimeEpoch != runtimeEpoch
                && string.Equals(current.SuspensionCode, DurableProblemCodes.RecoveryEpochRequired, StringComparison.Ordinal)
                && current.SuspendedFromState is DurableScheduleState.Active or DurableScheduleState.Paused
                    ? current.SuspendedFromState
                    : null;
        }

        return current.RuntimeEpoch != runtimeEpoch
            && current.State is DurableScheduleState.Active or DurableScheduleState.Paused
                ? current.State
                : null;
    }

    private sealed record ScheduleRecoveryState(
        DurableScheduleState State,
        long Generation,
        long Revision,
        DurableScheduleState? SuspendedFromState,
        string? SuspensionCode,
        Guid RuntimeEpoch);

    private static async ValueTask<DurableScheduleSnapshot?> ReadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT {PostgreSqlScheduleStorage.SnapshotColumns}
            FROM appsurface_durable.schedule_current AS current
            WHERE current.scope_id = @scope_id
              AND current.schedule_id = @schedule_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, scopeId, scheduleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? PostgreSqlScheduleStorage.ReadSnapshot(reader)
            : null;
    }

    private static async ValueTask<bool> ScheduleExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM appsurface_durable.schedule
                WHERE scope_id = @scope_id AND schedule_id = @schedule_id
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, scopeId, scheduleId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async ValueTask InsertHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        long generation,
        long revision,
        string eventType,
        DurableCommandId? commandId,
        DateTimeOffset? nominalDueUtc,
        string? actorId,
        string? reasonCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_history
                (scope_id, schedule_id, aggregate_revision, schedule_generation,
                 event_type, command_id, actor_id, reason_code, nominal_due_utc)
            VALUES
                (@scope_id, @schedule_id, @revision, @generation,
                 @event_type, @command_id, @actor_id, @reason_code, @nominal_due_utc);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, scopeId, scheduleId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("event_type", eventType);
        PostgreSqlScheduleStorage.AddNullable(
            command, "command_id", NpgsqlTypes.NpgsqlDbType.Text, commandId?.Value);
        PostgreSqlScheduleStorage.AddNullable(
            command, "actor_id", NpgsqlTypes.NpgsqlDbType.Text, actorId);
        PostgreSqlScheduleStorage.AddNullable(
            command, "reason_code", NpgsqlTypes.NpgsqlDbType.Text, reasonCode);
        PostgreSqlScheduleStorage.AddNullable(
            command, "nominal_due_utc", NpgsqlTypes.NpgsqlDbType.TimestampTz, nominalDueUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        DurableCommandId commandId,
        string? idempotencyKey,
        string commandType,
        byte[] fingerprint,
        DurableScheduleMutationCode resultCode,
        long generation,
        long revision,
        DateTimeOffset committedAt,
        string? actorId,
        string? reasonCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.schedule_command
                (scope_id, schedule_id, command_id, idempotency_key, command_type, actor_id, reason_code,
                 request_sha256, result_code, resulting_generation, resulting_revision, committed_at)
            VALUES
                (@scope_id, @schedule_id, @command_id, @idempotency_key, @command_type, @actor_id, @reason_code,
                 @request_sha256, @result_code, @generation, @revision, @committed_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentity(command, scopeId, scheduleId);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        PostgreSqlScheduleStorage.AddNullable(
            command, "idempotency_key", NpgsqlTypes.NpgsqlDbType.Text, idempotencyKey);
        command.Parameters.AddWithValue("command_type", commandType);
        PostgreSqlScheduleStorage.AddNullable(command, "actor_id", NpgsqlTypes.NpgsqlDbType.Text, actorId);
        PostgreSqlScheduleStorage.AddNullable(command, "reason_code", NpgsqlTypes.NpgsqlDbType.Text, reasonCode);
        command.Parameters.AddWithValue("request_sha256", fingerprint);
        command.Parameters.AddWithValue("result_code", PostgreSqlScheduleStorage.FormatMutationCode(resultCode));
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("committed_at", committedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask UpsertDispatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        DateTimeOffset? dueAtUtc,
        string scheduleState,
        long expectedRevision,
        CancellationToken cancellationToken,
        bool preserveExistingDueWhenNull = false)
    {
        var dispatchState = scheduleState switch
        {
            "active" when dueAtUtc is not null || preserveExistingDueWhenNull => "available",
            "active" => "terminal",
            "paused" => "suspended",
            "deleted" => "terminal",
            _ => "suspended",
        };
        const string sql = """
            INSERT INTO appsurface_durable.dispatch
                (dispatch_id, scope_id, aggregate_kind, aggregate_id, due_at, state, expected_revision)
            VALUES
                (@dispatch_id, @scope_id, 'schedule', @schedule_id,
                 COALESCE(@due_at, clock_timestamp()), @state, @expected_revision)
            ON CONFLICT (scope_id, aggregate_kind, aggregate_id)
            DO UPDATE SET
                due_at = CASE
                    WHEN @preserve_existing_due AND @due_at IS NULL THEN appsurface_durable.dispatch.due_at
                    ELSE COALESCE(@due_at, clock_timestamp())
                END,
                state = EXCLUDED.state,
                expected_revision = EXCLUDED.expected_revision,
                updated_at = clock_timestamp();
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("dispatch_id", Guid.NewGuid());
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        PostgreSqlScheduleStorage.AddNullable(
            command, "due_at", NpgsqlTypes.NpgsqlDbType.TimestampTz, dueAtUtc);
        command.Parameters.AddWithValue("state", dispatchState);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        command.Parameters.AddWithValue("preserve_existing_due", preserveExistingDueWhenNull);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<DurableOperationResult<DurableScheduleMutationResult>?> ReadDuplicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableCommandId commandId,
        string? idempotencyKey,
        DurableScheduleId scheduleId,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        var existing = await PostgreSqlScheduleStorage.ReadCommandAsync(
            connection,
            transaction,
            scopeId,
            commandId,
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        if (existing.ScheduleId != scheduleId
            || !existing.RequestSha256.AsSpan().SequenceEqual(fingerprint))
        {
            return CommandConflict<DurableScheduleMutationResult>(commandId.Value);
        }

        return DurableOperationResult<DurableScheduleMutationResult>.Success(new DurableScheduleMutationResult(
            existing.ScheduleId,
            existing.CommandId,
            DurableScheduleMutationCode.Duplicate,
            existing.Generation,
            existing.Revision,
            existing.CommittedAtUtc));
    }

    private static void AddIdentity(
        NpgsqlCommand command,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId)
    {
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
    }

    private static DurableOperationResult<T>? TransitionFailure<T>(
        ScheduleStateTransitionResult transition,
        string correlationId) => transition.Code switch
        {
            ScheduleStateTransitionCode.Applied or ScheduleStateTransitionCode.Unchanged => null,
            ScheduleStateTransitionCode.RevisionConflict => Failure<T>(
                DurableScheduleProblemCodes.RevisionConflict,
                "The schedule changed before this command could be committed.",
                "The expected revision does not match authoritative state.",
                "Reload the schedule and retry only if the requested transition is still valid.",
                correlationId),
            ScheduleStateTransitionCode.Deleted => NotFound<T>(correlationId),
            ScheduleStateTransitionCode.Suspended => Failure<T>(
                DurableScheduleProblemCodes.ScheduleInvalid,
                "The schedule is suspended and cannot be mutated through the ordinary client.",
                "Restore reconciliation or compatibility repair has not released this aggregate.",
                "Use an authorized runtime operator to reconcile and release the schedule.",
                correlationId),
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };

    private static DurableOperationResult<T> InvalidSchedule<T>(
        ScheduleDefinitionException exception,
        string correlationId) => Failure<T>(
        exception.Error is ScheduleDefinitionError.UnsupportedDialect or ScheduleDefinitionError.UnsupportedGrammar
            ? DurableScheduleProblemCodes.DialectUnsupported
            : DurableScheduleProblemCodes.ScheduleInvalid,
        "The durable schedule definition is invalid.",
        exception.Message,
        "Correct the expression, grammar, IANA time zone, interval, or absolute instant and retry.",
        correlationId);

    private static DurableOperationResult<T> NotFound<T>(string correlationId) => Failure<T>(
        DurableScheduleProblemCodes.ScheduleNotFound,
        "The durable schedule was not found in the authorized scope.",
        "It may never have existed, may belong to another scope, or may have been removed from retained history.",
        "Verify the authorized scope and opaque schedule id.",
        correlationId);

    private static DurableOperationResult<T> CommandConflict<T>(string correlationId) => Failure<T>(
        DurableScheduleProblemCodes.CommandConflict,
        "The schedule command identity was reused with different content.",
        "A command id, idempotency key, or schedule id already identifies another request.",
        "Retry with the original exact request or issue a new command identity.",
        correlationId);

    private static DurableOperationResult<T> Failure<T>(
        string code,
        string problem,
        string cause,
        string fix,
        string correlationId) => DurableOperationResult<T>.Failure(new DurableProblem(
            code,
            problem,
            cause,
            fix,
            ScheduleDocumentation,
            correlationId));
}
