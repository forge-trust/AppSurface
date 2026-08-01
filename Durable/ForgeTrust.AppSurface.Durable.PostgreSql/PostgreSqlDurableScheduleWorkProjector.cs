using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Requeues a QueueOne Schedule when its materialized Work target reaches terminal truth.
/// </summary>
/// <remarks>
/// <para>
/// The Work store invokes this projector in the same transaction that commits terminal Work
/// truth. That makes a coalesced occurrence eligible immediately, without relying on the
/// Schedule's normal interval to wake it.
/// </para>
/// <para>
/// The definition row is locked before its dispatch row is updated. Schedule processing takes
/// the same definition lock, so a terminal Work transition cannot race a pending occurrence
/// from one active generation into a later generation.
/// </para>
/// </remarks>
internal static class PostgreSqlDurableScheduleWorkProjector
{
    internal static async ValueTask RequeuePendingOccurrenceAsync(
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableWorkId workId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The Work projection transaction is no longer active.");

        const string sql = """
            WITH locked_definition AS
            (
                SELECT definition.scope_id, definition.schedule_id, definition.active_generation, definition.revision
                FROM appsurface_durable.schedule_definition AS definition
                WHERE definition.scope_id = @scope_id
                  AND definition.state = 'active'
                  AND EXISTS
                  (
                      SELECT 1
                      FROM appsurface_durable.schedule_occurrence AS materialized
                      WHERE materialized.scope_id = definition.scope_id
                        AND materialized.schedule_id = definition.schedule_id
                        AND materialized.state = 'materialized'
                        AND materialized.target_kind = 'work'
                        AND materialized.target_id = @work_id
                  )
                FOR UPDATE
            ),
            requeued_dispatch AS
            (
                UPDATE appsurface_durable.schedule_dispatch AS dispatch
                SET dispatch_revision = definition.revision,
                    due_at = clock_timestamp(),
                    state = 'available',
                    lease_owner = NULL,
                    lease_expires_at = NULL,
                    updated_at = clock_timestamp()
                FROM locked_definition AS definition
                WHERE dispatch.scope_id = definition.scope_id
                  AND dispatch.schedule_id = definition.schedule_id
                  AND EXISTS
                  (
                      SELECT 1
                      FROM appsurface_durable.schedule_occurrence AS pending
                      WHERE pending.scope_id = definition.scope_id
                        AND pending.schedule_id = definition.schedule_id
                        AND pending.generation = definition.active_generation
                        AND pending.occurrence_kind = 'coalesced'
                        AND pending.state = 'pending'
                  )
                RETURNING dispatch.scope_id, dispatch.schedule_id, definition.active_generation
            )
            INSERT INTO appsurface_durable.schedule_history
                (scope_id, schedule_id, generation, occurrence_id, event_type, details)
            SELECT scope_id,
                   schedule_id,
                   active_generation,
                   NULL,
                   'work-terminal-requeued',
                   jsonb_build_object('work_id', @work_id)
            FROM requeued_dispatch;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
