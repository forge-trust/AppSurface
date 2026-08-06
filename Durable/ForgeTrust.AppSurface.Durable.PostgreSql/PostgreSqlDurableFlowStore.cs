using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Owns scoped Flow command and query transactions. Processor transactions live in the processing partial.
/// </summary>
internal sealed partial class PostgreSqlDurableFlowStore
{
    private static readonly Uri Diagnostics =
        new("https://forge-trust.com/troubleshooting/durable-diagnostics");

    private const string FlowCommandProjection = """
        SELECT flow_instance_id, command_id, fingerprint_schema, fingerprint_sha256,
               outcome, resulting_state, resulting_revision
        FROM appsurface_durable.flow_command
        """;

    private static readonly FrozenDictionary<string, DurableFlowState> PersistedStates =
        new Dictionary<string, DurableFlowState>(StringComparer.Ordinal)
        {
            ["ready"] = DurableFlowState.Ready,
            ["evaluating"] = DurableFlowState.Ready,
            ["waiting_event"] = DurableFlowState.WaitingForEvent,
            ["waiting_timer"] = DurableFlowState.WaitingForTimer,
            ["waiting_activity"] = DurableFlowState.WaitingForActivity,
            ["cancel_pending"] = DurableFlowState.CancelPending,
            ["completed"] = DurableFlowState.Completed,
            ["faulted"] = DurableFlowState.Faulted,
            ["canceled"] = DurableFlowState.Canceled,
            ["suspended"] = DurableFlowState.Suspended,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly NpgsqlDataSource _dataSource;
    private readonly Guid _runtimeEpoch;
    private readonly Guid _expectedStoreId;
    private readonly bool _sendWakeNotification;

    internal PostgreSqlDurableFlowStore(NpgsqlDataSource dataSource, PostgreSqlDurableWorkOptions options)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentNullException.ThrowIfNull(options);
        _runtimeEpoch = options.RuntimeEpoch;
        _expectedStoreId = options.ExpectedStoreId;
        _sendWakeNotification = options.WakeNotificationMode == PostgreSqlDurableWakeNotificationMode.Enabled;
    }

