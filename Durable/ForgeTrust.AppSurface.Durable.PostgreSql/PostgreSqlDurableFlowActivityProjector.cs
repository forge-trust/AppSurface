using System.Diagnostics;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Projects terminal or suspended child Work truth into its retained parent Flow activity wait in the same transaction.
/// </summary>
internal static class PostgreSqlDurableFlowActivityProjector
{
    internal static async ValueTask ProjectAsync(
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId workId,
        DurableWorkState workState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The Work projection transaction is no longer active.");

        const string lineageSql = """
            SELECT wait.flow_instance_id, wait.wait_id, wait.callsite_id,
                   work.result_payload IS NOT NULL, trace.traceparent, trace.tracestate
            FROM appsurface_durable.flow_wait AS wait
            JOIN appsurface_durable.work AS work
              ON work.scope_id = wait.scope_id AND work.work_id = wait.child_work_id
            LEFT JOIN appsurface_durable.flow_trace_context AS trace
              ON trace.scope_id = work.scope_id AND trace.trace_context_id = work.trace_context_id
            WHERE wait.scope_id = @scope_id AND wait.child_work_id = @work_id
              AND wait.kind = 'activity' AND wait.state IN ('active', 'suspended');
            """;
        DurableFlowInstanceId? instanceId = null;
        Guid waitId = default;
        string? callsiteId = null;
        var hasResultPayload = false;
        var workTrace = DurableTraceContextCapture.Absent;
        await using (var lineage = new NpgsqlCommand(lineageSql, connection, transaction))
        {
            lineage.Parameters.AddWithValue("scope_id", scopeId.Value);
            lineage.Parameters.AddWithValue("work_id", workId.Value);
            await using var reader = await lineage.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                instanceId = new DurableFlowInstanceId(reader.GetString(0));
                waitId = reader.GetGuid(1);
                callsiteId = reader.GetString(2);
                hasResultPayload = reader.GetBoolean(3);
                workTrace = reader.IsDBNull(4)
                    ? DurableTraceContextCapture.Absent
                    : DurableTraceContext.Parse(reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }

        if (instanceId is null)
        {
            return;
        }

        const string lockSql = """
            SELECT state, revision, definition_fingerprint_sha256
            FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
            FOR UPDATE;
            """;
        string flowState;
        long flowRevision;
        string definitionFingerprint;
        await using (var flow = new NpgsqlCommand(lockSql, connection, transaction))
        {
            flow.Parameters.AddWithValue("scope_id", scopeId.Value);
            flow.Parameters.AddWithValue("flow_instance_id", instanceId.Value.Value);
            await using var reader = await flow.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("A Flow activity wait references a missing parent aggregate.");
            }

            flowState = reader.GetString(0);
            flowRevision = reader.GetInt64(1);
            definitionFingerprint = reader.GetString(2);
        }

        const string waitLockSql = """
            SELECT state
            FROM appsurface_durable.flow_wait
            WHERE wait_id = @wait_id AND scope_id = @scope_id AND flow_instance_id = @flow_instance_id
            FOR UPDATE;
            """;
        await using (var wait = new NpgsqlCommand(waitLockSql, connection, transaction))
        {
            wait.Parameters.AddWithValue("wait_id", waitId);
            wait.Parameters.AddWithValue("scope_id", scopeId.Value);
            wait.Parameters.AddWithValue("flow_instance_id", instanceId.Value.Value);
            var lockedState = (string?)await wait.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (lockedState is not ("active" or "suspended"))
            {
                return;
            }
        }

        var succeeded = workState is DurableWorkState.Succeeded or DurableWorkState.SucceededAfterCancelRequested;
        var canceledParent = flowState == "cancel_pending"
            && workState is DurableWorkState.CanceledBeforeEffect
                or DurableWorkState.Succeeded
                or DurableWorkState.SucceededAfterCancelRequested;
        if (flowState == "suspended")
        {
            // Scope recovery or an earlier projection already preserved the retained activity wait.
            // Let the child Work commit its terminal truth without trying to project the parent again.
            return;
        }

        var revision = checked(flowRevision + 1);
        if (succeeded && !canceledParent && hasResultPayload)
        {
            DurableTraceDiagnostics.Report(workTrace.DiagnosticCode);
            using var activity = DurableTraceActivity.StartRoot(
                "appsurface.durable.flow.activity",
                ActivityKind.Consumer,
                workTrace.Context);
            var completionTrace = activity is null
                ? workTrace
                : DurableTraceContext.Capture(activity);
            if (activity is not null)
            {
                DurableTraceDiagnostics.Report(completionTrace.DiagnosticCode);
            }

            await ProjectSuccessAsync(
                connection,
                transaction,
                scopeId,
                instanceId.Value,
                workId,
                waitId,
                callsiteId!,
                revision,
                definitionFingerprint,
                cancellationToken).ConfigureAwait(false);
            var trace = await PostgreSqlDurableFlowStore.InsertTraceContextAsync(
                connection,
                transaction,
                scopeId,
                instanceId.Value,
                completionTrace.Context,
                "activity_completed",
                cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableFlowStore.AttachTraceContextAsync(
                connection,
                transaction,
                scopeId,
                instanceId.Value,
                trace,
                commandId: null,
                revision,
                waitId,
                timerId: null,
                workId,
                cancellationToken).ConfigureAwait(false);
            DurableTraceTelemetry.Apply(
                activity,
                "activity",
                "activity",
                "ready",
                "completed",
                completionTrace.Context?.CorrelationToken ?? Guid.Empty,
                completionTrace.Status);
            return;
        }

        await ProjectFailureAsync(
            connection,
            transaction,
            scopeId,
            instanceId.Value,
            waitId,
            revision,
            canceledParent,
            flowState,
            workState,
            definitionFingerprint,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ProjectSuccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableWorkId workId,
        Guid waitId,
        string callsiteId,
        long revision,
        string definitionFingerprint,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH result AS
            (
                SELECT result_contract_id, result_schema_version, result_codec_id, result_payload,
                       result_sha256, result_classification, result_retention_policy_id
                FROM appsurface_durable.work
                WHERE scope_id = @scope_id AND work_id = @work_id
            ),
            projected_flow AS
            (
                UPDATE appsurface_durable.flow_instance
                SET state = 'ready', revision = @revision,
                    activity_callsite_id = @callsite_id,
                    activity_result_contract_id = result.result_contract_id,
                    activity_result_schema_version = result.result_schema_version,
                    activity_result_codec_id = result.result_codec_id,
                    activity_result_payload = result.result_payload,
                    activity_result_sha256 = result.result_sha256,
                    activity_result_classification = result.result_classification,
                    activity_result_retention = result.result_retention_policy_id,
                    updated_at = clock_timestamp()
                FROM result
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                  AND state IN ('waiting_activity', 'cancel_pending')
                  AND result.result_payload IS NOT NULL
                RETURNING revision
            ),
            projected_wait AS
            (
                UPDATE appsurface_durable.flow_wait
                SET state = 'activity_completed', resolved_revision = @revision,
                    resolved_at = clock_timestamp(), updated_at = clock_timestamp()
                WHERE wait_id = @wait_id AND state IN ('active', 'suspended')
                  AND EXISTS (SELECT 1 FROM projected_flow)
                RETURNING wait_id
            ),
            projected_dispatch AS
            (
                UPDATE appsurface_durable.flow_dispatch
                SET state = 'available', due_at = clock_timestamp(), expected_revision = @revision,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'flow'
                  AND EXISTS (SELECT 1 FROM projected_wait)
                RETURNING dispatch_id
            )
            INSERT INTO appsurface_durable.flow_history
                (scope_id, flow_instance_id, aggregate_revision, node_id, transition_kind, details)
            SELECT @scope_id, @flow_instance_id, @revision, @callsite_id, 'activity_completed',
                   jsonb_build_object('child_work_id', @work_id, 'definition_fingerprint', @definition_fingerprint)
            WHERE EXISTS (SELECT 1 FROM projected_dispatch);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddParameters(command, scopeId, instanceId, waitId, revision, definitionFingerprint);
        command.Parameters.AddWithValue("work_id", workId.Value);
        command.Parameters.AddWithValue("callsite_id", callsiteId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "Successful child Work did not project one parent Flow, wait, dispatch, and history transition.");
        }
    }

    private static async ValueTask ProjectFailureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        Guid waitId,
        long revision,
        bool canceledParent,
        string suspendedFromState,
        DurableWorkState workState,
        string definitionFingerprint,
        CancellationToken cancellationToken)
    {
        var nextState = canceledParent ? "canceled" : "suspended";
        var waitState = canceledParent ? "canceled" : "suspended";
        var code = canceledParent ? "canceled_before_effect" : "flow.child_work_requires_attention";
        const string sql = """
            WITH projected_flow AS
            (
                UPDATE appsurface_durable.flow_instance
                SET state = @next_state, revision = @revision,
                    terminal_at = CASE WHEN @next_state = 'canceled' THEN clock_timestamp() ELSE NULL END,
                    terminal_code = CASE WHEN @next_state = 'canceled' THEN @problem_code ELSE NULL END,
                    suspended_from_state = CASE WHEN @next_state = 'suspended' THEN @suspended_from_state ELSE NULL END,
                    suspension_descriptor = CASE WHEN @next_state = 'suspended'
                        THEN jsonb_build_object('code', @problem_code, 'source', 'child_work', 'work_state', @work_state)
                        ELSE NULL END,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
                  AND state IN ('waiting_activity', 'cancel_pending')
                RETURNING revision
            ),
            projected_wait AS
            (
                UPDATE appsurface_durable.flow_wait
                SET state = @wait_state,
                    suspension_descriptor = CASE WHEN @wait_state = 'suspended'
                        THEN jsonb_build_object('code', @problem_code, 'work_state', @work_state)
                        ELSE NULL END,
                    resolved_revision = CASE WHEN @wait_state = 'canceled' THEN @revision ELSE NULL END,
                    resolved_at = CASE WHEN @wait_state = 'canceled' THEN clock_timestamp() ELSE NULL END,
                    updated_at = clock_timestamp()
                WHERE wait_id = @wait_id AND state IN ('active', 'suspended')
                  AND EXISTS (SELECT 1 FROM projected_flow)
                RETURNING wait_id
            ),
            projected_dispatch AS
            (
                UPDATE appsurface_durable.flow_dispatch
                SET state = CASE WHEN @next_state = 'canceled' THEN 'terminal' ELSE 'suspended' END,
                    expected_revision = @revision, updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id AND kind = 'flow'
                  AND EXISTS (SELECT 1 FROM projected_wait)
                RETURNING dispatch_id
            )
            INSERT INTO appsurface_durable.flow_history
                (scope_id, flow_instance_id, aggregate_revision, transition_kind, details)
            SELECT @scope_id, @flow_instance_id, @revision, 'activity_attention_required',
                   jsonb_build_object('problem_code', @problem_code, 'work_state', @work_state,
                                      'definition_fingerprint', @definition_fingerprint)
            WHERE EXISTS (SELECT 1 FROM projected_dispatch);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddParameters(command, scopeId, instanceId, waitId, revision, definitionFingerprint);
        command.Parameters.AddWithValue("next_state", nextState);
        command.Parameters.AddWithValue("wait_state", waitState);
        command.Parameters.AddWithValue("suspended_from_state", suspendedFromState);
        command.Parameters.AddWithValue("problem_code", code);
        command.Parameters.AddWithValue("work_state", workState.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "Terminal child Work did not project one parent Flow, wait, dispatch, and history transition.");
        }
    }

    private static void AddParameters(
        NpgsqlCommand command,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        Guid waitId,
        long revision,
        string definitionFingerprint)
    {
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("wait_id", waitId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("definition_fingerprint", definitionFingerprint);
    }
}
