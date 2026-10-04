using Npgsql;
using static ForgeTrust.AppSurface.Durable.PostgreSql.PostgreSqlDurableProtocolCodec;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Preserves exact invocation evidence when cancellation races an opted-in timing boundary.</summary>
internal partial class PostgreSqlDurableWorkStore
{
    private static async ValueTask<PostgreSqlCancellationResult> CancelExecutionPolicyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSqlWorkExecutionRow row,
        long expectedRevision, string actorId, string reasonCode,
        Func<NpgsqlTransaction, DurableWorkState, CancellationToken, ValueTask>? onProjectionApplied,
        CancellationToken cancellationToken)
    {
        var parsed = ParseWorkState(row.State);
        if (IsTerminal(parsed))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(PostgreSqlCancellationOutcome.AlreadyTerminal, parsed, row.Revision);
        }
        if (row.Revision != expectedRevision)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(PostgreSqlCancellationOutcome.RevisionConflict, parsed, row.Revision);
        }
        if (row.AdmissionReason(false) is { } timingReason)
            await RecordAdmissionClosureAsync(connection, transaction, row, timingReason, cancellationToken).ConfigureAwait(false);

        await using var cancellation = new NpgsqlCommand("""
            UPDATE appsurface_durable.work SET cancellation_requested_at=COALESCE(cancellation_requested_at,@now)
            WHERE scope_id=@scope_id AND work_id=@work_id;
            """, connection, transaction);
        AddExecutionRowParameters(cancellation, row);
        await cancellation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var currentAdmitted = row.InvocationAdmittedAtUtc is not null && row.PermitStatus is ("granted" or "ambiguous");
        var liveAdmitted = currentAdmitted && row.State is ("leased" or "effect_permitted" or "cancel_pending")
            && row.LeaseExpiresAtUtc > row.NowUtc;
        var state = row.State == "reconciling" ? row.State
            : liveAdmitted ? "cancel_pending" : row.HasUncertainEffect ? ExecutionAmbiguousState(row.Safety) : "canceled_before_effect";
        var code = row.HasUncertainEffect ? DurableProblemCodes.AmbiguousExternalOutcome : "canceled_before_effect";
        long revision;
        if (liveAdmitted || state == "reconciling")
        {
            await using var update = new NpgsqlCommand("""
                UPDATE appsurface_durable.work SET state=@state,revision=revision+1,updated_at=@now
                WHERE scope_id=@scope_id AND work_id=@work_id AND revision=@revision RETURNING revision;
                """, connection, transaction);
            AddExecutionRowParameters(update, row);
            update.Parameters.AddWithValue("state", state);
            revision = (long)(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The locked cancellation lost its revision."));
            await UpdateExecutionDispatchAsync(connection, transaction, row, state, row.DueAtUtc, row.LeaseExpiresAtUtc,
                revision, currentAdmitted, cancellationToken).ConfigureAwait(false);
        }
        else revision = await ApplyExecutionStateAsync(connection, transaction, row, state, code, "cancellation_requested",
            row.DueAtUtc, null, cancellationToken).ConfigureAwait(false);

        await using var history = new NpgsqlCommand("""
            INSERT INTO appsurface_durable.work_history(scope_id,work_id,aggregate_revision,event_type,actor_id,reason_code,
                attempt_number,lease_generation,scope_generation,runtime_epoch,observed_at,details)
            VALUES(@scope_id,@work_id,@revision,'cancellation_requested',@actor,@reason,@attempt,@lease,@generation,@epoch,@now,'{}');
            """, connection, transaction);
        AddExecutionRowParameters(history, row with { Revision = revision });
        history.Parameters.AddWithValue("actor", actorId);
        history.Parameters.AddWithValue("reason", reasonCode);
        history.Parameters.AddWithValue("attempt", row.AttemptNumber);
        history.Parameters.AddWithValue("lease", row.LeaseGeneration);
        history.Parameters.AddWithValue("generation", row.ScopeGeneration);
        history.Parameters.AddWithValue("epoch", row.RuntimeEpoch);
        await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        parsed = ParseWorkState(state);
        if (onProjectionApplied is not null && (IsTerminal(parsed) || parsed == DurableWorkState.Suspended))
            await onProjectionApplied(transaction, parsed, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(PostgreSqlCancellationOutcome.Applied, parsed, revision);
    }
}
