using ForgeTrust.AppSurface.Durable;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Identifies transaction-local trace evidence before it is attached to its committed Flow lineage.</summary>
internal sealed record PostgreSqlDurableFlowTrace(Guid TraceContextId);

internal sealed partial class PostgreSqlDurableFlowStore
{
    /// <summary>Inserts immutable trace evidence into the caller-owned Flow mutation transaction.</summary>
    /// <remarks>
    /// The caller must set the scoped runtime context and commit or roll back the supplied transaction. A missing
    /// <paramref name="context"/> produces no row and returns <see langword="null"/>. A non-null context must insert
    /// exactly one row or the method throws so the enclosing durable mutation cannot commit partial evidence.
    /// </remarks>
    internal static async ValueTask<PostgreSqlDurableFlowTrace?> InsertTraceContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        DurableTraceContext? context,
        string causeKind,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return null;
        }

        var traceContextId = Guid.NewGuid();
        const string sql = """
            INSERT INTO appsurface_durable.flow_trace_context
                (trace_context_id, scope_id, flow_instance_id, contract_version, traceparent, tracestate,
                 correlation_token, cause_kind)
            VALUES
                (@trace_context_id, @scope_id, @flow_instance_id, @contract_version, @traceparent, @tracestate,
                 @correlation_token, @cause_kind)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("trace_context_id", traceContextId);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.AddWithValue("contract_version", DurableTraceContext.ContractVersion);
        command.Parameters.AddWithValue("traceparent", context.TraceParent);
        command.Parameters.Add(new NpgsqlParameter("tracestate", NpgsqlDbType.Varchar)
        {
            Value = context.TraceState ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue("correlation_token", context.CorrelationToken);
        command.Parameters.AddWithValue("cause_kind", causeKind);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The durable trace context insert did not affect exactly one row.");
        }

        return new PostgreSqlDurableFlowTrace(traceContextId);
    }

    /// <summary>Attaches inserted trace evidence to every Flow record created by the same committed transition.</summary>
    /// <remarks>
    /// The caller must use the transaction that inserted <paramref name="trace"/>. A missing trace is a no-op for an
    /// absent context. Otherwise the Flow instance and history pointer, plus every non-null command, wait, timer, or
    /// Work pointer, must each update exactly one row; any mismatch throws so the transaction rolls back rather than
    /// committing detached causal evidence.
    /// </remarks>
    internal static async ValueTask AttachTraceContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableFlowInstanceId instanceId,
        PostgreSqlDurableFlowTrace? trace,
        string? commandId,
        long revision,
        Guid? waitId,
        Guid? timerId,
        DurableWorkId? workId,
        CancellationToken cancellationToken)
    {
        if (trace is null)
        {
            return;
        }

        const string sql = """
            UPDATE appsurface_durable.flow_instance
            SET trace_context_id = @trace_context_id
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;

            UPDATE appsurface_durable.flow_command
            SET trace_context_id = @trace_context_id
            WHERE scope_id = @scope_id AND command_id = @command_id
              AND @command_id IS NOT NULL;

            UPDATE appsurface_durable.flow_history
            SET trace_context_id = @trace_context_id
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND aggregate_revision = @revision;

            UPDATE appsurface_durable.flow_wait
            SET trace_context_id = @trace_context_id
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND wait_id = @wait_id AND @wait_id IS NOT NULL;

            UPDATE appsurface_durable.flow_timer
            SET trace_context_id = @trace_context_id
            WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id
              AND timer_id = @timer_id AND @timer_id IS NOT NULL;

            UPDATE appsurface_durable.work
            SET trace_context_id = @trace_context_id
            WHERE scope_id = @scope_id AND work_id = @work_id AND @work_id IS NOT NULL;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("trace_context_id", trace.TraceContextId);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("flow_instance_id", instanceId.Value);
        command.Parameters.Add(new NpgsqlParameter("command_id", NpgsqlDbType.Text)
        {
            Value = commandId ?? (object)DBNull.Value,
        });
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.Add(new NpgsqlParameter("wait_id", NpgsqlDbType.Uuid)
        {
            Value = waitId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("timer_id", NpgsqlDbType.Uuid)
        {
            Value = timerId ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("work_id", NpgsqlDbType.Text)
        {
            Value = workId is { } durableWorkId ? durableWorkId.Value : DBNull.Value,
        });
        var expectedPointers = 2
            + (commandId is null ? 0 : 1)
            + (waitId is null ? 0 : 1)
            + (timerId is null ? 0 : 1)
            + (workId is null ? 0 : 1);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != expectedPointers)
        {
            throw new InvalidOperationException(
                $"The durable trace context attachment expected {expectedPointers} pointer updates.");
        }
    }
}
