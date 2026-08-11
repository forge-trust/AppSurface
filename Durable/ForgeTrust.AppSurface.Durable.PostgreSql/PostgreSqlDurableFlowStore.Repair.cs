using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;
using NpgsqlTypes;
using static ForgeTrust.AppSurface.Durable.PostgreSql.PostgreSqlDurableProtocolCodec;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed partial class PostgreSqlDurableFlowStore
{
    internal async ValueTask<DurableOperationResult<DurableFlowRepairAssessment>> GetRepairAssessmentAsync(
        DurableFlowRepairAssessmentRequest request,
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
                SELECT flow.state, flow.revision, flow.suspension_descriptor_schema, flow.suspension_descriptor_sha256,
                       flow.suspended_from_state, flow.suspension_descriptor ->> 'code',
                       flow.suspension_descriptor ->> 'source', flow.suspension_descriptor ->> 'work_state',
                       wait.wait_id, wait.state, wait.callsite_id, wait.child_work_id,
                       wait.result_contract_id, wait.result_schema_version, wait.result_codec_id,
                       wait.result_classification, wait.result_retention,
                       work.revision, work.state, work.lease_owner,
                       work.result_contract_id, work.result_schema_version, work.result_codec_id,
                       work.result_classification, work.result_retention_policy_id, work.result_sha256,
                       completed.event_id, not_applied.event_id, not_applied.command_id
                FROM appsurface_durable.flow_instance AS flow
                LEFT JOIN appsurface_durable.flow_wait AS wait
                  ON wait.scope_id = flow.scope_id
                 AND wait.flow_instance_id = flow.flow_instance_id
                 AND wait.kind = 'activity'
                 AND wait.state = 'suspended'
                LEFT JOIN appsurface_durable.work AS work
                  ON work.scope_id = wait.scope_id AND work.work_id = wait.child_work_id
                LEFT JOIN LATERAL
                (
                    SELECT history.event_id
                    FROM appsurface_durable.work_history AS history
                    LEFT JOIN appsurface_durable.work_operator_command AS command
                      ON command.scope_id = history.scope_id
                     AND command.work_id = history.work_id
                     AND command.command_id = history.command_id
                    WHERE history.scope_id = work.scope_id
                      AND history.work_id = work.work_id
                      AND history.aggregate_revision = work.revision
                      AND
                      (
                          history.event_type = 'completion_succeeded'
                          OR
                          (
                              history.event_type = 'operator_manual_resolve'
                              AND command.command_type = 'manual_resolve'
                              AND command.status = 'completed'
                              AND command.resolution_kind = 'applied'
                              AND history.details ->> 'resolution_kind' = 'applied'
                          )
                      )
                    ORDER BY history.event_id DESC
                    LIMIT 1
                ) AS completed ON true
                LEFT JOIN LATERAL
                (
                    SELECT history.event_id, command.command_id
                    FROM appsurface_durable.work_operator_command AS command
                    JOIN appsurface_durable.work_history AS history
                      ON history.scope_id = command.scope_id
                     AND history.work_id = command.work_id
                     AND history.command_id = command.command_id
                    WHERE command.scope_id = work.scope_id
                      AND command.work_id = work.work_id
                      AND command.command_type = 'manual_resolve'
                      AND command.status = 'completed'
                      AND command.resolution_kind = 'proven_not_applied'
                      AND history.event_type = 'operator_manual_resolve'
                      AND history.details ->> 'resolution_kind' = 'proven_not_applied'
                      AND history.aggregate_revision = work.revision
                    ORDER BY history.event_id DESC
                    LIMIT 1
                ) AS not_applied ON true
                WHERE flow.scope_id = @scope_id AND flow.flow_instance_id = @flow_instance_id;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableFlowRepairAssessment>(
                    request.InstanceId.Value,
                    DurableProblemCodes.FlowNotFound,
                    "The authorized scope does not contain the requested Flow instance.",
                    "No Flow row matched the trusted scope and instance identity.",
                    "Reload the authorized Flow inventory before requesting repair assessment.");
            }

            var state = ParseState(reader.GetString(0));
            var revision = reader.GetInt64(1);
            var descriptorSchema = reader.IsDBNull(2) ? null : reader.GetString(2);
            var descriptorSha256 = reader.IsDBNull(3) ? null : reader.GetString(3);
            var hasCompleteChildWorkIdentity = !reader.IsDBNull(8)
                && !reader.IsDBNull(11)
                && !reader.IsDBNull(17);
            var waitId = hasCompleteChildWorkIdentity ? reader.GetGuid(8) : (Guid?)null;
            var childWorkId = hasCompleteChildWorkIdentity
                ? new DurableWorkId(reader.GetString(11))
                : (DurableWorkId?)null;
            var childWorkRevision = hasCompleteChildWorkIdentity ? reader.GetInt64(17) : (long?)null;
            var candidates = new List<DurableFlowRepairCandidate>(2);
            var repairableShape = state == DurableFlowState.Suspended
                && string.Equals(reader.IsDBNull(4) ? null : reader.GetString(4), "waiting_activity", StringComparison.Ordinal)
                && string.Equals(descriptorSchema, "appsurface.durable.flow.child-suspension.v1", StringComparison.Ordinal)
                && descriptorSha256 is not null
                && hasCompleteChildWorkIdentity
                && string.Equals(reader.IsDBNull(9) ? null : reader.GetString(9), "suspended", StringComparison.Ordinal)
                && string.Equals(reader.IsDBNull(5) ? null : reader.GetString(5), "flow.child_work_requires_attention", StringComparison.Ordinal)
                && string.Equals(reader.IsDBNull(6) ? null : reader.GetString(6), "child_work", StringComparison.Ordinal);
            if (repairableShape
                && childWorkId is { } assessedChildWorkId
                && childWorkRevision is { } assessedChildWorkRevision)
            {
                var workState = reader.IsDBNull(18) ? null : reader.GetString(18);
                var expectedMetadataMatches = !reader.IsDBNull(12)
                    && !reader.IsDBNull(13)
                    && !reader.IsDBNull(14)
                    && !reader.IsDBNull(15)
                    && !reader.IsDBNull(16)
                    && !reader.IsDBNull(20)
                    && !reader.IsDBNull(21)
                    && !reader.IsDBNull(22)
                    && !reader.IsDBNull(23)
                    && !reader.IsDBNull(24)
                    && string.Equals(reader.GetString(12), reader.GetString(20), StringComparison.Ordinal)
                    && string.Equals(reader.GetString(13), reader.GetString(21), StringComparison.Ordinal)
                    && string.Equals(reader.GetString(14), reader.GetString(22), StringComparison.Ordinal)
                    && string.Equals(reader.GetString(15), reader.GetString(23), StringComparison.Ordinal)
                    && string.Equals(reader.GetString(16), reader.GetString(24), StringComparison.Ordinal);
                if (workState is "succeeded" or "succeeded_after_cancel_requested"
                    && expectedMetadataMatches
                    && !reader.IsDBNull(25)
                    && !reader.IsDBNull(26))
                {
                    candidates.Add(new DurableFlowRepairCandidate(
                        DurableFlowRepairAction.AssertChildEffectCompleted,
                        DurableFlowRepairEvidenceReference.Completed(
                            assessedChildWorkId,
                            assessedChildWorkRevision,
                            reader.GetInt64(26),
                            Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(25)))));
                }

                if (workState == "retry_wait"
                    && reader.IsDBNull(19)
                    && !reader.IsDBNull(27)
                    && !reader.IsDBNull(28))
                {
                    candidates.Add(new DurableFlowRepairCandidate(
                        DurableFlowRepairAction.AssertChildEffectNotApplied,
                        DurableFlowRepairEvidenceReference.ProvenNotApplied(
                            assessedChildWorkId,
                            assessedChildWorkRevision,
                            reader.GetInt64(27),
                            new DurableCommandId(reader.GetString(28)))));
                }
            }

            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowRepairAssessment>.Success(new DurableFlowRepairAssessment(
                request.InstanceId,
                state,
                revision,
                descriptorSchema,
                descriptorSha256,
                waitId,
                childWorkId,
                childWorkRevision,
                candidates));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<DurableOperationResult<DurableFlowRepairResult>> RepairAsync(
        DurableFlowRepairRequest request,
        IDurableWorkRegistry workRegistry,
        CancellationToken cancellationToken,
        bool retryAfterUniqueViolation = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workRegistry);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopeGeneration = await ValidateStoreSetScopeAndLockActiveScopeAsync(
                connection,
                transaction,
                request.ScopeId,
                createIfMissing: false,
                cancellationToken).ConfigureAwait(false);
            if (scopeGeneration is null)
            {
                return await CommitFailureAsync(
                    transaction,
                    Failure<DurableFlowRepairResult>(
                        request.CommandId.Value,
                        DurableProblemCodes.ScopeDisabled,
                        "The Flow repair was rejected because its owning scope is disabled.",
                        "The scope lifecycle changed before the repair could lock retained evidence.",
                        "Use an authorized active scope; do not bypass durable scope lifecycle policy."),
                    cancellationToken).ConfigureAwait(false);
            }

            var existing = await ReadRepairCommandAsync(
                connection, transaction, request.ScopeId, request.CommandId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (request.Fingerprint.Compare(existing.RequestFingerprint) == DurableCommandFingerprintMatch.Exact)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return DurableOperationResult<DurableFlowRepairResult>.Success(existing.ToResult());
                }

                await RecordRepairCollisionAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowRepairResult>.Success(new DurableFlowRepairResult(
                    DurableFlowRepairOutcome.Conflict,
                    null,
                    CreateRepairProblem(
                        request.CommandId.Value,
                        DurableProblemCodes.FlowCommandConflict,
                        "The Flow repair command identity was reused with different semantic input.",
                        "The prior command has a different V1 request schema or digest.",
                        "Retry only the exact original repair request or choose a new command identity."),
                    existing.ObservedState,
                    existing.ObservedRevision));
            }

            // Work completion holds the child Work row before projecting its parent Flow and wait. Keep repair in
            // that order so a retrying child cannot deadlock with its evidence-backed repair.
            var work = await LockRepairWorkAsync(
                connection,
                transaction,
                request.ScopeId,
                request.Evidence.ChildWorkId,
                cancellationToken).ConfigureAwait(false);
            var flow = await LockRepairFlowAsync(
                connection, transaction, request.ScopeId, request.InstanceId, cancellationToken).ConfigureAwait(false);
            if (flow is null)
            {
                return await CommitRepairTerminalAsync(
                    connection,
                    transaction,
                    request,
                    DurableFlowRepairOutcome.Refused,
                    null,
                    null,
                    CreateRepairProblem(
                        request.CommandId.Value,
                        DurableProblemCodes.FlowNotFound,
                        "The authorized scope does not contain the requested Flow instance.",
                        "No Flow row matched the trusted scope and instance identity.",
                        "Reload the authorized Flow inventory before retrying."),
                    cancellationToken).ConfigureAwait(false);
            }

            if (flow.Revision != request.ExpectedFlowRevision)
            {
                return await CommitRepairTerminalAsync(
                    connection,
                    transaction,
                    request,
                    DurableFlowRepairOutcome.RaceLost,
                    flow.PublicState,
                    flow.Revision,
                    CreateRepairProblem(
                        request.CommandId.Value,
                        DurableProblemCodes.FlowRaceLost,
                        "The Flow changed before the repair assertion could be applied.",
                        "The caller's expected Flow revision is stale.",
                        "Reload the scoped assessment and submit a new evidence-backed repair request."),
                    cancellationToken).ConfigureAwait(false);
            }

            var wait = await LockRepairWaitAsync(
                connection,
                transaction,
                request.ScopeId,
                request.InstanceId,
                request.Evidence.ChildWorkId,
                cancellationToken).ConfigureAwait(false);
            var descriptorProblem = ValidateRepairShape(request, flow, wait, work);
            if (descriptorProblem is not null)
            {
                return await CommitRepairTerminalAsync(
                    connection,
                    transaction,
                    request,
                    DurableFlowRepairOutcome.Refused,
                    flow.PublicState,
                    flow.Revision,
                    descriptorProblem,
                    cancellationToken).ConfigureAwait(false);
            }

            var acceptedAtUtc = await ReadDatabaseClockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (request.Action == DurableFlowRepairAction.AssertChildEffectCompleted)
            {
                var problem = await ValidateCompletedEvidenceAsync(
                    connection,
                    transaction,
                    request,
                    wait!,
                    work!,
                    workRegistry,
                    cancellationToken).ConfigureAwait(false);
                if (problem is not null)
                {
                    return await CommitRepairTerminalAsync(
                        connection,
                        transaction,
                        request,
                        DurableFlowRepairOutcome.Refused,
                        flow.PublicState,
                        flow.Revision,
                        problem,
                        cancellationToken).ConfigureAwait(false);
                }

                var result = work!.ToEncodedResult();
                var resultingRevision = checked(flow.Revision + 1);
                var historyEventId = await ApplyCompletedRepairAsync(
                    connection,
                    transaction,
                    request,
                    flow,
                    wait!,
                    result,
                    resultingRevision,
                    cancellationToken).ConfigureAwait(false);
                var receipt = new DurableFlowRepairReceipt(
                    request.ScopeId,
                    request.InstanceId,
                    request.CommandId,
                    request.Action,
                    request.Fingerprint,
                    flow.SuspensionDescriptorSha256!,
                    request.Evidence,
                    request.ActorId,
                    request.ReasonCode,
                    flow.PublicState,
                    flow.Revision,
                    DurableFlowState.Ready,
                    resultingRevision,
                    historyEventId,
                    acceptedAtUtc);
                await InsertRepairCommandAsync(
                    connection,
                    transaction,
                    request,
                    DurableFlowRepairOutcome.Applied,
                    flow.PublicState,
                    flow.Revision,
                    DurableFlowState.Ready,
                    resultingRevision,
                    historyEventId,
                    receipt,
                    problem: null,
                    acceptedAtUtc,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DurableOperationResult<DurableFlowRepairResult>.Success(new DurableFlowRepairResult(
                    DurableFlowRepairOutcome.Applied,
                    receipt,
                    null,
                    DurableFlowState.Ready,
                    resultingRevision));
            }

            var noEffectProblem = await ValidateNotAppliedEvidenceAsync(
                connection,
                transaction,
                request,
                work!,
                cancellationToken).ConfigureAwait(false);
            if (noEffectProblem is not null)
            {
                return await CommitRepairTerminalAsync(
                    connection,
                    transaction,
                    request,
                    DurableFlowRepairOutcome.Refused,
                    flow.PublicState,
                    flow.Revision,
                    noEffectProblem,
                    cancellationToken).ConfigureAwait(false);
            }

            var retryRevision = checked(flow.Revision + 1);
            var retryHistoryEventId = await ApplyNotAppliedRepairAsync(
                connection,
                transaction,
                request,
                flow,
                wait!,
                retryRevision,
                cancellationToken).ConfigureAwait(false);
            var retryReceipt = new DurableFlowRepairReceipt(
                request.ScopeId,
                request.InstanceId,
                request.CommandId,
                request.Action,
                request.Fingerprint,
                flow.SuspensionDescriptorSha256!,
                request.Evidence,
                request.ActorId,
                request.ReasonCode,
                flow.PublicState,
                flow.Revision,
                DurableFlowState.WaitingForActivity,
                retryRevision,
                retryHistoryEventId,
                acceptedAtUtc);
            await InsertRepairCommandAsync(
                connection,
                transaction,
                request,
                DurableFlowRepairOutcome.Applied,
                flow.PublicState,
                flow.Revision,
                DurableFlowState.WaitingForActivity,
                retryRevision,
                retryHistoryEventId,
                retryReceipt,
                problem: null,
                acceptedAtUtc,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableFlowRepairResult>.Success(new DurableFlowRepairResult(
                DurableFlowRepairOutcome.Applied,
                retryReceipt,
                null,
                DurableFlowState.WaitingForActivity,
                retryRevision));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && retryAfterUniqueViolation)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return await RepairAsync(request, workRegistry, cancellationToken, retryAfterUniqueViolation: false).ConfigureAwait(false);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<DurableOperationResult<DurableFlowRepairResult>> CommitRepairTerminalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowRepairRequest request,
        DurableFlowRepairOutcome outcome,
        DurableFlowState? observedState,
        long? observedRevision,
        DurableProblem problem,
        CancellationToken cancellationToken)
    {
        await InsertRepairCommandAsync(
            connection,
            transaction,
            request,
            outcome,
            observedState,
            observedRevision,
            resultingState: null,
            resultingRevision: null,
            resultingHistoryEventId: null,
            receipt: null,
            problem,
            acceptedAtUtc: await ReadDatabaseClockAsync(connection, transaction, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return DurableOperationResult<DurableFlowRepairResult>.Success(new DurableFlowRepairResult(
            outcome,
            null,
            problem,
            observedState,
            observedRevision));
    }

    private static DurableProblem? ValidateRepairShape(
        DurableFlowRepairRequest request,
        RepairFlowRow flow,
        RepairWaitRow? wait,
        RepairWorkRow? work)
    {
        if (flow.State != "suspended" || flow.SuspendedFromState != "waiting_activity" || wait is null || wait.State != "suspended")
        {
            return CreateRepairProblem(
                request.CommandId.Value,
                DurableProblemCodes.FlowRepairActionUnsupported,
                "The Flow suspension is outside the closed child-effect repair state matrix.",
                "The Flow is not a suspended waiting-activity instance with one suspended linked activity wait.",
                "Use the current repair assessment and choose only a candidate offered for this suspended Flow.");
        }

        if (work is null || wait.ChildWorkId != request.Evidence.ChildWorkId.Value)
        {
            return CreateRepairProblem(
                request.CommandId.Value,
                DurableProblemCodes.FlowRepairEvidenceMismatch,
                "The requested child Work is not the Flow's locked suspended activity child.",
                "The retained wait and evidence reference do not name the same scoped Work aggregate.",
                "Reload the scoped repair assessment and use its child Work evidence.");
        }

        if (!string.Equals(flow.SuspensionDescriptorSchema, PostgreSqlDurableFlowRepairDescriptor.SchemaId, StringComparison.Ordinal)
            || flow.SuspensionDescriptorSha256 is null)
        {
            return CreateRepairProblem(
                request.CommandId.Value,
                DurableProblemCodes.FlowRepairDescriptorUpgradeRequired,
                "The suspended Flow has no supported V1 repair descriptor identity.",
                "The suspension predates the closed child-effect descriptor grammar or was written by an incompatible runtime.",
                "Upgrade all writers, then obtain a fresh assessment; do not use legacy release as a repair fallback.");
        }

        var recomputed = PostgreSqlDurableFlowRepairDescriptor.CreateDigest(
            flow.SuspendedFromState!,
            flow.DescriptorCode,
            flow.DescriptorSource,
            flow.DescriptorWorkState,
            wait.WaitId,
            request.Evidence.ChildWorkId);
        if (!string.Equals(flow.SuspensionDescriptorSha256, recomputed, StringComparison.Ordinal)
            || !string.Equals(request.ExpectedSuspensionDescriptorSha256, recomputed, StringComparison.Ordinal))
        {
            return CreateRepairProblem(
                request.CommandId.Value,
                DurableProblemCodes.FlowRepairEvidenceMismatch,
                "The Flow repair descriptor digest does not match locked persisted suspension truth.",
                "A stale request, mixed writer, or altered descriptor cannot be accepted as repair evidence.",
                "Reload the assessment after all compatible writers are deployed and submit its current digest.");
        }

        return null;
    }

    private static async ValueTask<DurableProblem?> ValidateCompletedEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowRepairRequest request,
        RepairWaitRow wait,
        RepairWorkRow work,
        IDurableWorkRegistry workRegistry,
        CancellationToken cancellationToken)
    {
        if (work.Revision != request.Evidence.ExpectedChildWorkRevision
            || work.State is not ("succeeded" or "succeeded_after_cancel_requested")
            || work.Result is null
            || !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(work.Result.Content)),
                work.Result.Sha256,
                StringComparison.Ordinal)
            || !string.Equals(work.Result.Sha256, request.Evidence.ExpectedChildResultSha256, StringComparison.Ordinal)
            || !wait.ResultMatches(work.Result))
        {
            return EvidenceMismatch(request.CommandId.Value);
        }

        if (!await HasCompletedWorkEvidenceAsync(
                connection,
                transaction,
                request.ScopeId,
                request.Evidence.ChildWorkId,
                request.Evidence.ChildWorkHistoryEventId,
                work.Revision,
                cancellationToken).ConfigureAwait(false))
        {
            return EvidenceMismatch(request.CommandId.Value);
        }

        try
        {
            var registration = workRegistry.GetRequired(work.WorkName, work.WorkVersion);
            if (!string.Equals(registration.ResultCodec.ContractName, work.Result.ContractName, StringComparison.Ordinal)
                || !string.Equals(registration.ResultCodec.ContractVersion, work.Result.ContractVersion, StringComparison.Ordinal))
            {
                return EvidenceMismatch(request.CommandId.Value);
            }

            _ = registration.ResultCodec.DecodeObject(work.Result.ToPayload());
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or JsonException)
        {
            return EvidenceMismatch(request.CommandId.Value);
        }
    }

    private static async ValueTask<DurableProblem?> ValidateNotAppliedEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowRepairRequest request,
        RepairWorkRow work,
        CancellationToken cancellationToken)
    {
        if (work.Revision != request.Evidence.ExpectedChildWorkRevision
            || work.State != "retry_wait"
            || work.LeaseOwner is not null
            || request.Evidence.RequiredWorkOperatorCommandId is not { } operatorCommandId)
        {
            return EvidenceMismatch(request.CommandId.Value);
        }

        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM appsurface_durable.work_operator_command AS command
                JOIN appsurface_durable.work_history AS history
                  ON history.scope_id = command.scope_id
                 AND history.work_id = command.work_id
                 AND history.command_id = command.command_id
                WHERE command.scope_id = @scope_id
                  AND command.work_id = @work_id
                  AND command.command_id = @command_id
                  AND command.command_type = 'manual_resolve'
                  AND command.status = 'completed'
                  AND command.resolution_kind = 'proven_not_applied'
                  AND history.event_id = @history_event_id
                  AND history.aggregate_revision = @revision
                  AND history.event_type = 'operator_manual_resolve'
                  AND history.details ->> 'resolution_kind' = 'proven_not_applied'
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", request.Evidence.ChildWorkId.Value);
        command.Parameters.AddWithValue("command_id", operatorCommandId.Value);
        command.Parameters.AddWithValue("history_event_id", request.Evidence.ChildWorkHistoryEventId);
        command.Parameters.AddWithValue("revision", work.Revision);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true
            ? null
            : EvidenceMismatch(request.CommandId.Value);
    }

    private static async ValueTask<bool> HasCompletedWorkEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId workId,
        long eventId,
        long revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM appsurface_durable.work_history AS history
                LEFT JOIN appsurface_durable.work_operator_command AS command
                  ON command.scope_id = history.scope_id
                 AND command.work_id = history.work_id
                 AND command.command_id = history.command_id
                WHERE history.scope_id = @scope_id
                  AND history.work_id = @work_id
                  AND history.event_id = @event_id
                  AND history.aggregate_revision = @revision
                  AND
                  (
                      history.event_type = 'completion_succeeded'
                      OR
                      (
                          history.event_type = 'operator_manual_resolve'
                          AND command.command_type = 'manual_resolve'
                          AND command.status = 'completed'
                          AND command.resolution_kind = 'applied'
                          AND history.details ->> 'resolution_kind' = 'applied'
                      )
                  )
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("revision", revision);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async ValueTask<long> ApplyCompletedRepairAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowRepairRequest request,
        RepairFlowRow flow,
        RepairWaitRow wait,
        DurableEncodedPayload result,
        long revision,
        CancellationToken cancellationToken)
    {
        const string flowSql = """
            UPDATE appsurface_durable.flow_instance
            SET state = 'ready', revision = @revision,
                activity_callsite_id = @callsite_id,
                activity_result_contract_id = @activity_result_contract_id,
                activity_result_schema_version = @activity_result_schema_version,
                activity_result_codec_id = @activity_result_codec_id,
                activity_result_payload = @activity_result_payload,
                activity_result_sha256 = @activity_result_sha256,
                activity_result_classification = @activity_result_classification,
                activity_result_retention = @activity_result_retention,
                suspension_descriptor = NULL, suspended_from_state = NULL,
                suspension_descriptor_schema = NULL, suspension_descriptor_sha256 = NULL,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND revision = @prior_revision;
            """;
        await using (var command = new NpgsqlCommand(flowSql, connection, transaction))
        {
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
            command.Parameters.AddWithValue("prior_revision", flow.Revision);
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("callsite_id", wait.CallsiteId);
            AddPayloadParameters(command, "activity_result", result);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The Flow repair lost its locked completed-effect revision fence.");
            }
        }

        const string waitSql = """
            UPDATE appsurface_durable.flow_wait
            SET state = 'activity_completed', resolved_revision = @revision, resolved_at = clock_timestamp(),
                suspension_descriptor = NULL, updated_at = clock_timestamp()
            WHERE wait_id = @wait_id AND scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND state = 'suspended';
            """;
        await using (var command = new NpgsqlCommand(waitSql, connection, transaction))
        {
            command.Parameters.AddWithValue("wait_id", wait.WaitId);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
            command.Parameters.AddWithValue("revision", revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The Flow repair could not resolve exactly one locked activity wait.");
            }
        }

        await UpdateRepairDispatchAsync(connection, transaction, request, revision, "available", cancellationToken).ConfigureAwait(false);
        return await InsertRepairHistoryAsync(
            connection,
            transaction,
            request,
            revision,
            wait.CallsiteId,
            "activity_repair_completed",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ApplyNotAppliedRepairAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowRepairRequest request,
        RepairFlowRow flow,
        RepairWaitRow wait,
        long revision,
        CancellationToken cancellationToken)
    {
        const string flowSql = """
            UPDATE appsurface_durable.flow_instance
            SET state = 'waiting_activity', revision = @revision,
                suspension_descriptor = NULL, suspended_from_state = NULL,
                suspension_descriptor_schema = NULL, suspension_descriptor_sha256 = NULL,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND revision = @prior_revision;
            """;
        await using (var command = new NpgsqlCommand(flowSql, connection, transaction))
        {
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
            command.Parameters.AddWithValue("prior_revision", flow.Revision);
            command.Parameters.AddWithValue("revision", revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The Flow repair lost its locked no-effect revision fence.");
            }
        }

        const string waitSql = """
            UPDATE appsurface_durable.flow_wait
            SET state = 'active', resolved_revision = NULL, resolved_at = NULL,
                suspension_descriptor = NULL, updated_at = clock_timestamp()
            WHERE wait_id = @wait_id AND scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND state = 'suspended';
            """;
        await using (var command = new NpgsqlCommand(waitSql, connection, transaction))
        {
            command.Parameters.AddWithValue("wait_id", wait.WaitId);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The Flow repair could not restore exactly one locked activity wait.");
            }
        }

        await UpdateRepairDispatchAsync(connection, transaction, request, revision, "suspended", cancellationToken).ConfigureAwait(false);
        return await InsertRepairHistoryAsync(
            connection,
            transaction,
            request,
            revision,
            wait.CallsiteId,
            "activity_repair_not_applied",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateRepairDispatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowRepairRequest request,
        long revision,
        string state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appsurface_durable.flow_dispatch
            SET state = @state, expected_revision = @revision, updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'flow';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("state", state);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Flow repair could not project its unique Flow dispatch row.");
        }
    }

    private static async ValueTask<long> InsertRepairHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowRepairRequest request,
        long revision,
        string callsiteId,
        string transitionKind,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_history
                (scope_id, flow_instance_id, aggregate_revision, command_id, node_id, transition_kind, details)
            VALUES
                (@scope_id, @flow_instance_id, @revision, @command_id, @node_id, @transition_kind,
                 jsonb_build_object('repair_action', @action, 'child_work_id', @child_work_id,
                                    'child_work_history_event_id', @child_work_history_event_id))
            RETURNING event_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("node_id", callsiteId);
        command.Parameters.AddWithValue("transition_kind", transitionKind);
        command.Parameters.AddWithValue("action", FormatRepairAction(request.Action));
        command.Parameters.AddWithValue("child_work_id", request.Evidence.ChildWorkId.Value);
        command.Parameters.AddWithValue("child_work_history_event_id", request.Evidence.ChildWorkHistoryEventId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long eventId
            ? eventId
            : throw new InvalidOperationException("The Flow repair did not append one history event.");
    }

    private static async ValueTask InsertRepairCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowRepairRequest request,
        DurableFlowRepairOutcome outcome,
        DurableFlowState? observedState,
        long? observedRevision,
        DurableFlowState? resultingState,
        long? resultingRevision,
        long? resultingHistoryEventId,
        DurableFlowRepairReceipt? receipt,
        DurableProblem? problem,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_repair_command
            (
                scope_id, command_id, flow_instance_id, action, request_schema, request_sha256,
                expected_flow_revision, observed_flow_state, observed_flow_revision, suspension_descriptor_sha256,
                child_work_id, expected_child_work_revision, child_work_history_event_id,
                expected_child_result_sha256, required_work_operator_command_id, actor_id, reason_code,
                outcome, problem_code, resulting_state, resulting_revision, resulting_flow_history_event_id,
                receipt_sha256, accepted_at
            )
            VALUES
            (
                @scope_id, @command_id, @flow_instance_id, @action, @request_schema, @request_sha256,
                @expected_flow_revision, @observed_flow_state, @observed_flow_revision, @descriptor_sha256,
                @child_work_id, @expected_child_work_revision, @child_work_history_event_id,
                @expected_child_result_sha256, @required_work_operator_command_id, @actor_id, @reason_code,
                @outcome, @problem_code, @resulting_state, @resulting_revision, @resulting_flow_history_event_id,
                @receipt_sha256, @accepted_at
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("flow_instance_id", request.InstanceId.Value);
        command.Parameters.AddWithValue("action", FormatRepairAction(request.Action));
        command.Parameters.AddWithValue("request_schema", request.Fingerprint.SchemaId);
        command.Parameters.AddWithValue("request_sha256", request.Fingerprint.Sha256);
        command.Parameters.AddWithValue("expected_flow_revision", request.ExpectedFlowRevision);
        command.Parameters.Add(new NpgsqlParameter("observed_flow_state", NpgsqlDbType.Text)
        {
            Value = observedState.HasValue ? FormatState(observedState.Value) : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("observed_flow_revision", NpgsqlDbType.Bigint)
        {
            Value = observedRevision ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue("descriptor_sha256", request.ExpectedSuspensionDescriptorSha256);
        command.Parameters.AddWithValue("child_work_id", request.Evidence.ChildWorkId.Value);
        command.Parameters.AddWithValue("expected_child_work_revision", request.Evidence.ExpectedChildWorkRevision);
        command.Parameters.AddWithValue("child_work_history_event_id", request.Evidence.ChildWorkHistoryEventId);
        command.Parameters.Add(new NpgsqlParameter("expected_child_result_sha256", NpgsqlDbType.Text)
        {
            Value = request.Evidence.ExpectedChildResultSha256 ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("required_work_operator_command_id", NpgsqlDbType.Text)
        {
            Value = request.Evidence.RequiredWorkOperatorCommandId?.Value ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue("actor_id", request.ActorId);
        command.Parameters.AddWithValue("reason_code", request.ReasonCode);
        command.Parameters.AddWithValue("outcome", FormatRepairOutcome(outcome));
        command.Parameters.Add(new NpgsqlParameter("problem_code", NpgsqlDbType.Text) { Value = problem?.Code ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("resulting_state", NpgsqlDbType.Text)
        {
            Value = resultingState.HasValue ? FormatState(resultingState.Value) : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("resulting_revision", NpgsqlDbType.Bigint)
        {
            Value = resultingRevision ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("resulting_flow_history_event_id", NpgsqlDbType.Bigint)
        {
            Value = resultingHistoryEventId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("receipt_sha256", NpgsqlDbType.Text) { Value = receipt?.ReceiptSha256 ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("accepted_at", NpgsqlDbType.TimestampTz) { Value = acceptedAtUtc });
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Flow repair did not persist one terminal command record.");
        }
    }

    private static async ValueTask<RepairCommandRow?> ReadRepairCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableCommandId commandId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT flow_instance_id, action, request_schema, request_sha256,
                   expected_flow_revision, observed_flow_state, observed_flow_revision, suspension_descriptor_sha256,
                   child_work_id, expected_child_work_revision, child_work_history_event_id,
                   expected_child_result_sha256, required_work_operator_command_id, actor_id, reason_code,
                   outcome, problem_code, resulting_state, resulting_revision, resulting_flow_history_event_id,
                   receipt_sha256, accepted_at
            FROM appsurface_durable.flow_repair_command
            WHERE scope_id = @scope_id AND command_id = @command_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var action = ParseRepairAction(reader.GetString(1));
        var evidence = action == DurableFlowRepairAction.AssertChildEffectCompleted
            ? DurableFlowRepairEvidenceReference.Completed(
                new DurableWorkId(reader.GetString(8)), reader.GetInt64(9), reader.GetInt64(10), reader.GetString(11))
            : DurableFlowRepairEvidenceReference.ProvenNotApplied(
                new DurableWorkId(reader.GetString(8)), reader.GetInt64(9), reader.GetInt64(10), new DurableCommandId(reader.GetString(12)));
        return new RepairCommandRow(
            scopeId,
            commandId,
            new DurableFlowInstanceId(reader.GetString(0)),
            action,
            new DurableCommandFingerprint(reader.GetString(2), reader.GetString(3)),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : ParseState(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetString(7),
            evidence,
            reader.GetString(13),
            reader.GetString(14),
            ParseRepairOutcome(reader.GetString(15)),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : ParseState(reader.GetString(17)),
            reader.IsDBNull(18) ? null : reader.GetInt64(18),
            reader.IsDBNull(19) ? null : reader.GetInt64(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.GetFieldValue<DateTimeOffset>(21));
    }

    private static async ValueTask RecordRepairCollisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableFlowRepairRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO appsurface_durable.flow_repair_collision
                (scope_id, command_id, conflicting_request_schema, conflicting_request_sha256)
            VALUES (@scope_id, @command_id, @request_schema, @request_sha256)
            ON CONFLICT (scope_id, command_id, conflicting_request_schema, conflicting_request_sha256) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue("command_id", request.CommandId.Value);
        command.Parameters.AddWithValue("request_schema", request.Fingerprint.SchemaId);
        command.Parameters.AddWithValue("request_sha256", request.Fingerprint.Sha256);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RepairWorkRow?> LockRepairWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId workId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT work_id, work_name, work_version, state, revision, lease_owner,
                   result_contract_id, result_schema_version, result_codec_id, result_classification,
                   result_retention_policy_id, result_payload, result_sha256
            FROM appsurface_durable.work
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

        RepairResultPayload? result = null;
        if (!reader.IsDBNull(6))
        {
            result = new RepairResultPayload(
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetFieldValue<byte[]>(11),
                Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(12)));
        }

        return new RepairWorkRow(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            result);
    }

    private static async ValueTask<RepairFlowRow?> LockRepairFlowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state, revision, suspended_from_state, suspension_descriptor_schema, suspension_descriptor_sha256,
                   suspension_descriptor ->> 'code', suspension_descriptor ->> 'source', suspension_descriptor ->> 'work_state'
            FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new RepairFlowRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7))
            : null;
    }

    private static async ValueTask<RepairWaitRow?> LockRepairWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableWorkId childWorkId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT wait_id, state, callsite_id, child_work_id,
                   result_contract_id, result_schema_version, result_codec_id, result_classification, result_retention
            FROM appsurface_durable.flow_wait
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND kind = 'activity' AND child_work_id = @child_work_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("child_work_id", childWorkId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new RepairWaitRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8))
            : null;
    }

    private static async ValueTask<DateTimeOffset> ReadDatabaseClockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp();", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL did not return its transaction clock.");
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static DurableProblem EvidenceMismatch(string correlationId) =>
        CreateRepairProblem(
            correlationId,
            DurableProblemCodes.FlowRepairEvidenceMismatch,
            "The retained child Work evidence does not match the locked Flow repair request.",
            "The Work revision, result identity, history fact, or manual-resolution proof changed or is incompatible.",
            "Reload the scoped repair assessment and submit only a current payload-free candidate.");

    private static DurableProblem CreateRepairProblem(string correlationId, string code, string problem, string cause, string fix) =>
        new(
            code,
            problem,
            cause,
            fix,
            new Uri($"{Diagnostics}#{code.ToLowerInvariant()}"),
            correlationId);

    private static string FormatRepairAction(DurableFlowRepairAction action) => action switch
    {
        DurableFlowRepairAction.AssertChildEffectCompleted => "assert_child_effect_completed",
        DurableFlowRepairAction.AssertChildEffectNotApplied => "assert_child_effect_not_applied",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static DurableFlowRepairAction ParseRepairAction(string action) => action switch
    {
        "assert_child_effect_completed" => DurableFlowRepairAction.AssertChildEffectCompleted,
        "assert_child_effect_not_applied" => DurableFlowRepairAction.AssertChildEffectNotApplied,
        _ => throw new InvalidOperationException($"Unknown persisted Flow repair action '{action}'."),
    };

    private static string FormatRepairOutcome(DurableFlowRepairOutcome outcome) => outcome switch
    {
        DurableFlowRepairOutcome.Applied => "applied",
        DurableFlowRepairOutcome.Refused => "refused",
        DurableFlowRepairOutcome.RaceLost => "race_lost",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static DurableFlowRepairOutcome ParseRepairOutcome(string outcome) => outcome switch
    {
        "applied" => DurableFlowRepairOutcome.Applied,
        "refused" => DurableFlowRepairOutcome.Refused,
        "race_lost" => DurableFlowRepairOutcome.RaceLost,
        _ => throw new InvalidOperationException($"Unknown persisted Flow repair outcome '{outcome}'."),
    };

    private sealed record RepairResultPayload(
        string ContractName,
        string ContractVersion,
        string CodecId,
        string Classification,
        string RetentionPolicyId,
        byte[] Content,
        string Sha256)
    {
        internal DurableEncodedPayload ToPayload() => new(
            ContractName,
            ContractVersion,
            ParseClassification(Classification),
            Content,
            RetentionPolicyId);
    }

    private sealed record RepairWorkRow(
        string WorkName,
        string WorkVersion,
        string State,
        long Revision,
        string? LeaseOwner,
        RepairResultPayload? Result)
    {
        internal DurableEncodedPayload ToEncodedResult() => Result?.ToPayload()
            ?? throw new InvalidOperationException("Completed repair requires a retained child Work result.");
    }

    private sealed record RepairFlowRow(
        string State,
        long Revision,
        string? SuspendedFromState,
        string? SuspensionDescriptorSchema,
        string? SuspensionDescriptorSha256,
        string DescriptorCode,
        string DescriptorSource,
        string DescriptorWorkState)
    {
        internal DurableFlowState PublicState => ParseState(State);
    }

    private sealed record RepairWaitRow(
        Guid WaitId,
        string State,
        string CallsiteId,
        string ChildWorkId,
        string? ResultContractId,
        string? ResultSchemaVersion,
        string? ResultCodecId,
        string? ResultClassification,
        string? ResultRetention)
    {
        internal bool ResultMatches(RepairResultPayload result) =>
            ResultContractId is not null
            && ResultSchemaVersion is not null
            && ResultCodecId is not null
            && ResultClassification is not null
            && ResultRetention is not null
            && string.Equals(ResultContractId, result.ContractName, StringComparison.Ordinal)
            && string.Equals(ResultSchemaVersion, result.ContractVersion, StringComparison.Ordinal)
            && string.Equals(ResultCodecId, result.CodecId, StringComparison.Ordinal)
            && string.Equals(ResultClassification, result.Classification, StringComparison.Ordinal)
            && string.Equals(ResultRetention, result.RetentionPolicyId, StringComparison.Ordinal);
    }

    private sealed record RepairCommandRow(
        DurableScopeId ScopeId,
        DurableCommandId CommandId,
        DurableFlowInstanceId InstanceId,
        DurableFlowRepairAction Action,
        DurableCommandFingerprint RequestFingerprint,
        long ExpectedRevision,
        DurableFlowState? ObservedState,
        long? ObservedRevision,
        string DescriptorSha256,
        DurableFlowRepairEvidenceReference Evidence,
        string ActorId,
        string ReasonCode,
        DurableFlowRepairOutcome Outcome,
        string? ProblemCode,
        DurableFlowState? ResultingState,
        long? ResultingRevision,
        long? ResultingHistoryEventId,
        string? ReceiptSha256,
        DateTimeOffset AcceptedAtUtc)
    {
        internal DurableFlowRepairResult ToResult()
        {
            if (Outcome == DurableFlowRepairOutcome.Applied)
            {
                var receipt = new DurableFlowRepairReceipt(
                    ScopeId,
                    InstanceId,
                    CommandId,
                    Action,
                    RequestFingerprint,
                    DescriptorSha256,
                    Evidence,
                    ActorId,
                    ReasonCode,
                    ObservedState!.Value,
                    ObservedRevision!.Value,
                    ResultingState!.Value,
                    ResultingRevision!.Value,
                    ResultingHistoryEventId!.Value,
                    AcceptedAtUtc);
                if (!string.Equals(ReceiptSha256, receipt.ReceiptSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The persisted Flow repair receipt digest does not match its retained canonical evidence.");
                }

                return new DurableFlowRepairResult(
                    DurableFlowRepairOutcome.Duplicate,
                    receipt,
                    null,
                    ResultingState,
                    ResultingRevision);
            }

            return new DurableFlowRepairResult(
                Outcome,
                null,
                CreateRepairProblem(
                    CommandId.Value,
                    ProblemCode ?? DurableProblemCodes.FlowRaceLost,
                    "The prior Flow repair terminal result was returned.",
                    "The command identity already has a retained terminal outcome.",
                    "Inspect the retained receipt or current Flow assessment before submitting another repair."),
                ObservedState,
                ObservedRevision);
        }
    }
}
