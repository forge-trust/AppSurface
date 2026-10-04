using Npgsql;
using static ForgeTrust.AppSurface.Durable.PostgreSql.PostgreSqlDurableProtocolCodec;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Opt-in effect permits are exact fenced evidence; a separate durable marker admits invocation once.</summary>
internal partial class PostgreSqlDurableWorkStore
{
    private async ValueTask<PostgreSqlEffectPermit?> PermitExecutionPolicyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PostgreSqlDurableWorkClaim claim, PostgreSqlWorkExecutionRow row,
        Func<NpgsqlTransaction, DurableWorkState, string, CancellationToken, ValueTask>? onTerminalApplied,
        CancellationToken cancellationToken)
    {
        if (!row.Matches(claim) || row.State is not ("leased" or "effect_permitted" or "cancel_pending"))
        {
            RecordExecutionRefusal("permit", row.Matches(claim) ? "lease_lost" : "claim_lost");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (HasExpiredOrdinaryLease(row))
        {
            RecordExecutionRefusal("permit", "lease_lost");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var reason = row.AdmissionReason(false);
        if (reason is not null || row.CancellationRequested)
        {
            if (reason is not null) await CloseExecutionAdmissionAsync(connection, transaction, row, reason, "permit", cancellationToken).ConfigureAwait(false);
            if (row.InvocationAdmittedAtUtc is not null && !row.CancellationRequested
                && reason != "deadline_elapsed" && row.LeaseExpiresAtUtc > row.NowUtc)
            {
                // Admission closure alone does not revoke an already admitted current call.
                await UpdateExecutionDispatchAsync(connection, transaction, row, row.State, row.DueAtUtc,
                    row.LeaseExpiresAtUtc, row.Revision, true, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            var uncertain = row.HasUncertainEffect;
            var state = uncertain ? ExecutionAmbiguousState(row.Safety) : row.CancellationRequested ? "canceled_before_effect" : "failed";
            var code = uncertain ? DurableProblemCodes.AmbiguousExternalOutcome : row.CancellationRequested ? "canceled_before_effect" : ExecutionTimingCode(row, reason!, false);
            await ApplyExecutionStateAsync(connection, transaction, row, state, code, reason ?? "cancellation_requested", row.DueAtUtc, null, cancellationToken).ConfigureAwait(false);
            if (onTerminalApplied is not null)
                await onTerminalApplied(transaction, ParseWorkState(state), code, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (row.LeaseExpiresAtUtc <= row.NowUtc)
        {
            RecordExecutionRefusal("permit", "lease_lost");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (row.PermitId is { } existing)
        {
            await using var read = new NpgsqlCommand("SELECT activity_id, permitted_at FROM appsurface_durable.effect_permit WHERE permit_id=@permit_id;", connection, transaction);
            read.Parameters.AddWithValue("permit_id", existing);
            string key; DateTimeOffset permitted;
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("The locked permit disappeared.");
                key = reader.GetString(0); permitted = ReadUtc(reader, 1);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(existing, claim with { Revision = row.Revision, Execution = row.ExecutionSnapshot }, key, permitted);
        }
        var permitId = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            UPDATE appsurface_durable.work SET state='effect_permitted', revision=revision+1, updated_at=@now
            WHERE scope_id=@scope_id AND work_id=@work_id AND revision=@revision RETURNING activity_id, revision;
            """, connection, transaction);
        AddExecutionRowParameters(command, row);
        string providerKey; long revision;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("The locked permit decision lost its revision.");
            providerKey = reader.GetString(0); revision = reader.GetInt64(1);
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO appsurface_durable.effect_permit (permit_id,scope_id,work_id,attempt_number,lease_generation,
                scope_generation,runtime_epoch,activity_id,status,permitted_at)
            VALUES (@permit_id,@scope_id,@work_id,@attempt_number,@lease_generation,@scope_generation,@runtime_epoch,@activity,'granted',@now);
            """, connection, transaction);
        AddClaimIdentityParameters(insert, claim);
        insert.Parameters.AddWithValue("permit_id", permitId);
        insert.Parameters.AddWithValue("activity", providerKey);
        insert.Parameters.AddWithValue("now", row.NowUtc.UtcDateTime);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var permittedClaim = claim with { Revision = revision, Execution = row.ExecutionSnapshot };
        await UpdateExecutionDispatchAsync(connection, transaction, row, "effect_permitted", row.DueAtUtc, row.LeaseExpiresAtUtc, revision, false, cancellationToken).ConfigureAwait(false);
        await InsertHistoryAsync(connection, transaction, permittedClaim, "effect_permitted", false, "{}", cancellationToken, row.NowUtc).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(permitId, permittedClaim, providerKey, row.NowUtc);
    }

    /// <summary>Revalidates the store after permit commit and atomically consumes exact invocation admission once.</summary>
    /// <remarks>False never authorizes an executor; a committed marker is conservative effect uncertainty even before I/O.</remarks>
    internal async ValueTask<bool> TryAdmitInvocationAsync(PostgreSqlEffectPermit permit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permit);
        var claim = permit.Claim;
        await using var connection = await _runtimeDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentEpochAsync(connection, transaction, _runtimeEpoch, cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, claim.ScopeId, cancellationToken).ConfigureAwait(false);
            if (!await LockActiveScopeAsync(connection, transaction, claim.ScopeId, claim.ScopeGeneration, cancellationToken).ConfigureAwait(false))
            {
                RecordExecutionRefusal("invocation", "scope_disabled");
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); return false;
            }
            var row = await ReadExecutionRowLockedAsync(connection, transaction, claim.ScopeId, claim.WorkId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                // The database decides legacy compatibility; a missing caller snapshot is never authority.
                await using var legacy = new NpgsqlCommand("""
                    SELECT EXISTS(SELECT 1 FROM appsurface_durable.work AS work
                        JOIN appsurface_durable.effect_permit AS permit ON permit.scope_id=work.scope_id AND permit.work_id=work.work_id
                        WHERE work.scope_id=@scope_id AND work.work_id=@work_id AND work.execution_policy_schema IS NULL
                            AND work.attempt_number=@attempt_number AND work.lease_generation=@lease_generation
                            AND work.scope_generation=@scope_generation AND work.runtime_epoch=@runtime_epoch
                            AND work.lease_owner=@lease_owner AND work.lease_expires_at>pg_catalog.clock_timestamp()
                            AND work.state IN ('effect_permitted','cancel_pending') AND permit.permit_id=@permit_id
                            AND permit.attempt_number=work.attempt_number AND permit.lease_generation=work.lease_generation
                            AND permit.scope_generation=work.scope_generation AND permit.runtime_epoch=work.runtime_epoch);
                    """, connection, transaction);
                AddClaimIdentityParameters(legacy, claim);
                legacy.Parameters.AddWithValue("permit_id", permit.PermitId);
                var admitted = await legacy.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return admitted;
            }
            if (!row.Matches(claim) || row.PermitId != permit.PermitId || row.State is not ("effect_permitted" or "cancel_pending"))
            {
                RecordExecutionRefusal("invocation", "claim_lost");
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); return false;
            }
            if (HasExpiredOrdinaryLease(row))
            {
                RecordExecutionRefusal("invocation", "lease_lost");
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            var reason = row.AdmissionReason(false);
            if (row.InvocationAdmittedAtUtc is not null)
            {
                if (reason is not null)
                {
                    await CloseExecutionAdmissionAsync(connection, transaction, row, reason, "invocation", cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (reason is not null || row.CancellationRequested || row.LeaseExpiresAtUtc <= row.NowUtc)
            {
                if (reason is not null) await CloseExecutionAdmissionAsync(connection, transaction, row, reason, "invocation", cancellationToken).ConfigureAwait(false);
                else if (row.LeaseExpiresAtUtc <= row.NowUtc) RecordExecutionRefusal("invocation", "lease_lost");
                var uncertain = row.HasUncertainEffect;
                var state = uncertain ? ExecutionAmbiguousState(row.Safety) : row.CancellationRequested ? "canceled_before_effect" : "failed";
                var code = uncertain ? DurableProblemCodes.AmbiguousExternalOutcome : row.CancellationRequested ? "canceled_before_effect"
                    : reason is not null ? ExecutionTimingCode(row, reason, false) : DurableProblemCodes.LeaseLost;
                await ApplyExecutionStateAsync(connection, transaction, row, state, code, reason ?? "lease_lost", row.DueAtUtc, null, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return false;
            }
            await using var command = new NpgsqlCommand("""
                UPDATE appsurface_durable.effect_permit SET invocation_admitted_at=@now
                WHERE permit_id=@permit_id AND invocation_admitted_at IS NULL;
                """, connection, transaction);
            command.Parameters.AddWithValue("permit_id", permit.PermitId);
            command.Parameters.AddWithValue("now", row.NowUtc.UtcDateTime);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The locked invocation marker was already consumed.");
            await UpdateExecutionDispatchAsync(connection, transaction, row, row.State, row.DueAtUtc, row.LeaseExpiresAtUtc, row.Revision, true, cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(connection, transaction, claim with { Revision = row.Revision }, "invocation_admitted", false, "{}", cancellationToken, row.NowUtc).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return true;
        }
        catch { await TryRollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    private async ValueTask<PostgreSqlDurableWorkClaim?> RenewExecutionPolicyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PostgreSqlDurableWorkClaim claim, PostgreSqlWorkExecutionRow row, CancellationToken cancellationToken)
    {
        if (!row.Matches(claim) || row.State is not ("leased" or "effect_permitted" or "cancel_pending"))
        {
            RecordExecutionRefusal("renewal", row.Matches(claim) ? "lease_lost" : "claim_lost");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); return null;
        }
        if (HasExpiredOrdinaryLease(row))
        {
            RecordExecutionRefusal("renewal", "lease_lost");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (row.DeadlineReachedAtUtc is not null || row.Deadline is { } deadline && row.NowUtc >= deadline.NotAfterUtc)
        {
            await CloseExecutionAdmissionAsync(connection, transaction, row, "deadline_elapsed", "renewal", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return null;
        }
        if (row.LeaseExpiresAtUtc <= row.NowUtc)
        {
            RecordExecutionRefusal("renewal", "lease_lost");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var lifetimeEnd = AddExecutionTimeCapped(row.LeaseStartedAtUtc!.Value,
            row.Policy.RetryPolicy.MaximumLeaseLifetime, row.Deadline?.NotAfterUtc);
        var expiry = AddExecutionTimeCapped(row.NowUtc, row.Policy.RetryPolicy.LeaseDuration, lifetimeEnd);
        if (expiry <= row.NowUtc)
        {
            RecordExecutionRefusal("renewal", "lease_lost");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); return null;
        }
        await using var command = new NpgsqlCommand("""
            UPDATE appsurface_durable.work SET lease_expires_at=@expiry,revision=revision+1,updated_at=@now
            WHERE scope_id=@scope_id AND work_id=@work_id AND revision=@revision RETURNING revision;
            """, connection, transaction);
        AddExecutionRowParameters(command, row); command.Parameters.AddWithValue("expiry", expiry.UtcDateTime);
        var revision = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The locked renewal lost its revision."));
        var renewed = claim with { LeaseExpiresAtUtc = expiry, Revision = revision, CancellationRequested = row.CancellationRequested, Execution = row.ExecutionSnapshot };
        await UpdateExecutionDispatchAsync(connection, transaction, row, row.State, row.DueAtUtc, expiry, revision, row.InvocationAdmittedAtUtc is not null, cancellationToken).ConfigureAwait(false);
        await InsertHistoryAsync(connection, transaction, renewed, "lease_renewed", false, "{}", cancellationToken, row.NowUtc).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return renewed;
    }
    /// <summary>Distinguishes an independently lost lease from the immutable deadline cap.</summary>
    private static bool HasExpiredOrdinaryLease(PostgreSqlWorkExecutionRow row) =>
        row.LeaseExpiresAtUtc <= row.NowUtc
        && !(row.Deadline is { } deadline && row.NowUtc >= deadline.NotAfterUtc
            && row.LeaseExpiresAtUtc >= deadline.NotAfterUtc);

}
