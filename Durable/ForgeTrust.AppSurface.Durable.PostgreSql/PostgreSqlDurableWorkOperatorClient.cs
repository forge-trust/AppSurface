using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Implements the internal PostgreSQL operator path over the landed provider contracts.</summary>
/// <remarks>Applications must authorize every request before this client is called.</remarks>
internal sealed class PostgreSqlDurableWorkOperatorClient : IDurableWorkOperatorClient
{
    private static readonly Uri Documentation = new("https://appsurface.dev/docs/durable/work");
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDurableWorkRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly Guid _runtimeEpoch;

    /// <summary>Initializes the internal operator path for one active PostgreSQL runtime epoch.</summary>
    /// <param name="dataSource">Scoped-runtime PostgreSQL data source.</param>
    /// <param name="registry">Immutable Work registrations used for reconciliation and result validation.</param>
    /// <param name="services">Application services used only outside database transactions.</param>
    /// <param name="runtimeEpoch">Non-empty active runtime epoch.</param>
    internal PostgreSqlDurableWorkOperatorClient(
        NpgsqlDataSource dataSource,
        IDurableWorkRegistry registry,
        IServiceProvider services,
        Guid runtimeEpoch)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        _runtimeEpoch = runtimeEpoch;
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ReconcileAsync(
        DurableWorkReconcileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = await StartReconciliationAsync(request, cancellationToken).ConfigureAwait(false);
        if (started.Result is not null)
        {
            return started.Result;
        }

        var snapshot = started.Snapshot!;
        if (!TryGetRegistration(snapshot, out var registration))
        {
            return Failure(request.CommandId, DurableProblemCodes.WorkContractUnavailable,
                "The immutable Work registration required for reconciliation is unavailable.");
        }

        if (registration.ProviderSafety != DurableProviderSafety.ReconcileBeforeRetry || !registration.CanReconcile)
        {
            return Failure(request.CommandId, DurableProblemCodes.OperatorTransitionRejected,
                "The requested Work cannot be reconciled by its immutable registration.");
        }

        var proof = await DurableProviderWorkAdapter.ReconcileAsync(
            registration,
            _services,
            snapshot.ToProviderClaim(started.Payload!),
            cancellationToken).ConfigureAwait(false);
        if (proof.Result is { } result)
        {
            _ = registration.ResultCodec.DecodeObject(result);
        }

        return await CompleteReconciliationAsync(request, snapshot, proof, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ResolveAsync(
        DurableWorkManualResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDirectAsync(OperatorCommand.From(request), request.Result, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableWorkOperatorResult>> RetrySafeAsync(
        DurableWorkRetrySafeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDirectAsync(OperatorCommand.From(request), null, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ReleaseAfterRecoveryAsync(
        DurableWorkRecoveryReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDirectAsync(OperatorCommand.From(request), null, cancellationToken);
    }

    private async ValueTask<StartResult> StartReconciliationAsync(
        DurableWorkReconcileRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var precondition = await CheckCurrentEpochAndScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            if (precondition is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StartResult(
                    null, null, Failure(request.CommandId, precondition.Code, precondition.Problem));
            }
            var existing = await ReadExistingCommandAsync(
                connection, transaction, request.ScopeId, request.CommandId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var replay = ResolveReplay(existing, request.Fingerprint, "reconcile", request.WorkId, request.CommandId);
                if (replay is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new StartResult(null, null, replay);
                }
            }

            var snapshot = await ReadSnapshotAsync(
                connection, transaction, request.ScopeId, request.WorkId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                existing = await ReadExistingCommandAsync(
                    connection, transaction, request.ScopeId, request.CommandId, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    var concurrent = ResolveReplay(
                        existing, request.Fingerprint, "reconcile", request.WorkId, request.CommandId);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new StartResult(
                        null,
                        null,
                        concurrent ?? Failure(request.CommandId, DurableProblemCodes.OperatorCommandInProgress,
                            "The exact reconciliation command is already in progress."));
                }
            }

            var problem = ValidateSnapshot(snapshot, request.ExpectedRevision, "reconcile", request.CommandId);
            if (problem is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StartResult(null, null, problem);
            }

            if (snapshot!.ProviderSafety != DurableProviderSafety.ReconcileBeforeRetry
                || snapshot.State != "suspended_reconciliation_required"
                || !snapshot.HasExactAmbiguousPermit)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StartResult(null, null, Failure(request.CommandId, DurableProblemCodes.OperatorTransitionRejected,
                    "Only suspended ReconcileBeforeRetry Work with an exact ambiguous permit can run reconciliation."));
            }

            if (existing is null && !await TryInsertStartedCommandAsync(
                    connection, transaction, OperatorCommand.From(request), cancellationToken).ConfigureAwait(false))
            {
                existing = await ReadExistingCommandAsync(
                    connection, transaction, request.ScopeId, request.CommandId, cancellationToken).ConfigureAwait(false);
                var concurrent = ResolveReplay(existing!, request.Fingerprint, "reconcile", request.WorkId, request.CommandId);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StartResult(
                    null,
                    null,
                    concurrent ?? Failure(request.CommandId, DurableProblemCodes.OperatorCommandInProgress,
                        "The exact reconciliation command is already in progress."));
            }

            var payload = await ReadPayloadAsync(
                connection, transaction, request.ScopeId, request.WorkId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StartResult(snapshot, payload, null);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<DurableOperationResult<DurableWorkOperatorResult>> CompleteReconciliationAsync(
        DurableWorkReconcileRequest request,
        OperatorWorkSnapshot started,
        DurableEncodedEffectReconciliation proof,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var precondition = await CheckCurrentEpochAndScopeAsync(
                connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            if (precondition is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure(request.CommandId, precondition.Code, precondition.Problem);
            }
            var existing = await ReadExistingCommandAsync(
                connection, transaction, request.ScopeId, request.CommandId, cancellationToken).ConfigureAwait(false);
            var replay = existing is null
                ? Failure(request.CommandId, DurableProblemCodes.OperatorTransitionRejected, "The reconciliation command is no longer authoritative.")
                : ResolveReplay(existing, request.Fingerprint, "reconcile", request.WorkId, request.CommandId);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }

            var snapshot = await ReadSnapshotAsync(
                connection, transaction, request.ScopeId, request.WorkId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.Revision != started.Revision
                || snapshot.State != "suspended_reconciliation_required")
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure(request.CommandId, DurableProblemCodes.WorkRevisionConflict,
                    "Work changed while provider reconciliation was running; the observation was not applied.");
            }

            var transition = proof.Kind switch
            {
                DurableEffectReconciliationKind.Applied => new OperatorTransition(
                    snapshot.CancellationRequested ? "succeeded_after_cancel_requested" : "succeeded",
                    Terminal: true,
                    proof.Result,
                    PermitProof: proof.Kind),
                DurableEffectReconciliationKind.NotApplied => new OperatorTransition(
                    snapshot.CancellationRequested ? "canceled_before_effect" : "retry_wait",
                    Terminal: snapshot.CancellationRequested,
                    null,
                    PermitProof: proof.Kind),
                DurableEffectReconciliationKind.Unknown => new OperatorTransition(
                    snapshot.State, Terminal: false, null, PermitProof: proof.Kind),
                _ => throw new ArgumentOutOfRangeException(nameof(proof)),
            };
            var result = await ApplyTransitionAsync(
                connection, transaction, OperatorCommand.From(request), snapshot, transition, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ApplyDirectAsync(
        OperatorCommand command,
        DurableEncodedPayload? resultPayload,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var precondition = await CheckCurrentEpochAndScopeAsync(
                connection, transaction, command.ScopeId, cancellationToken).ConfigureAwait(false);
            if (precondition is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure(command.CommandId, precondition.Code, precondition.Problem);
            }
            var existing = await ReadExistingCommandAsync(
                connection, transaction, command.ScopeId, command.CommandId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var replay = ResolveReplay(existing, command.Fingerprint, command.Type, command.WorkId, command.CommandId);
                if (replay is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return replay;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure(command.CommandId, DurableProblemCodes.OperatorCommandInProgress,
                    "The exact operator command is already in progress.");
            }

            var snapshot = await ReadSnapshotAsync(
                connection, transaction, command.ScopeId, command.WorkId, cancellationToken).ConfigureAwait(false);
            existing = await ReadExistingCommandAsync(
                connection, transaction, command.ScopeId, command.CommandId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var concurrent = ResolveReplay(
                    existing, command.Fingerprint, command.Type, command.WorkId, command.CommandId);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return concurrent ?? Failure(command.CommandId, DurableProblemCodes.OperatorCommandInProgress,
                    "The exact operator command is already in progress.");
            }

            var problem = ValidateSnapshot(snapshot, command.ExpectedRevision, command.Type, command.CommandId);
            if (problem is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return problem;
            }

            if (resultPayload is not null)
            {
                if (!TryGetRegistration(snapshot!, out var registration))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Failure(command.CommandId, DurableProblemCodes.WorkContractUnavailable,
                        "The immutable Work registration required to validate the operator result is unavailable.");
                }

                _ = registration.ResultCodec.DecodeObject(resultPayload);
            }

            var transition = SelectDirectTransition(command, snapshot!, resultPayload);
            if (transition is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                var code = command.Type == "retry_safe" && snapshot!.HasAmbiguousPermit
                    ? DurableProblemCodes.OperatorProofRequired
                    : DurableProblemCodes.OperatorTransitionRejected;
                return Failure(command.CommandId, code,
                    "The requested operator transition is unsafe for the authoritative Work state and provider policy.");
            }

            if (!await TryInsertStartedCommandAsync(connection, transaction, command, cancellationToken).ConfigureAwait(false))
            {
                existing = await ReadExistingCommandAsync(
                    connection, transaction, command.ScopeId, command.CommandId, cancellationToken).ConfigureAwait(false);
                var concurrent = ResolveReplay(existing!, command.Fingerprint, command.Type, command.WorkId, command.CommandId);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return concurrent ?? Failure(command.CommandId, DurableProblemCodes.OperatorCommandInProgress,
                    "The exact operator command is already in progress.");
            }

            var result = await ApplyTransitionAsync(
                connection, transaction, command, snapshot!, transition, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private OperatorTransition? SelectDirectTransition(
        OperatorCommand command,
        OperatorWorkSnapshot snapshot,
        DurableEncodedPayload? result)
    {
        if (command.Type == "manual_resolve")
        {
            var manual = snapshot.ProviderSafety == DurableProviderSafety.ManualResolution
                && snapshot.State == "suspended_manual_resolution"
                && snapshot.HasExactAmbiguousPermit;
            var cancelSafe = snapshot.ProviderSafety is DurableProviderSafety.Idempotent or DurableProviderSafety.ProviderKeyed
                && snapshot.State == "suspended_ambiguous_external_outcome"
                && snapshot.CancellationRequested
                && snapshot.HasExactAmbiguousPermit;
            if (!manual && !cancelSafe)
            {
                return null;
            }

            if (command.Resolution == DurableManualResolutionKind.Applied)
            {
                return new OperatorTransition(
                    snapshot.CancellationRequested ? "succeeded_after_cancel_requested" : "succeeded",
                    Terminal: true,
                    result,
                    PermitProof: DurableEffectReconciliationKind.Applied);
            }

            return new OperatorTransition(
                snapshot.CancellationRequested ? "canceled_before_effect" : "retry_wait",
                Terminal: snapshot.CancellationRequested,
                null,
                PermitProof: DurableEffectReconciliationKind.NotApplied);
        }

        if (command.Type == "retry_safe")
        {
            var replaySafe = snapshot.State == "suspended_ambiguous_external_outcome"
                && snapshot.ProviderSafety is DurableProviderSafety.Idempotent or DurableProviderSafety.ProviderKeyed
                && snapshot.HasExactAmbiguousPermit;
            var preparationSafe = snapshot.State == "suspended_contract_unavailable"
                && !snapshot.HasAmbiguousPermit;
            return replaySafe || preparationSafe
                ? new OperatorTransition("retry_wait", Terminal: false, null, ClearCancellation: true)
                : null;
        }

        if (command.Type == "recovery_release")
        {
            if (snapshot.RuntimeEpoch == _runtimeEpoch || snapshot.TerminalCode != "runtime_epoch_mismatch")
            {
                return null;
            }

            var state = snapshot.HasExactAmbiguousPermit
                ? snapshot.ProviderSafety switch
                {
                    DurableProviderSafety.ReconcileBeforeRetry => "suspended_reconciliation_required",
                    DurableProviderSafety.ManualResolution => "suspended_manual_resolution",
                    _ => "suspended_ambiguous_external_outcome",
                }
                : "retry_wait";
            return new OperatorTransition(
                state,
                Terminal: false,
                null,
                ReplaceEpoch: true,
                MovePermitEpoch: snapshot.HasExactAmbiguousPermit);
        }

        return null;
    }

    private async ValueTask<DurableOperationResult<DurableWorkOperatorResult>> ApplyTransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand command,
        OperatorWorkSnapshot snapshot,
        OperatorTransition transition,
        CancellationToken cancellationToken)
    {
        var revision = snapshot.Revision + 1;
        const string workSql = """
            WITH updated_work AS
            (
            UPDATE appsurface_durable.work
            SET state = @state,
                due_at = CASE WHEN @state = 'retry_wait' THEN clock_timestamp() ELSE due_at END,
                terminal_at = CASE WHEN @terminal THEN clock_timestamp() ELSE NULL END,
                terminal_code = @terminal_code,
                lease_owner = NULL,
                lease_started_at = NULL,
                lease_expires_at = NULL,
                cancellation_requested_at = CASE WHEN @clear_cancellation THEN NULL ELSE cancellation_requested_at END,
                runtime_epoch = CASE WHEN @replace_epoch THEN @runtime_epoch ELSE runtime_epoch END,
                result_contract_id = @result_contract_id,
                result_schema_version = @result_schema_version,
                result_codec_id = @result_codec_id,
                result_classification = @result_classification,
                result_retention_policy_id = @result_retention,
                result_payload = @result_payload,
                result_sha256 = @result_sha256,
                revision = @revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND work_id = @work_id AND revision = @expected_revision
            RETURNING scope_id, work_id
            ), updated_dispatch AS
            (
            UPDATE appsurface_durable.dispatch
            SET state = CASE
                    WHEN @terminal THEN 'terminal'
                    WHEN @state = 'retry_wait' THEN 'available'
                    ELSE 'suspended'
                END,
                due_at = CASE WHEN @state = 'retry_wait' THEN clock_timestamp() ELSE due_at END,
                expected_revision = @revision,
                updated_at = clock_timestamp()
            FROM updated_work
            WHERE dispatch.scope_id = updated_work.scope_id
              AND dispatch.aggregate_kind = 'work'
              AND dispatch.aggregate_id = updated_work.work_id
            RETURNING dispatch.scope_id, dispatch.aggregate_id
            ), updated_permit AS
            (
            UPDATE appsurface_durable.effect_permit AS permit
            SET status = COALESCE(@permit_status, permit.status),
                runtime_epoch = CASE WHEN @move_permit_epoch THEN @runtime_epoch ELSE permit.runtime_epoch END,
                observed_at = clock_timestamp()
            FROM updated_dispatch
            WHERE (@permit_status IS NOT NULL OR @move_permit_epoch)
              AND permit.scope_id = updated_dispatch.scope_id
              AND permit.work_id = updated_dispatch.aggregate_id
              AND permit.attempt_number = @attempt_number
              AND permit.lease_generation = @lease_generation
              AND permit.scope_generation = @scope_generation
              AND permit.runtime_epoch = @work_epoch
              AND permit.status IN ('granted', 'ambiguous')
            RETURNING permit.scope_id, permit.work_id
            ), inserted_history AS
            (
            INSERT INTO appsurface_durable.work_history
                (scope_id, work_id, aggregate_revision, event_type, command_id, actor_id, reason_code,
                 attempt_number, lease_generation, scope_generation, runtime_epoch, details)
            SELECT
                @scope_id, @work_id, @revision, @event_type, @command_id, @actor_id, @reason_code,
                 @attempt_number, @lease_generation, @scope_generation,
                 CASE WHEN @replace_epoch THEN @runtime_epoch ELSE @work_epoch END,
                 jsonb_build_object('resulting_state', @state)
            FROM updated_dispatch
            WHERE @permit_status IS NULL OR (SELECT count(*) FROM updated_permit) = 1
            RETURNING scope_id, work_id
            ), completed_command AS
            (
            UPDATE appsurface_durable.work_operator_command
            SET status = 'completed', resulting_state = @state, resulting_revision = @revision,
                completed_at = clock_timestamp()
            FROM inserted_history
            WHERE work_operator_command.scope_id = inserted_history.scope_id
              AND work_operator_command.work_id = inserted_history.work_id
              AND work_operator_command.command_id = @command_id
              AND work_operator_command.status = 'started'
            RETURNING work_operator_command.scope_id
            )
            SELECT
                (SELECT count(*) FROM updated_work),
                (SELECT count(*) FROM updated_dispatch),
                (SELECT count(*) FROM updated_permit),
                (SELECT count(*) FROM inserted_history),
                (SELECT count(*) FROM completed_command);
            """;
        await using var sql = new NpgsqlCommand(workSql, connection, transaction);
        sql.Parameters.AddWithValue("scope_id", command.ScopeId.Value);
        sql.Parameters.AddWithValue("work_id", command.WorkId.Value);
        sql.Parameters.AddWithValue("expected_revision", snapshot.Revision);
        sql.Parameters.AddWithValue("revision", revision);
        sql.Parameters.AddWithValue("state", transition.State);
        sql.Parameters.AddWithValue("terminal", transition.Terminal);
        sql.Parameters.AddWithValue("terminal_code", $"operator_{command.Type}");
        sql.Parameters.AddWithValue("clear_cancellation", transition.ClearCancellation);
        sql.Parameters.AddWithValue("replace_epoch", transition.ReplaceEpoch);
        sql.Parameters.AddWithValue("move_permit_epoch", transition.MovePermitEpoch);
        sql.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
        sql.Parameters.AddWithValue("work_epoch", snapshot.RuntimeEpoch);
        sql.Parameters.AddWithValue("event_type", $"operator_{command.Type}");
        sql.Parameters.AddWithValue("actor_id", command.ActorId);
        sql.Parameters.AddWithValue("reason_code", command.ReasonCode);
        sql.Parameters.AddWithValue("attempt_number", snapshot.AttemptNumber);
        sql.Parameters.AddWithValue("lease_generation", snapshot.LeaseGeneration);
        sql.Parameters.AddWithValue("scope_generation", snapshot.ScopeGeneration);
        sql.Parameters.AddWithValue("command_id", command.CommandId.Value);
        sql.Parameters.Add(new NpgsqlParameter("permit_status", NpgsqlDbType.Text)
        {
            Value = transition.PermitProof is { } permitProof
                ? FormatPermitProof(permitProof)
                : DBNull.Value,
        });
        AddResultParameters(sql, transition.Result);
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt64(0) != 1
            || reader.GetInt64(1) != 1
            || reader.GetInt64(2) != (transition.PermitProof is null && !transition.MovePermitEpoch ? 0 : 1)
            || reader.GetInt64(3) != 1
            || reader.GetInt64(4) != 1)
        {
            throw new InvalidOperationException(
                "The authoritative Work, dispatch, permit, history, or operator-command projection changed unexpectedly.");
        }

        return DurableOperationResult<DurableWorkOperatorResult>.Success(new DurableWorkOperatorResult(
            command.WorkId,
            DurableWorkOperatorOutcome.Applied,
            ParseState(transition.State),
            revision));
    }

    private static async ValueTask<OperatorWorkSnapshot?> ReadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId workId,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT work_name, work_version, provider_safety, activity_id, attempt_number,
                   lease_generation, scope_generation, runtime_epoch, revision, state, terminal_code,
                   cancellation_requested_at IS NOT NULL,
                   EXISTS
                   (
                       SELECT 1 FROM appsurface_durable.effect_permit permit
                       WHERE permit.scope_id = work.scope_id AND permit.work_id = work.work_id
                         AND permit.status IN ('granted', 'ambiguous')
                   ),
                   EXISTS
                   (
                       SELECT 1 FROM appsurface_durable.effect_permit permit
                       WHERE permit.scope_id = work.scope_id AND permit.work_id = work.work_id
                         AND permit.attempt_number = work.attempt_number
                         AND permit.lease_generation = work.lease_generation
                         AND permit.scope_generation = work.scope_generation
                         AND permit.runtime_epoch = work.runtime_epoch
                         AND permit.status IN ('granted', 'ambiguous')
                   )
            FROM appsurface_durable.work work
            WHERE scope_id = @scope_id AND work_id = @work_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new OperatorWorkSnapshot(
            scopeId,
            workId,
            reader.GetString(0),
            reader.GetString(1),
            ParseSafety(reader.GetString(2)),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetGuid(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetBoolean(11),
            reader.GetBoolean(12),
            reader.GetBoolean(13));
    }

    private static async ValueTask<DurableEncodedPayload> ReadPayloadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId workId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT contract_id, payload_schema_version, payload_classification, payload, payload_retention, payload_sha256
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The locked reconciliation Work payload is no longer available.");
        }

        var payload = new DurableEncodedPayload(
            reader.GetString(0),
            reader.GetString(1),
            ParseClassification(reader.GetString(2)),
            reader.GetFieldValue<byte[]>(3),
            reader.GetString(4));
        if (!reader.GetFieldValue<byte[]>(5).AsSpan().SequenceEqual(Convert.FromHexString(payload.Sha256)))
        {
            throw new InvalidDataException("The durable reconciliation payload hash does not match its authoritative bytes.");
        }

        return payload;
    }

    private static async ValueTask<ExistingCommand?> ReadExistingCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableCommandId commandId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT work_id, command_type, request_schema, request_sha256, status, resulting_state, resulting_revision
            FROM appsurface_durable.work_operator_command
            WHERE scope_id = @scope_id AND command_id = @command_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ExistingCommand(
                reader.GetString(0), reader.GetString(1),
                new DurableCommandFingerprint(reader.GetString(2), Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(3))),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6))
            : null;
    }

    private static async ValueTask<bool> TryInsertStartedCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperatorCommand request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO appsurface_durable.work_operator_command
                (scope_id, work_id, command_id, command_type, actor_id, reason_code,
                 request_schema, request_sha256, status)
            VALUES (@scope_id, @work_id, @command_id, @command_type, @actor_id, @reason_code,
                    @request_schema, @request_sha256, 'started')
            ON CONFLICT (scope_id, command_id) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", request.WorkId.Value);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("command_type", request.Type);
        command.Parameters.AddWithValue("actor_id", request.ActorId);
        command.Parameters.AddWithValue("reason_code", request.ReasonCode);
        command.Parameters.AddWithValue("request_schema", request.Fingerprint.SchemaId);
        command.Parameters.AddWithValue("request_sha256", Convert.FromHexString(request.Fingerprint.Sha256));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static DurableOperationResult<DurableWorkOperatorResult>? ResolveReplay(
        ExistingCommand existing,
        DurableCommandFingerprint fingerprint,
        string type,
        DurableWorkId workId,
        DurableCommandId commandId)
    {
        var match = fingerprint.Compare(existing.Fingerprint);
        if (existing.WorkId != workId.Value || existing.Type != type
            || match != DurableCommandFingerprintMatch.Exact)
        {
            return Failure(commandId, DurableProblemCodes.CommandConflict,
                match == DurableCommandFingerprintMatch.UnsupportedSchema
                    ? "The operator command identity uses an unsupported persisted fingerprint schema."
                    : "The operator command identity was reused for different semantic input.");
        }

        if (existing.Status != "completed")
        {
            return null;
        }

        return DurableOperationResult<DurableWorkOperatorResult>.Success(new DurableWorkOperatorResult(
            workId,
            DurableWorkOperatorOutcome.Duplicate,
            ParseState(existing.ResultingState!),
            existing.ResultingRevision!.Value));
    }

    private static DurableOperationResult<DurableWorkOperatorResult>? ValidateSnapshot(
        OperatorWorkSnapshot? snapshot,
        long expectedRevision,
        string type,
        DurableCommandId commandId)
    {
        if (snapshot is null)
        {
            return Failure(commandId, DurableProblemCodes.WorkNotFound, "The target Work does not exist in the authorized scope.");
        }

        if (snapshot.Revision != expectedRevision)
        {
            return Failure(commandId, DurableProblemCodes.WorkRevisionConflict,
                "The Work revision changed before the operator command was applied.");
        }

        if (snapshot.State is "succeeded" or "succeeded_after_cancel_requested" or "failed" or "canceled_before_effect")
        {
            return Failure(commandId, DurableProblemCodes.AlreadyTerminal, "Terminal Work cannot accept an operator transition.");
        }

        _ = type;
        return null;
    }

    private bool TryGetRegistration(
        OperatorWorkSnapshot snapshot,
        out DurableWorkRegistration registration)
    {
        try
        {
            registration = _registry.GetRequired(snapshot.WorkName, snapshot.WorkVersion);
            return true;
        }
        catch (InvalidOperationException)
        {
            registration = null!;
            return false;
        }
    }

    private async ValueTask<OperatorPreconditionFailure?> CheckCurrentEpochAndScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using (var epochFence = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock_shared(@lock_id);",
            connection,
            transaction))
        {
            epochFence.Parameters.AddWithValue("lock_id", PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock);
            await epochFence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var epoch = new NpgsqlCommand(
            "SELECT active_runtime_epoch FROM appsurface_durable.store_metadata WHERE singleton;",
            connection,
            transaction))
        {
            if (await epoch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid active
                || active != _runtimeEpoch)
            {
                return new OperatorPreconditionFailure(
                    DurableProblemCodes.RecoveryEpochRequired,
                    "The configured operator epoch is not active.");
            }
        }

        await SetScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        await using var scope = new NpgsqlCommand(
            "SELECT state FROM appsurface_durable.scope WHERE scope_id = @scope_id FOR SHARE;",
            connection,
            transaction);
        scope.Parameters.AddWithValue("scope_id", scopeId.Value);
        var state = await scope.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (state is null)
        {
            return new OperatorPreconditionFailure(
                DurableProblemCodes.ScopeNotFound,
                "The requested operator scope does not exist.");
        }

        return state == "active"
            ? null
            : new OperatorPreconditionFailure(
                DurableProblemCodes.ScopeDisabled,
                "The requested operator scope is disabled.");
    }

    private static async ValueTask SetScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);", connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddResultParameters(NpgsqlCommand command, DurableEncodedPayload? result)
    {
        command.Parameters.Add(new NpgsqlParameter("result_contract_id", NpgsqlDbType.Text) { Value = result?.ContractName ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("result_schema_version", NpgsqlDbType.Text) { Value = result?.ContractVersion ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("result_codec_id", NpgsqlDbType.Text) { Value = result is null ? DBNull.Value : $"{result.ContractName}@{result.ContractVersion}" });
        command.Parameters.Add(new NpgsqlParameter("result_classification", NpgsqlDbType.Text) { Value = result is null ? DBNull.Value : FormatClassification(result.Classification) });
        command.Parameters.Add(new NpgsqlParameter("result_retention", NpgsqlDbType.Text) { Value = result?.RetentionPolicyId ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("result_payload", NpgsqlDbType.Bytea) { Value = result?.Content.ToArray() ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("result_sha256", NpgsqlDbType.Bytea) { Value = result is null ? DBNull.Value : Convert.FromHexString(result.Sha256) });
    }

    private static DurableOperationResult<DurableWorkOperatorResult> Failure(
        DurableCommandId commandId,
        string code,
        string problem) =>
        DurableOperationResult<DurableWorkOperatorResult>.Failure(new DurableProblem(
            code,
            problem,
            "Authoritative Work state, immutable provider policy, or command identity rejected the transition.",
            "Reload Work truth and submit only the exact authorized operation supported by provider evidence.",
            Documentation,
            commandId.Value));

    private static DurableWorkState ParseState(string state) => state switch
    {
        "pending" or "retry_wait" => DurableWorkState.Ready,
        "leased" or "effect_permitted" => DurableWorkState.Claimed,
        "cancel_pending" => DurableWorkState.CancelPending,
        "succeeded" => DurableWorkState.Succeeded,
        "succeeded_after_cancel_requested" => DurableWorkState.SucceededAfterCancelRequested,
        "failed" => DurableWorkState.FailedTerminal,
        "canceled_before_effect" => DurableWorkState.CanceledBeforeEffect,
        _ when state.StartsWith("suspended_", StringComparison.Ordinal) || state == "reconciling" => DurableWorkState.Suspended,
        _ => throw new InvalidDataException($"Unknown persisted durable Work state '{state}'."),
    };

    private static DurableProviderSafety ParseSafety(string safety) => safety switch
    {
        "idempotent" => DurableProviderSafety.Idempotent,
        "provider_keyed" => DurableProviderSafety.ProviderKeyed,
        "reconcile_before_retry" => DurableProviderSafety.ReconcileBeforeRetry,
        "manual_resolution" => DurableProviderSafety.ManualResolution,
        _ => throw new InvalidDataException($"Unknown persisted provider safety '{safety}'."),
    };

    private static DurableDataClassification ParseClassification(string classification) => classification switch
    {
        "operational" => DurableDataClassification.Operational,
        "approved_application" => DurableDataClassification.ApprovedApplication,
        _ => throw new InvalidDataException($"Unknown persisted classification '{classification}'."),
    };

    private static string FormatClassification(DurableDataClassification classification) => classification switch
    {
        DurableDataClassification.Operational => "operational",
        DurableDataClassification.ApprovedApplication => "approved_application",
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    private static string FormatPermitProof(DurableEffectReconciliationKind proof) => proof switch
    {
        DurableEffectReconciliationKind.Applied => "known_succeeded",
        DurableEffectReconciliationKind.NotApplied => "proven_no_effect",
        DurableEffectReconciliationKind.Unknown => "ambiguous",
        _ => throw new ArgumentOutOfRangeException(nameof(proof)),
    };

    private static async ValueTask TryRollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(exception))
        {
            // Preserve the original store or connection exception.
        }
    }

    private sealed record StartResult(
        OperatorWorkSnapshot? Snapshot,
        DurableEncodedPayload? Payload,
        DurableOperationResult<DurableWorkOperatorResult>? Result);

    private sealed record OperatorPreconditionFailure(string Code, string Problem);

    private sealed record ExistingCommand(
        string WorkId,
        string Type,
        DurableCommandFingerprint Fingerprint,
        string Status,
        string? ResultingState,
        long? ResultingRevision);

    private sealed record OperatorTransition(
        string State,
        bool Terminal,
        DurableEncodedPayload? Result,
        DurableEffectReconciliationKind? PermitProof = null,
        bool ClearCancellation = false,
        bool ReplaceEpoch = false,
        bool MovePermitEpoch = false);

    private sealed record OperatorWorkSnapshot(
        DurableScopeId ScopeId,
        DurableWorkId WorkId,
        string WorkName,
        string WorkVersion,
        DurableProviderSafety ProviderSafety,
        string ActivityId,
        int AttemptNumber,
        long LeaseGeneration,
        long ScopeGeneration,
        Guid RuntimeEpoch,
        long Revision,
        string State,
        string? TerminalCode,
        bool CancellationRequested,
        bool HasAmbiguousPermit,
        bool HasExactAmbiguousPermit)
    {
        internal DurableClaimedWork ToProviderClaim(DurableEncodedPayload payload) => new(
            ScopeId,
            WorkId,
            ActivityId,
            WorkName,
            WorkVersion,
            payload,
            ProviderSafety,
            AttemptNumber,
            LeaseGeneration,
            ScopeGeneration,
            RuntimeEpoch.ToString("D"));
    }

    private sealed record OperatorCommand(
        DurableScopeId ScopeId,
        DurableWorkId WorkId,
        DurableCommandId CommandId,
        string ActorId,
        string ReasonCode,
        long ExpectedRevision,
        DurableCommandFingerprint Fingerprint,
        string Type,
        DurableManualResolutionKind? Resolution)
    {
        internal static OperatorCommand From(DurableWorkReconcileRequest request) => new(
            request.ScopeId, request.WorkId, request.CommandId, request.ActorId, request.ReasonCode,
            request.ExpectedRevision, request.Fingerprint, "reconcile", null);

        internal static OperatorCommand From(DurableWorkManualResolutionRequest request) => new(
            request.ScopeId, request.WorkId, request.CommandId, request.ActorId, request.ReasonCode,
            request.ExpectedRevision, request.Fingerprint, "manual_resolve", request.Resolution);

        internal static OperatorCommand From(DurableWorkRetrySafeRequest request) => new(
            request.ScopeId, request.WorkId, request.CommandId, request.ActorId, request.ReasonCode,
            request.ExpectedRevision, request.Fingerprint, "retry_safe", null);

        internal static OperatorCommand From(DurableWorkRecoveryReleaseRequest request) => new(
            request.ScopeId, request.WorkId, request.CommandId, request.ActorId, request.ReasonCode,
            request.ExpectedRevision, request.Fingerprint, "recovery_release", null);
    }
}
