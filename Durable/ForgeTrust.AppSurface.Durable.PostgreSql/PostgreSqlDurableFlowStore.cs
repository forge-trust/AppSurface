using System.Data;
using System.Globalization;
using System.Text.Json;
using ForgeTrust.AppSurface.Flow;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// PostgreSQL persistence engine for durable Flow aggregates.
/// </summary>
/// <remarks>
/// Public callers use <see cref="PostgreSqlDurableFlowClient"/>. Runtime pumps use the internal discovery and
/// evaluation operations so every host evaluates exactly one node through <see cref="IDurableFlowRegistry"/> and
/// commits its transition before another node becomes eligible.
/// </remarks>
internal sealed class PostgreSqlDurableFlowStore
{
    private const string FlowCommandSchemaVersion = "flow-command-v1";
    private const string RuntimeEpochMismatchCode = "flow.runtime_epoch_mismatch";
    private const string RecoveryReleasePredicateSql =
        "flow.runtime_epoch <> @runtime_epoch AND flow.state IN " +
        "('ready', 'waiting_event', 'waiting_timer', 'waiting_activity', 'cancel_pending', 'suspended')";
    private static readonly Uri FlowDocumentation = new("https://appsurface.dev/docs/durable/flow");
    private static readonly Uri ScopeDocumentation = new("https://appsurface.dev/docs/durable/scopes");
    private static readonly TimeSpan EvaluationLeaseDuration = TimeSpan.FromMinutes(2);
    private readonly NpgsqlDataSource _dataSource;
    private readonly Guid _runtimeEpoch;
    private readonly bool _sendWakeNotification;
    private readonly Func<NpgsqlTransaction, DurableScopeId, DurableFlowInstanceId, string, CancellationToken, ValueTask>?
        _onTerminalApplied;

    internal PostgreSqlDurableFlowStore(
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        bool sendWakeNotification = true,
        Func<NpgsqlTransaction, DurableScopeId, DurableFlowInstanceId, string, CancellationToken, ValueTask>?
            onTerminalApplied = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        _runtimeEpoch = runtimeEpoch;
        _sendWakeNotification = sendWakeNotification;
        _onTerminalApplied = onTerminalApplied;
    }