    internal async ValueTask<DurableOperationResult<DurableFlowSnapshot>> GetAsync(
        DurableFlowGetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateStoreAndSetScopeAsync(connection, transaction, request.ScopeId, cancellationToken)
                .ConfigureAwait(false);
            const string sql = """
                SELECT flow_instance_id, flow_id, flow_version, state, current_node_id, revision,
                       created_at, updated_at, cancellation_requested_at, terminal_at, terminal_code,
                       runtime_epoch
                FROM appsurface_durable.flow_instance
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableFlowSnapshot>(
                    request.InstanceId.Value,
                    DurableProblemCodes.FlowNotFound,
                    "The authorized scope does not contain the requested Flow instance.",
                    "No Flow row matched the trusted scope and instance identity.",
                    "Reload the authorized scope inventory and use an instance identity from that scope.");
            }

            var snapshot = ReadSnapshot(reader);
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowSnapshot>.Success(snapshot);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableFlowListResult>> ListAsync(
        DurableFlowListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var continuation = DecodeContinuation(request.ContinuationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateStoreAndSetScopeAsync(connection, transaction, request.ScopeId, cancellationToken)
                .ConfigureAwait(false);
            const string sql = """
                SELECT flow_instance_id, flow_id, flow_version, state, current_node_id, revision,
                       created_at, updated_at, cancellation_requested_at, terminal_at, terminal_code,
                       runtime_epoch
                FROM appsurface_durable.flow_instance
                WHERE scope_id = @scope_id
                  AND
                  (
                    @state IS NULL
                    OR state = @state
                    OR (@state = 'ready' AND state = 'evaluating')
                  )
                  AND
                  (
                    @requires_recovery IS NULL
                    OR @requires_recovery =
                       (runtime_epoch <> @runtime_epoch
                        AND state IN ('ready', 'waiting_event', 'waiting_timer', 'waiting_activity', 'cancel_pending', 'suspended'))
                  )
                  AND
                  (
                    @after_updated_at IS NULL
                    OR (updated_at, flow_instance_id) > (@after_updated_at, @after_flow_instance_id)
                  )
                ORDER BY updated_at, flow_instance_id
                LIMIT @limit;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Text)
            {
                Value = request.State is null ? DBNull.Value : FormatState(request.State.Value),
            });
            command.Parameters.Add(new NpgsqlParameter("requires_recovery", NpgsqlDbType.Boolean)
            {
                Value = request.RequiresRecoveryRelease ?? (object)DBNull.Value,
            });
            command.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
            command.Parameters.Add(new NpgsqlParameter("after_updated_at", NpgsqlDbType.TimestampTz)
            {
                Value = continuation?.UpdatedAtUtc ?? (object)DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("after_flow_instance_id", NpgsqlDbType.Text)
            {
                Value = continuation?.InstanceId ?? (object)DBNull.Value,
            });
            command.Parameters.AddWithValue("limit", request.PageSize + 1);

            var rows = new List<DurableFlowSnapshot>(request.PageSize + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadSnapshot(reader));
            }

            await reader.DisposeAsync().ConfigureAwait(false);
            var hasMore = rows.Count > request.PageSize;
            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            var next = hasMore && rows.Count > 0
                ? EncodeContinuation(rows[^1].UpdatedAtUtc, rows[^1].InstanceId.Value)
                : null;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowListResult>.Success(new DurableFlowListResult(rows, next));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableFlowCommandResult>> StartAsync(
        DurableFlowStartRequest request,
        DurableFlowRegistration registration,
        DurableTraceContext? traceContext,
        CancellationToken cancellationToken,
        bool retryAfterUniqueViolation = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndLockActiveScopeAsync(
                connection, transaction, request.ScopeId, createIfMissing: true, cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                return await CommitFailureAsync(
                    transaction,
                    Failure<DurableFlowCommandResult>(
                        request.CommandId.Value,
                        DurableProblemCodes.ScopeDisabled,
                        "The Flow was not accepted because its owning scope is disabled.",
                        "The scope lifecycle was disabled before durable acceptance.",
                        "Use a currently authorized active scope; do not bypass scope lifecycle policy."),
                    cancellationToken).ConfigureAwait(false);
            }

            var existingCommand = await ReadCommandByIdentityAsync(
                connection, transaction, request.ScopeId, request.CommandId.Value, eventId: null, cancellationToken)
                .ConfigureAwait(false);
            var existingStart = await ReadStartByIdempotencyAsync(
                connection, transaction, request.ScopeId, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            var conflict = ResolveStartIdentity(request, existingCommand, existingStart);
            if (conflict is not null)
            {
                return await CommitFailureAsync(transaction, conflict, cancellationToken).ConfigureAwait(false);
            }

            var duplicate = existingCommand ?? existingStart;
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(
                    duplicate.ToResult(DurableFlowCommandOutcome.Duplicate));
            }

            var existingInstance = await LockCurrentAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                cancellationToken).ConfigureAwait(false);
            if (existingInstance is not null)
            {
                return await CommitFailureAsync(
                    transaction,
                    Failure<DurableFlowCommandResult>(
                        request.CommandId.Value,
                        DurableProblemCodes.FlowStartConflict,
                        "The Flow instance identity is already owned by a different start.",
                        "The supplied command and idempotency identities do not resolve to the existing Flow instance.",
                        "Retry the original start identities or choose a new Flow instance identity."),
                    cancellationToken).ConfigureAwait(false);
            }

            var dispatchId = Guid.NewGuid();
            const string sql = """
                INSERT INTO appsurface_durable.flow_instance
                (
                    scope_id, flow_instance_id, flow_id, flow_version, manifest_id, authoring_model,
                    definition_fingerprint_schema, definition_fingerprint_sha256, current_node_id, state,
                    context_contract_id, context_schema_version, context_codec_id, context_payload,
                    context_sha256, context_classification, context_retention,
                    scope_generation, runtime_epoch, revision
                )
                VALUES
                (
                    @scope_id, @flow_instance_id, @flow_id, @flow_version, @manifest_id, @authoring_model,
                    @definition_fingerprint_schema, @definition_fingerprint, @current_node_id, 'ready',
                    @context_contract_id, @context_schema_version, @context_codec_id, @context_payload,
                    @context_sha256, @context_classification, @context_retention,
                    @scope_generation, @runtime_epoch, 1
                );

                INSERT INTO appsurface_durable.flow_command
                (
                    scope_id, flow_instance_id, command_id, command_type, start_idempotency_key,
                    fingerprint_schema, fingerprint_sha256, outcome, resulting_state, resulting_revision
                )
                VALUES
                (
                    @scope_id, @flow_instance_id, @command_id, 'start', @start_idempotency_key,
                    @fingerprint_schema, @fingerprint_sha256, 'accepted', 'ready', 1
                );

                INSERT INTO appsurface_durable.flow_history
                (
                    scope_id, flow_instance_id, aggregate_revision, command_id,
                    node_id, transition_kind
                )
                VALUES
                (
                    @scope_id, @flow_instance_id, 1, @command_id,
                    @current_node_id, 'start_accepted'
                );

                INSERT INTO appsurface_durable.flow_dispatch
                (
                    dispatch_id, scope_id, kind, flow_instance_id, due_at, state,
                    expected_revision, priority
                )
                VALUES
                (
                    @dispatch_id, @scope_id, 'flow', @flow_instance_id, clock_timestamp(), 'available', 1, 0
                );
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
            command.Parameters.AddWithValue("flow_id", request.FlowId);
            command.Parameters.AddWithValue("flow_version", request.FlowVersion);
            command.Parameters.AddWithValue("manifest_id", registration.ImplementationVersion);
            command.Parameters.AddWithValue("authoring_model", registration.AuthoringModel);
            command.Parameters.AddWithValue("definition_fingerprint_schema", "durable-flow-definition-manifest-v2");
            command.Parameters.AddWithValue("definition_fingerprint", registration.DefinitionFingerprint);
            command.Parameters.AddWithValue("current_node_id", registration.StartNodeId);
            AddPayloadParameters(command, "context", request.Context);
            command.Parameters.AddWithValue("scope_generation", scopeGeneration.Value);
            command.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
            command.Parameters.AddWithValue("command_id", request.CommandId.Value);
            command.Parameters.AddWithValue("start_idempotency_key", request.IdempotencyKey);
            command.Parameters.AddWithValue("fingerprint_schema", request.Fingerprint.SchemaId);
            command.Parameters.AddWithValue("fingerprint_sha256", request.Fingerprint.Sha256);
            command.Parameters.AddWithValue("dispatch_id", dispatchId);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 4)
            {
                throw new InvalidOperationException($"Flow start expected four writes but PostgreSQL reported {affected}.");
            }

            var trace = await InsertTraceContextAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                traceContext,
                "command_accepted",
                cancellationToken).ConfigureAwait(false);
            await AttachTraceContextAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                trace,
                request.CommandId.Value,
                revision: 1,
                waitId: null,
                timerId: null,
                workId: null,
                cancellationToken).ConfigureAwait(false);
            await NotifyAsync(connection, transaction, dispatchId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowCommandResult>.Success(
                new DurableFlowCommandResult(
                    request.InstanceId,
                    DurableFlowCommandOutcome.Accepted,
                    DurableFlowState.Ready,
                    1));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            if (retryAfterUniqueViolation)
            {
                return await StartAsync(request, registration, traceContext, cancellationToken, retryAfterUniqueViolation: false)
                    .ConfigureAwait(false);
            }

            return Failure<DurableFlowCommandResult>(
                request.CommandId.Value,
                DurableProblemCodes.FlowStartConflict,
                "The Flow start could not claim its instance identity after a concurrent insert.",
                "A conflicting durable identity remained after the bounded retry.",
                "Reload the persisted Flow and retry only with its original start identities.");
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask<DurableOperationResult<DurableFlowCommandResult>> RaiseEventAsync(
        DurableFlowEventRequest request,
        CancellationToken cancellationToken) =>
        RaiseEventAsync(request, validatePayload: null, traceContext: null, cancellationToken);

    internal async ValueTask<DurableOperationResult<DurableFlowCommandResult>> RaiseEventAsync(
        DurableFlowEventRequest request,
        Action<DurableEncodedPayload?>? validatePayload,
        DurableTraceContext? traceContext,
        CancellationToken cancellationToken,
        bool retryAfterUniqueViolation = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndLockActiveScopeAsync(
                connection, transaction, request.ScopeId, createIfMissing: false, cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                return await CommitFailureAsync(
                    transaction,
                    Failure<DurableFlowCommandResult>(
                        request.CommandId.Value,
                        DurableProblemCodes.ScopeDisabled,
                        "The Flow event was rejected because its owning scope is disabled.",
                        "The scope lifecycle changed before event delivery.",
                        "Do not deliver events to a disabled scope; inspect its retained Flow truth instead."),
                    cancellationToken).ConfigureAwait(false);
            }
            var commandIdentity = await ReadCommandByIdentityAsync(
                connection, transaction, request.ScopeId, request.CommandId.Value, eventId: null, cancellationToken)
                .ConfigureAwait(false);
            var eventIdentity = await ReadCommandByIdentityAsync(
                connection, transaction, request.ScopeId, commandId: null, request.EventId.Value, cancellationToken)
                .ConfigureAwait(false);
            var identityResult = ResolveCommandIdentities(
                request.CommandId.Value, request.Fingerprint, commandIdentity, eventIdentity);
            if (identityResult is not null)
            {
                return await CommitFailureAsync(transaction, identityResult, cancellationToken).ConfigureAwait(false);
            }

            var duplicate = commandIdentity ?? eventIdentity;
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(
                    duplicate.ToResult(DurableFlowCommandOutcome.Duplicate));
            }

            // An exact duplicate is durable truth even if the original payload codec has since been retired.
            validatePayload?.Invoke(request.Payload);

            var current = await LockCurrentAsync(
                connection, transaction, request.ScopeId, request.InstanceId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return await CommitFailureAsync(
                    transaction,
                    Failure<DurableFlowCommandResult>(
                        request.CommandId.Value,
                        DurableProblemCodes.FlowNotFound,
                        "The authorized scope does not contain the requested Flow instance.",
                        "No current Flow row matched the scope and instance identity.",
                        "Reload the authorized Flow inventory before retrying."),
                    cancellationToken).ConfigureAwait(false);
            }

            var wait = await ReadMatchingWaitAsync(
                connection, transaction, request.ScopeId, request.InstanceId, request.EventName, cancellationToken)
                .ConfigureAwait(false);
            if (wait is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(
                    new DurableFlowCommandResult(
                        request.InstanceId,
                        DurableFlowCommandOutcome.NotWaitingYet,
                        current.PublicState,
                        current.Revision));
            }

            if (!EventContractMatches(wait, request.Payload))
            {
                return await CommitFailureAsync(
                    transaction,
                    Failure<DurableFlowCommandResult>(
                        request.CommandId.Value,
                        DurableProblemCodes.FlowEventContractMismatch,
                        "The external event payload does not match the active Flow wait.",
                        "The event name, required payload contract, classification, or retention identity differs.",
                        "Encode the exact registered event contract and retry with the same unconsumed event identity."),
                    cancellationToken).ConfigureAwait(false);
            }

            var canWin = wait.State == "active"
                && current.State is "waiting_event" or "waiting_timer"
                && (request.ExpectedRevision is null || request.ExpectedRevision.Value == current.Revision);
            if (!canWin)
            {
                await InsertCommandAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    request.CommandId.Value,
                    "event",
                    request.Fingerprint,
                    "race_lost",
                    current.State,
                    current.Revision,
                    eventId: request.EventId.Value,
                    actorId: null,
                    reasonCode: null,
                    cancellationToken).ConfigureAwait(false);
                await AppendHistoryAsync(
                    connection, transaction, current, "event_race_lost", request.CommandId.Value, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(
                    new DurableFlowCommandResult(
                        request.InstanceId,
                        DurableFlowCommandOutcome.RaceLost,
                        current.PublicState,
                        current.Revision));
            }

            var revision = checked(current.Revision + 1);
            var dispatchId = Guid.NewGuid();
            const string winSql = """
                WITH won_wait AS
                (
                UPDATE appsurface_durable.flow_wait
                SET state = 'event_won', resolved_revision = @revision, resolved_at = clock_timestamp()
                WHERE wait_id = @wait_id AND state = 'active'
                RETURNING wait_id
                ),
                superseded_timer AS
                (
                UPDATE appsurface_durable.flow_timer
                SET state = 'superseded', resolved_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                  AND wait_id = @wait_id AND state = 'scheduled'
                  AND EXISTS (SELECT 1 FROM won_wait)
                RETURNING timer_id
                ),
                terminal_timer_dispatch AS
                (
                UPDATE appsurface_durable.flow_dispatch
                SET state = 'terminal', updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'timer'
                  AND timer_id IN (SELECT timer_id FROM superseded_timer)
                  AND state IN ('available', 'leased')
                RETURNING dispatch_id
                ),
                projected_flow AS
                (
                UPDATE appsurface_durable.flow_instance
                SET state = 'ready', revision = @revision, updated_at = clock_timestamp(),
                    runtime_epoch = @runtime_epoch,
                    resume_event_name = @event_name, resume_event_is_timeout = false,
                    resume_event_contract_id = @event_contract_id,
                    resume_event_schema_version = @event_schema_version,
                    resume_event_codec_id = @event_codec_id,
                    resume_event_payload = @event_payload,
                    resume_event_sha256 = @event_sha256,
                    resume_event_classification = @event_classification,
                    resume_event_retention = @event_retention
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                  AND revision = @prior_revision
                  AND EXISTS (SELECT 1 FROM won_wait)
                RETURNING revision
                ),
                projected_flow_dispatch AS
                (
                INSERT INTO appsurface_durable.flow_dispatch
                    (dispatch_id, scope_id, kind, flow_instance_id, due_at, state, expected_revision, priority)
                SELECT
                    @dispatch_id, @scope_id, 'flow', @flow_instance_id,
                    clock_timestamp(), 'available', @revision, 0
                FROM projected_flow
                ON CONFLICT (scope_id, flow_instance_id) WHERE kind = 'flow'
                DO UPDATE SET due_at = EXCLUDED.due_at, state = 'available',
                              expected_revision = EXCLUDED.expected_revision, updated_at = clock_timestamp()
                RETURNING dispatch_id
                )
                SELECT
                    (SELECT count(*) FROM won_wait),
                    (SELECT count(*) FROM superseded_timer),
                    (SELECT count(*) FROM terminal_timer_dispatch),
                    (SELECT count(*) FROM projected_flow),
                    (SELECT count(*) FROM projected_flow_dispatch);
                """;
            await using (var win = new NpgsqlCommand(winSql, connection, transaction))
            {
                win.Parameters.AddWithValue("wait_id", wait.WaitId);
                win.Parameters.AddWithValue("revision", revision);
                win.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
                win.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
                win.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
                win.Parameters.AddWithValue("event_name", request.EventName);
                AddNullablePayloadParameters(win, "event", request.Payload);
                win.Parameters.AddWithValue("prior_revision", current.Revision);
                win.Parameters.AddWithValue("dispatch_id", dispatchId);
                await using var result = await win.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await result.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || result.GetInt64(0) != 1
                    || result.GetInt64(1) != result.GetInt64(2)
                    || result.GetInt64(3) != 1
                    || result.GetInt64(4) != 1)
                {
                    throw new InvalidOperationException(
                        "Flow event did not resolve its wait, timer lineage, parent, and dispatch exactly once.");
                }
            }

            await InsertCommandAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                request.CommandId.Value,
                "event",
                request.Fingerprint,
                "accepted",
                "ready",
                revision,
                request.EventId.Value,
                actorId: null,
                reasonCode: null,
                cancellationToken).ConfigureAwait(false);
            await AppendHistoryAsync(
                connection, transaction, current with { Revision = revision, State = "ready" }, "event_accepted",
                request.CommandId.Value, cancellationToken).ConfigureAwait(false);
            var trace = await InsertTraceContextAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                traceContext,
                "event_winner",
                cancellationToken).ConfigureAwait(false);
            await AttachTraceContextAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                trace,
                request.CommandId.Value,
                revision,
                wait.WaitId,
                timerId: null,
                workId: null,
                cancellationToken).ConfigureAwait(false);
            await NotifyAsync(connection, transaction, dispatchId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowCommandResult>.Success(
                new DurableFlowCommandResult(
                    request.InstanceId,
                    DurableFlowCommandOutcome.Accepted,
                    DurableFlowState.Ready,
                    revision));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            if (retryAfterUniqueViolation)
            {
                return await RaiseEventAsync(request, validatePayload, traceContext, cancellationToken, retryAfterUniqueViolation: false)
                    .ConfigureAwait(false);
            }

            return Failure<DurableFlowCommandResult>(
                request.CommandId.Value,
                DurableProblemCodes.FlowCommandConflict,
                "The Flow event could not claim its command or event identity after a concurrent insert.",
                "A conflicting durable identity remained after the bounded retry.",
                "Reload the persisted Flow and retry only with its original event identities.");
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableFlowCommandResult>> CancelAsync(
        DurableFlowCancelRequest request,
        DurableTraceContext? traceContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ApplyLifecycleCommandAsync(
            request.ScopeId,
            request.CommandId,
            request.InstanceId,
            request.ActorId,
            request.ReasonCode,
            request.ExpectedRevision,
            request.Fingerprint,
            isRelease: false,
            registry: null,
            traceContext,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<DurableOperationResult<DurableFlowCommandResult>> ReleaseSuspensionAsync(
        DurableFlowReleaseRequest request,
        IDurableFlowRegistry registry,
        DurableTraceContext? traceContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registry);
        return await ApplyLifecycleCommandAsync(
            request.ScopeId,
            request.CommandId,
            request.InstanceId,
            request.ActorId,
            request.ReasonCode,
            request.ExpectedRevision,
            request.Fingerprint,
            isRelease: true,
            registry,
            traceContext,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DurableOperationResult<DurableFlowCommandResult>> ApplyLifecycleCommandAsync(
        DurableScopeId scopeId,
        DurableCommandId commandId,
        DurableFlowInstanceId instanceId,
        string actorId,
        string reasonCode,
        long expectedRevision,
        DurableCommandFingerprint fingerprint,
        bool isRelease,
        IDurableFlowRegistry? registry,
        DurableTraceContext? traceContext,
        CancellationToken cancellationToken,
        bool retryAfterUniqueViolation = true)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndLockActiveScopeAsync(
                connection, transaction, scopeId, createIfMissing: false, cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                return await CommitFailureAsync(
                    transaction,
                    Failure<DurableFlowCommandResult>(
                        commandId.Value,
                        DurableProblemCodes.ScopeDisabled,
                        "The Flow command was rejected because its owning scope is disabled.",
                        "The scope lifecycle changed before the command could lock the aggregate.",
                        "Inspect retained state; a disabled scope is not reactivated by Flow commands."),
                    cancellationToken).ConfigureAwait(false);
            }
            var existing = await ReadCommandByIdentityAsync(
                connection, transaction, scopeId, commandId.Value, eventId: null, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (fingerprint.Compare(existing.Fingerprint) != DurableCommandFingerprintMatch.Exact)
                {
                    return await CommitFailureAsync(
                        transaction,
                        Failure<DurableFlowCommandResult>(
                            commandId.Value,
                            DurableProblemCodes.FlowCommandConflict,
                            "The Flow command identity was reused with different semantics.",
                            "The persisted fingerprint differs from this retry.",
                            "Retry only the exact original command or use a new command identity."),
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(
                    existing.ToResult(DurableFlowCommandOutcome.Duplicate));
            }

            var linkedChild = await LockLinkedActivityChildAsync(
                connection,
                transaction,
                scopeId,
                instanceId,
                cancellationToken).ConfigureAwait(false);
            var current = await LockCurrentAsync(connection, transaction, scopeId, instanceId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                return await CommitFailureAsync(
                    transaction,
                    Failure<DurableFlowCommandResult>(
                        commandId.Value,
                        DurableProblemCodes.FlowNotFound,
                        "The authorized scope does not contain the requested Flow instance.",
                        "No current Flow row matched the scope and instance identity.",
                        "Reload the authorized Flow inventory before retrying."),
                    cancellationToken).ConfigureAwait(false);
            }

            if (current.IsTerminal)
            {
                await InsertCommandAsync(
                    connection, transaction, scopeId, instanceId, commandId.Value, isRelease ? "release" : "cancel",
                    fingerprint, "already_terminal", current.State, current.Revision, eventId: null, actorId, reasonCode,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(
                    new DurableFlowCommandResult(
                        instanceId,
                        DurableFlowCommandOutcome.AlreadyTerminal,
                        current.PublicState,
                        current.Revision));
            }

            if (current.Revision != expectedRevision)
            {
                await InsertCommandAsync(
                    connection, transaction, scopeId, instanceId, commandId.Value, isRelease ? "release" : "cancel",
                    fingerprint, "race_lost", current.State, current.Revision, eventId: null, actorId, reasonCode,
                    cancellationToken).ConfigureAwait(false);
                await AppendHistoryAsync(
                    connection, transaction, current, isRelease ? "release_race_lost" : "cancel_race_lost",
                    commandId.Value, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(
                    new DurableFlowCommandResult(
                        instanceId,
                        DurableFlowCommandOutcome.RaceLost,
                        current.PublicState,
                        current.Revision));
            }

            string nextState;
            string? terminalCode;
            if (isRelease)
            {
                var registration = registry!.GetRequired(current.FlowId, current.FlowVersion);
                if (!string.Equals(registration.ImplementationVersion, current.ManifestId, StringComparison.Ordinal)
                    || !string.Equals(registration.DefinitionFingerprint, current.DefinitionFingerprint, StringComparison.Ordinal)
                    || !string.Equals(registration.AuthoringModel, current.AuthoringModel, StringComparison.Ordinal))
                {
                    return await CommitFailureAsync(
                        transaction,
                        Failure<DurableFlowCommandResult>(
                            commandId.Value,
                            DurableProblemCodes.FlowReleaseManifestMismatch,
                            "The suspended Flow manifest is incompatible with the active registration.",
                            "The persisted definition fingerprint or authoring model differs.",
                            "Deploy the exact compatible Flow registration before releasing this instance."),
                        cancellationToken).ConfigureAwait(false);
                }

                if (current.State != "suspended" && current.RuntimeEpoch == _runtimeEpoch)
                {
                    return await CommitFailureAsync(
                        transaction,
                        Failure<DurableFlowCommandResult>(
                            commandId.Value,
                            DurableProblemCodes.FlowReleaseStateMismatch,
                            "The Flow is not in a recoverable suspended or old-epoch state.",
                            "Its current state shape cannot be safely adopted by a release command.",
                            "Reload current truth and use release only for a documented recoverable state."),
                        cancellationToken).ConfigureAwait(false);
                }

                if (current.State == "suspended"
                    && current.SuspendedFromState == "waiting_activity"
                    && linkedChild?.State is not ("pending" or "retry_wait" or "leased" or "reconciling"
                        or "effect_permitted" or "cancel_pending"))
                {
                    return await CommitFailureAsync(
                        transaction,
                        Failure<DurableFlowCommandResult>(
                            commandId.Value,
                            DurableProblemCodes.FlowReleaseStateMismatch,
                            "The suspended Flow activity does not have a safely restorable child Work state.",
                            "The linked child is missing, terminal, or suspended with unresolved provider-effect truth.",
                            "Resolve the child through the Work operator protocol before releasing the parent Flow."),
                        cancellationToken).ConfigureAwait(false);
                }

                nextState = current.State == "suspended"
                    ? current.SuspendedFromState ?? "ready"
                    : current.State;
                terminalCode = null;
            }
            else
            {
                var childState = current.State == "waiting_activity" && linkedChild is not null
                    ? await RequestLinkedChildCancellationAsync(
                        connection,
                        transaction,
                        scopeId,
                        linkedChild,
                        actorId,
                        reasonCode,
                        cancellationToken).ConfigureAwait(false)
                    : null;
                nextState = current.State == "waiting_activity" && childState != "canceled_before_effect"
                    ? "cancel_pending"
                    : "canceled";
                terminalCode = nextState == "canceled" ? "canceled" : null;
            }

            var revision = checked(current.Revision + 1);
            var terminal = nextState == "canceled";
            const string updateSql = """
                WITH dispatch_evidence AS MATERIALIZED
                (
                    SELECT dispatch_id
                    FROM appsurface_durable.flow_dispatch
                    WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                ),
                projected_flow AS
                (
                UPDATE appsurface_durable.flow_instance
                SET state = @state, revision = @revision, runtime_epoch = @runtime_epoch,
                    cancellation_requested_at =
                        CASE WHEN @is_release THEN cancellation_requested_at ELSE COALESCE(cancellation_requested_at, clock_timestamp()) END,
                    terminal_at = CASE WHEN @terminal THEN clock_timestamp() ELSE NULL END,
                    terminal_code = @terminal_code,
                    suspended_from_state = NULL, suspension_descriptor = NULL,
                    lease_owner = NULL, lease_started_at = NULL, lease_expires_at = NULL,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND revision = @prior_revision
                RETURNING revision
                ),
                projected_dispatch AS
                (
                UPDATE appsurface_durable.flow_dispatch
                SET state = CASE WHEN @state = 'ready' THEN 'available'
                                 WHEN @state = 'suspended' OR @state = 'cancel_pending' THEN 'suspended'
                                 ELSE 'terminal' END,
                    expected_revision = @revision, updated_at = clock_timestamp()
                WHERE dispatch_id IN (SELECT dispatch_id FROM dispatch_evidence)
                  AND EXISTS (SELECT 1 FROM projected_flow)
                RETURNING dispatch_id
                ),
                projected_wait AS
                (
                UPDATE appsurface_durable.flow_wait
                SET state = CASE WHEN @terminal THEN 'canceled'
                                 WHEN @is_release AND state = 'suspended' THEN 'active'
                                 ELSE state END,
                    resolved_revision = CASE WHEN @terminal THEN @revision ELSE resolved_revision END,
                    resolved_at = CASE WHEN @terminal THEN clock_timestamp() ELSE resolved_at END
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                  AND state IN ('active', 'suspended')
                  AND EXISTS (SELECT 1 FROM projected_flow)
                RETURNING wait_id
                )
                SELECT
                    (SELECT count(*) FROM projected_flow),
                    (SELECT count(*) FROM dispatch_evidence),
                    (SELECT count(*) FROM projected_dispatch),
                    (SELECT count(*) FROM projected_wait);
                """;
            await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
            {
                update.Parameters.AddWithValue("state", nextState);
                update.Parameters.AddWithValue("revision", revision);
                update.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
                update.Parameters.AddWithValue("is_release", isRelease);
                update.Parameters.AddWithValue("terminal", terminal);
                update.Parameters.Add(new NpgsqlParameter("terminal_code", NpgsqlDbType.Text)
                {
                    Value = terminalCode ?? (object)DBNull.Value,
                });
                update.Parameters.AddWithValue("scope_id", scopeId.Value);
                update.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
                update.Parameters.AddWithValue("prior_revision", current.Revision);
                await using var result = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var requiresWaitProjection = current.State is
                    "waiting_event" or "waiting_timer" or "waiting_activity" or "cancel_pending"
                    || (current.State == "suspended" && linkedChild is not null);
                if (!await result.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || result.GetInt64(0) != 1
                    || result.GetInt64(1) == 0
                    || result.GetInt64(1) != result.GetInt64(2)
                    || result.GetInt64(3) > 1
                    || (requiresWaitProjection && result.GetInt64(3) != 1))
                {
                    throw new InvalidOperationException(
                        "Flow lifecycle command did not project its parent, dispatch, and wait lineage exactly once.");
                }
            }

            await InsertCommandAsync(
                connection, transaction, scopeId, instanceId, commandId.Value, isRelease ? "release" : "cancel",
                fingerprint, "accepted", nextState, revision, eventId: null, actorId, reasonCode, cancellationToken)
                .ConfigureAwait(false);
            await AppendHistoryAsync(
                connection, transaction, current with { Revision = revision, State = nextState },
                isRelease ? "suspension_released" : "cancellation_requested", commandId.Value, cancellationToken)
                .ConfigureAwait(false);
            var trace = await InsertTraceContextAsync(
                connection,
                transaction,
                scopeId,
                instanceId,
                traceContext,
                "command_accepted",
                cancellationToken).ConfigureAwait(false);
            await AttachTraceContextAsync(
                connection,
                transaction,
                scopeId,
                instanceId,
                trace,
                commandId.Value,
                revision,
                waitId: null,
                timerId: null,
                workId: null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowCommandResult>.Success(
                new DurableFlowCommandResult(
                    instanceId,
                    DurableFlowCommandOutcome.Accepted,
                    ParseState(nextState),
                    revision));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            if (retryAfterUniqueViolation)
            {
                return await ApplyLifecycleCommandAsync(
                    scopeId,
                    commandId,
                    instanceId,
                    actorId,
                    reasonCode,
                    expectedRevision,
                    fingerprint,
                    isRelease,
                    registry,
                    traceContext,
                    cancellationToken,
                    retryAfterUniqueViolation: false).ConfigureAwait(false);
            }

            return Failure<DurableFlowCommandResult>(
                commandId.Value,
                DurableProblemCodes.FlowCommandConflict,
                "The Flow lifecycle command could not claim its command identity after a concurrent insert.",
                "A conflicting durable identity remained after the bounded retry.",
                "Reload the persisted Flow and retry only with its original command identity.");
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<LinkedActivityChild?> LockLinkedActivityChildAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT work.work_id, work.state, work.revision, work.provider_safety,
                   work.attempt_number, work.lease_generation, work.scope_generation, work.runtime_epoch,
                   EXISTS
                   (
                       SELECT 1
                       FROM appsurface_durable.effect_permit AS permit
                       WHERE permit.scope_id = work.scope_id
                         AND permit.work_id = work.work_id
                         AND permit.status IN ('granted', 'ambiguous')
                   ) AS has_ambiguous_permit
            FROM appsurface_durable.flow_wait AS wait
            JOIN appsurface_durable.work AS work
              ON work.scope_id = wait.scope_id
             AND work.work_id = wait.child_work_id
            WHERE wait.scope_id = @scope_id
              AND wait.flow_instance_id = @flow_instance_id
              AND wait.kind = 'activity'
              AND wait.state IN ('active', 'suspended')
            -- Lock ordering: keep OF work. A bare FOR UPDATE also locks flow_wait, which would invert
            -- the flow_instance -> flow_wait order used by event delivery and permit a deadlock.
            FOR UPDATE OF work;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new LinkedActivityChild(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetGuid(7),
                reader.GetBoolean(8))
            : null;
    }

    private static async ValueTask<string> RequestLinkedChildCancellationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        LinkedActivityChild child,
        string actorId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var state = child.State switch
        {
            "reconciling" => "reconciling",
            "effect_permitted" or "cancel_pending" when child.HasAmbiguousPermit => "cancel_pending",
            _ when child.HasAmbiguousPermit && child.ProviderSafety == "reconcile_before_retry" =>
                "suspended_reconciliation_required",
            _ when child.HasAmbiguousPermit && child.ProviderSafety == "manual_resolution" =>
                "suspended_manual_resolution",
            _ when child.HasAmbiguousPermit => "suspended_ambiguous_external_outcome",
            _ => "canceled_before_effect",
        };
        var revision = checked(child.Revision + 1);
        const string sql = """
            UPDATE appsurface_durable.work
            SET state = @state,
                cancellation_requested_at = COALESCE(cancellation_requested_at, clock_timestamp()),
                terminal_at = CASE WHEN @state = 'canceled_before_effect' THEN clock_timestamp() ELSE NULL END,
                terminal_code = CASE WHEN @state = 'canceled_before_effect' THEN 'flow_canceled' ELSE terminal_code END,
                lease_owner = CASE WHEN @state = 'cancel_pending' THEN lease_owner ELSE NULL END,
                lease_started_at = CASE WHEN @state = 'cancel_pending' THEN lease_started_at ELSE NULL END,
                lease_expires_at = CASE WHEN @state = 'cancel_pending' THEN lease_expires_at ELSE NULL END,
                revision = @revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND work_id = @work_id AND revision = @prior_revision;

            UPDATE appsurface_durable.dispatch
            SET state = CASE
                    WHEN @state = 'canceled_before_effect' THEN 'terminal'
                    WHEN @state = 'cancel_pending' THEN 'leased'
                    ELSE 'suspended'
                END,
                expected_revision = @revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND aggregate_kind = 'work'
              AND aggregate_id = @work_id;

            INSERT INTO appsurface_durable.work_history
                (scope_id, work_id, aggregate_revision, event_type, actor_id, reason_code,
                 attempt_number, lease_generation, scope_generation, runtime_epoch, details)
            VALUES
                (@scope_id, @work_id, @revision, 'cancellation_requested', @actor_id, @reason_code,
                 @attempt_number, @lease_generation, @scope_generation, @runtime_epoch,
                 jsonb_build_object('source', 'parent_flow', 'resulting_state', @state));
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", child.WorkId);
        command.Parameters.AddWithValue("prior_revision", child.Revision);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("reason_code", reasonCode);
        command.Parameters.AddWithValue("attempt_number", child.AttemptNumber);
        command.Parameters.AddWithValue("lease_generation", child.LeaseGeneration);
        command.Parameters.AddWithValue("scope_generation", child.ScopeGeneration);
        command.Parameters.AddWithValue("runtime_epoch", child.RuntimeEpoch);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 3)
        {
            throw new InvalidOperationException(
                "Parent Flow cancellation did not project its linked child Work, dispatch, and history atomically.");
        }

        return state;
    }

    private sealed record LinkedActivityChild(
        string WorkId,
        string State,
        long Revision,
        string ProviderSafety,
        int AttemptNumber,
        long LeaseGeneration,
        long ScopeGeneration,
        Guid RuntimeEpoch,
        bool HasAmbiguousPermit);

    private async ValueTask ValidateStoreAndSetScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT store_id, active_runtime_epoch, schema_version
            FROM appsurface_durable.store_metadata
            WHERE singleton;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Durable store metadata is missing.");
        }

        var storeId = reader.GetGuid(0);
        var epoch = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        var version = reader.GetInt32(2);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (version < 3)
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.SchemaUpgradeRequired}: PostgreSQL durable Flow requires schema version 3.");
        }

        if (storeId != _expectedStoreId)
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.StoreIdentityMismatch}: The configured durable store identity does not match PostgreSQL.");
        }

        if (epoch != _runtimeEpoch)
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.RecoveryEpochRequired}: The configured runtime epoch is not active.");
        }

        await using var scope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction);
        scope.Parameters.AddWithValue("scope_id", scopeId.Value);
        await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long?> ValidateStoreSetScopeAndLockActiveScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        await ValidateStoreAndSetScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        if (createIfMissing)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO appsurface_durable.scope (scope_id)
                VALUES (@scope_id)
                ON CONFLICT (scope_id) DO NOTHING;
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("scope_id", scopeId.Value);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT generation, state
            FROM appsurface_durable.scope
            WHERE scope_id = @scope_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var generation = reader.GetInt64(0);
        var active = string.Equals(reader.GetString(1), "active", StringComparison.Ordinal);
        return active ? generation : null;
    }

    private static async ValueTask<CurrentFlowRow?> LockCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT flow_instance_id, flow_id, flow_version, manifest_id, authoring_model, definition_fingerprint_sha256,
                   current_node_id, state, suspended_from_state, revision, runtime_epoch,
                   scope_generation, lease_generation
            FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CurrentFlowRow(
            scopeId,
            new DurableFlowInstanceId(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt64(9),
            reader.GetGuid(10),
            reader.GetInt64(11),
            reader.GetInt64(12));
    }

    private static async ValueTask<FlowCommandRow?> ReadCommandByIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        string? commandId,
        string? eventId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            {FlowCommandProjection}
            WHERE scope_id = @scope_id
              AND ((@command_id IS NOT NULL AND command_id = @command_id)
                   OR (@event_id IS NOT NULL AND event_id = @event_id))
            LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.Add(new NpgsqlParameter("command_id", NpgsqlDbType.Text)
        {
            Value = commandId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text)
        {
            Value = eventId ?? (object)DBNull.Value,
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadFlowCommandRow(reader)
            : null;
    }

    private static async ValueTask<FlowCommandRow?> ReadStartByIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            {FlowCommandProjection}
            WHERE scope_id = @scope_id AND start_idempotency_key = @idempotency_key
            LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadFlowCommandRow(reader)
            : null;
    }

    private static FlowCommandRow ReadFlowCommandRow(NpgsqlDataReader reader) =>
        new(
            new DurableFlowInstanceId(reader.GetString(0)),
            reader.GetString(1),
            new DurableCommandFingerprint(reader.GetString(2), reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));

    private static DurableOperationResult<DurableFlowCommandResult>? ResolveStartIdentity(
        DurableFlowStartRequest request,
        FlowCommandRow? command,
        FlowCommandRow? idempotency)
    {
        if (command is not null
            && (request.Fingerprint.Compare(command.Fingerprint) != DurableCommandFingerprintMatch.Exact
                || command.InstanceId != request.InstanceId))
        {
            return Failure<DurableFlowCommandResult>(
                request.CommandId.Value,
                DurableProblemCodes.FlowCommandConflict,
                "The Flow command identity was reused with different semantics.",
                "The command fingerprint or target instance differs from the persisted command.",
                "Retry only the exact original command or choose a new command identity.");
        }

        if (idempotency is not null
            && (request.Fingerprint.Compare(idempotency.Fingerprint) != DurableCommandFingerprintMatch.Exact
                || idempotency.InstanceId != request.InstanceId))
        {
            return Failure<DurableFlowCommandResult>(
                request.CommandId.Value,
                DurableProblemCodes.FlowStartConflict,
                "The Flow start idempotency key was reused with different semantics.",
                "The start fingerprint or target instance differs from the persisted start.",
                "Retry the original start exactly or choose a new start idempotency key.");
        }

        if (command is not null && idempotency is not null && command.InstanceId != idempotency.InstanceId)
        {
            return Failure<DurableFlowCommandResult>(
                request.CommandId.Value,
                DurableProblemCodes.FlowStartConflict,
                "The Flow start identities resolve to different instances.",
                "The command identity and idempotency key were previously consumed by separate starts.",
                "Do not choose a winner; inspect both original outcomes and submit a new coherent start.");
        }

        return null;
    }

    private static DurableOperationResult<DurableFlowCommandResult>? ResolveCommandIdentities(
        string correlationId,
        DurableCommandFingerprint fingerprint,
        FlowCommandRow? command,
        FlowCommandRow? secondary)
    {
        foreach (var row in new[] { command, secondary })
        {
            if (row is not null && fingerprint.Compare(row.Fingerprint) != DurableCommandFingerprintMatch.Exact)
            {
                return Failure<DurableFlowCommandResult>(
                    correlationId,
                    DurableProblemCodes.FlowCommandConflict,
                    "The Flow command or event identity was reused with different semantics.",
                    "The persisted fingerprint differs from this retry.",
                    "Retry only the exact original request or choose new command and event identities.");
            }
        }

        if (command is not null && secondary is not null && command.CommandId != secondary.CommandId)
        {
            return Failure<DurableFlowCommandResult>(
                correlationId,
                DurableProblemCodes.FlowCommandConflict,
                "The Flow command and event identities resolve to different commands.",
                "Two independently consumed identities cannot be merged safely.",
                "Inspect the original outcomes and submit no changed retry under either identity.");
        }

        return null;
    }

    private static async ValueTask<FlowWaitRow?> ReadMatchingWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        string eventName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT wait_id, state, event_payload_required, event_contract_id, event_schema_version,
                   event_classification, event_retention, registered_revision
            FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND kind = 'event' AND event_name = @event_name
            ORDER BY created_at DESC
            LIMIT 1
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("event_name", eventName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new FlowWaitRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7))
            : null;
    }

    private static bool EventContractMatches(FlowWaitRow wait, DurableEncodedPayload? payload)
    {
        if (!wait.PayloadRequired)
        {
            return payload is null;
        }

        return payload is not null
            && string.Equals(wait.ContractName, payload.ContractName, StringComparison.Ordinal)
            && string.Equals(wait.ContractVersion, payload.ContractVersion, StringComparison.Ordinal)
            && string.Equals(wait.Classification, FormatClassification(payload.Classification), StringComparison.Ordinal)
            && string.Equals(wait.RetentionPolicyId, payload.RetentionPolicyId, StringComparison.Ordinal);
    }

    private static async ValueTask InsertCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        string commandId,
        string commandType,
        DurableCommandFingerprint fingerprint,
        string outcome,
        string resultingState,
        long resultingRevision,
        string? eventId,
        string? actorId,
        string? reasonCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_command
            (
                scope_id, flow_instance_id, command_id, command_type, event_id, actor_id, reason_code,
                fingerprint_schema, fingerprint_sha256, outcome, resulting_state, resulting_revision
            )
            VALUES
            (
                @scope_id, @flow_instance_id, @command_id, @command_type, @event_id, @actor_id, @reason_code,
                @fingerprint_schema, @fingerprint_sha256, @outcome, @resulting_state, @resulting_revision
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("command_id", commandId);
        command.Parameters.AddWithValue("command_type", commandType);
        command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text) { Value = eventId ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("actor_id", NpgsqlDbType.Text) { Value = actorId ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("reason_code", NpgsqlDbType.Text) { Value = reasonCode ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("fingerprint_schema", fingerprint.SchemaId);
        command.Parameters.AddWithValue("fingerprint_sha256", fingerprint.Sha256);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("resulting_state", resultingState);
        command.Parameters.AddWithValue("resulting_revision", resultingRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Flow command evidence was not inserted exactly once.");
        }
    }

    private static async ValueTask AppendHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CurrentFlowRow current,
        string eventType,
        string? commandId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_history
            (
                scope_id, flow_instance_id, aggregate_revision, command_id,
                node_id, transition_kind
            )
            VALUES
            (
                @scope_id, @flow_instance_id, @aggregate_revision, @command_id,
                @node_id, @transition_kind
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", current.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", current.InstanceId.Value);
        command.Parameters.AddWithValue("aggregate_revision", current.Revision);
        command.Parameters.Add(new NpgsqlParameter("command_id", NpgsqlDbType.Text)
        {
            Value = commandId ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue("node_id", current.CurrentNodeId);
        command.Parameters.AddWithValue("transition_kind", eventType);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Flow history evidence was not inserted exactly once.");
        }
    }

    private async ValueTask NotifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid dispatchId,
        CancellationToken cancellationToken)
    {
        if (!_sendWakeNotification)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            "SELECT pg_notify('appsurface_durable_wake', @dispatch_id);",
            connection,
            transaction);
        command.Parameters.AddWithValue("dispatch_id", dispatchId.ToString("D", CultureInfo.InvariantCulture));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private DurableFlowSnapshot ReadSnapshot(NpgsqlDataReader reader)
    {
        var state = reader.GetString(3);
        var epoch = reader.GetGuid(11);
        var publicState = ParseState(state);
        return new DurableFlowSnapshot(
            new DurableFlowInstanceId(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            publicState,
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            epoch != _runtimeEpoch
                && state is "ready" or "waiting_event" or "waiting_timer" or "waiting_activity" or "cancel_pending" or "suspended");
    }

    private static string FormatState(DurableFlowState state) => state switch
    {
        DurableFlowState.Ready => "ready",
        DurableFlowState.WaitingForEvent => "waiting_event",
        DurableFlowState.WaitingForTimer => "waiting_timer",
        DurableFlowState.WaitingForActivity => "waiting_activity",
        DurableFlowState.CancelPending => "cancel_pending",
        DurableFlowState.Completed => "completed",
        DurableFlowState.Faulted => "faulted",
        DurableFlowState.Canceled => "canceled",
        DurableFlowState.Suspended => "suspended",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static DurableFlowState ParseState(string state) =>
        PersistedStates.TryGetValue(state, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Unknown persisted Flow state '{state}'.");

    private static string FormatClassification(DurableDataClassification classification) => classification switch
    {
        DurableDataClassification.Operational => "operational",
        DurableDataClassification.ApprovedApplication => "approved_application",
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    private static void AddPayloadParameters(NpgsqlCommand command, string prefix, DurableEncodedPayload payload)
    {
        command.Parameters.AddWithValue($"{prefix}_contract_id", payload.ContractName);
        command.Parameters.AddWithValue($"{prefix}_schema_version", payload.ContractVersion);
        command.Parameters.AddWithValue($"{prefix}_codec_id", $"{payload.ContractName}@{payload.ContractVersion}");
        command.Parameters.AddWithValue($"{prefix}_payload", payload.Content.ToArray());
        command.Parameters.AddWithValue($"{prefix}_sha256", Convert.FromHexString(payload.Sha256));
        command.Parameters.AddWithValue($"{prefix}_classification", FormatClassification(payload.Classification));
        command.Parameters.AddWithValue($"{prefix}_retention", payload.RetentionPolicyId);
    }

    private static void AddNullablePayloadParameters(
        NpgsqlCommand command,
        string prefix,
        DurableEncodedPayload? payload)
    {
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_contract_id", NpgsqlDbType.Text)
        {
            Value = payload?.ContractName ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_schema_version", NpgsqlDbType.Text)
        {
            Value = payload?.ContractVersion ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_codec_id", NpgsqlDbType.Text)
        {
            Value = payload is null ? DBNull.Value : $"{payload.ContractName}@{payload.ContractVersion}",
        });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_payload", NpgsqlDbType.Bytea)
        {
            Value = payload?.Content.ToArray() ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_sha256", NpgsqlDbType.Bytea)
        {
            Value = payload is null ? DBNull.Value : Convert.FromHexString(payload.Sha256),
        });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_classification", NpgsqlDbType.Text)
        {
            Value = payload is null ? DBNull.Value : FormatClassification(payload.Classification),
        });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_retention", NpgsqlDbType.Text)
        {
            Value = payload?.RetentionPolicyId ?? (object)DBNull.Value,
        });
    }

    private static DurableOperationResult<T> Failure<T>(
        string correlationId,
        string code,
        string problem,
        string cause,
        string fix)
        where T : class =>
        DurableOperationResult<T>.Failure(new DurableProblem(
            code,
            problem,
            cause,
            fix,
            new Uri($"{Diagnostics}#{code.ToLowerInvariant()}"),
            correlationId));

    private static async ValueTask<DurableOperationResult<T>> CommitFailureAsync<T>(
        NpgsqlTransaction transaction,
        DurableOperationResult<T> result,
        CancellationToken cancellationToken)
        where T : class
    {
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask TryRollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(exception))
        {
            // Preserve the original operation failure.
        }
    }

    private static string EncodeContinuation(DateTimeOffset updatedAtUtc, string instanceId)
    {
        var json = JsonSerializer.Serialize(new ContinuationToken(1, updatedAtUtc, instanceId));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static ContinuationToken? DecodeContinuation(string? token)
    {
        if (token is null)
        {
            return null;
        }

        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            var decoded = JsonSerializer.Deserialize<ContinuationToken>(
                Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
            if (decoded is null || decoded.Version != 1 || string.IsNullOrWhiteSpace(decoded.InstanceId))
            {
                throw new FormatException();
            }

            return decoded;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Flow continuation token is malformed or uses an unknown version.", nameof(token), exception);
        }
    }

    private sealed record ContinuationToken(int Version, DateTimeOffset UpdatedAtUtc, string InstanceId);

    private sealed record FlowCommandRow(
        DurableFlowInstanceId InstanceId,
        string CommandId,
        DurableCommandFingerprint Fingerprint,
        string Outcome,
        string ResultingState,
        long ResultingRevision)
    {
        internal DurableFlowCommandResult ToResult(DurableFlowCommandOutcome duplicateOutcome) =>
            new(InstanceId, duplicateOutcome, ParseState(ResultingState), ResultingRevision);
    }

    private sealed record FlowWaitRow(
        Guid WaitId,
        string State,
        bool PayloadRequired,
        string? ContractName,
        string? ContractVersion,
        string? Classification,
        string? RetentionPolicyId,
        long RegisteredRevision);

    private sealed record CurrentFlowRow(
        DurableScopeId ScopeId,
        DurableFlowInstanceId InstanceId,
        string FlowId,
        string FlowVersion,
        string ManifestId,
        string AuthoringModel,
        string DefinitionFingerprint,
        string CurrentNodeId,
        string State,
        string? SuspendedFromState,
        long Revision,
        Guid RuntimeEpoch,
        long ScopeGeneration,
        long LeaseGeneration)
    {
        internal DurableFlowState PublicState => ParseState(State);

        internal bool IsTerminal => State is "completed" or "faulted" or "canceled";
    }
}
