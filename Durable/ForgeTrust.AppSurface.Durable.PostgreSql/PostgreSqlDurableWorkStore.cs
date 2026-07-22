using System.Data;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;
using NpgsqlTypes;
using static ForgeTrust.AppSurface.Durable.PostgreSql.PostgreSqlDurableProtocolCodec;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed class PostgreSqlDurableWorkStore
{
    private static readonly Uri WorkDocumentation = new("https://appsurface.dev/docs/durable/work");
    private static readonly Uri ScopeDocumentation = new("https://appsurface.dev/docs/durable/scopes");
    private readonly NpgsqlDataSource _dataSource;
    private readonly Guid _runtimeEpoch;

    internal static int RequiredSchemaVersion => DurablePostgreSqlMigrationCatalog.RequiredVersion;

    internal PostgreSqlDurableWorkStore(NpgsqlDataSource dataSource, Guid runtimeEpoch)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        _runtimeEpoch = runtimeEpoch;
    }

    internal static async ValueTask<DurableOperationResult<DurableWorkAcceptance>> AcceptAsync(
        NpgsqlTransaction transaction,
        DurableWorkRequest request,
        Guid runtimeEpoch,
        Guid? expectedStoreId,
        bool sendWakeNotification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The supplied Npgsql transaction is not active.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The supplied Npgsql transaction connection must be open.");
        }

        if (!string.Equals(request.RetryPolicy.BackoffAlgorithm, "exponential-v1", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"The PostgreSQL durable protocol does not support retry algorithm '{request.RetryPolicy.BackoffAlgorithm}'.");
        }

        await ValidateSchemaAsync(connection, transaction, expectedStoreId, cancellationToken).ConfigureAwait(false);
        await EnsureCurrentEpochAsync(
            connection, transaction, runtimeEpoch, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
        var scopeGeneration = await EnsureActiveScopeAsync(
            connection,
            transaction,
            request.ScopeId,
            cancellationToken).ConfigureAwait(false);
        if (scopeGeneration is null)
        {
            return DurableOperationResult<DurableWorkAcceptance>.Failure(new DurableProblem(
                DurableProblemCodes.ScopeDisabled,
                "Durable work was not accepted because its owning scope is disabled.",
                "The scope lifecycle was disabled before this transaction reached durable acceptance.",
                "Use a currently authorized active scope; do not re-enable a scope merely to bypass lifecycle policy.",
                ScopeDocumentation,
                request.CommandId.Value));
        }

        var fingerprint = request.Fingerprint;
        var workId = DurableWorkId.New();
        var dispatchId = Guid.NewGuid();
        var accepted = await TryInsertAcceptanceAsync(
            connection,
            transaction,
            request,
            runtimeEpoch,
            scopeGeneration.Value,
            workId,
            dispatchId,
            fingerprint,
            cancellationToken).ConfigureAwait(false);

        if (accepted is not null)
        {
            if (sendWakeNotification)
            {
                await SendWakeNotificationAsync(connection, transaction, dispatchId, cancellationToken).ConfigureAwait(false);
            }

            return DurableOperationResult<DurableWorkAcceptance>.Success(accepted);
        }

        return await ReadDuplicateAcceptanceAsync(
            connection,
            transaction,
            request,
            fingerprint,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyList<PostgreSqlDispatchCandidate>> DiscoverAsync(
        int maximumCandidates,
        CancellationToken cancellationToken = default)
    {
        if (maximumCandidates is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCandidates),
                maximumCandidates,
                "A discovery pass must request between 1 and 1000 candidates.");
        }

        const string sql = """
            SELECT dispatch_id, scope_id, aggregate_id, due_at, expected_revision, priority
            FROM appsurface_durable.dispatch
            WHERE aggregate_kind = 'work'
              AND state IN ('available', 'leased')
              AND due_at <= clock_timestamp()
            ORDER BY due_at, priority DESC, dispatch_id
            LIMIT @maximum_candidates;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("maximum_candidates", maximumCandidates);
        var candidates = new List<PostgreSqlDispatchCandidate>(maximumCandidates);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new PostgreSqlDispatchCandidate(
                reader.GetGuid(0),
                new DurableScopeId(reader.GetString(1)),
                new DurableWorkId(reader.GetString(2)),
                ReadUtc(reader, 3),
                reader.GetInt64(4),
                reader.GetInt16(5)));
        }

        return candidates;
    }

    internal async ValueTask<PostgreSqlDurableWorkClaim?> TryClaimAsync(
        PostgreSqlDispatchCandidate candidate,
        string workerId,
        CancellationToken cancellationToken = default,
        Func<NpgsqlTransaction, DurableWorkState, string, CancellationToken, ValueTask>? onTransitionApplied = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        workerId = RequireBoundedText(workerId, nameof(workerId), 200);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(
                connection, transaction, _runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, candidate.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(
                    connection,
                    transaction,
                    candidate.ScopeId,
                    expectedGeneration: null,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var transition = await TrySuspendEpochMismatchAsync(
                    connection,
                    transaction,
                    candidate,
                    _runtimeEpoch,
                    cancellationToken).ConfigureAwait(false);
            if (transition is not null)
            {
                await CommitClaimTransitionAsync(
                    transaction, transition, onTransitionApplied, cancellationToken).ConfigureAwait(false);
                return null;
            }

            transition = await TrySuspendExpiredUnsafePermitAsync(
                    connection,
                    transaction,
                    candidate,
                    cancellationToken).ConfigureAwait(false);
            if (transition is not null)
            {
                await CommitClaimTransitionAsync(
                    transaction, transition, onTransitionApplied, cancellationToken).ConfigureAwait(false);
                return null;
            }

            transition = await TryTerminalizeExhaustedAsync(
                    connection,
                    transaction,
                    candidate,
                    cancellationToken).ConfigureAwait(false);
            if (transition is not null)
            {
                await CommitClaimTransitionAsync(
                    transaction, transition, onTransitionApplied, cancellationToken).ConfigureAwait(false);
                return null;
            }

            const string claimSql = """
                UPDATE appsurface_durable.work AS work
                SET state = 'leased',
                    attempt_number = work.attempt_number + 1,
                    lease_generation = work.lease_generation + 1,
                    lease_owner = @worker_id,
                    lease_started_at = clock_timestamp(),
                    lease_expires_at = clock_timestamp() + work.lease_duration,
                    updated_at = clock_timestamp(),
                    revision = work.revision + 1
                FROM appsurface_durable.scope AS scope
                WHERE work.scope_id = @scope_id
                  AND work.work_id = @work_id
                  AND work.revision = @expected_revision
                  AND work.runtime_epoch = @runtime_epoch
                  AND work.due_at <= clock_timestamp()
                  AND work.attempt_number < work.maximum_attempts
                  AND work.accepted_at + work.maximum_elapsed > clock_timestamp()
                  AND
                  (
                      work.state IN ('pending', 'retry_wait')
                      OR
                      (work.state = 'leased' AND work.lease_expires_at <= clock_timestamp())
                      OR
                      (work.state = 'effect_permitted'
                       AND work.provider_safety IN ('idempotent', 'provider_keyed')
                       AND work.lease_expires_at <= clock_timestamp())
                  )
                  AND scope.scope_id = work.scope_id
                  AND scope.state = 'active'
                  AND scope.generation = work.scope_generation
                RETURNING
                    work.work_name,
                    work.work_version,
                    work.contract_id,
                    work.payload_schema_version,
                    work.payload_classification,
                    work.payload,
                    work.payload_sha256,
                    work.provider_safety,
                    work.activity_id,
                    work.attempt_number,
                    work.lease_generation,
                    work.scope_generation,
                    work.runtime_epoch,
                    work.lease_started_at,
                    work.lease_expires_at,
                    work.revision,
                    work.payload_retention,
                    work.cancellation_requested_at IS NOT NULL,
                    work.lease_renewal_cadence;
                """;
            await using var claimCommand = new NpgsqlCommand(claimSql, connection, transaction);
            claimCommand.Parameters.AddWithValue("worker_id", workerId);
            claimCommand.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
            claimCommand.Parameters.AddWithValue("work_id", candidate.WorkId.Value);
            claimCommand.Parameters.AddWithValue("expected_revision", candidate.ExpectedRevision);
            claimCommand.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);

            PostgreSqlDurableWorkClaim? claim;
            await using (var reader = await claimCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    await reader.CloseAsync().ConfigureAwait(false);
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                var payloadBytes = reader.GetFieldValue<byte[]>(5);
                var storedHash = reader.GetFieldValue<byte[]>(6);
                var payload = new DurableEncodedPayload(
                    reader.GetString(2),
                    reader.GetString(3),
                    ParseClassification(reader.GetString(4)),
                    payloadBytes,
                    reader.GetString(16));
                if (!storedHash.AsSpan().SequenceEqual(Convert.FromHexString(payload.Sha256)))
                {
                    throw new InvalidDataException("The durable payload hash does not match its authoritative bytes.");
                }

                claim = new PostgreSqlDurableWorkClaim(
                    candidate.DispatchId,
                    candidate.ScopeId,
                    candidate.WorkId,
                    reader.GetString(0),
                    reader.GetString(1),
                    payload,
                    ParseProviderSafety(reader.GetString(7)),
                    workerId,
                    reader.GetString(8),
                    reader.GetInt32(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11),
                    reader.GetGuid(12),
                    ReadUtc(reader, 13),
                    ReadUtc(reader, 14),
                    reader.GetInt64(15),
                    reader.GetBoolean(17),
                    reader.GetFieldValue<TimeSpan>(18));
            }

            await UpdateDispatchForLeaseAsync(connection, transaction, claim, cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection,
                transaction,
                claim,
                "claimed",
                isStaleObservation: false,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claim;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask CommitClaimTransitionAsync(
        NpgsqlTransaction transaction,
        PostgreSqlWorkClaimTransition transition,
        Func<NpgsqlTransaction, DurableWorkState, string, CancellationToken, ValueTask>? onTransitionApplied,
        CancellationToken cancellationToken)
    {
        if (onTransitionApplied is not null)
        {
            await onTransitionApplied(
                transaction,
                transition.State,
                transition.Code,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PostgreSqlWorkClaimTransition?> TrySuspendEpochMismatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDispatchCandidate candidate,
        Guid runtimeEpoch,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH suspended AS
            (
                UPDATE appsurface_durable.work
                SET state = 'suspended_manual_resolution',
                    terminal_code = 'runtime_epoch_mismatch',
                    lease_owner = NULL,
                    lease_started_at = NULL,
                    lease_expires_at = NULL,
                    updated_at = clock_timestamp(),
                    revision = revision + 1
                WHERE scope_id = @scope_id
                  AND work_id = @work_id
                  AND revision = @expected_revision
                  AND runtime_epoch <> @runtime_epoch
                  AND state IN ('pending', 'leased', 'effect_permitted', 'retry_wait', 'cancel_pending')
                RETURNING work_id, attempt_number, lease_generation, scope_generation, runtime_epoch, revision,
                          state, terminal_code
            ),
            dispatched AS
            (
                UPDATE appsurface_durable.dispatch AS dispatch
                SET state = 'suspended',
                    expected_revision = suspended.revision,
                    updated_at = clock_timestamp()
                FROM suspended
                WHERE dispatch.dispatch_id = @dispatch_id
                  AND dispatch.scope_id = @scope_id
                  AND dispatch.aggregate_kind = 'work'
                  AND dispatch.aggregate_id = suspended.work_id
                RETURNING dispatch.aggregate_id
            ),
            historied AS
            (
                INSERT INTO appsurface_durable.work_history
                    (scope_id, work_id, aggregate_revision, event_type, attempt_number, lease_generation, scope_generation,
                     runtime_epoch, details)
                SELECT @scope_id, work_id, revision, 'runtime_epoch_mismatch', attempt_number, lease_generation,
                       scope_generation, runtime_epoch, '{}'::jsonb
                FROM suspended
                RETURNING work_id
            )
            SELECT state, terminal_code,
                   (SELECT count(*) FROM dispatched),
                   (SELECT count(*) FROM historied)
            FROM suspended;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", candidate.WorkId.Value);
        command.Parameters.AddWithValue("expected_revision", candidate.ExpectedRevision);
        command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
        command.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        EnsureExactProjectionCounts(reader, 2, 3, "runtime-epoch suspension");
        return new PostgreSqlWorkClaimTransition(ParseWorkState(reader.GetString(0)), reader.GetString(1));
    }

    private static async ValueTask<PostgreSqlWorkClaimTransition?> TrySuspendExpiredUnsafePermitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDispatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH suspended AS
            (
                UPDATE appsurface_durable.work
                SET state = CASE provider_safety
                        WHEN 'reconcile_before_retry' THEN 'suspended_reconciliation_required'
                        WHEN 'manual_resolution' THEN 'suspended_manual_resolution'
                        ELSE 'suspended_ambiguous_external_outcome'
                    END,
                    terminal_code = CASE provider_safety
                        WHEN 'reconcile_before_retry' THEN 'expired_effect_permit_reconciliation_required'
                        WHEN 'manual_resolution' THEN 'expired_effect_permit_manual_resolution'
                        ELSE 'expired_effect_permit_ambiguous'
                    END,
                    lease_owner = NULL,
                    lease_started_at = NULL,
                    lease_expires_at = NULL,
                    updated_at = clock_timestamp(),
                    revision = revision + 1
                WHERE scope_id = @scope_id
                  AND work_id = @work_id
                  AND revision = @expected_revision
                  AND lease_expires_at <= clock_timestamp()
                  AND
                  (
                      state = 'cancel_pending'
                      OR
                      (state = 'effect_permitted'
                       AND
                       (
                           provider_safety IN ('reconcile_before_retry', 'manual_resolution')
                           OR attempt_number >= maximum_attempts
                           OR accepted_at + maximum_elapsed <= clock_timestamp()
                       ))
                  )
                RETURNING work_id, attempt_number, lease_generation, scope_generation, runtime_epoch, revision,
                          state, terminal_code
            ),
            dispatched AS
            (
                UPDATE appsurface_durable.dispatch AS dispatch
                SET state = 'suspended',
                    expected_revision = suspended.revision,
                    updated_at = clock_timestamp()
                FROM suspended
                WHERE dispatch.dispatch_id = @dispatch_id
                  AND dispatch.scope_id = @scope_id
                  AND dispatch.aggregate_kind = 'work'
                  AND dispatch.aggregate_id = suspended.work_id
                RETURNING dispatch.aggregate_id
            ),
            historied AS
            (
                INSERT INTO appsurface_durable.work_history
                    (scope_id, work_id, aggregate_revision, event_type, attempt_number, lease_generation, scope_generation,
                     runtime_epoch, details)
                SELECT @scope_id, work_id, revision, terminal_code, attempt_number, lease_generation,
                       scope_generation, runtime_epoch, '{}'::jsonb
                FROM suspended
                RETURNING work_id
            )
            SELECT state, terminal_code,
                   (SELECT count(*) FROM dispatched),
                   (SELECT count(*) FROM historied)
            FROM suspended;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", candidate.WorkId.Value);
        command.Parameters.AddWithValue("expected_revision", candidate.ExpectedRevision);
        command.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        EnsureExactProjectionCounts(reader, 2, 3, "expired-permit suspension");
        return new PostgreSqlWorkClaimTransition(ParseWorkState(reader.GetString(0)), reader.GetString(1));
    }

    private static async ValueTask<PostgreSqlWorkClaimTransition?> TryTerminalizeExhaustedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDispatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH evidence AS
            (
                SELECT EXISTS
                (
                    SELECT 1
                    FROM appsurface_durable.effect_permit
                    WHERE scope_id = @scope_id
                      AND work_id = @work_id
                      AND status IN ('granted', 'ambiguous')
                ) AS has_ambiguous_permit
            ),
            exhausted AS
            (
                UPDATE appsurface_durable.work AS work
                SET state = CASE
                        WHEN evidence.has_ambiguous_permit
                             AND work.provider_safety = 'reconcile_before_retry'
                            THEN 'suspended_reconciliation_required'
                        WHEN evidence.has_ambiguous_permit
                             AND work.provider_safety = 'manual_resolution'
                            THEN 'suspended_manual_resolution'
                        WHEN evidence.has_ambiguous_permit
                            THEN 'suspended_ambiguous_external_outcome'
                        ELSE 'failed'
                    END,
                    terminal_code = CASE
                        WHEN evidence.has_ambiguous_permit
                            THEN 'retry_policy_exhausted_with_ambiguous_effect'
                        ELSE 'retry_policy_exhausted'
                    END,
                    terminal_at = CASE
                        WHEN evidence.has_ambiguous_permit THEN NULL
                        ELSE clock_timestamp()
                    END,
                    lease_owner = NULL,
                    lease_started_at = NULL,
                    lease_expires_at = NULL,
                    updated_at = clock_timestamp(),
                    revision = work.revision + 1
                FROM evidence
                WHERE work.scope_id = @scope_id
                  AND work.work_id = @work_id
                  AND work.revision = @expected_revision
                  AND work.due_at <= clock_timestamp()
                  AND
                  (
                      work.attempt_number >= work.maximum_attempts
                      OR work.accepted_at + work.maximum_elapsed <= clock_timestamp()
                  )
                  AND
                  (
                      work.state IN ('pending', 'retry_wait')
                      OR (work.state = 'leased' AND work.lease_expires_at <= clock_timestamp())
                  )
                RETURNING work_id, attempt_number, lease_generation, scope_generation, runtime_epoch, revision,
                          state, terminal_code
            ),
            dispatched AS
            (
                UPDATE appsurface_durable.dispatch AS dispatch
                SET state = CASE WHEN exhausted.state = 'failed' THEN 'terminal' ELSE 'suspended' END,
                    expected_revision = exhausted.revision,
                    updated_at = clock_timestamp()
                FROM exhausted
                WHERE dispatch.dispatch_id = @dispatch_id
                  AND dispatch.scope_id = @scope_id
                  AND dispatch.aggregate_kind = 'work'
                  AND dispatch.aggregate_id = exhausted.work_id
                RETURNING dispatch.aggregate_id
            ),
            historied AS
            (
                INSERT INTO appsurface_durable.work_history
                    (scope_id, work_id, aggregate_revision, event_type, attempt_number, lease_generation, scope_generation,
                     runtime_epoch, details)
                SELECT @scope_id, work_id, revision, 'retry_policy_exhausted', attempt_number, lease_generation,
                       scope_generation, runtime_epoch, '{}'::jsonb
                FROM exhausted
                RETURNING work_id
            )
            SELECT state, terminal_code,
                   (SELECT count(*) FROM dispatched),
                   (SELECT count(*) FROM historied)
            FROM exhausted;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", candidate.WorkId.Value);
        command.Parameters.AddWithValue("expected_revision", candidate.ExpectedRevision);
        command.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        EnsureExactProjectionCounts(reader, 2, 3, "retry-exhaustion projection");
        return new PostgreSqlWorkClaimTransition(ParseWorkState(reader.GetString(0)), reader.GetString(1));
    }

    internal async ValueTask<PostgreSqlDurableWorkClaim?> RenewLeaseAsync(
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(
                connection, transaction, _runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, claim.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(
                    connection,
                    transaction,
                    claim.ScopeId,
                    claim.ScopeGeneration,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            const string sql = """
                UPDATE appsurface_durable.work AS work
                SET lease_expires_at = LEAST(
                        clock_timestamp() + work.lease_duration,
                        work.lease_started_at + work.maximum_lease_lifetime),
                    updated_at = clock_timestamp(),
                    revision = work.revision + 1
                FROM appsurface_durable.scope AS scope
                WHERE work.scope_id = @scope_id
                  AND work.work_id = @work_id
                  AND work.attempt_number = @attempt_number
                  AND work.lease_generation = @lease_generation
                  AND work.scope_generation = @scope_generation
                  AND work.runtime_epoch = @runtime_epoch
                  AND work.lease_owner = @lease_owner
                  AND work.lease_expires_at > clock_timestamp()
                  AND work.state IN ('leased', 'effect_permitted', 'cancel_pending')
                  AND scope.scope_id = work.scope_id
                  AND scope.state = 'active'
                  AND scope.generation = work.scope_generation
                RETURNING work.lease_expires_at, work.revision,
                          work.cancellation_requested_at IS NOT NULL;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddClaimIdentityParameters(command, claim);
            DateTimeOffset? leaseExpiresAt = null;
            long revision = 0;
            var cancellationRequested = claim.CancellationRequested;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    leaseExpiresAt = ReadUtc(reader, 0);
                    revision = reader.GetInt64(1);
                    cancellationRequested = reader.GetBoolean(2);
                }
            }

            if (leaseExpiresAt is null)
            {
                await InsertHistoryAsync(
                    connection,
                    transaction,
                    claim,
                    "stale_lease_renewal",
                    isStaleObservation: true,
                    "{}",
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var renewed = claim with
            {
                LeaseExpiresAtUtc = leaseExpiresAt.Value,
                Revision = revision,
                CancellationRequested = cancellationRequested,
            };
            await UpdateDispatchForLeaseAsync(connection, transaction, renewed, cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection,
                transaction,
                renewed,
                "lease_renewed",
                isStaleObservation: false,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return renewed;
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<PostgreSqlEffectPermit?> TryAcquireEffectPermitAsync(
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken = default,
        Func<NpgsqlTransaction, DurableWorkState, string, CancellationToken, ValueTask>? onTerminalApplied = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(
                connection, transaction, _runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, claim.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(
                    connection,
                    transaction,
                    claim.ScopeId,
                    claim.ScopeGeneration,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var existing = await ReadCurrentEffectPermitAsync(
                connection,
                transaction,
                claim,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }

            const string workSql = """
                UPDATE appsurface_durable.work AS work
                SET state = CASE
                        WHEN work.cancellation_requested_at IS NULL THEN 'effect_permitted'
                        ELSE 'cancel_pending'
                    END,
                    updated_at = clock_timestamp(),
                    revision = work.revision + 1
                FROM appsurface_durable.scope AS scope
                WHERE work.scope_id = @scope_id
                  AND work.work_id = @work_id
                  AND work.attempt_number = @attempt_number
                  AND work.lease_generation = @lease_generation
                  AND work.scope_generation = @scope_generation
                  AND work.runtime_epoch = @runtime_epoch
                  AND work.lease_owner = @lease_owner
                  AND work.lease_expires_at > clock_timestamp()
                  AND work.state = 'leased'
                  AND work.cancellation_requested_at IS NULL
                  AND scope.scope_id = work.scope_id
                  AND scope.state = 'active'
                  AND scope.generation = work.scope_generation
                RETURNING work.activity_id, work.revision, work.lease_expires_at,
                          work.cancellation_requested_at IS NOT NULL;
                """;
            await using var workCommand = new NpgsqlCommand(workSql, connection, transaction);
            AddClaimIdentityParameters(workCommand, claim);
            string? providerKey = null;
            long revision = 0;
            DateTimeOffset leaseExpiresAt = default;
            var cancellationRequested = claim.CancellationRequested;
            await using (var reader = await workCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    providerKey = reader.GetString(0);
                    revision = reader.GetInt64(1);
                    leaseExpiresAt = ReadUtc(reader, 2);
                    cancellationRequested = reader.GetBoolean(3);
                }
            }

            if (providerKey is null)
            {
                var canceledProjection = await TryProjectCanceledClaimBeforeEffectAsync(
                    connection,
                    transaction,
                    claim,
                    cancellationToken).ConfigureAwait(false);
                if (canceledProjection is not null)
                {
                    var projectedState = ParseWorkState(canceledProjection.State);
                    await UpdateDispatchForCompletionAsync(
                        connection,
                        transaction,
                        canceledProjection.Claim,
                        canceledProjection.State,
                        canceledProjection.Claim.LeaseExpiresAtUtc,
                        canceledProjection.Claim.Revision,
                        cancellationToken).ConfigureAwait(false);
                    await InsertHistoryAsync(
                        connection,
                        transaction,
                        canceledProjection.Claim,
                        projectedState == DurableWorkState.CanceledBeforeEffect
                            ? "canceled_before_effect"
                            : "cancel_pending",
                        isStaleObservation: false,
                        "{}",
                        cancellationToken).ConfigureAwait(false);
                    if (onTerminalApplied is not null)
                    {
                        await onTerminalApplied(
                            transaction,
                            projectedState,
                            canceledProjection.Code,
                            cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                await InsertHistoryAsync(
                    connection,
                    transaction,
                    claim,
                    "stale_effect_permit_request",
                    isStaleObservation: true,
                    "{}",
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var permittedClaim = claim with
            {
                Revision = revision,
                LeaseExpiresAtUtc = leaseExpiresAt,
                CancellationRequested = cancellationRequested,
            };
            await UpdateDispatchForLeaseAsync(connection, transaction, permittedClaim, cancellationToken).ConfigureAwait(false);

            var permitId = Guid.NewGuid();
            const string permitSql = """
                INSERT INTO appsurface_durable.effect_permit
                    (permit_id, scope_id, work_id, attempt_number, lease_generation, scope_generation,
                     runtime_epoch, activity_id, status)
                VALUES
                    (@permit_id, @scope_id, @work_id, @attempt_number, @lease_generation, @scope_generation,
                     @runtime_epoch, @activity_id, 'granted')
                RETURNING permitted_at;
                """;
            await using var permitCommand = new NpgsqlCommand(permitSql, connection, transaction);
            AddClaimIdentityParameters(permitCommand, claim);
            permitCommand.Parameters.AddWithValue("permit_id", permitId);
            permitCommand.Parameters.AddWithValue("activity_id", providerKey);
            var permittedAtValue = await permitCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (permittedAtValue is not DateTime permittedAt)
            {
                throw new InvalidOperationException("PostgreSQL did not return the committed effect permit timestamp.");
            }

            await InsertHistoryAsync(
                connection,
                transaction,
                permittedClaim,
                "effect_permitted",
                isStaleObservation: false,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlEffectPermit(
                permitId,
                permittedClaim,
                providerKey,
                new DateTimeOffset(permittedAt, TimeSpan.Zero));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<PostgreSqlEffectPermit?> ReadCurrentEffectPermitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT permit.permit_id, permit.activity_id, permit.permitted_at, work.revision, work.lease_expires_at,
                   work.cancellation_requested_at IS NOT NULL
            FROM appsurface_durable.effect_permit AS permit
            JOIN appsurface_durable.work AS work
              ON work.scope_id = permit.scope_id
             AND work.work_id = permit.work_id
            WHERE permit.scope_id = @scope_id
              AND permit.work_id = @work_id
              AND permit.attempt_number = @attempt_number
              AND permit.lease_generation = @lease_generation
              AND permit.scope_generation = @scope_generation
              AND permit.runtime_epoch = @runtime_epoch
              AND work.attempt_number = permit.attempt_number
              AND work.lease_generation = permit.lease_generation
              AND work.scope_generation = permit.scope_generation
              AND work.runtime_epoch = permit.runtime_epoch
              AND work.lease_owner = @lease_owner
              AND work.state IN ('effect_permitted', 'cancel_pending')
              AND work.lease_expires_at > clock_timestamp();
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddClaimIdentityParameters(command, claim);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var currentClaim = claim with
        {
            Revision = reader.GetInt64(3),
            LeaseExpiresAtUtc = ReadUtc(reader, 4),
            CancellationRequested = reader.GetBoolean(5),
        };
        return new PostgreSqlEffectPermit(
            reader.GetGuid(0),
            currentClaim,
            reader.GetString(1),
            ReadUtc(reader, 2));
    }

    private static async ValueTask<PostgreSqlCanceledClaimProjection?> TryProjectCanceledClaimBeforeEffectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH evidence AS
            (
                SELECT EXISTS
                (
                    SELECT 1
                    FROM appsurface_durable.effect_permit
                    WHERE scope_id = @scope_id
                      AND work_id = @work_id
                      AND status IN ('granted', 'ambiguous')
                ) AS has_ambiguous_permit
            ),
            projected AS
            (
                UPDATE appsurface_durable.work AS work
                SET state = CASE
                        WHEN evidence.has_ambiguous_permit
                             AND work.provider_safety = 'reconcile_before_retry'
                            THEN 'suspended_reconciliation_required'
                        WHEN evidence.has_ambiguous_permit
                             AND work.provider_safety = 'manual_resolution'
                            THEN 'suspended_manual_resolution'
                        WHEN evidence.has_ambiguous_permit
                            THEN 'suspended_ambiguous_external_outcome'
                        ELSE 'canceled_before_effect'
                    END,
                    terminal_at = CASE
                        WHEN evidence.has_ambiguous_permit THEN NULL
                        ELSE clock_timestamp()
                    END,
                    terminal_code = CASE
                        WHEN evidence.has_ambiguous_permit THEN 'cancellation_with_ambiguous_effect'
                        ELSE 'canceled_before_effect'
                    END,
                    lease_owner = NULL,
                    lease_started_at = NULL,
                    lease_expires_at = NULL,
                    revision = work.revision + 1,
                    updated_at = clock_timestamp()
                FROM evidence
                WHERE work.scope_id = @scope_id
                  AND work.work_id = @work_id
                  AND work.attempt_number = @attempt_number
                  AND work.lease_generation = @lease_generation
                  AND work.scope_generation = @scope_generation
                  AND work.runtime_epoch = @runtime_epoch
                  AND work.state = 'leased'
                  AND work.cancellation_requested_at IS NOT NULL
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM appsurface_durable.effect_permit AS permit
                      WHERE permit.scope_id = work.scope_id
                        AND permit.work_id = work.work_id
                        AND permit.attempt_number = work.attempt_number
                        AND permit.lease_generation = work.lease_generation
                        AND permit.scope_generation = work.scope_generation
                        AND permit.runtime_epoch = work.runtime_epoch
                  )
                RETURNING work.state, work.terminal_code, work.revision
            )
            SELECT state, terminal_code, revision
            FROM projected;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddClaimIdentityParameters(command, claim);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PostgreSqlCanceledClaimProjection(
                claim with { Revision = reader.GetInt64(2), CancellationRequested = true },
                reader.GetString(0),
                reader.GetString(1))
            : null;
    }

    internal async ValueTask<PostgreSqlWorkCompletionResult> RecordCompletionAsync(
        PostgreSqlDurableWorkClaim claim,
        PostgreSqlWorkCompletion completion,
        Func<NpgsqlTransaction, DurableWorkState, CancellationToken, ValueTask>? onProjectionApplied = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(completion);
        if (!Enum.IsDefined(completion.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(completion), "The completion kind must be defined.");
        }

        var terminalCode = RequireBoundedText(completion.Code, nameof(completion), 200);
        EnsureBoundedJson(completion.DetailsJson);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(
                connection, transaction, _runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, claim.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(
                    connection,
                    transaction,
                    claim.ScopeId,
                    claim.ScopeGeneration,
                    cancellationToken).ConfigureAwait(false))
            {
                var staleResult = await RecordStaleCompletionAsync(
                    connection,
                    transaction,
                    claim,
                    completion,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return staleResult with { Outcome = PostgreSqlWorkObservationOutcome.StaleObservation };
            }

            var effectiveRetryDelay = completion.Kind is
                PostgreSqlWorkCompletionKind.Retry or PostgreSqlWorkCompletionKind.ProvenNoEffect
                ? await ReadRetryDelayAsync(
                    connection,
                    transaction,
                    claim,
                    cancellationToken).ConfigureAwait(false)
                : null;
            const string sql = """
                WITH permit AS
                (
                    SELECT EXISTS
                    (
                        SELECT 1
                        FROM appsurface_durable.effect_permit
                        WHERE scope_id = @scope_id
                          AND work_id = @work_id
                          AND attempt_number = @attempt_number
                          AND lease_generation = @lease_generation
                          AND scope_generation = @scope_generation
                          AND runtime_epoch = @runtime_epoch
                    ) AS exists
                ),
                decision AS
                (
                    SELECT
                        work.scope_id,
                        work.work_id,
                        CASE
                            WHEN @completion_kind = 'succeeded' AND work.cancellation_requested_at IS NULL
                                THEN 'succeeded'
                            WHEN @completion_kind = 'succeeded'
                                THEN 'succeeded_after_cancel_requested'
                            WHEN @completion_kind = 'failed_terminal' AND NOT permit.exists
                                THEN 'failed'
                            WHEN @completion_kind = 'failed_terminal'
                                 AND work.provider_safety = 'reconcile_before_retry'
                                THEN 'suspended_reconciliation_required'
                            WHEN @completion_kind = 'failed_terminal'
                                 AND work.provider_safety = 'manual_resolution'
                                THEN 'suspended_manual_resolution'
                            WHEN @completion_kind = 'failed_terminal'
                                THEN 'suspended_ambiguous_external_outcome'
                            WHEN @completion_kind = 'contract_unavailable'
                                THEN 'suspended_contract_unavailable'
                            WHEN @completion_kind = 'ambiguous_external_outcome'
                                 AND permit.exists
                                 AND work.provider_safety = 'reconcile_before_retry'
                                THEN 'suspended_reconciliation_required'
                            WHEN @completion_kind = 'ambiguous_external_outcome'
                                 AND permit.exists
                                 AND work.provider_safety = 'manual_resolution'
                                THEN 'suspended_manual_resolution'
                            WHEN @completion_kind = 'ambiguous_external_outcome'
                                THEN 'suspended_ambiguous_external_outcome'
                            WHEN @completion_kind = 'proven_no_effect' AND work.cancellation_requested_at IS NOT NULL
                                THEN 'canceled_before_effect'
                            WHEN @completion_kind = 'proven_no_effect'
                                 AND (work.attempt_number >= work.maximum_attempts
                                      OR work.accepted_at + work.maximum_elapsed <= clock_timestamp())
                                THEN 'failed'
                            WHEN @completion_kind = 'proven_no_effect'
                                THEN 'retry_wait'
                            WHEN @completion_kind = 'retry'
                                 AND NOT permit.exists
                                 AND work.cancellation_requested_at IS NOT NULL
                                THEN 'canceled_before_effect'
                            WHEN @completion_kind = 'retry'
                                 AND NOT permit.exists
                                 AND (work.attempt_number >= work.maximum_attempts
                                      OR work.accepted_at + work.maximum_elapsed <= clock_timestamp())
                                THEN 'failed'
                            WHEN @completion_kind = 'retry'
                                 AND permit.exists
                                 AND work.provider_safety = 'reconcile_before_retry'
                                THEN 'suspended_reconciliation_required'
                            WHEN @completion_kind = 'retry'
                                 AND permit.exists
                                 AND work.provider_safety = 'manual_resolution'
                                THEN 'suspended_manual_resolution'
                            WHEN @completion_kind = 'retry'
                                 AND permit.exists
                                 AND work.cancellation_requested_at IS NOT NULL
                                THEN 'suspended_ambiguous_external_outcome'
                            WHEN @completion_kind = 'retry'
                                 AND permit.exists
                                 AND (work.attempt_number >= work.maximum_attempts
                                      OR work.accepted_at + work.maximum_elapsed <= clock_timestamp())
                                THEN 'suspended_ambiguous_external_outcome'
                            ELSE 'retry_wait'
                        END AS next_state,
                        clock_timestamp() + LEAST(
                            @retry_delay,
                            work.maximum_retry_delay) AS next_due_at
                    FROM appsurface_durable.work AS work
                    CROSS JOIN permit
                    WHERE work.scope_id = @scope_id
                      AND work.work_id = @work_id
                      AND work.attempt_number = @attempt_number
                      AND work.lease_generation = @lease_generation
                      AND work.scope_generation = @scope_generation
                      AND work.runtime_epoch = @runtime_epoch
                      AND work.lease_owner = @lease_owner
                      AND work.lease_expires_at > clock_timestamp()
                      AND work.state IN ('leased', 'effect_permitted', 'cancel_pending')
                      AND (@completion_kind <> 'succeeded' OR permit.exists)
                ),
                updated AS
                (
                    UPDATE appsurface_durable.work AS work
                    SET state = decision.next_state,
                        due_at = CASE WHEN decision.next_state = 'retry_wait' THEN decision.next_due_at ELSE work.due_at END,
                        updated_at = clock_timestamp(),
                        terminal_at = CASE
                            WHEN decision.next_state IN
                                ('succeeded', 'succeeded_after_cancel_requested', 'failed', 'canceled_before_effect')
                                THEN clock_timestamp()
                            ELSE NULL
                        END,
                        lease_owner = NULL,
                        lease_started_at = NULL,
                        lease_expires_at = NULL,
                        revision = work.revision + 1,
                        result_contract_id = CASE WHEN @completion_kind = 'succeeded' THEN @result_contract_id ELSE NULL END,
                        result_schema_version = CASE WHEN @completion_kind = 'succeeded' THEN @result_schema_version ELSE NULL END,
                        result_codec_id = CASE WHEN @completion_kind = 'succeeded' THEN @result_codec_id ELSE NULL END,
                        result_classification = CASE WHEN @completion_kind = 'succeeded' THEN @result_classification ELSE NULL END,
                        result_retention_policy_id = CASE WHEN @completion_kind = 'succeeded' THEN @result_retention_policy_id ELSE NULL END,
                        result_payload = CASE WHEN @completion_kind = 'succeeded' THEN @result_payload ELSE NULL END,
                        result_sha256 = CASE WHEN @completion_kind = 'succeeded' THEN @result_sha256 ELSE NULL END,
                        terminal_code = @terminal_code
                    FROM decision
                    WHERE work.scope_id = decision.scope_id
                      AND work.work_id = decision.work_id
                    RETURNING work.state, work.due_at, work.revision
                )
                SELECT state, due_at, revision
                FROM updated;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddClaimIdentityParameters(command, claim);
            command.Parameters.AddWithValue("completion_kind", FormatCompletionKind(completion.Kind));
            command.Parameters.Add(new NpgsqlParameter("retry_delay", NpgsqlDbType.Interval)
            {
                Value = effectiveRetryDelay is { } calculatedRetryDelay ? calculatedRetryDelay : DBNull.Value,
            });
            command.Parameters.AddWithValue("terminal_code", terminalCode);
            AddResultParameters(command, completion.Result);

            string? state = null;
            DateTimeOffset? dueAt = null;
            long revision = 0;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    state = reader.GetString(0);
                    dueAt = ReadUtc(reader, 1);
                    revision = reader.GetInt64(2);
                }
            }

            if (state is null || dueAt is null)
            {
                var staleResult = await RecordStaleCompletionAsync(
                    connection,
                    transaction,
                    claim,
                    completion,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return staleResult;
            }

            await UpdateEffectPermitObservationAsync(
                connection,
                transaction,
                claim,
                completion,
                cancellationToken).ConfigureAwait(false);
            await UpdateDispatchForCompletionAsync(
                connection,
                transaction,
                claim,
                state,
                dueAt.Value,
                revision,
                cancellationToken).ConfigureAwait(false);
            var completedClaim = claim with { Revision = revision };
            await InsertHistoryAsync(
                connection,
                transaction,
                completedClaim,
                $"completion_{FormatCompletionKind(completion.Kind)}",
                isStaleObservation: false,
                completion.DetailsJson,
                cancellationToken).ConfigureAwait(false);
            var parsedState = ParseWorkState(state);
            if (onProjectionApplied is not null &&
                (IsTerminal(parsedState) || parsedState == DurableWorkState.Suspended))
            {
                await onProjectionApplied(transaction, parsedState, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlWorkCompletionResult(
                PostgreSqlWorkObservationOutcome.Applied,
                parsedState,
                revision,
                state == "retry_wait" ? dueAt : null);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask<PostgreSqlWorkCompletionResult> RecordPreparationFailureAsync(
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken = default) =>
        RecordCompletionAsync(
            claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.ContractUnavailable,
                DurableProblemCodes.WorkContractUnavailable,
                "{}"),
            cancellationToken: cancellationToken);

    internal async ValueTask<PostgreSqlCancellationResult> RequestCancellationAsync(
        DurableScopeId scopeId,
        DurableWorkId workId,
        string actorId,
        string reasonCode,
        long expectedRevision,
        Func<NpgsqlTransaction, DurableWorkState, CancellationToken, ValueTask>? onProjectionApplied = null,
        CancellationToken cancellationToken = default)
    {
        actorId = RequireBoundedText(actorId, nameof(actorId), 200);
        reasonCode = RequireBoundedText(reasonCode, nameof(reasonCode), 120);
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(
                connection, transaction, _runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
            const string sql = """
                WITH evidence AS
                (
                    SELECT EXISTS
                    (
                        SELECT 1
                        FROM appsurface_durable.effect_permit
                        WHERE scope_id = @scope_id
                          AND work_id = @work_id
                          AND status IN ('granted', 'ambiguous')
                    ) AS has_ambiguous_permit
                ),
                updated AS
                (
                    UPDATE appsurface_durable.work AS work
                    SET cancellation_requested_at = COALESCE(work.cancellation_requested_at, clock_timestamp()),
                        state = CASE
                            WHEN work.state = 'reconciling' THEN 'reconciling'
                            WHEN work.state IN ('effect_permitted', 'cancel_pending')
                                 AND evidence.has_ambiguous_permit
                                THEN 'cancel_pending'
                            WHEN evidence.has_ambiguous_permit
                                 AND work.provider_safety = 'reconcile_before_retry'
                                THEN 'suspended_reconciliation_required'
                            WHEN evidence.has_ambiguous_permit
                                 AND work.provider_safety = 'manual_resolution'
                                THEN 'suspended_manual_resolution'
                            WHEN evidence.has_ambiguous_permit
                                THEN 'suspended_ambiguous_external_outcome'
                            ELSE 'canceled_before_effect'
                        END,
                        terminal_at = CASE
                            WHEN work.state <> 'reconciling' AND NOT evidence.has_ambiguous_permit
                                THEN clock_timestamp()
                            ELSE NULL
                        END,
                        lease_owner = CASE
                            WHEN work.state IN ('effect_permitted', 'cancel_pending')
                                 AND evidence.has_ambiguous_permit THEN work.lease_owner
                            ELSE NULL
                        END,
                        lease_started_at = CASE
                            WHEN work.state IN ('effect_permitted', 'cancel_pending')
                                 AND evidence.has_ambiguous_permit THEN work.lease_started_at
                            ELSE NULL
                        END,
                        lease_expires_at = CASE
                            WHEN work.state IN ('effect_permitted', 'cancel_pending')
                                 AND evidence.has_ambiguous_permit THEN work.lease_expires_at
                            ELSE NULL
                        END,
                        updated_at = clock_timestamp(),
                        revision = work.revision + 1
                    FROM evidence
                    WHERE work.scope_id = @scope_id
                      AND work.work_id = @work_id
                      AND work.revision = @expected_revision
                      AND work.state IN
                          ('pending', 'retry_wait', 'leased', 'reconciling', 'effect_permitted', 'cancel_pending',
                           'suspended_ambiguous_external_outcome', 'suspended_reconciliation_required',
                           'suspended_manual_resolution', 'suspended_contract_unavailable')
                    RETURNING work.state, work.revision, work.attempt_number, work.lease_generation,
                              work.scope_generation, work.runtime_epoch
                )
                SELECT state, revision, attempt_number, lease_generation, scope_generation, runtime_epoch
                FROM updated;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("work_id", workId.Value);
            command.Parameters.AddWithValue("expected_revision", expectedRevision);
            string? state = null;
            long revision = 0;
            int attempt = 0;
            long leaseGeneration = 0;
            long scopeGeneration = 0;
            Guid runtimeEpoch = default;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    state = reader.GetString(0);
                    revision = reader.GetInt64(1);
                    attempt = reader.GetInt32(2);
                    leaseGeneration = reader.GetInt64(3);
                    scopeGeneration = reader.GetInt64(4);
                    runtimeEpoch = reader.GetGuid(5);
                }
            }

            if (state is null)
            {
                var existing = await ReadWorkStateAsync(
                    connection,
                    transaction,
                    scopeId,
                    workId,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    return new PostgreSqlCancellationResult(PostgreSqlCancellationOutcome.NotFound, null, 0);
                }

                return existing.Value.Revision != expectedRevision
                    ? new PostgreSqlCancellationResult(
                        PostgreSqlCancellationOutcome.RevisionConflict,
                        ParseWorkState(existing.Value.State),
                        existing.Value.Revision)
                    : new PostgreSqlCancellationResult(
                        PostgreSqlCancellationOutcome.AlreadyTerminal,
                        ParseWorkState(existing.Value.State),
                        existing.Value.Revision);
            }

            var dispatchState = state switch
            {
                "canceled_before_effect" => "terminal",
                "reconciling" => "suspended",
                "suspended_ambiguous_external_outcome" or
                "suspended_reconciliation_required" or
                "suspended_manual_resolution" or
                "suspended_contract_unavailable" => "suspended",
                _ when state.StartsWith("suspended_", StringComparison.Ordinal) => "suspended",
                _ => "leased",
            };
            const string dispatchSql = """
                UPDATE appsurface_durable.dispatch
                SET state = @state,
                    expected_revision = @revision,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id
                  AND aggregate_kind = 'work'
                  AND aggregate_id = @work_id;
                """;
            await using (var dispatchCommand = new NpgsqlCommand(dispatchSql, connection, transaction))
            {
                dispatchCommand.Parameters.AddWithValue("state", dispatchState);
                dispatchCommand.Parameters.AddWithValue("revision", revision);
                dispatchCommand.Parameters.AddWithValue("scope_id", scopeId.Value);
                dispatchCommand.Parameters.AddWithValue("work_id", workId.Value);
                if (await dispatchCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The authoritative dispatch row was absent while projecting cancellation.");
                }
            }

            const string historySql = """
                INSERT INTO appsurface_durable.work_history
                    (scope_id, work_id, aggregate_revision, event_type, actor_id, reason_code, attempt_number,
                     lease_generation, scope_generation, runtime_epoch, details)
                VALUES
                    (@scope_id, @work_id, @revision, @event_type, @actor_id, @reason_code, @attempt_number,
                     @lease_generation, @scope_generation, @runtime_epoch,
                     jsonb_build_object('actor_id', @actor_id, 'reason_code', @reason_code));
                """;
            await using (var historyCommand = new NpgsqlCommand(historySql, connection, transaction))
            {
                historyCommand.Parameters.AddWithValue("scope_id", scopeId.Value);
                historyCommand.Parameters.AddWithValue("work_id", workId.Value);
                historyCommand.Parameters.AddWithValue("revision", revision);
                historyCommand.Parameters.AddWithValue(
                    "event_type",
                    state == "canceled_before_effect" ? "canceled_before_effect" : "cancel_pending");
                historyCommand.Parameters.AddWithValue("attempt_number", attempt);
                historyCommand.Parameters.AddWithValue("lease_generation", leaseGeneration);
                historyCommand.Parameters.AddWithValue("scope_generation", scopeGeneration);
                historyCommand.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
                historyCommand.Parameters.AddWithValue("actor_id", actorId);
                historyCommand.Parameters.AddWithValue("reason_code", reasonCode);
                if (await historyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The authoritative cancellation history row was not appended exactly once.");
                }
            }

            var parsedState = ParseWorkState(state);
            if (onProjectionApplied is not null &&
                (IsTerminal(parsedState) || parsedState == DurableWorkState.Suspended))
            {
                await onProjectionApplied(transaction, parsedState, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlCancellationResult(
                PostgreSqlCancellationOutcome.Applied,
                parsedState,
                revision);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<TimeSpan?> ReadRetryDelayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT backoff_algorithm,
                   attempt_number,
                   initial_retry_delay,
                   maximum_retry_delay
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id
              AND work_id = @work_id
              AND attempt_number = @attempt_number
              AND lease_generation = @lease_generation
              AND scope_generation = @scope_generation
              AND runtime_epoch = @runtime_epoch
              AND state IN ('leased', 'effect_permitted', 'cancel_pending');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddClaimIdentityParameters(command, claim);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return PostgreSqlDurableRetryDelayCalculator.Calculate(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetTimeSpan(2),
            reader.GetTimeSpan(3));
    }

    internal async ValueTask<PostgreSqlScopeMutationResult> DisableScopeAsync(
        DurableScopeId scopeId,
        string actorId,
        string reasonCode,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        actorId = RequireBoundedText(actorId, nameof(actorId), 200);
        reasonCode = RequireBoundedText(reasonCode, nameof(reasonCode), 120);
        if (expectedGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(
                connection, transaction, _runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
            const string sql = """
                WITH updated AS
                (
                    UPDATE appsurface_durable.scope
                    SET state = 'disabled',
                        generation = generation + 1,
                        updated_at = clock_timestamp()
                    WHERE scope_id = @scope_id
                      AND generation = @expected_generation
                      AND state = 'active'
                    RETURNING scope_id, generation
                ),
                historied AS
                (
                    INSERT INTO appsurface_durable.scope_history
                        (scope_id, generation, event_type, actor_id, reason_code)
                    SELECT scope_id, generation, 'disabled', @actor_id, @reason_code
                    FROM updated
                    RETURNING generation
                )
                SELECT generation
                FROM updated;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("expected_generation", expectedGeneration);
            command.Parameters.AddWithValue("actor_id", actorId);
            command.Parameters.AddWithValue("reason_code", reasonCode);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is long generation)
            {
                await SuspendScopeAggregatesAsync(
                    connection,
                    transaction,
                    scopeId,
                    actorId,
                    reasonCode,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlScopeMutationResult(PostgreSqlScopeMutationOutcome.Applied, generation);
            }

            const string currentSql = "SELECT generation, state FROM appsurface_durable.scope WHERE scope_id = @scope_id;";
            await using var currentCommand = new NpgsqlCommand(currentSql, connection, transaction);
            currentCommand.Parameters.AddWithValue("scope_id", scopeId.Value);
            long? currentGeneration = null;
            string? currentState = null;
            await using (var reader = await currentCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    currentGeneration = reader.GetInt64(0);
                    currentState = reader.GetString(1);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return currentGeneration switch
            {
                null => new PostgreSqlScopeMutationResult(PostgreSqlScopeMutationOutcome.NotFound, 0),
                _ when currentGeneration.Value != expectedGeneration => new PostgreSqlScopeMutationResult(
                    PostgreSqlScopeMutationOutcome.GenerationConflict,
                    currentGeneration.Value),
                _ when currentState == "disabled" => new PostgreSqlScopeMutationResult(
                    PostgreSqlScopeMutationOutcome.AlreadyDisabled,
                    currentGeneration.Value),
                _ => new PostgreSqlScopeMutationResult(
                    PostgreSqlScopeMutationOutcome.GenerationConflict,
                    currentGeneration.Value),
            };
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask SuspendScopeAggregatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        string actorId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH work_evidence AS
            (
                SELECT work.scope_id,
                       work.work_id,
                       EXISTS
                       (
                           SELECT 1
                           FROM appsurface_durable.effect_permit AS permit
                           WHERE permit.scope_id = work.scope_id
                             AND permit.work_id = work.work_id
                             AND permit.status IN ('granted', 'ambiguous')
                       ) AS has_ambiguous_permit
                FROM appsurface_durable.work AS work
                WHERE work.scope_id = @scope_id
                  AND work.state IN
                      ('pending', 'retry_wait', 'leased', 'reconciling', 'effect_permitted', 'cancel_pending',
                       'suspended_ambiguous_external_outcome', 'suspended_reconciliation_required',
                       'suspended_manual_resolution', 'suspended_contract_unavailable')
                FOR UPDATE OF work
            ),
            projected_work AS
            (
                UPDATE appsurface_durable.work AS work
                SET state = CASE
                        WHEN work.state = 'reconciling' THEN 'reconciling'
                        WHEN evidence.has_ambiguous_permit
                             AND work.provider_safety = 'reconcile_before_retry'
                            THEN 'suspended_reconciliation_required'
                        WHEN evidence.has_ambiguous_permit
                             AND work.provider_safety = 'manual_resolution'
                            THEN 'suspended_manual_resolution'
                        WHEN evidence.has_ambiguous_permit
                            THEN 'suspended_ambiguous_external_outcome'
                        ELSE 'canceled_before_effect'
                    END,
                    cancellation_requested_at = COALESCE(work.cancellation_requested_at, clock_timestamp()),
                    terminal_at = CASE
                        WHEN work.state <> 'reconciling' AND NOT evidence.has_ambiguous_permit
                            THEN clock_timestamp()
                        ELSE NULL
                    END,
                    terminal_code = CASE
                        WHEN work.state = 'reconciling' THEN work.terminal_code
                        ELSE @scope_disabled_code
                    END,
                    lease_owner = NULL,
                    lease_started_at = NULL,
                    lease_expires_at = NULL,
                    revision = work.revision + 1,
                    updated_at = clock_timestamp()
                FROM work_evidence AS evidence
                WHERE work.scope_id = evidence.scope_id
                  AND work.work_id = evidence.work_id
                RETURNING work.work_id, work.state, work.revision, work.attempt_number,
                          work.lease_generation, work.scope_generation, work.runtime_epoch
            ),
            projected_work_dispatch AS
            (
                UPDATE appsurface_durable.dispatch AS dispatch
                SET state = CASE
                        WHEN projected_work.state = 'canceled_before_effect' THEN 'terminal'
                        ELSE 'suspended'
                    END,
                    expected_revision = projected_work.revision,
                    updated_at = clock_timestamp()
                FROM projected_work
                WHERE dispatch.scope_id = @scope_id
                  AND dispatch.aggregate_kind = 'work'
                  AND dispatch.aggregate_id = projected_work.work_id
                RETURNING dispatch.aggregate_id
            ),
            historied_work AS
            (
                INSERT INTO appsurface_durable.work_history
                    (scope_id, work_id, aggregate_revision, event_type, actor_id, reason_code,
                     attempt_number, lease_generation, scope_generation, runtime_epoch, details)
                SELECT @scope_id, work_id, revision, 'scope_disabled', @actor_id, @reason_code,
                       attempt_number, lease_generation, scope_generation, runtime_epoch,
                       jsonb_build_object('code', @scope_disabled_code, 'resulting_state', state)
                FROM projected_work
                RETURNING event_id
            )
            SELECT
                (SELECT count(*) FROM projected_work),
                (SELECT count(*) FROM projected_work_dispatch),
                (SELECT count(*) FROM historied_work);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("reason_code", reasonCode);
        command.Parameters.AddWithValue("scope_disabled_code", DurableProblemCodes.ScopeDisabled);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt64(0) != reader.GetInt64(1)
            || reader.GetInt64(0) != reader.GetInt64(2))
        {
            throw new InvalidOperationException(
                "Scope disable did not project every authoritative Work to dispatch and history exactly once.");
        }
    }

    private static async ValueTask<(string State, long Revision)?> ReadWorkStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId workId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state, revision
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id
              AND work_id = @work_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private static async ValueTask<PostgreSqlWorkCompletionResult> RecordStaleCompletionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim,
        PostgreSqlWorkCompletion completion,
        CancellationToken cancellationToken)
    {
        const string stateSql = """
            SELECT state, revision,
                   attempt_number = @attempt_number
                   AND lease_generation = @lease_generation
                   AND scope_generation = @scope_generation
                   AND runtime_epoch = @runtime_epoch AS same_identity
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id
              AND work_id = @work_id;
            """;
        await using var stateCommand = new NpgsqlCommand(stateSql, connection, transaction);
        AddClaimIdentityParameters(stateCommand, claim);
        string? state = null;
        long revision = claim.Revision;
        var sameIdentity = false;
        await using (var reader = await stateCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                state = reader.GetString(0);
                revision = reader.GetInt64(1);
                sameIdentity = reader.GetBoolean(2);
            }
        }

        if (state is null)
        {
            return new PostgreSqlWorkCompletionResult(
                PostgreSqlWorkObservationOutcome.StaleObservation,
                DurableWorkState.Suspended,
                revision,
                null);
        }

        await InsertStaleCompletionHistoryAsync(
            connection,
            transaction,
            claim,
            $"stale_completion_{FormatCompletionKind(completion.Kind)}",
            completion,
            cancellationToken).ConfigureAwait(false);
        var parsedState = ParseWorkState(state);
        var alreadyTerminal = sameIdentity && parsedState is
            DurableWorkState.Succeeded or
            DurableWorkState.SucceededAfterCancelRequested or
            DurableWorkState.FailedTerminal or
            DurableWorkState.CanceledBeforeEffect or
            DurableWorkState.Suspended;
        return new PostgreSqlWorkCompletionResult(
            alreadyTerminal
                ? PostgreSqlWorkObservationOutcome.AlreadyTerminal
                : PostgreSqlWorkObservationOutcome.StaleObservation,
            parsedState,
            revision,
            null);
    }

    private static async ValueTask InsertStaleCompletionHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim,
        string eventType,
        PostgreSqlWorkCompletion completion,
        CancellationToken cancellationToken)
    {
        EnsureBoundedJson(completion.DetailsJson);
        const string sql = """
            INSERT INTO appsurface_durable.work_history
            (
                scope_id, work_id, event_type, attempt_number, lease_generation, scope_generation,
                aggregate_revision,
                runtime_epoch, is_stale_observation,
                observation_contract_id, observation_schema_version, observation_codec_id,
                observation_classification, observation_payload, observation_sha256, details
            )
            VALUES
            (
                @scope_id, @work_id, @event_type, @attempt_number, @lease_generation, @scope_generation,
                @aggregate_revision,
                @runtime_epoch, true,
                @observation_contract_id, @observation_schema_version, @observation_codec_id,
                @observation_classification, @observation_payload, @observation_sha256, @details::jsonb
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddClaimIdentityParameters(command, claim);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("aggregate_revision", claim.Revision);
        command.Parameters.AddWithValue("details", completion.DetailsJson);
        AddObservationParameters(command, completion.Result);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddObservationParameters(NpgsqlCommand command, DurableEncodedPayload? result)
    {
        command.Parameters.Add(new NpgsqlParameter("observation_contract_id", NpgsqlDbType.Text)
        {
            Value = result?.ContractName ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("observation_schema_version", NpgsqlDbType.Text)
        {
            Value = result?.ContractVersion ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("observation_codec_id", NpgsqlDbType.Text)
        {
            Value = result is null ? DBNull.Value : $"{result.ContractName}@{result.ContractVersion}",
        });
        command.Parameters.Add(new NpgsqlParameter("observation_classification", NpgsqlDbType.Text)
        {
            Value = result is null ? DBNull.Value : FormatClassification(result.Classification),
        });
        command.Parameters.Add(new NpgsqlParameter("observation_payload", NpgsqlDbType.Bytea)
        {
            Value = result?.Content.ToArray() ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("observation_sha256", NpgsqlDbType.Bytea)
        {
            Value = result is null ? DBNull.Value : Convert.FromHexString(result.Sha256),
        });
    }

    private static async ValueTask UpdateEffectPermitObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim,
        PostgreSqlWorkCompletion completion,
        CancellationToken cancellationToken)
    {
        var status = completion.Kind switch
        {
            PostgreSqlWorkCompletionKind.Succeeded => "known_succeeded",
            PostgreSqlWorkCompletionKind.ProvenNoEffect => "proven_no_effect",
            PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome => "ambiguous",
            _ => null,
        };
        if (status is null)
        {
            return;
        }

        const string sql = """
            UPDATE appsurface_durable.effect_permit
            SET status = @status,
                observed_at = clock_timestamp(),
                details = @details::jsonb
            WHERE scope_id = @scope_id
              AND work_id = @work_id
              AND attempt_number = @attempt_number
              AND lease_generation = @lease_generation
              AND scope_generation = @scope_generation
              AND runtime_epoch = @runtime_epoch;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddClaimIdentityParameters(command, claim);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("details", completion.DetailsJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateDispatchForCompletionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim,
        string state,
        DateTimeOffset dueAt,
        long revision,
        CancellationToken cancellationToken)
    {
        var dispatchState = state switch
        {
            "retry_wait" => "available",
            "suspended_ambiguous_external_outcome" or
            "suspended_reconciliation_required" or
            "suspended_manual_resolution" or
            "suspended_contract_unavailable" => "suspended",
            _ => "terminal",
        };
        const string sql = """
            UPDATE appsurface_durable.dispatch
            SET state = @state,
                due_at = @due_at,
                expected_revision = @revision,
                updated_at = clock_timestamp()
            WHERE dispatch_id = @dispatch_id
              AND scope_id = @scope_id
              AND aggregate_kind = 'work'
              AND aggregate_id = @work_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("state", dispatchState);
        command.Parameters.AddWithValue("due_at", dueAt.UtcDateTime);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("dispatch_id", claim.DispatchId);
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", claim.WorkId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The authoritative dispatch row was absent while recording completion.");
        }
    }

    private static void AddResultParameters(NpgsqlCommand command, DurableEncodedPayload? result)
    {
        command.Parameters.Add(new NpgsqlParameter("result_contract_id", NpgsqlDbType.Text)
        {
            Value = result?.ContractName ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("result_schema_version", NpgsqlDbType.Text)
        {
            Value = result?.ContractVersion ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("result_codec_id", NpgsqlDbType.Text)
        {
            Value = result is null ? DBNull.Value : $"{result.ContractName}@{result.ContractVersion}",
        });
        command.Parameters.Add(new NpgsqlParameter("result_classification", NpgsqlDbType.Text)
        {
            Value = result is null ? DBNull.Value : FormatClassification(result.Classification),
        });
        command.Parameters.Add(new NpgsqlParameter("result_retention_policy_id", NpgsqlDbType.Text)
        {
            Value = result?.RetentionPolicyId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("result_payload", NpgsqlDbType.Bytea)
        {
            Value = result?.Content.ToArray() ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("result_sha256", NpgsqlDbType.Bytea)
        {
            Value = result is null ? DBNull.Value : Convert.FromHexString(result.Sha256),
        });
    }

    private static string FormatCompletionKind(PostgreSqlWorkCompletionKind kind) => kind switch
    {
        PostgreSqlWorkCompletionKind.Succeeded => "succeeded",
        PostgreSqlWorkCompletionKind.FailedTerminal => "failed_terminal",
        PostgreSqlWorkCompletionKind.Retry => "retry",
        PostgreSqlWorkCompletionKind.ProvenNoEffect => "proven_no_effect",
        PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome => "ambiguous_external_outcome",
        PostgreSqlWorkCompletionKind.ContractUnavailable => "contract_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool IsTerminal(DurableWorkState state) => state is
        DurableWorkState.Succeeded or
        DurableWorkState.SucceededAfterCancelRequested or
        DurableWorkState.FailedTerminal or
        DurableWorkState.CanceledBeforeEffect;

    private static ValueTask ValidateSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? expectedStoreId,
        CancellationToken cancellationToken) =>
        ValidateSchemaCoreAsync(connection, transaction, expectedStoreId, cancellationToken, afterExistence: null);

    /// <summary>Validates schema removal after the successful existence probe without relying on timing.</summary>
    /// <param name="connection">Open connection that owns the caller transaction.</param>
    /// <param name="transaction">Caller-owned transaction used for every validation query.</param>
    /// <param name="expectedStoreId">Expected durable store identity, or <see langword="null"/> to omit identity validation.</param>
    /// <param name="cancellationToken">Token that cancels validation database operations.</param>
    /// <param name="afterExistence">Test callback invoked after the catalog probe and before metadata is read.</param>
    /// <remarks>This test-only seam preserves the callback-free production validation contract.</remarks>
    internal static ValueTask ValidateSchemaRemovalForTestingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? expectedStoreId,
        CancellationToken cancellationToken,
        Func<ValueTask> afterExistence)
    {
        ArgumentNullException.ThrowIfNull(afterExistence);
        return ValidateSchemaCoreAsync(connection, transaction, expectedStoreId, cancellationToken, afterExistence);
    }

    private static async ValueTask ValidateSchemaCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? expectedStoreId,
        CancellationToken cancellationToken,
        Func<ValueTask>? afterExistence)
    {
        const string existenceSql = "SELECT to_regclass('appsurface_durable.store_metadata') IS NOT NULL;";
        await using (var existenceCommand = new NpgsqlCommand(existenceSql, connection, transaction))
        {
            var exists = await existenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is not true)
            {
                throw CreateMissingSchemaException();
            }
        }

        if (afterExistence is not null)
        {
            await afterExistence().ConfigureAwait(false);
        }

        const string sql = """
            SELECT store_id,
                   schema_version,
                   minimum_reader_version,
                   maximum_reader_version,
                   minimum_writer_version,
                   maximum_writer_version
            FROM appsurface_durable.store_metadata
            WHERE singleton;
            """;
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            var required = RequiredSchemaVersion;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var hasMetadata = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var actualStoreId = hasMetadata ? reader.GetGuid(0) : Guid.Empty;
            var installed = hasMetadata ? reader.GetInt32(1) : 0;
            var minimumReader = hasMetadata ? reader.GetInt32(2) : 0;
            var maximumReader = hasMetadata ? reader.GetInt32(3) : 0;
            var minimumWriter = hasMetadata ? reader.GetInt32(4) : 0;
            var maximumWriter = hasMetadata ? reader.GetInt32(5) : 0;
            var compatible =
                installed >= required &&
                required >= minimumReader &&
                required <= maximumReader &&
                required >= minimumWriter &&
                required <= maximumWriter;
            if (!compatible)
            {
                var compatibility = installed < required
                    ? DurableRuntimeSchemaCompatibility.UpgradeRequired
                    : DurableRuntimeSchemaCompatibility.StoreTooNew;
                throw new DurableRuntimeSchemaException(new DurableRuntimeSchemaStatus(
                    compatibility,
                    actualStoreId,
                    null,
                    installed,
                    required,
                    minimumReader,
                    maximumReader,
                    minimumWriter,
                    maximumWriter,
                    [],
                    [],
                    "The caller-owned transaction targets a database with an incompatible durable schema."));
            }

            if (expectedStoreId is { } expected && actualStoreId != expected)
            {
                throw new InvalidOperationException(
                    $"{DurableProblemCodes.StoreIdentityMismatch}: The caller-owned transaction targets a different durable store. " +
                    "Use a transaction opened from the same physical PostgreSQL database configured for this runtime.");
            }
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            throw CreateMissingSchemaException(exception);
        }
    }

    private static DurableRuntimeSchemaException CreateMissingSchemaException() =>
        new(CreateMissingSchemaStatus());

    /// <summary>Preserves the PostgreSQL failure that exposed a missing schema without copying its server text.</summary>
    internal static DurableRuntimeSchemaException CreateMissingSchemaException(PostgresException innerException) =>
        new(CreateMissingSchemaStatus(), innerException);

    private static DurableRuntimeSchemaStatus CreateMissingSchemaStatus() =>
        new(
            DurableRuntimeSchemaCompatibility.Missing,
            Guid.Empty,
            null,
            0,
            RequiredSchemaVersion,
            0,
            0,
            0,
            0,
            [],
            Enumerable.Range(1, RequiredSchemaVersion).ToArray(),
            "The caller-owned transaction targets a database where the durable schema is not installed.");

    private static async ValueTask EnsureCurrentEpochAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runtimeEpoch,
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

        const string sql = """
            SELECT active_runtime_epoch
            FROM appsurface_durable.store_metadata
            WHERE singleton;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not Guid activeEpoch || activeEpoch != runtimeEpoch)
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.RecoveryEpochRequired}: The configured runtime epoch is not the active durable store epoch. " +
                "Initialize or rotate the epoch explicitly before accepting or executing Work.");
        }
    }

    private static async ValueTask SetScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long?> EnsureActiveScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.scope (scope_id)
            VALUES (@scope_id)
            ON CONFLICT (scope_id) DO NOTHING;

            SELECT generation
            FROM appsurface_durable.scope
            WHERE scope_id = @scope_id
              AND state = 'active'
            FOR SHARE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long generation ? generation : null;
    }

    private static async ValueTask<bool> LockActiveScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        long? expectedGeneration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT generation
            FROM appsurface_durable.scope
            WHERE scope_id = @scope_id
              AND state = 'active'
              AND (@expected_generation IS NULL OR generation = @expected_generation)
            FOR SHARE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.Add(new NpgsqlParameter("expected_generation", NpgsqlDbType.Bigint)
        {
            Value = expectedGeneration is { } generation ? generation : DBNull.Value,
        });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long;
    }

    private static async ValueTask<DurableWorkAcceptance?> TryInsertAcceptanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableWorkRequest request,
        Guid runtimeEpoch,
        long scopeGeneration,
        DurableWorkId workId,
        Guid dispatchId,
        DurableCommandFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH accepted AS
            (
                INSERT INTO appsurface_durable.work
                (
                    scope_id, work_id, activity_id, command_id, idempotency_key, work_name, work_version,
                    contract_id, payload_schema_version, codec_id, payload, payload_sha256,
                    payload_classification, payload_retention, request_fingerprint_schema, request_fingerprint_sha256,
                    state, provider_safety, due_at, scope_generation, runtime_epoch,
                    maximum_attempts, maximum_elapsed, backoff_algorithm,
                    initial_retry_delay, maximum_retry_delay, lease_duration, lease_renewal_cadence,
                    maximum_lease_lifetime
                )
                VALUES
                (
                    @scope_id, @work_id, @activity_id, @command_id, @idempotency_key, @work_name, @work_version,
                    @contract_id, @payload_schema_version, @codec_id, @payload, @payload_sha256,
                    @payload_classification, @payload_retention, @request_fingerprint_schema, @request_fingerprint_sha256,
                    'pending', @provider_safety, COALESCE(@due_at, clock_timestamp()),
                    @scope_generation, @runtime_epoch,
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
                    (dispatch_id, scope_id, aggregate_kind, aggregate_id, due_at, state, expected_revision)
                SELECT @dispatch_id, @scope_id, 'work', work_id, due_at, 'available', revision
                FROM accepted
                RETURNING aggregate_id
            ),
            historied AS
            (
                INSERT INTO appsurface_durable.work_history
                    (scope_id, work_id, aggregate_revision, event_type, command_id,
                     attempt_number, lease_generation, scope_generation, runtime_epoch, details)
                SELECT @scope_id, work_id, revision, 'accepted', command_id,
                       0, 0, @scope_generation, @runtime_epoch,
                       jsonb_build_object('work_name', @work_name, 'work_version', @work_version)
                FROM accepted
                RETURNING work_id
            )
            SELECT work_id, command_id, revision, accepted_at
            FROM accepted;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddAcceptanceParameters(command, request, runtimeEpoch, scopeGeneration, workId, dispatchId, fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DurableWorkAcceptance(
            new DurableWorkId(reader.GetString(0)),
            new DurableCommandId(reader.GetString(1)),
            DurableWorkAcceptanceKind.Accepted,
            reader.GetInt64(2),
            ReadUtc(reader, 3));
    }

    private static void AddAcceptanceParameters(
        NpgsqlCommand command,
        DurableWorkRequest request,
        Guid runtimeEpoch,
        long scopeGeneration,
        DurableWorkId workId,
        Guid dispatchId,
        DurableCommandFingerprint fingerprint)
    {
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        command.Parameters.AddWithValue("activity_id", workId.Value);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("work_name", request.WorkName);
        command.Parameters.AddWithValue("work_version", request.WorkVersion);
        command.Parameters.AddWithValue("contract_id", request.Payload.ContractName);
        command.Parameters.AddWithValue("payload_schema_version", request.Payload.ContractVersion);
        command.Parameters.AddWithValue("codec_id", $"{request.Payload.ContractName}@{request.Payload.ContractVersion}");
        command.Parameters.AddWithValue("payload", request.Payload.Content.ToArray());
        command.Parameters.AddWithValue("payload_sha256", Convert.FromHexString(request.Payload.Sha256));
        command.Parameters.AddWithValue("payload_classification", FormatClassification(request.Payload.Classification));
        command.Parameters.AddWithValue("payload_retention", request.Payload.RetentionPolicyId);
        command.Parameters.AddWithValue("request_fingerprint_schema", fingerprint.SchemaId);
        command.Parameters.AddWithValue("request_fingerprint_sha256", fingerprint.Sha256);
        command.Parameters.AddWithValue("provider_safety", FormatProviderSafety(request.ProviderSafety));
        command.Parameters.Add(new NpgsqlParameter("due_at", NpgsqlDbType.TimestampTz)
        {
            Value = request.DueAtUtc is { } dueAt ? dueAt.UtcDateTime : DBNull.Value,
        });
        command.Parameters.AddWithValue("scope_generation", scopeGeneration);
        command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
        command.Parameters.AddWithValue("maximum_attempts", request.RetryPolicy.MaximumAttempts);
        command.Parameters.AddWithValue("maximum_elapsed", request.RetryPolicy.MaximumElapsedTime);
        command.Parameters.AddWithValue("backoff_algorithm", request.RetryPolicy.BackoffAlgorithm);
        command.Parameters.AddWithValue("initial_retry_delay", request.RetryPolicy.InitialRetryDelay);
        command.Parameters.AddWithValue("maximum_retry_delay", request.RetryPolicy.MaximumRetryDelay);
        command.Parameters.AddWithValue("lease_duration", request.RetryPolicy.LeaseDuration);
        command.Parameters.AddWithValue("lease_renewal_cadence", request.RetryPolicy.RenewalCadence);
        command.Parameters.AddWithValue("maximum_lease_lifetime", request.RetryPolicy.MaximumLeaseLifetime);
        command.Parameters.AddWithValue("dispatch_id", dispatchId);
    }

    private static async ValueTask<DurableOperationResult<DurableWorkAcceptance>> ReadDuplicateAcceptanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableWorkRequest request,
        DurableCommandFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT work_id, command_id, revision, accepted_at, request_fingerprint_schema, request_fingerprint_sha256
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id
              AND (command_id = @command_id OR idempotency_key = @idempotency_key)
            ORDER BY work_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        var matches = new List<DuplicateAcceptanceRow>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            matches.Add(new DuplicateAcceptanceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                ReadUtc(reader, 3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException("A durable command conflict was reported but its authoritative row is absent.");
        }

        if (matches.Count != 1 || fingerprint.Compare(new DurableCommandFingerprint(
                matches[0].RequestFingerprintSchema,
                matches[0].RequestFingerprintSha256)) != DurableCommandFingerprintMatch.Exact)
        {
            return DurableOperationResult<DurableWorkAcceptance>.Failure(new DurableProblem(
                DurableProblemCodes.CommandConflict,
                "The durable command identifier was already used for a different request.",
                "A retry reused its command identifier after changing payload, target, policy, or due time.",
                "Retry the exact original request, or submit the changed operation with a new command identifier.",
                WorkDocumentation,
                request.CommandId.Value));
        }

        return DurableOperationResult<DurableWorkAcceptance>.Success(new DurableWorkAcceptance(
            new DurableWorkId(matches[0].WorkId),
            new DurableCommandId(matches[0].CommandId),
            DurableWorkAcceptanceKind.Duplicate,
            matches[0].Revision,
            matches[0].AcceptedAtUtc));
    }

    private static async ValueTask SendWakeNotificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid dispatchId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT pg_notify('appsurface_durable_wake', @dispatch_id);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("dispatch_id", dispatchId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateDispatchForLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.dispatch
            SET state = 'leased',
                due_at = @lease_expires_at,
                expected_revision = @revision,
                updated_at = clock_timestamp()
            WHERE dispatch_id = @dispatch_id
              AND scope_id = @scope_id
              AND aggregate_kind = 'work'
              AND aggregate_id = @work_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("lease_expires_at", claim.LeaseExpiresAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("revision", claim.Revision);
        command.Parameters.AddWithValue("dispatch_id", claim.DispatchId);
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", claim.WorkId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The authoritative dispatch row was absent while committing a work claim.");
        }
    }

    private static void AddClaimIdentityParameters(NpgsqlCommand command, PostgreSqlDurableWorkClaim claim)
    {
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", claim.WorkId.Value);
        command.Parameters.AddWithValue("attempt_number", claim.AttemptNumber);
        command.Parameters.AddWithValue("lease_generation", claim.LeaseGeneration);
        command.Parameters.AddWithValue("scope_generation", claim.ScopeGeneration);
        command.Parameters.AddWithValue("runtime_epoch", claim.RuntimeEpoch);
        command.Parameters.AddWithValue("lease_owner", claim.LeaseOwner);
    }

    private static void EnsureExactProjectionCounts(
        NpgsqlDataReader reader,
        int dispatchOrdinal,
        int historyOrdinal,
        string transition)
    {
        if (reader.GetInt64(dispatchOrdinal) != 1 || reader.GetInt64(historyOrdinal) != 1)
        {
            throw new InvalidOperationException(
                $"The {transition} did not update one authoritative dispatch row and append one history row.");
        }
    }

    private static async ValueTask InsertHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDurableWorkClaim claim,
        string eventType,
        bool isStaleObservation,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        EnsureBoundedJson(detailsJson);
        const string sql = """
            INSERT INTO appsurface_durable.work_history
                (scope_id, work_id, aggregate_revision, event_type, attempt_number, lease_generation, scope_generation,
                 runtime_epoch, is_stale_observation, details)
            VALUES
                (@scope_id, @work_id, @aggregate_revision, @event_type, @attempt_number, @lease_generation, @scope_generation,
                 @runtime_epoch, @is_stale, @details::jsonb);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", claim.WorkId.Value);
        command.Parameters.AddWithValue("aggregate_revision", claim.Revision);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("attempt_number", claim.AttemptNumber);
        command.Parameters.AddWithValue("lease_generation", claim.LeaseGeneration);
        command.Parameters.AddWithValue("scope_generation", claim.ScopeGeneration);
        command.Parameters.AddWithValue("runtime_epoch", claim.RuntimeEpoch);
        command.Parameters.AddWithValue("is_stale", isStaleObservation);
        command.Parameters.AddWithValue("details", detailsJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string RequireBoundedText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"Value must contain 1 to {maximumLength} non-control characters.", parameterName);
        }

        return value;
    }

    private static async ValueTask TryRollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(exception))
        {
            // Preserve the original database or transport failure; disposal owns transaction cleanup.
        }
    }

    private static void EnsureBoundedJson(string detailsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detailsJson);
        using var document = JsonDocument.Parse(detailsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Durable history details must be a JSON object.", nameof(detailsJson));
        }

        if (JsonSerializer.SerializeToUtf8Bytes(document.RootElement).Length > 16_384)
        {
            throw new ArgumentException("Durable history details must not exceed 16384 UTF-8 bytes.", nameof(detailsJson));
        }
    }

    private sealed record DuplicateAcceptanceRow(
        string WorkId,
        string CommandId,
        long Revision,
        DateTimeOffset AcceptedAtUtc,
        string RequestFingerprintSchema,
        string RequestFingerprintSha256);

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);
}

internal sealed record PostgreSqlDispatchCandidate(
    Guid DispatchId,
    DurableScopeId ScopeId,
    DurableWorkId WorkId,
    DateTimeOffset DueAtUtc,
    long ExpectedRevision,
    short Priority);

internal sealed record PostgreSqlWorkClaimTransition(
    DurableWorkState State,
    string Code);

internal sealed record PostgreSqlDurableWorkClaim(
    Guid DispatchId,
    DurableScopeId ScopeId,
    DurableWorkId WorkId,
    string WorkName,
    string WorkVersion,
    DurableEncodedPayload Payload,
    DurableProviderSafety ProviderSafety,
    string LeaseOwner,
    string ActivityId,
    int AttemptNumber,
    long LeaseGeneration,
    long ScopeGeneration,
    Guid RuntimeEpoch,
    DateTimeOffset LeaseStartedAtUtc,
    DateTimeOffset LeaseExpiresAtUtc,
    long Revision,
    bool CancellationRequested,
    TimeSpan LeaseRenewalCadence)
{
    internal DurableClaimedWork ToProviderClaim() => new(
        ScopeId,
        WorkId,
        ActivityId,
        WorkName,
        WorkVersion,
        Payload,
        ProviderSafety,
        AttemptNumber,
        LeaseGeneration,
        ScopeGeneration,
        RuntimeEpoch.ToString("D"));
}

internal sealed record PostgreSqlEffectPermit(
    Guid PermitId,
    PostgreSqlDurableWorkClaim Claim,
    string ProviderKey,
    DateTimeOffset PermittedAtUtc);

internal sealed record PostgreSqlCanceledClaimProjection(
    PostgreSqlDurableWorkClaim Claim,
    string State,
    string Code);

internal enum PostgreSqlWorkCompletionKind
{
    Succeeded = 0,
    FailedTerminal = 1,
    Retry = 2,
    ProvenNoEffect = 3,
    AmbiguousExternalOutcome = 4,
    ContractUnavailable = 5,
}

internal sealed record PostgreSqlWorkCompletion(
    PostgreSqlWorkCompletionKind Kind,
    string Code,
    string DetailsJson,
    DurableEncodedPayload? Result = null);

internal enum PostgreSqlWorkObservationOutcome
{
    Applied = 0,
    AlreadyTerminal = 1,
    StaleObservation = 2,
}

internal sealed record PostgreSqlWorkCompletionResult(
    PostgreSqlWorkObservationOutcome Outcome,
    DurableWorkState State,
    long Revision,
    DateTimeOffset? NextDueAtUtc);

internal enum PostgreSqlCancellationOutcome
{
    Applied = 0,
    AlreadyTerminal = 1,
    NotFound = 2,
    RevisionConflict = 3,
}

internal sealed record PostgreSqlCancellationResult(
    PostgreSqlCancellationOutcome Outcome,
    DurableWorkState? State,
    long Revision);

internal enum PostgreSqlScopeMutationOutcome
{
    Applied = 0,
    AlreadyDisabled = 1,
    NotFound = 2,
    GenerationConflict = 3,
}

internal sealed record PostgreSqlScopeMutationResult(
    PostgreSqlScopeMutationOutcome Outcome,
    long Generation);