    internal static async ValueTask<DurableOperationResult<DurableFlowCommandResult>> StartAsync(
        NpgsqlTransaction transaction,
        DurableFlowStartRequest request,
        DurableFlowRegistration registration,
        Guid runtimeEpoch,
        bool sendWakeNotification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registration);
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        if (!string.Equals(request.FlowId, registration.FlowId, StringComparison.Ordinal) ||
            !string.Equals(request.FlowVersion, registration.FlowVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Flow start request does not match the selected durable registration.", nameof(request));
        }

        if (!string.Equals(request.Context.ContractName, registration.ContextCodec.ContractName, StringComparison.Ordinal) ||
            !string.Equals(request.Context.ContractVersion, registration.ContextCodec.ContractVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Flow start context does not match its registered durable codec.", nameof(request));
        }

        var connection = RequireActiveConnection(transaction);
        await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
            connection,
            transaction,
            runtimeEpoch,
            cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
        var scopeGeneration = await EnsureActiveScopeAsync(
            connection,
            transaction,
            request.ScopeId,
            cancellationToken).ConfigureAwait(false);
        if (scopeGeneration is null)
        {
            return DurableOperationResult<DurableFlowCommandResult>.Failure(new DurableProblem(
                DurableProblemCodes.ScopeDisabled,
                "The durable Flow was not started because its owning scope is disabled.",
                "The scope lifecycle was disabled before the start reached durable acceptance.",
                "Use a currently authorized active scope; do not bypass scope lifecycle policy.",
                ScopeDocumentation,
                request.CommandId.Value));
        }

        var fingerprint = DurableFlowRequestFingerprint.Compute(request, registration);
        var inserted = await TryInsertStartAsync(
            connection,
            transaction,
            request,
            registration,
            runtimeEpoch,
            scopeGeneration.Value,
            fingerprint,
            cancellationToken).ConfigureAwait(false);
        if (inserted is not null)
        {
            if (sendWakeNotification)
            {
                await SendWakeNotificationAsync(connection, transaction, inserted.DispatchId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return DurableOperationResult<DurableFlowCommandResult>.Success(inserted.Result);
        }

        return await ReadDuplicateStartAsync(
            connection,
            transaction,
            request,
            fingerprint,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<DurableOperationResult<DurableFlowSnapshot>> GetAsync(
        DurableFlowGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var sql = $"""
                SELECT flow.flow_instance_id, flow.flow_id, flow.flow_version, flow.state, flow.current_node_id,
                       flow.revision, flow.created_at, flow.updated_at, flow.cancellation_requested_at,
                       flow.terminal_at, flow.terminal_code,
                       ({RecoveryReleasePredicateSql}) AS requires_recovery_release
                FROM appsurface_durable.flow_instance AS flow
                WHERE flow.scope_id = @scope_id
                  AND flow.flow_instance_id = @flow_instance_id;
                """;
            DurableFlowSnapshot? snapshot = null;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
                command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
                command.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    snapshot = ReadFlowSnapshot(reader);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshot is null
                ? DurableOperationResult<DurableFlowSnapshot>.Failure(new DurableProblem(
                    DurableProblemCodes.FlowNotFound,
                    "The durable Flow instance was not found in the authorized scope.",
                    "The instance does not exist or belongs to another scope.",
                    "Verify the authorized scope and opaque instance id before retrying.",
                    FlowDocumentation,
                    request.InstanceId.Value))
                : DurableOperationResult<DurableFlowSnapshot>.Success(snapshot);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Lists a bounded, scope-authorized page of payload-free Flow snapshots for status and recovery inventory.
    /// </summary>
    /// <remarks>
    /// The recovery predicate selects directly releasable dormant states whose persisted runtime epoch differs from
    /// this store. It deliberately excludes evaluating and terminal rows and does not validate manifests or wait shape.
    /// </remarks>
    internal async ValueTask<DurableOperationResult<DurableFlowListResult>> ListAsync(
        DurableFlowListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var sql = $"""
                SELECT flow.flow_instance_id, flow.flow_id, flow.flow_version, flow.state, flow.current_node_id,
                       flow.revision, flow.created_at, flow.updated_at, flow.cancellation_requested_at,
                       flow.terminal_at, flow.terminal_code,
                       ({RecoveryReleasePredicateSql}) AS requires_recovery_release
                FROM appsurface_durable.flow_instance AS flow
                WHERE flow.scope_id = @scope_id
                  AND (@continuation_token IS NULL OR flow.flow_instance_id > @continuation_token)
                  AND
                  (
                      @state_filter IS NULL
                      OR (@state_filter = 'ready' AND flow.state IN ('ready', 'evaluating'))
                      OR flow.state = @state_filter
                  )
                  AND
                  (
                      @requires_recovery_release IS NULL
                      OR ({RecoveryReleasePredicateSql}) = @requires_recovery_release
                  )
                ORDER BY flow.flow_instance_id
                LIMIT @query_size;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
            command.Parameters.Add(new NpgsqlParameter("continuation_token", NpgsqlDbType.Text)
            {
                Value = request.ContinuationToken ?? (object)DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("state_filter", NpgsqlDbType.Text)
            {
                Value = request.State is { } state ? FormatFlowStateFilter(state) : DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("requires_recovery_release", NpgsqlDbType.Boolean)
            {
                Value = request.RequiresRecoveryRelease ?? (object)DBNull.Value,
            });
            command.Parameters.AddWithValue("query_size", request.PageSize + 1);
            var flows = new List<DurableFlowSnapshot>(request.PageSize + 1);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    flows.Add(ReadFlowSnapshot(reader));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            string? continuationToken = null;
            if (flows.Count > request.PageSize)
            {
                flows.RemoveAt(flows.Count - 1);
                continuationToken = flows[^1].InstanceId.Value;
            }

            return DurableOperationResult<DurableFlowListResult>.Success(
                new DurableFlowListResult(flows, continuationToken));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableFlowCommandResult>> RaiseEventAsync(
        DurableFlowEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ScopeDisabled(request.CommandId.Value);
            }

            var duplicate = await ReadDuplicateCommandAsync(
                connection,
                transaction,
                request.ScopeId,
                request.CommandId,
                request.EventId,
                DurableFlowRequestFingerprint.Compute(request),
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            var current = await LockCurrentAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return MissingFlow(request.CommandId.Value);
            }

            var epochFenced = await SuspendForRuntimeEpochMismatchAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                current,
                cancellationToken).ConfigureAwait(false);
            if (epochFenced is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(new DurableFlowCommandResult(
                    request.InstanceId,
                    DurableFlowCommandOutcome.RaceLost,
                    DurableFlowState.Suspended,
                    epochFenced.Revision));
            }

            if (IsTerminal(current.State))
            {
                var result = await RecordNonMutatingCommandAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    request.CommandId,
                    "external_event",
                    request.EventId,
                    DurableFlowRequestFingerprint.Compute(request),
                    "already_terminal",
                    current,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(result);
            }

            if (request.ExpectedRevision is { } expectedRevision && expectedRevision != current.Revision)
            {
                var result = await RecordNonMutatingCommandAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    request.CommandId,
                    "external_event",
                    request.EventId,
                    DurableFlowRequestFingerprint.Compute(request),
                    "race_lost",
                    current,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(result);
            }

            var wait = await ReadActiveEventWaitAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                request.EventName,
                cancellationToken).ConfigureAwait(false);
            if (wait is null)
            {
                // Active-wait-only delivery deliberately records nothing. The event id and command id remain reusable.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(new DurableFlowCommandResult(
                    request.InstanceId,
                    DurableFlowCommandOutcome.NotWaitingYet,
                    ParseFlowState(current.State),
                    current.Revision));
            }

            if (!MatchesEventContract(wait, request.Payload))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Failure(new DurableProblem(
                    DurableProblemCodes.FlowEventContractMismatch,
                    "The external event payload does not match the active Flow wait contract.",
                    "The active wait requires an exact payload contract or explicitly requires no payload.",
                    "Send the exact typed event payload declared by the Flow wait; reuse the same event id after correcting the request.",
                    FlowDocumentation,
                    request.CommandId.Value));
            }

            var nextRevision = current.Revision + 1;
            await ResolveEventWaitAsync(
                connection,
                transaction,
                request,
                wait,
                nextRevision,
                cancellationToken).ConfigureAwait(false);
            await SetFlowReadyFromEventAsync(
                connection,
                transaction,
                request,
                current.Revision,
                nextRevision,
                cancellationToken).ConfigureAwait(false);
            await InsertCommandAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                request.CommandId,
                "external_event",
                request.EventId,
                DurableFlowRequestFingerprint.Compute(request),
                "accepted",
                "ready",
                nextRevision,
                cancellationToken).ConfigureAwait(false);
            await InsertResumeHistoryAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                nextRevision,
                "external_event_delivered",
                current.NodeId,
                "external_event",
                request.EventName,
                request.Payload,
                request.CommandId.Value,
                request.EventId.Value,
                "accepted",
                cancellationToken).ConfigureAwait(false);
            var dispatchId = await UpsertFlowDispatchAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                nextRevision,
                cancellationToken).ConfigureAwait(false);
            if (_sendWakeNotification)
            {
                await SendWakeNotificationAsync(connection, transaction, dispatchId, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowCommandResult>.Success(new DurableFlowCommandResult(
                request.InstanceId,
                DurableFlowCommandOutcome.Accepted,
                DurableFlowState.Ready,
                nextRevision));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableFlowCommandResult>> CancelAsync(
        DurableFlowCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ScopeDisabled(request.CommandId.Value);
            }

            var fingerprint = DurableFlowRequestFingerprint.Compute(request);
            var duplicate = await ReadDuplicateCommandAsync(
                connection,
                transaction,
                request.ScopeId,
                request.CommandId,
                eventId: null,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            // Work completion owns the work row before its transaction callback locks the Flow. Acquire the same
            // work-then-Flow order here so simultaneous completion and cancellation cannot deadlock each other.
            await LockActiveActivityWorkAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                cancellationToken).ConfigureAwait(false);
            var current = await LockCurrentAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return MissingFlow(request.CommandId.Value);
            }

            var epochFenced = await SuspendForRuntimeEpochMismatchAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                current,
                cancellationToken).ConfigureAwait(false);
            if (epochFenced is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(new DurableFlowCommandResult(
                    request.InstanceId,
                    DurableFlowCommandOutcome.RaceLost,
                    DurableFlowState.Suspended,
                    epochFenced.Revision));
            }

            if (IsTerminal(current.State))
            {
                var result = await RecordNonMutatingCancelCommandAsync(
                    connection,
                    transaction,
                    request,
                    fingerprint,
                    "already_terminal",
                    current,
                    cancellationToken).ConfigureAwait(false);
                if (_onTerminalApplied is not null)
                {
                    await _onTerminalApplied(
                        transaction,
                        request.ScopeId,
                        request.InstanceId,
                        current.State,
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(result);
            }

            if (request.ExpectedRevision != current.Revision)
            {
                var result = await RecordNonMutatingCancelCommandAsync(
                    connection,
                    transaction,
                    request,
                    fingerprint,
                    "race_lost",
                    current,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(result);
            }

            var activityWorkId = await ReadActiveActivityWorkIdAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                cancellationToken).ConfigureAwait(false);
            var nextState = "canceled";
            if (activityWorkId is not null)
            {
                nextState = await RequestActivityCancellationAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    activityWorkId,
                    cancellationToken).ConfigureAwait(false)
                    ? "canceled"
                    : "cancel_pending";
            }

            var nextRevision = current.Revision + 1;
            await ApplyFlowCancellationAsync(
                connection,
                transaction,
                request,
                current.Revision,
                nextRevision,
                nextState,
                cancellationToken).ConfigureAwait(false);
            await CloseWaitsForCancellationAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                nextRevision,
                nextState,
                cancellationToken).ConfigureAwait(false);
            await InsertCancelCommandAsync(
                connection,
                transaction,
                request,
                fingerprint,
                "accepted",
                nextState,
                nextRevision,
                cancellationToken).ConfigureAwait(false);
            await InsertCommandHistoryAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                nextRevision,
                nextState == "canceled" ? "canceled" : "cancel_pending",
                request.CommandId.Value,
                eventId: null,
                request.ActorId,
                request.ReasonCode,
                "accepted",
                current.NodeId,
                cancellationToken).ConfigureAwait(false);
            await SetFlowDispatchStateAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                nextRevision,
                "terminal",
                cancellationToken).ConfigureAwait(false);
            if (nextState == "canceled" && _onTerminalApplied is not null)
            {
                await _onTerminalApplied(
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    request.ReasonCode,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowCommandResult>.Success(new DurableFlowCommandResult(
                request.InstanceId,
                DurableFlowCommandOutcome.Accepted,
                ParseFlowState(nextState),
                nextRevision));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Applies an audited recovery release to a suspended Flow or an exact-revision dormant Flow from an older runtime
    /// epoch.
    /// </summary>
    /// <remarks>
    /// Direct epoch release preserves the prior nonterminal state and active wait identity. Future timer due times are
    /// retained while their expected Flow revision is advanced atomically. Current-epoch non-suspended, evaluating,
    /// terminal, incompatible-manifest, and invalid wait-shape rows are not adopted.
    /// </remarks>
    internal async ValueTask<DurableOperationResult<DurableFlowCommandResult>> ReleaseSuspensionAsync(
        DurableFlowReleaseRequest request,
        IDurableFlowRegistry flowRegistry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(flowRegistry);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ScopeDisabled(request.CommandId.Value);
            }

            var fingerprint = DurableFlowRequestFingerprint.Compute(request);
            var duplicate = await ReadDuplicateCommandAsync(
                connection,
                transaction,
                request.ScopeId,
                request.CommandId,
                eventId: null,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            var suspended = await ReadRecoverableSuspensionAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                cancellationToken).ConfigureAwait(false);
            if (suspended is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return MissingFlow(request.CommandId.Value);
            }

            if (suspended.Revision != request.ExpectedRevision || IsTerminal(suspended.State))
            {
                var outcome = IsTerminal(suspended.State) ? "already_terminal" : "race_lost";
                var result = await RecordReleaseCommandAsync(
                    connection,
                    transaction,
                    request,
                    fingerprint,
                    outcome,
                    suspended.State,
                    suspended.Revision,
                    suspended.NodeId,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowCommandResult>.Success(result);
            }

            var releasesSuspension = suspended.State == "suspended" && suspended.SuspendedFromState is not null;
            var directlyReFencesDormantFlow =
                suspended.RuntimeEpoch != _runtimeEpoch &&
                suspended.SuspendedFromState is null &&
                IsDirectlyReleasableDormantState(suspended.State);
            if (!releasesSuspension && !directlyReFencesDormantFlow)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ReleaseStateMismatch(request.CommandId.Value);
            }

            DurableFlowRegistration registration;
            try
            {
                registration = flowRegistry.GetRequired(suspended.FlowId, suspended.FlowVersion);
            }
            catch (InvalidOperationException)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ReleaseManifestMismatch(request.CommandId.Value);
            }

            if (!string.Equals(suspended.CommandSchemaVersion, FlowCommandSchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(suspended.AuthoringModel, registration.AuthoringModel, StringComparison.Ordinal) ||
                !suspended.DefinitionFingerprint.AsSpan().SequenceEqual(
                    Convert.FromHexString(registration.DefinitionFingerprint)))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ReleaseManifestMismatch(request.CommandId.Value);
            }

            var restoredState = directlyReFencesDormantFlow
                ? suspended.State
                : suspended.SuspendedFromState == "evaluating"
                    ? "ready"
                    : suspended.SuspendedFromState!;
            if (!await HasValidRestoreShapeAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    restoredState,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ReleaseStateMismatch(request.CommandId.Value);
            }

            var nextRevision = suspended.Revision + 1;
            if (directlyReFencesDormantFlow)
            {
                await ReFenceDormantFlowRuntimeEpochAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    suspended,
                    _runtimeEpoch,
                    nextRevision,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RestoreSuspendedFlowAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    suspended,
                    restoredState,
                    _runtimeEpoch,
                    nextRevision,
                    cancellationToken).ConfigureAwait(false);
            }

            Guid? wakeDispatchId = null;
            if (restoredState == "ready")
            {
                wakeDispatchId = await UpsertFlowDispatchAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    nextRevision,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SetFlowDispatchStateAsync(
                    connection,
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    nextRevision,
                    "terminal",
                    cancellationToken).ConfigureAwait(false);
            }

            await RestoreTimerDispatchesAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                nextRevision,
                restoredState == "waiting_timer",
                cancellationToken).ConfigureAwait(false);
            await InsertReleaseCommandAsync(
                connection,
                transaction,
                request,
                fingerprint,
                "accepted",
                restoredState,
                nextRevision,
                cancellationToken).ConfigureAwait(false);
            await InsertCommandHistoryAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                nextRevision,
                directlyReFencesDormantFlow ? "runtime_epoch_released" : "suspension_released",
                request.CommandId.Value,
                eventId: null,
                request.ActorId,
                request.ReasonCode,
                "accepted",
                suspended.NodeId,
                cancellationToken).ConfigureAwait(false);
            if (releasesSuspension && restoredState == "canceled" && _onTerminalApplied is not null)
            {
                await _onTerminalApplied(
                    transaction,
                    request.ScopeId,
                    request.InstanceId,
                    "canceled_after_activity_success",
                    cancellationToken).ConfigureAwait(false);
            }

            if (_sendWakeNotification && wakeDispatchId is { } dispatchId)
            {
                await SendWakeNotificationAsync(connection, transaction, dispatchId, cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowCommandResult>.Success(new DurableFlowCommandResult(
                request.InstanceId,
                DurableFlowCommandOutcome.Accepted,
                ParseFlowState(restoredState),
                nextRevision));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<IReadOnlyList<PostgreSqlFlowDispatchCandidate>> DiscoverAsync(
        int maximumCandidates,
        CancellationToken cancellationToken = default)
    {
        if (maximumCandidates is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCandidates),
                maximumCandidates,
                "A Flow discovery pass must request between 1 and 1000 candidates.");
        }

        const string sql = """
            SELECT dispatch_id, scope_id, aggregate_kind, aggregate_id, due_at, expected_revision
            FROM appsurface_durable.dispatch
            WHERE aggregate_kind IN ('flow', 'timer')
              AND state IN ('available', 'leased')
              AND due_at <= clock_timestamp()
            ORDER BY due_at, priority DESC, dispatch_id
            LIMIT @maximum_candidates;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("maximum_candidates", maximumCandidates);
        var candidates = new List<PostgreSqlFlowDispatchCandidate>(maximumCandidates);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new PostgreSqlFlowDispatchCandidate(
                reader.GetGuid(0),
                new DurableScopeId(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                ReadUtc(reader, 4),
                reader.GetInt64(5)));
        }

        return candidates;
    }

    internal async ValueTask<PostgreSqlFlowProcessingResult> TryProcessAsync(
        PostgreSqlFlowDispatchCandidate candidate,
        string workerId,
        IDurableFlowRegistry flowRegistry,
        IDurablePayloadCodecRegistry payloadCodecs,
        CancellationToken cancellationToken = default)
        => await TryProcessAsync(
            candidate,
            workerId,
            flowRegistry,
            payloadCodecs,
            _onTerminalApplied,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Claims and evaluates one Flow dispatch, invoking an optional transaction-owned callback for a newly applied
    /// terminal transition before either side commits.
    /// </summary>
    /// <remarks>
    /// The callback must perform database-only work, must not commit or roll back the supplied transaction, and should
    /// be idempotent under ordinary transaction retry. It is not invoked for waits, activities, stale claims, or timers.
    /// </remarks>
    internal async ValueTask<PostgreSqlFlowProcessingResult> TryProcessAsync(
        PostgreSqlFlowDispatchCandidate candidate,
        string workerId,
        IDurableFlowRegistry flowRegistry,
        IDurablePayloadCodecRegistry payloadCodecs,
        Func<NpgsqlTransaction, DurableScopeId, DurableFlowInstanceId, string, CancellationToken, ValueTask>?
            onTerminalApplied,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(flowRegistry);
        ArgumentNullException.ThrowIfNull(payloadCodecs);
        workerId = RequireBoundedText(workerId, nameof(workerId), 200);
        if (string.Equals(candidate.AggregateKind, "timer", StringComparison.Ordinal))
        {
            return await TryFireTimerAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(candidate.AggregateKind, "flow", StringComparison.Ordinal))
        {
            throw new ArgumentException("The dispatch candidate is not a Flow or Flow timer.", nameof(candidate));
        }

        var claim = await TryClaimAsync(candidate, workerId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return new PostgreSqlFlowProcessingResult(
                PostgreSqlFlowProcessingOutcome.NotClaimed,
                candidate.ScopeId,
                new DurableFlowInstanceId(candidate.AggregateId),
                State: null,
                candidate.ExpectedRevision,
                ActivityWorkId: null);
        }

        DurableFlowEvaluationResult transition;
        try
        {
            var registration = flowRegistry.GetRequired(claim.FlowId, claim.FlowVersion);
            if (!string.Equals(claim.CommandSchemaVersion, FlowCommandSchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(claim.AuthoringModel, registration.AuthoringModel, StringComparison.Ordinal) ||
                !claim.DefinitionFingerprint.AsSpan().SequenceEqual(
                    Convert.FromHexString(registration.DefinitionFingerprint)))
            {
                throw new InvalidOperationException(
                    $"Flow '{claim.FlowId}' version '{claim.FlowVersion}' persisted definition manifest does not match its runtime registration.");
            }

            transition = await registration.EvaluateAsync(
                claim.Input,
                payloadCodecs,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not StackOverflowException
                                          and not OutOfMemoryException)
        {
            return await SuspendClaimAsync(claim, "flow.evaluation_failed", CancellationToken.None)
                .ConfigureAwait(false);
        }

        return await CommitTransitionAsync(
            claim,
            transition,
            onTerminalApplied ?? _onTerminalApplied,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<PostgreSqlFlowProcessingResult> ResumeActivityAsync(
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableWorkId activityWorkId,
        string callsiteId,
        DurableEncodedPayload result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        callsiteId = RequireBoundedText(callsiteId, nameof(callsiteId), 200);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.NotClaimed,
                    scopeId,
                    instanceId,
                    State: null,
                    Revision: 0,
                    activityWorkId);
            }

            var resumed = await ResumeActivityCoreAsync(
                connection,
                transaction,
                scopeId,
                instanceId,
                activityWorkId,
                callsiteId,
                result,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return resumed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<PostgreSqlFlowProcessingResult?> ResumeActivityAsync(
        DurableScopeId scopeId,
        DurableWorkId activityWorkId,
        DurableEncodedPayload result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var resumed = await ResumeActivityAsync(
                transaction,
                scopeId,
                activityWorkId,
                result,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return resumed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Records a successful activity result and makes its waiting Flow eligible in the caller's transaction.
    /// </summary>
    /// <remarks>
    /// A missing active activity wait returns <see langword="null"/>, which makes this safe to invoke for any terminal
    /// work fact. If the parent belongs to an older runtime epoch, the same transaction first fences it and projects the
    /// result into its recoverable suspension; an audited Flow release then restores the projected ready state without
    /// a second callback. This method never commits or rolls back <paramref name="transaction"/>.
    /// </remarks>
    internal async ValueTask<PostgreSqlFlowProcessingResult?> ResumeActivityAsync(
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId activityWorkId,
        DurableEncodedPayload result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(result);
        var connection = RequireActiveConnection(transaction);
        await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
            connection,
            transaction,
            _runtimeEpoch,
            cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        if (!await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var identity = await ReadActiveActivityIdentityAsync(
            connection,
            transaction,
            scopeId,
            activityWorkId,
            cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return null;
        }

        return await ResumeActivityCoreAsync(
            connection,
            transaction,
            scopeId,
            identity.InstanceId,
            activityWorkId,
            identity.CallsiteId,
            result,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves an activity that cannot produce a typed result in the caller's transaction.
    /// </summary>
    /// <remarks>
    /// Retry exhaustion suspends the Flow for explicit repair. A proven cancellation before an effect cancels the Flow.
    /// A proven cancellation remains terminal even when the same transaction must first fence an older-epoch parent.
    /// A missing active activity wait returns <see langword="null"/>. This method never owns the transaction outcome.
    /// </remarks>
    internal async ValueTask<PostgreSqlFlowProcessingResult?> FailActivityAsync(
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId activityWorkId,
        PostgreSqlFlowActivityFailureKind failureKind,
        string terminalCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        terminalCode = DurableIdentifier.Require(terminalCode, nameof(terminalCode), 120);
        var connection = RequireActiveConnection(transaction);
        await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
            connection,
            transaction,
            _runtimeEpoch,
            cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        if (!await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var locked = await LockActiveActivityAsync(
            connection,
            transaction,
            scopeId,
            activityWorkId,
            instanceId: null,
            callsiteId: null,
            cancellationToken).ConfigureAwait(false);
        if (locked is null)
        {
            return null;
        }

        var epochFenced = await SuspendForRuntimeEpochMismatchAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            new CurrentFlowRow(
                locked.State,
                locked.Revision,
                locked.NodeId,
                locked.RuntimeEpoch,
                locked.TerminalCode),
            cancellationToken).ConfigureAwait(false);
        if (epochFenced is not null)
        {
            locked = locked with
            {
                State = "suspended",
                Revision = epochFenced.Revision,
                TerminalCode = epochFenced.TerminalCode,
                SuspendedFromState = locked.State == "suspended" ? locked.SuspendedFromState : locked.State,
            };
        }

        if (locked.State == "suspended" && failureKind == PostgreSqlFlowActivityFailureKind.Suspended)
        {
            return new PostgreSqlFlowProcessingResult(
                PostgreSqlFlowProcessingOutcome.Stale,
                scopeId,
                locked.InstanceId,
                DurableFlowState.Suspended,
                locked.Revision,
                activityWorkId);
        }

        var nextRevision = locked.Revision + 1;
        var canceled = failureKind == PostgreSqlFlowActivityFailureKind.CanceledBeforeEffect;
        var recoverable = failureKind == PostgreSqlFlowActivityFailureKind.Suspended;
        var flowState = canceled ? "canceled" : "suspended";
        var waitState = canceled ? "canceled" : "superseded";
        const string sql = """
            UPDATE appsurface_durable.flow_wait
            SET state = @wait_state,
                resolved_revision = @next_revision,
                resolved_at = clock_timestamp()
            WHERE wait_id = @wait_id AND state = 'active' AND NOT @preserve_wait;

            UPDATE appsurface_durable.flow_instance
            SET state = @flow_state,
                suspended_from_state = CASE WHEN @preserve_wait THEN @suspended_from_state ELSE NULL END,
                suspended_from_terminal_code = CASE WHEN @preserve_wait THEN terminal_code ELSE NULL END,
                cancellation_requested_at = CASE
                    WHEN @flow_state = 'canceled' THEN COALESCE(cancellation_requested_at, clock_timestamp())
                    ELSE cancellation_requested_at
                END,
                terminal_at = CASE WHEN @flow_state = 'canceled' THEN clock_timestamp() ELSE NULL END,
                terminal_code = @terminal_code,
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND revision = @revision;
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("wait_state", waitState);
            command.Parameters.AddWithValue("preserve_wait", recoverable);
            command.Parameters.AddWithValue("suspended_from_state", locked.State);
            command.Parameters.AddWithValue("next_revision", nextRevision);
            command.Parameters.AddWithValue("wait_id", locked.WaitId);
            command.Parameters.AddWithValue("flow_state", flowState);
            command.Parameters.AddWithValue("terminal_code", terminalCode);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", locked.InstanceId.Value);
            command.Parameters.AddWithValue("revision", locked.Revision);
            var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var expectedUpdates = recoverable ? 1 : 2;
            if (updated != expectedUpdates)
            {
                throw new DBConcurrencyException(
                    "The Flow activity failure lost its aggregate or wait revision while holding both locks.");
            }
        }

        await SetFlowDispatchStateAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            canceled ? "terminal" : "suspended",
            cancellationToken).ConfigureAwait(false);
        await InsertHistoryAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            canceled ? "activity_canceled_before_effect" : "activity_failure_suspended",
            commandId: null,
            locked.NodeId,
            "{}",
            cancellationToken).ConfigureAwait(false);
        if (canceled && _onTerminalApplied is not null)
        {
            await _onTerminalApplied(
                transaction,
                scopeId,
                locked.InstanceId,
                terminalCode,
                cancellationToken).ConfigureAwait(false);
        }

        return new PostgreSqlFlowProcessingResult(
            PostgreSqlFlowProcessingOutcome.Applied,
            scopeId,
            locked.InstanceId,
            canceled ? DurableFlowState.Canceled : DurableFlowState.Suspended,
            nextRevision,
            activityWorkId);
    }

    private async ValueTask<PostgreSqlFlowProcessingResult> ResumeActivityCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableWorkId activityWorkId,
        string callsiteId,
        DurableEncodedPayload result,
        CancellationToken cancellationToken)
    {
        var locked = await LockActiveActivityAsync(
            connection,
            transaction,
            scopeId,
            activityWorkId,
            instanceId,
            callsiteId,
            cancellationToken).ConfigureAwait(false);
        if (locked is null)
        {
            return new PostgreSqlFlowProcessingResult(
                PostgreSqlFlowProcessingOutcome.NotClaimed,
                scopeId,
                instanceId,
                State: null,
                Revision: 0,
                activityWorkId);
        }

        var epochFenced = await SuspendForRuntimeEpochMismatchAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            new CurrentFlowRow(
                locked.State,
                locked.Revision,
                locked.NodeId,
                locked.RuntimeEpoch,
                locked.TerminalCode),
            cancellationToken).ConfigureAwait(false);
        if (epochFenced is not null)
        {
            locked = locked with
            {
                State = "suspended",
                Revision = epochFenced.Revision,
                TerminalCode = epochFenced.TerminalCode,
                SuspendedFromState = locked.State == "suspended" ? locked.SuspendedFromState : locked.State,
            };
        }

        if (locked.State == "suspended")
        {
            return locked.SuspendedFromState switch
            {
                "waiting_activity" => await ProjectActivityResultIntoSuspensionAsync(
                    connection,
                    transaction,
                    scopeId,
                    activityWorkId,
                    locked,
                    result,
                    cancellationToken).ConfigureAwait(false),
                "cancel_pending" => await ProjectCanceledActivityResultIntoSuspensionAsync(
                    connection,
                    transaction,
                    scopeId,
                    activityWorkId,
                    locked,
                    result,
                    cancellationToken).ConfigureAwait(false),
                _ => new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.Stale,
                    scopeId,
                    locked.InstanceId,
                    DurableFlowState.Suspended,
                    locked.Revision,
                    activityWorkId),
            };
        }

        if (locked.State == "cancel_pending")
        {
            return await CompleteCanceledActivityAsync(
                connection,
                transaction,
                scopeId,
                activityWorkId,
                locked,
                result,
                cancellationToken).ConfigureAwait(false);
        }

        var nextRevision = locked.Revision + 1;
        const string updateSql = """
            UPDATE appsurface_durable.flow_wait
            SET state = 'activity_completed',
                resolved_revision = @next_revision,
                resolved_at = clock_timestamp()
            WHERE wait_id = @wait_id AND state = 'active';

            UPDATE appsurface_durable.flow_instance
            SET state = 'ready',
                activity_callsite_id = @callsite_id,
                activity_result_contract_id = @result_contract_id,
                activity_result_schema_version = @result_schema_version,
                activity_result_codec_id = @result_codec_id,
                activity_result_payload = @result_payload,
                activity_result_sha256 = @result_sha256,
                activity_result_classification = @result_classification,
                activity_result_retention_policy_id = @result_retention_policy_id,
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND revision = @revision;
            """;
        await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
        {
            command.Parameters.AddWithValue("next_revision", nextRevision);
            command.Parameters.AddWithValue("wait_id", locked.WaitId);
            command.Parameters.AddWithValue("callsite_id", locked.CallsiteId);
            AddPayloadParameters(command, "result", result);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", locked.InstanceId.Value);
            command.Parameters.AddWithValue("revision", locked.Revision);
            var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (updated != 2)
            {
                throw new DBConcurrencyException(
                    "The Flow activity result lost its aggregate or wait revision while holding both locks.");
            }
        }

        await InsertResumeHistoryAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            "activity_result_recorded",
            locked.NodeId,
            "activity_result",
            locked.CallsiteId,
            result,
            commandId: null,
            commandEventId: null,
            commandOutcome: null,
            cancellationToken).ConfigureAwait(false);
        var dispatchId = await UpsertFlowDispatchAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            cancellationToken).ConfigureAwait(false);
        if (_sendWakeNotification)
        {
            await SendWakeNotificationAsync(connection, transaction, dispatchId, cancellationToken).ConfigureAwait(false);
        }

        return new PostgreSqlFlowProcessingResult(
            PostgreSqlFlowProcessingOutcome.Applied,
            scopeId,
            locked.InstanceId,
            DurableFlowState.Ready,
            nextRevision,
            activityWorkId);
    }

    private async ValueTask<PostgreSqlFlowProcessingResult> ProjectActivityResultIntoSuspensionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId activityWorkId,
        LockedActivityWait locked,
        DurableEncodedPayload result,
        CancellationToken cancellationToken)
    {
        var nextRevision = locked.Revision + 1;
        const string sql = """
            UPDATE appsurface_durable.flow_wait
            SET state = 'activity_completed',
                resolved_revision = @next_revision,
                resolved_at = clock_timestamp()
            WHERE wait_id = @wait_id AND state = 'active';

            UPDATE appsurface_durable.flow_instance
            SET suspended_from_state = 'ready',
                activity_callsite_id = @callsite_id,
                activity_result_contract_id = @result_contract_id,
                activity_result_schema_version = @result_schema_version,
                activity_result_codec_id = @result_codec_id,
                activity_result_payload = @result_payload,
                activity_result_sha256 = @result_sha256,
                activity_result_classification = @result_classification,
                activity_result_retention_policy_id = @result_retention_policy_id,
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND state = 'suspended'
              AND suspended_from_state = 'waiting_activity'
              AND revision = @revision;
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("next_revision", nextRevision);
            command.Parameters.AddWithValue("wait_id", locked.WaitId);
            command.Parameters.AddWithValue("callsite_id", locked.CallsiteId);
            AddPayloadParameters(command, "result", result);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", locked.InstanceId.Value);
            command.Parameters.AddWithValue("revision", locked.Revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
            {
                throw new DBConcurrencyException(
                    "The suspended Flow activity result lost its aggregate or wait revision while holding both locks.");
            }
        }

        await SetFlowDispatchStateAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            "suspended",
            cancellationToken).ConfigureAwait(false);
        await InsertResumeHistoryAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            "activity_result_projected_while_suspended",
            locked.NodeId,
            "activity_result",
            locked.CallsiteId,
            result,
            commandId: null,
            commandEventId: null,
            commandOutcome: null,
            cancellationToken).ConfigureAwait(false);
        return new PostgreSqlFlowProcessingResult(
            PostgreSqlFlowProcessingOutcome.Applied,
            scopeId,
            locked.InstanceId,
            DurableFlowState.Suspended,
            nextRevision,
            activityWorkId);
    }

    private async ValueTask<PostgreSqlFlowProcessingResult> ProjectCanceledActivityResultIntoSuspensionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId activityWorkId,
        LockedActivityWait locked,
        DurableEncodedPayload result,
        CancellationToken cancellationToken)
    {
        var nextRevision = locked.Revision + 1;
        const string sql = """
            UPDATE appsurface_durable.flow_wait
            SET state = 'activity_completed',
                resolved_revision = @next_revision,
                resolved_at = clock_timestamp()
            WHERE wait_id = @wait_id AND state = 'active';

            UPDATE appsurface_durable.flow_instance
            SET suspended_from_state = 'canceled',
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND state = 'suspended'
              AND suspended_from_state = 'cancel_pending'
              AND revision = @revision;
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("next_revision", nextRevision);
            command.Parameters.AddWithValue("wait_id", locked.WaitId);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", locked.InstanceId.Value);
            command.Parameters.AddWithValue("revision", locked.Revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
            {
                throw new DBConcurrencyException(
                    "The suspended cancel-pending Flow lost its activity result projection revision.");
            }
        }

        await SetFlowDispatchStateAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            "suspended",
            cancellationToken).ConfigureAwait(false);
        await InsertResumeHistoryAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            "activity_succeeded_after_cancel_requested_while_suspended",
            locked.NodeId,
            "activity_result",
            locked.CallsiteId,
            result,
            commandId: null,
            commandEventId: null,
            commandOutcome: null,
            cancellationToken).ConfigureAwait(false);
        return new PostgreSqlFlowProcessingResult(
            PostgreSqlFlowProcessingOutcome.Applied,
            scopeId,
            locked.InstanceId,
            DurableFlowState.Suspended,
            nextRevision,
            activityWorkId);
    }

    private async ValueTask<PostgreSqlFlowProcessingResult> CompleteCanceledActivityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId activityWorkId,
        LockedActivityWait locked,
        DurableEncodedPayload result,
        CancellationToken cancellationToken)
    {
        var nextRevision = locked.Revision + 1;
        const string sql = """
            UPDATE appsurface_durable.flow_wait
            SET state = 'activity_completed',
                resolved_revision = @next_revision,
                resolved_at = clock_timestamp()
            WHERE wait_id = @wait_id AND state = 'active';

            UPDATE appsurface_durable.flow_instance
            SET state = 'canceled',
                terminal_at = clock_timestamp(),
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND state = 'cancel_pending'
              AND revision = @revision;
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("next_revision", nextRevision);
            command.Parameters.AddWithValue("wait_id", locked.WaitId);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", locked.InstanceId.Value);
            command.Parameters.AddWithValue("revision", locked.Revision);
            var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (updated != 2)
            {
                throw new DBConcurrencyException(
                    "The canceled Flow activity result lost its aggregate or wait revision while holding both locks.");
            }
        }

        await SetFlowDispatchStateAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            "terminal",
            cancellationToken).ConfigureAwait(false);
        await InsertResumeHistoryAsync(
            connection,
            transaction,
            scopeId,
            locked.InstanceId,
            nextRevision,
            "activity_succeeded_after_cancel_requested",
            locked.NodeId,
            "activity_result",
            locked.CallsiteId,
            result,
            commandId: null,
            commandEventId: null,
            commandOutcome: null,
            cancellationToken).ConfigureAwait(false);
        if (_onTerminalApplied is not null)
        {
            await _onTerminalApplied(
                transaction,
                scopeId,
                locked.InstanceId,
                "canceled_after_activity_success",
                cancellationToken).ConfigureAwait(false);
        }

        return new PostgreSqlFlowProcessingResult(
            PostgreSqlFlowProcessingOutcome.Applied,
            scopeId,
            locked.InstanceId,
            DurableFlowState.Canceled,
            nextRevision,
            activityWorkId);
    }

    private static async ValueTask<ActivityWaitIdentity?> ReadActiveActivityIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId activityWorkId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT flow_instance_id, activity_callsite_id
            FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id
              AND activity_work_id = @work_id
              AND wait_kind = 'activity'
              AND state = 'active';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", activityWorkId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ActivityWaitIdentity(
                new DurableFlowInstanceId(reader.GetString(0)),
                reader.GetString(1))
            : null;
    }

    private static async ValueTask<LockedActivityWait?> LockActiveActivityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId activityWorkId,
        DurableFlowInstanceId? instanceId,
        string? callsiteId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT flow.flow_instance_id, flow.state, flow.revision,
                   wait.wait_id, wait.node_id, wait.activity_callsite_id,
                   flow.runtime_epoch, flow.terminal_code, flow.suspended_from_state
            FROM appsurface_durable.flow_instance AS flow
            JOIN appsurface_durable.flow_wait AS wait
              ON wait.scope_id = flow.scope_id
             AND wait.flow_instance_id = flow.flow_instance_id
             AND wait.state = 'active'
             AND wait.wait_kind = 'activity'
            WHERE flow.scope_id = @scope_id
              AND
              (
                  flow.state IN ('waiting_activity', 'cancel_pending')
                  OR
                  (flow.state = 'suspended'
                      AND flow.suspended_from_state IN ('waiting_activity', 'cancel_pending'))
              )
              AND wait.activity_work_id = @work_id
              AND (@flow_instance_id IS NULL OR flow.flow_instance_id = @flow_instance_id)
              AND (@callsite_id IS NULL OR wait.activity_callsite_id = @callsite_id)
            FOR UPDATE OF flow, wait;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", activityWorkId.Value);
        command.Parameters.Add(new NpgsqlParameter("flow_instance_id", NpgsqlDbType.Text)
        {
            Value = instanceId is { } flowId ? flowId.Value : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("callsite_id", NpgsqlDbType.Text)
        {
            Value = callsiteId ?? (object)DBNull.Value,
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new LockedActivityWait(
                new DurableFlowInstanceId(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8))
            : null;
    }

    private async ValueTask<PostgreSqlFlowClaim?> TryClaimAsync(
        PostgreSqlFlowDispatchCandidate candidate,
        string workerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, candidate.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(
                    connection,
                    transaction,
                    candidate.ScopeId,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            const string sql = """
                UPDATE appsurface_durable.flow_instance AS flow
                SET state = 'evaluating',
                    lease_generation = flow.lease_generation + 1,
                    lease_owner = @worker_id,
                    lease_started_at = clock_timestamp(),
                    lease_expires_at = clock_timestamp() + @lease_duration,
                    revision = flow.revision + 1,
                    updated_at = clock_timestamp()
                FROM appsurface_durable.scope AS scope
                WHERE flow.scope_id = @scope_id
                  AND flow.flow_instance_id = @flow_instance_id
                  AND flow.revision = @expected_revision
                  AND flow.runtime_epoch = @runtime_epoch
                  AND
                  (
                      flow.state = 'ready'
                      OR (flow.state = 'evaluating' AND flow.lease_expires_at <= clock_timestamp())
                  )
                  AND scope.scope_id = flow.scope_id
                  AND scope.state = 'active'
                  AND scope.generation = flow.scope_generation
                RETURNING
                    flow.flow_id, flow.flow_version, flow.current_node_id,
                    flow.context_contract_id, flow.context_schema_version, flow.context_classification,
                    flow.context_retention_policy_id, flow.context_payload, flow.context_sha256,
                    flow.resume_event_name, flow.resume_event_is_timeout,
                    flow.resume_event_contract_id, flow.resume_event_schema_version,
                    flow.resume_event_classification, flow.resume_event_retention_policy_id,
                    flow.resume_event_payload, flow.resume_event_sha256,
                    flow.activity_callsite_id, flow.activity_result_contract_id,
                    flow.activity_result_schema_version, flow.activity_result_classification,
                    flow.activity_result_retention_policy_id, flow.activity_result_payload, flow.activity_result_sha256,
                    flow.lease_generation, flow.revision,
                    flow.authoring_model, flow.definition_fingerprint, flow.command_schema_version;
                """;
            PostgreSqlFlowClaim? claim = null;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("worker_id", workerId);
                command.Parameters.AddWithValue("lease_duration", EvaluationLeaseDuration);
                command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
                command.Parameters.AddWithValue("flow_instance_id", candidate.AggregateId);
                command.Parameters.AddWithValue("expected_revision", candidate.ExpectedRevision);
                command.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var context = ReadPayload(reader, 3, 4, 5, 6, 7, 8);
                    DurableEncodedPayload? eventPayload = reader.IsDBNull(15)
                        ? null
                        : ReadPayload(reader, 11, 12, 13, 14, 15, 16);
                    DurableEncodedPayload? activityResult = reader.IsDBNull(22)
                        ? null
                        : ReadPayload(reader, 18, 19, 20, 21, 22, 23);
                    claim = new PostgreSqlFlowClaim(
                        candidate.DispatchId,
                        candidate.ScopeId,
                        new DurableFlowInstanceId(candidate.AggregateId),
                        reader.GetString(0),
                        reader.GetString(1),
                        new DurableFlowEvaluationInput(
                            reader.GetString(2),
                            context,
                            reader.IsDBNull(9) ? null : reader.GetString(9),
                            eventPayload,
                            reader.GetBoolean(10),
                            reader.IsDBNull(17) ? null : reader.GetString(17),
                            activityResult),
                        reader.GetInt64(24),
                        reader.GetInt64(25),
                        reader.GetString(26),
                        reader.GetFieldValue<byte[]>(27),
                        reader.GetString(28));
                }
            }

            if (claim is null)
            {
                var instanceId = new DurableFlowInstanceId(candidate.AggregateId);
                var current = await LockCurrentAsync(
                    connection,
                    transaction,
                    candidate.ScopeId,
                    instanceId,
                    cancellationToken).ConfigureAwait(false);
                var epochFenced = current is null
                    ? null
                    : await SuspendForRuntimeEpochMismatchAsync(
                        connection,
                        transaction,
                        candidate.ScopeId,
                        instanceId,
                        current,
                        cancellationToken).ConfigureAwait(false);
                if (epochFenced is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            await SetFlowDispatchLeaseAsync(connection, transaction, claim, cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection,
                transaction,
                claim.ScopeId,
                claim.InstanceId,
                claim.Revision,
                "evaluation_claimed",
                commandId: null,
                claim.Input.NodeId,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claim;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<PostgreSqlFlowProcessingResult> CommitTransitionAsync(
        PostgreSqlFlowClaim claim,
        DurableFlowEvaluationResult transition,
        Func<NpgsqlTransaction, DurableScopeId, DurableFlowInstanceId, string, CancellationToken, ValueTask>?
            onTerminalApplied,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ValidateTransition(claim, transition);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, claim.ScopeId, cancellationToken).ConfigureAwait(false);
            var nextRevision = claim.Revision + 1;
            var nextState = TransitionState(transition);
            var nextNodeId = transition.Kind == FlowTransitionKind.Next
                ? transition.NextNodeId!
                : transition.NodeId;
            const string updateSql = """
                UPDATE appsurface_durable.flow_instance
                SET state = @state,
                    current_node_id = @node_id,
                    context_contract_id = CASE WHEN @has_context THEN @context_contract_id ELSE context_contract_id END,
                    context_schema_version = CASE WHEN @has_context THEN @context_schema_version ELSE context_schema_version END,
                    context_codec_id = CASE WHEN @has_context THEN @context_codec_id ELSE context_codec_id END,
                    context_payload = CASE WHEN @has_context THEN @context_payload ELSE context_payload END,
                    context_sha256 = CASE WHEN @has_context THEN @context_sha256 ELSE context_sha256 END,
                    context_classification = CASE WHEN @has_context THEN @context_classification ELSE context_classification END,
                    context_retention_policy_id = CASE
                        WHEN @has_context THEN @context_retention_policy_id ELSE context_retention_policy_id END,
                    resume_event_name = NULL,
                    resume_event_is_timeout = false,
                    resume_event_contract_id = NULL,
                    resume_event_schema_version = NULL,
                    resume_event_codec_id = NULL,
                    resume_event_payload = NULL,
                    resume_event_sha256 = NULL,
                    resume_event_classification = NULL,
                    resume_event_retention_policy_id = NULL,
                    activity_callsite_id = NULL,
                    activity_result_contract_id = NULL,
                    activity_result_schema_version = NULL,
                    activity_result_codec_id = NULL,
                    activity_result_payload = NULL,
                    activity_result_sha256 = NULL,
                    activity_result_classification = NULL,
                    activity_result_retention_policy_id = NULL,
                    lease_owner = NULL,
                    lease_started_at = NULL,
                    lease_expires_at = NULL,
                    revision = @next_revision,
                    terminal_at = CASE WHEN @is_terminal THEN clock_timestamp() ELSE NULL END,
                    terminal_code = @terminal_code,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id
                  AND flow_instance_id = @flow_instance_id
                  AND state = 'evaluating'
                  AND revision = @revision
                  AND lease_generation = @lease_generation
                  AND runtime_epoch = @runtime_epoch;
                """;
            int updated;
            await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
            {
                command.Parameters.AddWithValue("state", nextState);
                command.Parameters.AddWithValue("node_id", nextNodeId);
                command.Parameters.AddWithValue("has_context", transition.Context is not null);
                AddOptionalPayloadParameters(command, "context", transition.Context);
                command.Parameters.AddWithValue("next_revision", nextRevision);
                command.Parameters.AddWithValue("is_terminal", IsTerminal(nextState));
                command.Parameters.Add(new NpgsqlParameter("terminal_code", NpgsqlDbType.Text)
                {
                    Value = transition.Fault?.Code is { } code ? code : DBNull.Value,
                });
                command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
                command.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
                command.Parameters.AddWithValue("revision", claim.Revision);
                command.Parameters.AddWithValue("lease_generation", claim.LeaseGeneration);
                command.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
                updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (updated == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.Stale,
                    claim.ScopeId,
                    claim.InstanceId,
                    State: null,
                    claim.Revision,
                    ActivityWorkId: null);
            }

            DurableWorkId? activityWorkId = null;
            switch (transition.Kind)
            {
                case FlowTransitionKind.Next:
                    var nextDispatch = await UpsertFlowDispatchAsync(
                        connection,
                        transaction,
                        claim.ScopeId,
                        claim.InstanceId,
                        nextRevision,
                        cancellationToken).ConfigureAwait(false);
                    if (_sendWakeNotification)
                    {
                        await SendWakeNotificationAsync(connection, transaction, nextDispatch, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;
                case FlowTransitionKind.Wait:
                    await RegisterEventWaitAsync(
                        connection,
                        transaction,
                        claim,
                        transition,
                        nextRevision,
                        cancellationToken).ConfigureAwait(false);
                    await SetFlowDispatchStateAsync(
                        connection,
                        transaction,
                        claim.ScopeId,
                        claim.InstanceId,
                        nextRevision,
                        "terminal",
                        cancellationToken).ConfigureAwait(false);
                    break;
                case FlowTransitionKind.Activity:
                    activityWorkId = await AcceptActivityAsync(
                        connection,
                        transaction,
                        claim,
                        transition.Activity!,
                        nextRevision,
                        cancellationToken).ConfigureAwait(false);
                    await RegisterActivityWaitAsync(
                        connection,
                        transaction,
                        claim,
                        transition.Activity!,
                        activityWorkId.Value,
                        nextRevision,
                        cancellationToken).ConfigureAwait(false);
                    await SetFlowDispatchStateAsync(
                        connection,
                        transaction,
                        claim.ScopeId,
                        claim.InstanceId,
                        nextRevision,
                        "terminal",
                        cancellationToken).ConfigureAwait(false);
                    break;
                case FlowTransitionKind.TimedOut:
                case FlowTransitionKind.Complete:
                case FlowTransitionKind.Fault:
                    await SetFlowDispatchStateAsync(
                        connection,
                        transaction,
                        claim.ScopeId,
                        claim.InstanceId,
                        nextRevision,
                        "terminal",
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported persisted Flow transition kind '{transition.Kind}'.");
            }

            await InsertTransitionHistoryAsync(
                connection,
                transaction,
                claim,
                transition,
                nextRevision,
                TransitionHistoryEvent(transition.Kind),
                cancellationToken).ConfigureAwait(false);
            if (IsTerminal(nextState) && onTerminalApplied is not null)
            {
                await onTerminalApplied(
                    transaction,
                    claim.ScopeId,
                    claim.InstanceId,
                    TransitionTerminalCode(transition),
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlFlowProcessingResult(
                PostgreSqlFlowProcessingOutcome.Applied,
                claim.ScopeId,
                claim.InstanceId,
                ParseFlowState(nextState),
                nextRevision,
                activityWorkId);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<PostgreSqlFlowProcessingResult> TryFireTimerAsync(
        PostgreSqlFlowDispatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(candidate.AggregateId, out var timerId))
        {
            throw new InvalidDataException("A Flow timer dispatch carries an invalid timer identifier.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, candidate.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(
                    connection,
                    transaction,
                    candidate.ScopeId,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.NotClaimed,
                    candidate.ScopeId,
                    new DurableFlowInstanceId("inactive-scope-timer-owner"),
                    State: null,
                    candidate.ExpectedRevision,
                    ActivityWorkId: null);
            }

            const string lockSql = """
                SELECT timer.flow_instance_id, timer.wait_id, timer.expected_flow_revision,
                       wait.node_id, wait.event_name, wait.state,
                       flow.state, flow.revision, flow.runtime_epoch, flow.terminal_code
                FROM appsurface_durable.flow_timer AS timer
                JOIN appsurface_durable.flow_wait AS wait
                  ON wait.wait_id = timer.wait_id
                 AND wait.scope_id = timer.scope_id
                 AND wait.flow_instance_id = timer.flow_instance_id
                JOIN appsurface_durable.flow_instance AS flow
                  ON flow.scope_id = timer.scope_id
                 AND flow.flow_instance_id = timer.flow_instance_id
                WHERE timer.scope_id = @scope_id
                  AND timer.timer_id = @timer_id
                  AND timer.state = 'scheduled'
                  AND timer.due_at <= clock_timestamp()
                FOR UPDATE OF timer, wait, flow;
                """;
            string? flowInstanceId = null;
            Guid waitId = default;
            long expectedRevision = 0;
            string? nodeId = null;
            string? eventName = null;
            string? waitState = null;
            string? flowState = null;
            long flowRevision = 0;
            Guid flowRuntimeEpoch = default;
            string? terminalCode = null;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
                command.Parameters.AddWithValue("timer_id", timerId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    flowInstanceId = reader.GetString(0);
                    waitId = reader.GetGuid(1);
                    expectedRevision = reader.GetInt64(2);
                    nodeId = reader.GetString(3);
                    eventName = reader.GetString(4);
                    waitState = reader.GetString(5);
                    flowState = reader.GetString(6);
                    flowRevision = reader.GetInt64(7);
                    flowRuntimeEpoch = reader.GetGuid(8);
                    terminalCode = reader.IsDBNull(9) ? null : reader.GetString(9);
                }
            }

            if (flowInstanceId is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.NotClaimed,
                    candidate.ScopeId,
                    new DurableFlowInstanceId("unknown-timer-owner"),
                    State: null,
                    candidate.ExpectedRevision,
                    ActivityWorkId: null);
            }

            var instanceId = new DurableFlowInstanceId(flowInstanceId);
            var epochFenced = await SuspendForRuntimeEpochMismatchAsync(
                connection,
                transaction,
                candidate.ScopeId,
                instanceId,
                new CurrentFlowRow(flowState!, flowRevision, nodeId!, flowRuntimeEpoch, terminalCode),
                cancellationToken).ConfigureAwait(false);
            if (epochFenced is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.Stale,
                    candidate.ScopeId,
                    instanceId,
                    DurableFlowState.Suspended,
                    epochFenced.Revision,
                    ActivityWorkId: null);
            }

            if (waitState != "active" ||
                flowRevision != expectedRevision ||
                flowState is not ("waiting_event" or "waiting_timer"))
            {
                await SupersedeTimerAsync(connection, transaction, candidate, timerId, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.RaceLost,
                    candidate.ScopeId,
                    instanceId,
                    ParseFlowState(flowState!),
                    flowRevision,
                    ActivityWorkId: null);
            }

            var nextRevision = flowRevision + 1;
            const string updateSql = """
                UPDATE appsurface_durable.flow_timer
                SET state = 'fired', resolved_at = clock_timestamp()
                WHERE timer_id = @timer_id;

                UPDATE appsurface_durable.flow_wait
                SET state = 'timer_won', resolved_revision = @next_revision, resolved_at = clock_timestamp()
                WHERE wait_id = @wait_id AND state = 'active';

                UPDATE appsurface_durable.flow_instance
                SET state = 'ready',
                    resume_event_name = @event_name,
                    resume_event_is_timeout = true,
                    revision = @next_revision,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id
                  AND flow_instance_id = @flow_instance_id
                  AND revision = @flow_revision;
                """;
            await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
            {
                command.Parameters.AddWithValue("timer_id", timerId);
                command.Parameters.AddWithValue("next_revision", nextRevision);
                command.Parameters.AddWithValue("wait_id", waitId);
                command.Parameters.AddWithValue("event_name", eventName!);
                command.Parameters.AddWithValue("scope_id", candidate.ScopeId.Value);
                command.Parameters.AddWithValue("flow_instance_id", flowInstanceId);
                command.Parameters.AddWithValue("flow_revision", flowRevision);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SetDispatchTerminalAsync(connection, transaction, candidate.DispatchId, nextRevision, cancellationToken)
                .ConfigureAwait(false);
            await InsertResumeHistoryAsync(
                connection,
                transaction,
                candidate.ScopeId,
                instanceId,
                nextRevision,
                "wait_timer_fired",
                nodeId,
                "timeout",
                eventName,
                transitionInput: null,
                commandId: null,
                commandEventId: null,
                commandOutcome: null,
                cancellationToken).ConfigureAwait(false);
            var flowDispatch = await UpsertFlowDispatchAsync(
                connection,
                transaction,
                candidate.ScopeId,
                instanceId,
                nextRevision,
                cancellationToken).ConfigureAwait(false);
            if (_sendWakeNotification)
            {
                await SendWakeNotificationAsync(connection, transaction, flowDispatch, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlFlowProcessingResult(
                PostgreSqlFlowProcessingOutcome.Applied,
                candidate.ScopeId,
                instanceId,
                DurableFlowState.Ready,
                nextRevision,
                ActivityWorkId: null);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<PostgreSqlFlowProcessingResult> SuspendClaimAsync(
        PostgreSqlFlowClaim claim,
        string terminalCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlDurableEpochFence.EnsureCurrentAsync(
                connection,
                transaction,
                _runtimeEpoch,
                cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, claim.ScopeId, cancellationToken).ConfigureAwait(false);
            const string sql = """
                UPDATE appsurface_durable.flow_instance
                SET state = 'suspended',
                    suspended_from_state = 'evaluating',
                    suspended_from_terminal_code = terminal_code,
                    terminal_code = @terminal_code,
                    lease_owner = NULL,
                    lease_started_at = NULL,
                    lease_expires_at = NULL,
                    revision = revision + 1,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id
                  AND flow_instance_id = @flow_instance_id
                  AND state = 'evaluating'
                  AND revision = @revision
                  AND lease_generation = @lease_generation
                  AND runtime_epoch = @runtime_epoch
                RETURNING revision;
                """;
            long? revision = null;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("terminal_code", terminalCode);
                command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
                command.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
                command.Parameters.AddWithValue("revision", claim.Revision);
                command.Parameters.AddWithValue("lease_generation", claim.LeaseGeneration);
                command.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                revision = value as long?;
            }

            if (revision is not null)
            {
                await SetFlowDispatchStateAsync(
                    connection,
                    transaction,
                    claim.ScopeId,
                    claim.InstanceId,
                    revision.Value,
                    "suspended",
                    cancellationToken).ConfigureAwait(false);
                await InsertHistoryAsync(
                    connection,
                    transaction,
                    claim.ScopeId,
                    claim.InstanceId,
                    revision.Value,
                    "evaluation_suspended",
                    commandId: null,
                    claim.Input.NodeId,
                    "{}",
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return revision is null
                ? new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.Stale,
                    claim.ScopeId,
                    claim.InstanceId,
                    State: null,
                    claim.Revision,
                    ActivityWorkId: null)
                : new PostgreSqlFlowProcessingResult(
                    PostgreSqlFlowProcessingOutcome.Failed,
                    claim.ScopeId,
                    claim.InstanceId,
                    DurableFlowState.Suspended,
                    revision.Value,
                    ActivityWorkId: null);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<StartInsertResult?> TryInsertStartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowStartRequest request,
        DurableFlowRegistration registration,
        Guid runtimeEpoch,
        long scopeGeneration,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO appsurface_durable.flow_instance
            (
                scope_id, flow_instance_id, start_idempotency_key, flow_id, flow_version,
                authoring_model, command_schema_version, definition_fingerprint, current_node_id, state,
                context_contract_id, context_schema_version, context_codec_id,
                context_payload, context_sha256, context_classification, context_retention_policy_id,
                scope_generation, runtime_epoch
            )
            VALUES
            (
                @scope_id, @flow_instance_id, @idempotency_key, @flow_id, @flow_version,
                @authoring_model, @command_schema_version, @definition_fingerprint, @start_node_id, 'ready',
                @context_contract_id, @context_schema_version, @context_codec_id,
                @context_payload, @context_sha256, @context_classification, @context_retention_policy_id,
                @scope_generation, @runtime_epoch
            )
            ON CONFLICT DO NOTHING
            RETURNING created_at;
            """;
        DateTimeOffset? createdAt = null;
        await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
        {
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
            command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
            command.Parameters.AddWithValue("flow_id", request.FlowId);
            command.Parameters.AddWithValue("flow_version", request.FlowVersion);
            command.Parameters.AddWithValue("authoring_model", registration.AuthoringModel);
            command.Parameters.AddWithValue("command_schema_version", FlowCommandSchemaVersion);
            command.Parameters.AddWithValue(
                "definition_fingerprint",
                Convert.FromHexString(registration.DefinitionFingerprint));
            command.Parameters.AddWithValue("start_node_id", registration.StartNodeId);
            AddPayloadParameters(command, "context", request.Context);
            command.Parameters.AddWithValue("scope_generation", scopeGeneration);
            command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is DateTime dateTime)
            {
                createdAt = new DateTimeOffset(dateTime, TimeSpan.Zero);
            }
        }

        if (createdAt is null)
        {
            return null;
        }

        await InsertCommandAsync(
            connection,
            transaction,
            request.ScopeId,
            request.InstanceId,
            request.CommandId,
            "start",
            eventId: null,
            fingerprint,
            "accepted",
            "ready",
            resultingRevision: 1,
            cancellationToken).ConfigureAwait(false);
        await InsertHistoryAsync(
            connection,
            transaction,
            request.ScopeId,
            request.InstanceId,
            aggregateRevision: 1,
            "started",
            request.CommandId.Value,
            registration.StartNodeId,
            "{}",
            cancellationToken).ConfigureAwait(false);
        var dispatchId = await UpsertFlowDispatchAsync(
            connection,
            transaction,
            request.ScopeId,
            request.InstanceId,
            expectedRevision: 1,
            cancellationToken).ConfigureAwait(false);
        return new StartInsertResult(
            new DurableFlowCommandResult(
                request.InstanceId,
                DurableFlowCommandOutcome.Accepted,
                DurableFlowState.Ready,
                revision: 1),
            dispatchId,
            createdAt.Value);
    }

    private static async ValueTask<DurableOperationResult<DurableFlowCommandResult>> ReadDuplicateStartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowStartRequest request,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT flow.flow_instance_id, flow.flow_id, flow.flow_version, flow.state, flow.revision,
                   command.request_sha256, flow.command_schema_version, command.command_schema_version
            FROM appsurface_durable.flow_instance AS flow
            JOIN appsurface_durable.flow_command AS command
              ON command.scope_id = flow.scope_id
             AND command.flow_instance_id = flow.flow_instance_id
             AND command.command_type = 'start'
            WHERE flow.scope_id = @scope_id
              AND
              (
                  flow.flow_instance_id = @flow_instance_id
                  OR flow.start_idempotency_key = @idempotency_key
                  OR command.command_id = @command_id
              )
            ORDER BY (command.command_id = @command_id) DESC
            LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            reader.GetFieldValue<byte[]>(5).AsSpan().SequenceEqual(fingerprint) &&
            string.Equals(reader.GetString(6), FlowCommandSchemaVersion, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(7), FlowCommandSchemaVersion, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(0), request.InstanceId.Value, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(1), request.FlowId, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), request.FlowVersion, StringComparison.Ordinal))
        {
            return DurableOperationResult<DurableFlowCommandResult>.Success(new DurableFlowCommandResult(
                request.InstanceId,
                DurableFlowCommandOutcome.Duplicate,
                ParseFlowState(reader.GetString(3)),
                reader.GetInt64(4)));
        }

        return DurableOperationResult<DurableFlowCommandResult>.Failure(new DurableProblem(
            DurableProblemCodes.FlowStartConflict,
            "The Flow start conflicts with an existing command, instance, or idempotency key.",
            "A durable identity was reused with different Flow metadata or encoded context bytes.",
            "Retry with the exact original request, or use new command, instance, and idempotency identities.",
            FlowDocumentation,
            request.CommandId.Value));
    }

    private static async ValueTask<DurableOperationResult<DurableFlowCommandResult>?> ReadDuplicateCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableCommandId commandId,
        DurableFlowEventId? eventId,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT flow_instance_id, request_sha256, resulting_state, resulting_revision, command_schema_version
            FROM appsurface_durable.flow_command
            WHERE scope_id = @scope_id
              AND (command_id = @command_id OR (@event_id IS NOT NULL AND event_id = @event_id))
            ORDER BY (command_id = @command_id) DESC
            LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text)
        {
            Value = eventId is { } value ? value.Value : DBNull.Value,
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(fingerprint) &&
            string.Equals(reader.GetString(4), FlowCommandSchemaVersion, StringComparison.Ordinal))
        {
            return DurableOperationResult<DurableFlowCommandResult>.Success(new DurableFlowCommandResult(
                new DurableFlowInstanceId(reader.GetString(0)),
                DurableFlowCommandOutcome.Duplicate,
                ParseFlowState(reader.GetString(2)),
                reader.GetInt64(3)));
        }

        return DurableOperationResult<DurableFlowCommandResult>.Failure(new DurableProblem(
            DurableProblemCodes.FlowCommandConflict,
            "The Flow command or event identity was already used for a different request.",
            "The stored request fingerprint does not match this retry.",
            "Retry only with the exact original request, or allocate a new command and event identity.",
            FlowDocumentation,
            commandId.Value));
    }

    private static async ValueTask<CurrentFlowRow?> LockCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state, revision, current_node_id, runtime_epoch, terminal_code
            FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CurrentFlowRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }

    private static async ValueTask<RecoverableSuspensionRow?> ReadRecoverableSuspensionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state, revision, current_node_id, suspended_from_state,
                   flow_id, flow_version, authoring_model, command_schema_version,
                   definition_fingerprint, runtime_epoch
            FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new RecoverableSuspensionRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetFieldValue<byte[]>(8),
                reader.GetGuid(9))
            : null;
    }

    private static async ValueTask RestoreSuspendedFlowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        RecoverableSuspensionRow suspended,
        string restoredState,
        Guid runtimeEpoch,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.flow_instance
            SET state = @restored_state,
                suspended_from_state = NULL,
                terminal_code = suspended_from_terminal_code,
                suspended_from_terminal_code = NULL,
                runtime_epoch = @runtime_epoch,
                lease_owner = NULL,
                lease_started_at = NULL,
                lease_expires_at = NULL,
                terminal_at = CASE WHEN @restored_state = 'canceled' THEN clock_timestamp() ELSE NULL END,
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND state = 'suspended'
              AND revision = @expected_revision
              AND suspended_from_state = @suspended_from_state;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("restored_state", restoredState);
        command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
        command.Parameters.AddWithValue("next_revision", nextRevision);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("expected_revision", suspended.Revision);
        command.Parameters.AddWithValue("suspended_from_state", suspended.SuspendedFromState!);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException(
                "The Flow suspension release lost its expected revision while holding the aggregate lock.");
        }
    }

    private static async ValueTask ReFenceDormantFlowRuntimeEpochAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        RecoverableSuspensionRow dormant,
        Guid runtimeEpoch,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.flow_instance
            SET runtime_epoch = @runtime_epoch,
                lease_owner = NULL,
                lease_started_at = NULL,
                lease_expires_at = NULL,
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND state = @state
              AND state IN ('ready', 'waiting_event', 'waiting_timer', 'waiting_activity', 'cancel_pending')
              AND suspended_from_state IS NULL
              AND revision = @expected_revision
              AND runtime_epoch = @previous_runtime_epoch
              AND runtime_epoch <> @runtime_epoch;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
        command.Parameters.AddWithValue("next_revision", nextRevision);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("state", dormant.State);
        command.Parameters.AddWithValue("expected_revision", dormant.Revision);
        command.Parameters.AddWithValue("previous_runtime_epoch", dormant.RuntimeEpoch);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException(
                "The dormant Flow runtime-epoch release lost its expected revision while holding the aggregate lock.");
        }
    }

    private static async ValueTask<bool> HasValidRestoreShapeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        string restoredState,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                count(*) FILTER (WHERE wait.state = 'active' AND wait.wait_kind = 'external_event'),
                count(*) FILTER (WHERE wait.state = 'active' AND wait.wait_kind = 'activity'),
                count(*) FILTER (WHERE timer.state = 'scheduled')
            FROM appsurface_durable.flow_instance AS flow
            LEFT JOIN appsurface_durable.flow_wait AS wait
              ON wait.scope_id = flow.scope_id
             AND wait.flow_instance_id = flow.flow_instance_id
            LEFT JOIN appsurface_durable.flow_timer AS timer
              ON timer.scope_id = flow.scope_id
             AND timer.flow_instance_id = flow.flow_instance_id
             AND (wait.wait_id IS NULL OR timer.wait_id = wait.wait_id)
            WHERE flow.scope_id = @scope_id
              AND flow.flow_instance_id = @flow_instance_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var eventWaits = reader.GetInt64(0);
        var activityWaits = reader.GetInt64(1);
        var scheduledTimers = reader.GetInt64(2);
        return restoredState switch
        {
            "ready" => eventWaits == 0 && activityWaits == 0 && scheduledTimers == 0,
            "waiting_event" => eventWaits == 1 && activityWaits == 0 && scheduledTimers == 0,
            "waiting_timer" => eventWaits == 1 && activityWaits == 0 && scheduledTimers == 1,
            "waiting_activity" or "cancel_pending" =>
                eventWaits == 0 && activityWaits == 1 && scheduledTimers == 0,
            "canceled" => eventWaits == 0 && activityWaits == 0 && scheduledTimers == 0,
            _ => false,
        };
    }

    private async ValueTask<CurrentFlowRow?> SuspendForRuntimeEpochMismatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        CurrentFlowRow current,
        CancellationToken cancellationToken)
    {
        if (current.RuntimeEpoch == _runtimeEpoch || IsTerminal(current.State))
        {
            return null;
        }

        if (current.State == "suspended")
        {
            return current;
        }

        var nextRevision = current.Revision + 1;
        const string sql = """
            UPDATE appsurface_durable.flow_instance
            SET state = 'suspended',
                suspended_from_state = @suspended_from_state,
                suspended_from_terminal_code = terminal_code,
                terminal_code = @terminal_code,
                lease_owner = NULL,
                lease_started_at = NULL,
                lease_expires_at = NULL,
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND revision = @revision
              AND runtime_epoch <> @runtime_epoch;

            UPDATE appsurface_durable.dispatch
            SET state = 'suspended',
                expected_revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND
              (
                  (aggregate_kind = 'flow' AND aggregate_id = @flow_instance_id)
                  OR
                  (aggregate_kind = 'timer' AND aggregate_id IN
                  (
                      SELECT timer_id::text
                      FROM appsurface_durable.flow_timer
                      WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                  ))
              );
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("terminal_code", RuntimeEpochMismatchCode);
            command.Parameters.AddWithValue("suspended_from_state", current.State);
            command.Parameters.AddWithValue("next_revision", nextRevision);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
            command.Parameters.AddWithValue("revision", current.Revision);
            command.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
            var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (updated < 1)
            {
                throw new DBConcurrencyException(
                    "The Flow runtime-epoch fence lost its revision while holding the aggregate lock.");
            }
        }

        await InsertHistoryAsync(
            connection,
            transaction,
            scopeId,
            instanceId,
            nextRevision,
            "runtime_epoch_mismatch_suspended",
            commandId: null,
            current.NodeId,
            "{}",
            cancellationToken).ConfigureAwait(false);
        return new CurrentFlowRow(
            "suspended",
            nextRevision,
            current.NodeId,
            current.RuntimeEpoch,
            RuntimeEpochMismatchCode);
    }

    private static async ValueTask<DurableFlowCommandResult> RecordNonMutatingCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableCommandId commandId,
        string commandType,
        DurableFlowEventId? eventId,
        byte[] fingerprint,
        string outcome,
        CurrentFlowRow current,
        CancellationToken cancellationToken)
    {
        await InsertCommandAsync(
            connection,
            transaction,
            scopeId,
            instanceId,
            commandId,
            commandType,
            eventId,
            fingerprint,
            outcome,
            current.State,
            current.Revision,
            cancellationToken).ConfigureAwait(false);
        await InsertCommandHistoryAsync(
            connection,
            transaction,
            scopeId,
            instanceId,
            current.Revision,
            outcome == "race_lost" ? $"{commandType}_race_lost" : $"{commandType}_already_terminal",
            commandId.Value,
            eventId?.Value,
            actorId: null,
            reasonCode: null,
            outcome,
            current.NodeId,
            cancellationToken).ConfigureAwait(false);
        return new DurableFlowCommandResult(
            instanceId,
            outcome == "race_lost" ? DurableFlowCommandOutcome.RaceLost : DurableFlowCommandOutcome.AlreadyTerminal,
            ParseFlowState(current.State),
            current.Revision);
    }

    private static async ValueTask<DurableFlowCommandResult> RecordNonMutatingCancelCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowCancelRequest request,
        byte[] fingerprint,
        string outcome,
        CurrentFlowRow current,
        CancellationToken cancellationToken)
    {
        await InsertCancelCommandAsync(
            connection,
            transaction,
            request,
            fingerprint,
            outcome,
            current.State,
            current.Revision,
            cancellationToken).ConfigureAwait(false);
        await InsertCommandHistoryAsync(
            connection,
            transaction,
            request.ScopeId,
            request.InstanceId,
            current.Revision,
            outcome == "race_lost" ? "cancel_race_lost" : "cancel_already_terminal",
            request.CommandId.Value,
            eventId: null,
            request.ActorId,
            request.ReasonCode,
            outcome,
            current.NodeId,
            cancellationToken).ConfigureAwait(false);
        return new DurableFlowCommandResult(
            request.InstanceId,
            outcome == "race_lost" ? DurableFlowCommandOutcome.RaceLost : DurableFlowCommandOutcome.AlreadyTerminal,
            ParseFlowState(current.State),
            current.Revision);
    }

    private static async ValueTask<DurableFlowCommandResult> RecordReleaseCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowReleaseRequest request,
        byte[] fingerprint,
        string outcome,
        string resultingState,
        long resultingRevision,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await InsertReleaseCommandAsync(
            connection,
            transaction,
            request,
            fingerprint,
            outcome,
            resultingState,
            resultingRevision,
            cancellationToken).ConfigureAwait(false);
        await InsertCommandHistoryAsync(
            connection,
            transaction,
            request.ScopeId,
            request.InstanceId,
            resultingRevision,
            outcome == "race_lost" ? "release_race_lost" : "release_already_terminal",
            request.CommandId.Value,
            eventId: null,
            request.ActorId,
            request.ReasonCode,
            outcome,
            nodeId,
            cancellationToken).ConfigureAwait(false);
        return new DurableFlowCommandResult(
            request.InstanceId,
            outcome == "race_lost" ? DurableFlowCommandOutcome.RaceLost : DurableFlowCommandOutcome.AlreadyTerminal,
            ParseFlowState(resultingState),
            resultingRevision);
    }

    private static ValueTask InsertCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableCommandId commandId,
        string commandType,
        DurableFlowEventId? eventId,
        byte[] fingerprint,
        string outcome,
        string resultingState,
        long resultingRevision,
        CancellationToken cancellationToken) =>
        InsertCommandCoreAsync(
            connection,
            transaction,
            scopeId,
            instanceId,
            commandId,
            commandType,
            eventId,
            actorId: null,
            reasonCode: null,
            fingerprint,
            outcome,
            resultingState,
            resultingRevision,
            cancellationToken);

    private static ValueTask InsertCancelCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowCancelRequest request,
        byte[] fingerprint,
        string outcome,
        string resultingState,
        long resultingRevision,
        CancellationToken cancellationToken) =>
        InsertCommandCoreAsync(
            connection,
            transaction,
            request.ScopeId,
            request.InstanceId,
            request.CommandId,
            "cancel",
            eventId: null,
            request.ActorId,
            request.ReasonCode,
            fingerprint,
            outcome,
            resultingState,
            resultingRevision,
            cancellationToken);

    private static ValueTask InsertReleaseCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowReleaseRequest request,
        byte[] fingerprint,
        string outcome,
        string resultingState,
        long resultingRevision,
        CancellationToken cancellationToken) =>
        InsertCommandCoreAsync(
            connection,
            transaction,
            request.ScopeId,
            request.InstanceId,
            request.CommandId,
            "release",
            eventId: null,
            request.ActorId,
            request.ReasonCode,
            fingerprint,
            outcome,
            resultingState,
            resultingRevision,
            cancellationToken);

    private static async ValueTask InsertCommandCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableCommandId commandId,
        string commandType,
        DurableFlowEventId? eventId,
        string? actorId,
        string? reasonCode,
        byte[] fingerprint,
        string outcome,
        string resultingState,
        long resultingRevision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_command
                (scope_id, flow_instance_id, command_id, command_type, command_schema_version,
                 event_id, actor_id, reason_code, request_sha256,
                 outcome, resulting_state, resulting_revision)
            VALUES
                (@scope_id, @flow_instance_id, @command_id, @command_type, @command_schema_version,
                 @event_id, @actor_id, @reason_code, @request_sha256,
                 @outcome, @resulting_state, @resulting_revision);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        command.Parameters.AddWithValue("command_type", commandType);
        command.Parameters.AddWithValue("command_schema_version", FlowCommandSchemaVersion);
        command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text)
        {
            Value = eventId is { } value ? value.Value : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("actor_id", NpgsqlDbType.Text)
        {
            Value = actorId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("reason_code", NpgsqlDbType.Text)
        {
            Value = reasonCode ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue("request_sha256", fingerprint);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("resulting_state", resultingState);
        command.Parameters.AddWithValue("resulting_revision", resultingRevision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ActiveWaitRow?> ReadActiveEventWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        string eventName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT wait_id, node_id, event_payload_required, event_contract_id, event_schema_version,
                   event_classification, event_retention_policy_id
            FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND wait_kind = 'external_event'
              AND state = 'active'
              AND event_name = @event_name
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("event_name", eventName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ActiveWaitRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6))
            : null;
    }

    private static bool MatchesEventContract(ActiveWaitRow wait, DurableEncodedPayload? payload)
    {
        if (!wait.PayloadRequired)
        {
            return payload is null;
        }

        return payload is not null &&
            string.Equals(payload.ContractName, wait.ContractName, StringComparison.Ordinal) &&
            string.Equals(payload.ContractVersion, wait.ContractVersion, StringComparison.Ordinal) &&
            string.Equals(FormatClassification(payload.Classification), wait.Classification, StringComparison.Ordinal) &&
            string.Equals(payload.RetentionPolicyId, wait.RetentionPolicyId, StringComparison.Ordinal);
    }

    private static async ValueTask ResolveEventWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowEventRequest request,
        ActiveWaitRow wait,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.dispatch
            SET state = 'terminal', updated_at = clock_timestamp()
            WHERE aggregate_kind = 'timer'
              AND aggregate_id IN
              (
                  SELECT timer_id::text
                  FROM appsurface_durable.flow_timer
                  WHERE wait_id = @wait_id AND state = 'scheduled'
              );

            UPDATE appsurface_durable.flow_timer
            SET state = 'superseded', resolved_at = clock_timestamp()
            WHERE wait_id = @wait_id AND state = 'scheduled';

            UPDATE appsurface_durable.flow_wait
            SET state = 'event_won',
                resolved_revision = @next_revision,
                resolved_at = clock_timestamp()
            WHERE wait_id = @wait_id AND state = 'active';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("wait_id", wait.WaitId);
        command.Parameters.AddWithValue("next_revision", nextRevision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SetFlowReadyFromEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowEventRequest request,
        long expectedRevision,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.flow_instance
            SET state = 'ready',
                resume_event_name = @event_name,
                resume_event_is_timeout = false,
                resume_event_contract_id = @event_contract_id,
                resume_event_schema_version = @event_schema_version,
                resume_event_codec_id = @event_codec_id,
                resume_event_payload = @event_payload,
                resume_event_sha256 = @event_sha256,
                resume_event_classification = @event_classification,
                resume_event_retention_policy_id = @event_retention_policy_id,
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND revision = @expected_revision;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("event_name", request.EventName);
        AddOptionalPayloadParameters(command, "event", request.Payload);
        command.Parameters.AddWithValue("next_revision", nextRevision);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new DBConcurrencyException("The Flow event lost its expected revision while holding the aggregate lock.");
        }
    }

    private static async ValueTask<string?> ReadActiveActivityWorkIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT activity_work_id
            FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND wait_kind = 'activity'
              AND state = 'active';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async ValueTask<bool> RequestActivityCancellationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        string workId,
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
            )
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
                revision = work.revision + 1,
                updated_at = clock_timestamp()
            FROM evidence
            WHERE work.scope_id = @scope_id
              AND work.work_id = @work_id
              AND work.state IN
              (
                  'pending',
                  'retry_wait',
                  'leased',
                  'reconciling',
                  'effect_permitted',
                  'cancel_pending',
                  'suspended_ambiguous_external_outcome',
                  'suspended_reconciliation_required',
                  'suspended_manual_resolution',
                  'suspended_contract_unavailable'
              )
            RETURNING work.state, work.revision, work.attempt_number, work.lease_generation,
                      work.scope_generation, work.runtime_epoch;
            """;
        string? state = null;
        long revision = 0;
        int attemptNumber = 0;
        long leaseGeneration = 0;
        long scopeGeneration = 0;
        Guid runtimeEpoch = default;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("work_id", workId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                state = reader.GetString(0);
                revision = reader.GetInt64(1);
                attemptNumber = reader.GetInt32(2);
                leaseGeneration = reader.GetInt64(3);
                scopeGeneration = reader.GetInt64(4);
                runtimeEpoch = reader.GetGuid(5);
            }
        }

        if (state is null)
        {
            return false;
        }

        const string dispatchSql = """
            UPDATE appsurface_durable.dispatch
            SET state = @dispatch_state,
                expected_revision = @revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND aggregate_kind = 'work'
              AND aggregate_id = @work_id;
            """;
        await using (var command = new NpgsqlCommand(dispatchSql, connection, transaction))
        {
            command.Parameters.AddWithValue(
                "dispatch_state",
                state switch
                {
                    "canceled_before_effect" => "terminal",
                    "reconciling" or
                    "suspended_ambiguous_external_outcome" or
                    "suspended_reconciliation_required" or
                    "suspended_manual_resolution" or
                    "suspended_contract_unavailable" => "suspended",
                    _ => "leased",
                });
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("work_id", workId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string historySql = """
            INSERT INTO appsurface_durable.work_history
                (scope_id, work_id, aggregate_revision, event_type, attempt_number, lease_generation,
                 scope_generation, runtime_epoch, details)
            VALUES
                (@scope_id, @work_id, @aggregate_revision, @event_type, @attempt_number, @lease_generation,
                 @scope_generation, @runtime_epoch, '{}'::jsonb);
            """;
        await using (var command = new NpgsqlCommand(historySql, connection, transaction))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("work_id", workId);
            command.Parameters.AddWithValue("aggregate_revision", revision);
            command.Parameters.AddWithValue(
                "event_type",
                state == "canceled_before_effect" ? "canceled_before_effect" : "cancel_pending");
            command.Parameters.AddWithValue("attempt_number", attemptNumber);
            command.Parameters.AddWithValue("lease_generation", leaseGeneration);
            command.Parameters.AddWithValue("scope_generation", scopeGeneration);
            command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return state == "canceled_before_effect";
    }

    private static async ValueTask LockActiveActivityWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT work.work_id
            FROM appsurface_durable.flow_wait AS wait
            JOIN appsurface_durable.work AS work
              ON work.scope_id = wait.scope_id
             AND work.work_id = wait.activity_work_id
            WHERE wait.scope_id = @scope_id
              AND wait.flow_instance_id = @flow_instance_id
              AND wait.wait_kind = 'activity'
              AND wait.state = 'active'
            FOR UPDATE OF work;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyFlowCancellationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowCancelRequest request,
        long expectedRevision,
        long nextRevision,
        string state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.flow_instance
            SET state = @state,
                suspended_from_state = NULL,
                suspended_from_terminal_code = NULL,
                cancellation_requested_at = COALESCE(cancellation_requested_at, clock_timestamp()),
                terminal_at = CASE WHEN @state = 'canceled' THEN clock_timestamp() ELSE terminal_at END,
                terminal_code = @reason_code,
                lease_owner = NULL,
                lease_started_at = NULL,
                lease_expires_at = NULL,
                revision = @next_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND revision = @expected_revision;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("reason_code", request.ReasonCode);
        command.Parameters.AddWithValue("next_revision", nextRevision);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new DBConcurrencyException("The Flow cancellation lost its expected revision while holding the aggregate lock.");
        }
    }

    private static async ValueTask CloseWaitsForCancellationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long nextRevision,
        string flowState,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.dispatch
            SET state = 'terminal', updated_at = clock_timestamp()
            WHERE aggregate_kind = 'timer'
              AND aggregate_id IN
              (
                  SELECT timer.timer_id::text
                  FROM appsurface_durable.flow_timer AS timer
                  JOIN appsurface_durable.flow_wait AS wait ON wait.wait_id = timer.wait_id
                  WHERE wait.scope_id = @scope_id
                    AND wait.flow_instance_id = @flow_instance_id
                    AND timer.state = 'scheduled'
              );

            UPDATE appsurface_durable.flow_timer AS timer
            SET state = 'canceled', resolved_at = clock_timestamp()
            FROM appsurface_durable.flow_wait AS wait
            WHERE wait.wait_id = timer.wait_id
              AND wait.scope_id = @scope_id
              AND wait.flow_instance_id = @flow_instance_id
              AND timer.state = 'scheduled';

            UPDATE appsurface_durable.flow_wait
            SET state = 'canceled',
                resolved_revision = @next_revision,
                resolved_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND state = 'active'
              AND (@flow_state = 'canceled' OR wait_kind <> 'activity');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("next_revision", nextRevision);
        command.Parameters.AddWithValue("flow_state", flowState);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RegisterEventWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowClaim claim,
        DurableFlowEvaluationResult transition,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        var waitId = Guid.NewGuid();
        DateTimeOffset? timeoutAt = null;
        const string waitSql = """
            INSERT INTO appsurface_durable.flow_wait
                (wait_id, scope_id, flow_instance_id, node_id, wait_kind, state,
                 event_name, event_payload_required, event_contract_id, event_schema_version,
                 event_classification, event_retention_policy_id,
                 timeout_at, registered_revision)
            VALUES
                (@wait_id, @scope_id, @flow_instance_id, @node_id, 'external_event', 'active',
                 @event_name, @event_payload_required, @event_contract_id, @event_schema_version,
                 @event_classification, @event_retention_policy_id,
                 CASE WHEN @has_timeout THEN clock_timestamp() + @timeout ELSE NULL END,
                 @registered_revision)
            RETURNING timeout_at;
            """;
        await using (var command = new NpgsqlCommand(waitSql, connection, transaction))
        {
            command.Parameters.AddWithValue("wait_id", waitId);
            command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
            command.Parameters.AddWithValue("node_id", transition.NodeId);
            command.Parameters.AddWithValue("event_name", transition.EventName!);
            command.Parameters.AddWithValue(
                "event_payload_required",
                transition.EventContract!.PayloadRequired);
            command.Parameters.Add(new NpgsqlParameter("event_contract_id", NpgsqlDbType.Text)
            {
                Value = transition.EventContract.ContractName ?? (object)DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("event_schema_version", NpgsqlDbType.Text)
            {
                Value = transition.EventContract.ContractVersion ?? (object)DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("event_classification", NpgsqlDbType.Text)
            {
                Value = transition.EventContract.Classification is { } classification
                    ? FormatClassification(classification)
                    : DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("event_retention_policy_id", NpgsqlDbType.Text)
            {
                Value = transition.EventContract.RetentionPolicyId ?? (object)DBNull.Value,
            });
            command.Parameters.AddWithValue("has_timeout", transition.Timeout is not null);
            command.Parameters.Add(new NpgsqlParameter("timeout", NpgsqlDbType.Interval)
            {
                Value = transition.Timeout is { } timeout ? timeout.Duration : DBNull.Value,
            });
            command.Parameters.AddWithValue("registered_revision", nextRevision);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is DateTime dateTime)
            {
                timeoutAt = new DateTimeOffset(dateTime, TimeSpan.Zero);
            }
        }

        if (timeoutAt is null)
        {
            return;
        }

        var timerId = Guid.NewGuid();
        var dispatchId = Guid.NewGuid();
        const string timerSql = """
            INSERT INTO appsurface_durable.flow_timer
                (timer_id, wait_id, scope_id, flow_instance_id, due_at, state, expected_flow_revision)
            VALUES
                (@timer_id, @wait_id, @scope_id, @flow_instance_id, @due_at, 'scheduled', @expected_revision);

            INSERT INTO appsurface_durable.dispatch
                (dispatch_id, scope_id, aggregate_kind, aggregate_id, due_at, state, expected_revision)
            VALUES
                (@dispatch_id, @scope_id, 'timer', @timer_aggregate_id, @due_at, 'available', @expected_revision);
            """;
        await using var timerCommand = new NpgsqlCommand(timerSql, connection, transaction);
        timerCommand.Parameters.AddWithValue("timer_id", timerId);
        timerCommand.Parameters.AddWithValue("wait_id", waitId);
        timerCommand.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        timerCommand.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
        timerCommand.Parameters.AddWithValue("due_at", timeoutAt.Value.UtcDateTime);
        timerCommand.Parameters.AddWithValue("expected_revision", nextRevision);
        timerCommand.Parameters.AddWithValue("dispatch_id", dispatchId);
        timerCommand.Parameters.AddWithValue("timer_aggregate_id", timerId.ToString("D", CultureInfo.InvariantCulture));
        await timerCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DurableWorkId> AcceptActivityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowClaim claim,
        DurableFlowActivityCommand activity,
        long transitionRevision,
        CancellationToken cancellationToken)
    {
        var activityIdentity = DurableFlowRequestFingerprint.CreateActivityIdentity(
            claim.ScopeId,
            claim.InstanceId,
            transitionRevision,
            activity.CallsiteId);
        var request = new DurableWorkRequest(
            claim.ScopeId,
            new DurableCommandId(activityIdentity),
            activityIdentity,
            activity.WorkName,
            activity.WorkVersion,
            activity.Work,
            activity.ProviderSafety);
        var accepted = await PostgreSqlDurableWorkStore.AcceptAsync(
            transaction,
            request,
            _runtimeEpoch,
            expectedStoreId: null,
            sendWakeNotification: _sendWakeNotification,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!accepted.IsSuccess || accepted.Value is null)
        {
            throw new InvalidOperationException(
                $"Flow activity '{activity.CallsiteId}' could not be atomically accepted as durable work.");
        }

        return accepted.Value.WorkId;
    }

    private static async ValueTask RegisterActivityWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowClaim claim,
        DurableFlowActivityCommand activity,
        DurableWorkId workId,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_wait
                (wait_id, scope_id, flow_instance_id, node_id, wait_kind, state,
                 activity_callsite_id, activity_work_id, result_contract_version, registered_revision)
            VALUES
                (@wait_id, @scope_id, @flow_instance_id, @node_id, 'activity', 'active',
                 @callsite_id, @work_id, @result_contract_version, @registered_revision);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("wait_id", Guid.NewGuid());
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
        command.Parameters.AddWithValue("node_id", claim.Input.NodeId);
        command.Parameters.AddWithValue("callsite_id", activity.CallsiteId);
        command.Parameters.AddWithValue("work_id", workId.Value);
        command.Parameters.AddWithValue("result_contract_version", activity.ResultContractVersion);
        command.Parameters.AddWithValue("registered_revision", nextRevision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Guid> UpsertFlowDispatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var dispatchId = Guid.NewGuid();
        const string sql = """
            INSERT INTO appsurface_durable.dispatch
                (dispatch_id, scope_id, aggregate_kind, aggregate_id, due_at, state, expected_revision)
            VALUES
                (@dispatch_id, @scope_id, 'flow', @flow_instance_id, clock_timestamp(), 'available', @expected_revision)
            ON CONFLICT (scope_id, aggregate_kind, aggregate_id)
            DO UPDATE SET due_at = EXCLUDED.due_at,
                          state = 'available',
                          expected_revision = EXCLUDED.expected_revision,
                          updated_at = clock_timestamp()
            RETURNING dispatch_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("dispatch_id", dispatchId);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async ValueTask SetFlowDispatchStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long expectedRevision,
        string state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.dispatch
            SET state = @state,
                expected_revision = @expected_revision,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id
              AND aggregate_kind = 'flow'
              AND aggregate_id = @flow_instance_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RestoreTimerDispatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long expectedRevision,
        bool restoreScheduledTimer,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.flow_timer
            SET expected_flow_revision = @expected_revision
            WHERE scope_id = @scope_id
              AND flow_instance_id = @flow_instance_id
              AND state = 'scheduled'
              AND @restore_scheduled_timer;

            UPDATE appsurface_durable.dispatch AS dispatch
            SET state = CASE WHEN @restore_scheduled_timer AND timer.state = 'scheduled'
                             THEN 'available'
                             ELSE 'terminal'
                        END,
                expected_revision = @expected_revision,
                updated_at = clock_timestamp()
            FROM appsurface_durable.flow_timer AS timer
            WHERE timer.scope_id = @scope_id
              AND timer.flow_instance_id = @flow_instance_id
              AND dispatch.scope_id = timer.scope_id
              AND dispatch.aggregate_kind = 'timer'
              AND dispatch.aggregate_id = timer.timer_id::text;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("restore_scheduled_timer", restoreScheduledTimer);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SetFlowDispatchLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowClaim claim,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.dispatch
            SET state = 'leased',
                due_at = clock_timestamp() + @lease_duration,
                expected_revision = @revision,
                updated_at = clock_timestamp()
            WHERE dispatch_id = @dispatch_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("lease_duration", EvaluationLeaseDuration);
        command.Parameters.AddWithValue("revision", claim.Revision);
        command.Parameters.AddWithValue("dispatch_id", claim.DispatchId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SetDispatchTerminalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid dispatchId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.dispatch
            SET state = 'terminal',
                expected_revision = @expected_revision,
                updated_at = clock_timestamp()
            WHERE dispatch_id = @dispatch_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        command.Parameters.AddWithValue("dispatch_id", dispatchId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SupersedeTimerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowDispatchCandidate candidate,
        Guid timerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.flow_timer
            SET state = 'superseded', resolved_at = clock_timestamp()
            WHERE timer_id = @timer_id AND state = 'scheduled';

            UPDATE appsurface_durable.dispatch
            SET state = 'terminal', updated_at = clock_timestamp()
            WHERE dispatch_id = @dispatch_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("timer_id", timerId);
        command.Parameters.AddWithValue("dispatch_id", candidate.DispatchId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long aggregateRevision,
        string eventType,
        string? commandId,
        string? nodeId,
        string detailsJson,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_history
                (scope_id, flow_instance_id, aggregate_revision, event_type,
                 authoring_model, command_schema_version, definition_fingerprint, command_id, node_id, details)
            SELECT
                @scope_id, @flow_instance_id, @aggregate_revision, @event_type,
                flow.authoring_model, flow.command_schema_version, flow.definition_fingerprint,
                @command_id, @node_id, @details
            FROM appsurface_durable.flow_instance AS flow
            WHERE flow.scope_id = @scope_id AND flow.flow_instance_id = @flow_instance_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("aggregate_revision", aggregateRevision);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.Add(new NpgsqlParameter("command_id", NpgsqlDbType.Text)
        {
            Value = commandId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("node_id", NpgsqlDbType.Text)
        {
            Value = nodeId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb) { Value = detailsJson });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertTransitionHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlFlowClaim claim,
        DurableFlowEvaluationResult transition,
        long aggregateRevision,
        string eventType,
        CancellationToken cancellationToken)
    {
        var transitionInput = claim.Input.ActivityResult ?? claim.Input.ResumeEventPayload;
        var transitionInputKind = claim.Input.ActivityResult is not null
            ? "activity_result"
            : claim.Input.IsTimeout
                ? "timeout"
                : claim.Input.ResumeEventName is not null
                    ? "external_event"
                    : "initial";
        var transitionInputName = claim.Input.ActivityCallsiteId ?? claim.Input.ResumeEventName;
        var transitionOutput = EncodeTransitionOutput(claim.AuthoringModel, transition);
        const string sql = """
            INSERT INTO appsurface_durable.flow_history
            (
                scope_id, flow_instance_id, aggregate_revision, event_type,
                authoring_model, command_schema_version, definition_fingerprint, node_id, transition_kind,
                input_context_contract_id, input_context_schema_version, input_context_codec_id,
                input_context_payload, input_context_sha256, input_context_classification,
                input_context_retention_policy_id,
                transition_input_kind, transition_input_name,
                transition_input_contract_id, transition_input_schema_version, transition_input_codec_id,
                transition_input_payload, transition_input_sha256, transition_input_classification,
                transition_input_retention_policy_id,
                output_context_contract_id, output_context_schema_version, output_context_codec_id,
                output_context_payload, output_context_sha256, output_context_classification,
                output_context_retention_policy_id,
                transition_output_contract_id, transition_output_schema_version, transition_output_codec_id,
                transition_output_payload, transition_output_sha256, transition_output_classification,
                transition_output_retention_policy_id,
                details
            )
            VALUES
            (
                @scope_id, @flow_instance_id, @aggregate_revision, @event_type,
                @authoring_model, @command_schema_version, @definition_fingerprint, @node_id, @transition_kind,
                @input_context_contract_id, @input_context_schema_version, @input_context_codec_id,
                @input_context_payload, @input_context_sha256, @input_context_classification,
                @input_context_retention_policy_id,
                @transition_input_kind, @transition_input_name,
                @transition_input_contract_id, @transition_input_schema_version, @transition_input_codec_id,
                @transition_input_payload, @transition_input_sha256, @transition_input_classification,
                @transition_input_retention_policy_id,
                @output_context_contract_id, @output_context_schema_version, @output_context_codec_id,
                @output_context_payload, @output_context_sha256, @output_context_classification,
                @output_context_retention_policy_id,
                @transition_output_contract_id, @transition_output_schema_version, @transition_output_codec_id,
                @transition_output_payload, @transition_output_sha256, @transition_output_classification,
                @transition_output_retention_policy_id,
                '{}'::jsonb
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", claim.InstanceId.Value);
        command.Parameters.AddWithValue("aggregate_revision", aggregateRevision);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("authoring_model", claim.AuthoringModel);
        command.Parameters.AddWithValue("command_schema_version", claim.CommandSchemaVersion);
        command.Parameters.AddWithValue("definition_fingerprint", claim.DefinitionFingerprint);
        command.Parameters.AddWithValue("node_id", transition.NodeId);
        command.Parameters.AddWithValue("transition_kind", FormatTransitionKind(transition.Kind));
        AddPayloadParameters(command, "input_context", claim.Input.Context);
        command.Parameters.AddWithValue("transition_input_kind", transitionInputKind);
        command.Parameters.Add(new NpgsqlParameter("transition_input_name", NpgsqlDbType.Text)
        {
            Value = transitionInputName ?? (object)DBNull.Value,
        });
        AddOptionalPayloadParameters(command, "transition_input", transitionInput);
        AddOptionalPayloadParameters(command, "output_context", transition.Context);
        AddPayloadParameters(command, "transition_output", transitionOutput);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertResumeHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long aggregateRevision,
        string eventType,
        string? nodeId,
        string transitionInputKind,
        string? transitionInputName,
        DurableEncodedPayload? transitionInput,
        string? commandId,
        string? commandEventId,
        string? commandOutcome,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_history
            (
                scope_id, flow_instance_id, aggregate_revision, event_type,
                authoring_model, command_schema_version, definition_fingerprint,
                command_id, command_event_id, command_outcome, node_id,
                transition_input_kind, transition_input_name,
                transition_input_contract_id, transition_input_schema_version, transition_input_codec_id,
                transition_input_payload, transition_input_sha256, transition_input_classification,
                transition_input_retention_policy_id,
                details
            )
            SELECT
                @scope_id, @flow_instance_id, @aggregate_revision, @event_type,
                flow.authoring_model, flow.command_schema_version, flow.definition_fingerprint,
                @command_id, @command_event_id, @command_outcome, @node_id,
                @transition_input_kind, @transition_input_name,
                @transition_input_contract_id, @transition_input_schema_version, @transition_input_codec_id,
                @transition_input_payload, @transition_input_sha256, @transition_input_classification,
                @transition_input_retention_policy_id,
                '{}'::jsonb
            FROM appsurface_durable.flow_instance AS flow
            WHERE flow.scope_id = @scope_id AND flow.flow_instance_id = @flow_instance_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("aggregate_revision", aggregateRevision);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.Add(new NpgsqlParameter("command_id", NpgsqlDbType.Text)
        {
            Value = commandId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("command_event_id", NpgsqlDbType.Text)
        {
            Value = commandEventId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("command_outcome", NpgsqlDbType.Text)
        {
            Value = commandOutcome ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("node_id", NpgsqlDbType.Text)
        {
            Value = nodeId ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue("transition_input_kind", transitionInputKind);
        command.Parameters.Add(new NpgsqlParameter("transition_input_name", NpgsqlDbType.Text)
        {
            Value = transitionInputName ?? (object)DBNull.Value,
        });
        AddOptionalPayloadParameters(command, "transition_input", transitionInput);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertCommandHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        long aggregateRevision,
        string eventType,
        string commandId,
        string? eventId,
        string? actorId,
        string? reasonCode,
        string outcome,
        string? nodeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_history
                (scope_id, flow_instance_id, aggregate_revision, event_type,
                 authoring_model, command_schema_version, definition_fingerprint, command_id,
                 command_event_id, actor_id, reason_code, command_outcome, node_id, details)
            SELECT
                @scope_id, @flow_instance_id, @aggregate_revision, @event_type,
                flow.authoring_model, flow.command_schema_version, flow.definition_fingerprint, @command_id,
                @event_id, @actor_id, @reason_code, @outcome, @node_id, '{}'::jsonb
            FROM appsurface_durable.flow_instance AS flow
            WHERE flow.scope_id = @scope_id AND flow.flow_instance_id = @flow_instance_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("aggregate_revision", aggregateRevision);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("command_id", commandId);
        command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text)
        {
            Value = eventId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("actor_id", NpgsqlDbType.Text)
        {
            Value = actorId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("reason_code", NpgsqlDbType.Text)
        {
            Value = reasonCode ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.Add(new NpgsqlParameter("node_id", NpgsqlDbType.Text)
        {
            Value = nodeId ?? (object)DBNull.Value,
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static void ValidateTransition(PostgreSqlFlowClaim claim, DurableFlowEvaluationResult transition)
    {
        if (!string.Equals(claim.Input.NodeId, transition.NodeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The evaluated Flow transition changed its source node identity.");
        }

        var valid = transition.Kind switch
        {
            FlowTransitionKind.Next => transition.Context is not null && transition.NextNodeId is not null &&
                transition.EventName is null && transition.EventContract is null &&
                transition.Fault is null && transition.Activity is null,
            FlowTransitionKind.Wait => transition.Context is not null && transition.NextNodeId is null &&
                transition.EventName is not null && transition.EventContract is not null &&
                transition.Fault is null && transition.Activity is null,
            FlowTransitionKind.TimedOut => transition.Context is not null && transition.EventName is not null &&
                transition.EventContract is null && transition.NextNodeId is null &&
                transition.Fault is null && transition.Activity is null,
            FlowTransitionKind.Complete => transition.Context is not null && transition.NextNodeId is null &&
                transition.EventName is null && transition.EventContract is null &&
                transition.Fault is null && transition.Activity is null,
            FlowTransitionKind.Fault => transition.Context is null && transition.NextNodeId is null &&
                transition.EventName is null && transition.EventContract is null &&
                transition.Fault is not null && transition.Activity is null,
            FlowTransitionKind.Activity => transition.Context is not null && transition.NextNodeId is null &&
                transition.EventName is null && transition.EventContract is null &&
                transition.Fault is null && transition.Activity is not null,
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException($"Flow transition '{transition.Kind}' contains inconsistent persisted fields.");
        }
    }

    private static string TransitionState(DurableFlowEvaluationResult transition) => transition.Kind switch
    {
        FlowTransitionKind.Next => "ready",
        FlowTransitionKind.Wait when transition.Timeout is not null => "waiting_timer",
        FlowTransitionKind.Wait => "waiting_event",
        FlowTransitionKind.TimedOut => "completed",
        FlowTransitionKind.Complete => "completed",
        FlowTransitionKind.Fault => "faulted",
        FlowTransitionKind.Activity => "waiting_activity",
        _ => throw new ArgumentOutOfRangeException(nameof(transition)),
    };

    private static string FormatTransitionKind(FlowTransitionKind kind) => kind switch
    {
        FlowTransitionKind.Next => "next",
        FlowTransitionKind.Wait => "wait",
        FlowTransitionKind.TimedOut => "timed_out",
        FlowTransitionKind.Complete => "complete",
        FlowTransitionKind.Fault => "fault",
        FlowTransitionKind.Activity => "activity",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static DurableEncodedPayload EncodeTransitionOutput(
        string authoringModel,
        DurableFlowEvaluationResult transition)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("authoring_model", authoringModel);
            writer.WriteString("kind", FormatTransitionKind(transition.Kind));
            writer.WriteString("node_id", transition.NodeId);
            if (transition.NextNodeId is { } nextNodeId)
            {
                writer.WriteString("next_node_id", nextNodeId);
            }

            if (transition.EventName is { } eventName)
            {
                writer.WriteString("event_name", eventName);
            }

            if (transition.Timeout is { } timeout)
            {
                writer.WriteNumber("timeout_ticks", timeout.Duration.Ticks);
            }

            if (transition.Fault?.Code is { } faultCode)
            {
                writer.WriteString("fault_code", faultCode);
            }

            if (transition.Activity?.CallsiteId is { } callsiteId)
            {
                writer.WriteString("activity_callsite_id", callsiteId);
            }

            writer.WriteEndObject();
        }

        return new DurableEncodedPayload(
            "appsurface.flow-transition-output",
            "v1",
            DurableDataClassification.Operational,
            stream.ToArray());
    }

    private static string TransitionTerminalCode(DurableFlowEvaluationResult transition) => transition.Kind switch
    {
        FlowTransitionKind.TimedOut => "timed_out",
        FlowTransitionKind.Complete => "completed",
        FlowTransitionKind.Fault => transition.Fault!.Code,
        _ => throw new ArgumentException("Only terminal Flow transitions have a terminal code.", nameof(transition)),
    };

    private static string TransitionHistoryEvent(FlowTransitionKind kind) => kind switch
    {
        FlowTransitionKind.Next => "transition_next",
        FlowTransitionKind.Wait => "wait_registered",
        FlowTransitionKind.TimedOut => "transition_timed_out",
        FlowTransitionKind.Complete => "completed",
        FlowTransitionKind.Fault => "faulted",
        FlowTransitionKind.Activity => "activity_registered",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool IsTerminal(string state) => state is "completed" or "faulted" or "canceled";

    private static bool IsDirectlyReleasableDormantState(string state) =>
        state is "ready" or "waiting_event" or "waiting_timer" or "waiting_activity" or "cancel_pending";

    private static DurableFlowState ParseFlowState(string state) => state switch
    {
        "ready" or "evaluating" => DurableFlowState.Ready,
        "waiting_event" => DurableFlowState.WaitingForEvent,
        "waiting_timer" => DurableFlowState.WaitingForTimer,
        "waiting_activity" => DurableFlowState.WaitingForActivity,
        "cancel_pending" => DurableFlowState.CancelPending,
        "completed" => DurableFlowState.Completed,
        "faulted" => DurableFlowState.Faulted,
        "canceled" => DurableFlowState.Canceled,
        "suspended" => DurableFlowState.Suspended,
        _ => throw new InvalidDataException($"Unknown persisted durable Flow state '{state}'."),
    };

    private static string FormatFlowStateFilter(DurableFlowState state) => state switch
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

    private static DurableFlowSnapshot ReadFlowSnapshot(NpgsqlDataReader reader) =>
        new(
            new DurableFlowInstanceId(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            ParseFlowState(reader.GetString(3)),
            reader.GetString(4),
            reader.GetInt64(5),
            ReadUtc(reader, 6),
            ReadUtc(reader, 7),
            reader.IsDBNull(8) ? null : ReadUtc(reader, 8),
            reader.IsDBNull(9) ? null : ReadUtc(reader, 9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetBoolean(11));

    private static string FormatClassification(DurableDataClassification classification) => classification switch
    {
        DurableDataClassification.Operational => "operational",
        DurableDataClassification.ApprovedApplication => "approved_application",
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    private static DurableDataClassification ParseClassification(string classification) => classification switch
    {
        "operational" => DurableDataClassification.Operational,
        "approved_application" => DurableDataClassification.ApprovedApplication,
        _ => throw new InvalidDataException($"Unknown persisted data classification '{classification}'."),
    };

    private static DurableEncodedPayload ReadPayload(
        NpgsqlDataReader reader,
        int contractOrdinal,
        int versionOrdinal,
        int classificationOrdinal,
        int retentionPolicyOrdinal,
        int payloadOrdinal,
        int shaOrdinal)
    {
        var payload = new DurableEncodedPayload(
            reader.GetString(contractOrdinal),
            reader.GetString(versionOrdinal),
            ParseClassification(reader.GetString(classificationOrdinal)),
            reader.GetFieldValue<byte[]>(payloadOrdinal),
            reader.GetString(retentionPolicyOrdinal));
        if (!reader.GetFieldValue<byte[]>(shaOrdinal).AsSpan().SequenceEqual(Convert.FromHexString(payload.Sha256)))
        {
            throw new InvalidDataException("The durable Flow payload hash does not match its authoritative bytes.");
        }

        return payload;
    }

    private static void AddPayloadParameters(
        NpgsqlCommand command,
        string prefix,
        DurableEncodedPayload payload)
    {
        command.Parameters.AddWithValue($"{prefix}_contract_id", payload.ContractName);
        command.Parameters.AddWithValue($"{prefix}_schema_version", payload.ContractVersion);
        command.Parameters.AddWithValue($"{prefix}_codec_id", $"{payload.ContractName}@{payload.ContractVersion}");
        command.Parameters.AddWithValue($"{prefix}_payload", payload.Content.ToArray());
        command.Parameters.AddWithValue($"{prefix}_sha256", Convert.FromHexString(payload.Sha256));
        command.Parameters.AddWithValue($"{prefix}_classification", FormatClassification(payload.Classification));
        command.Parameters.AddWithValue($"{prefix}_retention_policy_id", payload.RetentionPolicyId);
    }

    private static void AddOptionalPayloadParameters(
        NpgsqlCommand command,
        string prefix,
        DurableEncodedPayload? payload)
    {
        if (payload is not null)
        {
            AddPayloadParameters(command, prefix, payload);
            return;
        }

        command.Parameters.Add(new NpgsqlParameter($"{prefix}_contract_id", NpgsqlDbType.Text) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_schema_version", NpgsqlDbType.Text) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_codec_id", NpgsqlDbType.Text) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_payload", NpgsqlDbType.Bytea) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_sha256", NpgsqlDbType.Bytea) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_classification", NpgsqlDbType.Text) { Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter($"{prefix}_retention_policy_id", NpgsqlDbType.Text)
        {
            Value = DBNull.Value,
        });
    }

    private static NpgsqlConnection RequireActiveConnection(NpgsqlTransaction transaction)
    {
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The supplied Npgsql transaction is not active.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The supplied Npgsql transaction connection must be open.");
        }

        return connection;
    }

    private static async ValueTask ValidateSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT schema_version, minimum_reader_version, maximum_reader_version,
                   minimum_writer_version, maximum_writer_version
            FROM appsurface_durable.store_metadata
            WHERE singleton;
            """;
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            var required = PostgreSqlDurableRuntimeSchemaManager.RequiredVersion;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var hasMetadata = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var installed = hasMetadata ? reader.GetInt32(0) : 0;
            var minimumReader = hasMetadata ? reader.GetInt32(1) : 0;
            var maximumReader = hasMetadata ? reader.GetInt32(2) : 0;
            var minimumWriter = hasMetadata ? reader.GetInt32(3) : 0;
            var maximumWriter = hasMetadata ? reader.GetInt32(4) : 0;
            var compatible = hasMetadata &&
                installed >= required &&
                required >= minimumReader &&
                required <= maximumReader &&
                required >= minimumWriter &&
                required <= maximumWriter;
            if (!compatible)
            {
                throw new DurableRuntimeSchemaException(new DurableRuntimeSchemaStatus(
                    installed < required
                        ? DurableRuntimeSchemaCompatibility.UpgradeRequired
                        : DurableRuntimeSchemaCompatibility.StoreTooNew,
                    installed,
                    required,
                    minimumReader,
                    maximumReader,
                    minimumWriter,
                    maximumWriter,
                    [],
                    [],
                    "The durable Flow operation targets an incompatible PostgreSQL schema."));
            }
        }
        catch (PostgresException exception) when (exception.SqlState is
            PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            throw new DurableRuntimeSchemaException(new DurableRuntimeSchemaStatus(
                DurableRuntimeSchemaCompatibility.Missing,
                0,
                PostgreSqlDurableRuntimeSchemaManager.RequiredVersion,
                0,
                0,
                0,
                0,
                [],
                Enumerable.Range(1, PostgreSqlDurableRuntimeSchemaManager.RequiredVersion).ToArray(),
                "The durable PostgreSQL schema is not installed."));
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
            WHERE scope_id = @scope_id AND state = 'active'
            FOR SHARE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long generation
            ? generation
            : null;
    }

    private static async ValueTask<bool> LockActiveScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT generation
            FROM appsurface_durable.scope
            WHERE scope_id = @scope_id AND state = 'active'
            FOR SHARE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long;
    }

    private static async ValueTask<bool> LockScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT generation
            FROM appsurface_durable.scope
            WHERE scope_id = @scope_id
            FOR SHARE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long;
    }

    private static DurableOperationResult<DurableFlowCommandResult> MissingFlow(string correlationId) =>
        DurableOperationResult<DurableFlowCommandResult>.Failure(new DurableProblem(
            DurableProblemCodes.FlowNotFound,
            "The durable Flow instance was not found in the authorized scope.",
            "The instance does not exist or belongs to another scope.",
            "Verify the authorized scope and opaque instance id before retrying.",
            FlowDocumentation,
            correlationId));

    private static DurableOperationResult<DurableFlowCommandResult> ScopeDisabled(string correlationId) =>
        DurableOperationResult<DurableFlowCommandResult>.Failure(new DurableProblem(
            DurableProblemCodes.ScopeDisabled,
            "The durable Flow command was not accepted because its owning scope is disabled.",
            "The scope lifecycle is no longer active.",
            "Use a currently authorized active scope; do not bypass scope lifecycle policy.",
            ScopeDocumentation,
            correlationId));

    private static DurableOperationResult<DurableFlowCommandResult> ReleaseManifestMismatch(string correlationId) =>
        DurableOperationResult<DurableFlowCommandResult>.Failure(new DurableProblem(
            DurableProblemCodes.FlowReleaseManifestMismatch,
            "The durable Flow was not released because its runtime manifest is not compatible.",
            "The registered Flow definition, authoring model, or command schema differs from the persisted instance.",
            "Deploy the exact compatible Flow registration or a deliberate migration before retrying release.",
            FlowDocumentation,
            correlationId));

    private static DurableOperationResult<DurableFlowCommandResult> ReleaseStateMismatch(string correlationId) =>
        DurableOperationResult<DurableFlowCommandResult>.Failure(new DurableProblem(
            DurableProblemCodes.FlowReleaseStateMismatch,
            "The durable Flow cannot be safely released from its authoritative state.",
            "The instance is neither a recoverable suspension nor an exact-revision dormant nonterminal Flow from an older runtime epoch, or its active wait shape does not match that state.",
            "Reconcile the Flow state, wait, timer, and child-work truth before retrying the audited release.",
            FlowDocumentation,
            correlationId));

    private static string RequireBoundedText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        var result = value;
        if (result.Length > maximumLength)
        {
            throw new ArgumentException($"Value must not exceed {maximumLength} characters.", parameterName);
        }

        return result;
    }

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);
}

internal sealed record PostgreSqlFlowDispatchCandidate(
    Guid DispatchId,
    DurableScopeId ScopeId,
    string AggregateKind,
    string AggregateId,
    DateTimeOffset DueAtUtc,
    long ExpectedRevision);

internal enum PostgreSqlFlowProcessingOutcome
{
    Applied = 0,
    NotClaimed = 1,
    Stale = 2,
    RaceLost = 3,
    Failed = 4,
}

/// <summary>
/// Classifies terminal durable-work facts that cannot resume a Flow with a typed activity result.
/// </summary>
internal enum PostgreSqlFlowActivityFailureKind
{
    /// <summary>Retries were exhausted or the work encountered a terminal technical failure.</summary>
    FailedTerminal = 0,

    /// <summary>Cancellation was durably established before any external effect permit.</summary>
    CanceledBeforeEffect = 1,

    /// <summary>Provider truth is ambiguous or requires explicit reconciliation or operator repair.</summary>
    Suspended = 2,
}

internal sealed record PostgreSqlFlowProcessingResult(
    PostgreSqlFlowProcessingOutcome Outcome,
    DurableScopeId ScopeId,
    DurableFlowInstanceId InstanceId,
    DurableFlowState? State,
    long Revision,
    DurableWorkId? ActivityWorkId);

internal sealed record PostgreSqlFlowClaim(
    Guid DispatchId,
    DurableScopeId ScopeId,
    DurableFlowInstanceId InstanceId,
    string FlowId,
    string FlowVersion,
    DurableFlowEvaluationInput Input,
    long LeaseGeneration,
    long Revision,
    string AuthoringModel,
    byte[] DefinitionFingerprint,
    string CommandSchemaVersion);

internal sealed record CurrentFlowRow(
    string State,
    long Revision,
    string NodeId,
    Guid RuntimeEpoch,
    string? TerminalCode);

internal sealed record RecoverableSuspensionRow(
    string State,
    long Revision,
    string NodeId,
    string? SuspendedFromState,
    string FlowId,
    string FlowVersion,
    string AuthoringModel,
    string CommandSchemaVersion,
    byte[] DefinitionFingerprint,
    Guid RuntimeEpoch);

internal sealed record ActiveWaitRow(
    Guid WaitId,
    string NodeId,
    bool PayloadRequired,
    string? ContractName,
    string? ContractVersion,
    string? Classification,
    string? RetentionPolicyId);

internal sealed record ActivityWaitIdentity(DurableFlowInstanceId InstanceId, string CallsiteId);

internal sealed record LockedActivityWait(
    DurableFlowInstanceId InstanceId,
    string State,
    long Revision,
    Guid WaitId,
    string NodeId,
    string CallsiteId,
    Guid RuntimeEpoch,
    string? TerminalCode,
    string? SuspendedFromState);

internal sealed record StartInsertResult(
    DurableFlowCommandResult Result,
    Guid DispatchId,
    DateTimeOffset CreatedAtUtc);
