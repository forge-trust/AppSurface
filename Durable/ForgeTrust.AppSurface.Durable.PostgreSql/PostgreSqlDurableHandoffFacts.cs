using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Current lease and optional deadline facts read for one invocation-bound handoff.</summary>
/// <remarks>
/// These facts are reread under native row locks for each call. Lease expiry and Work revision are deliberately not
/// cached or compared with the claim snapshot because a legitimate renewal can change them. A null deadline means
/// either legacy Work or opted-in Work without a configured absolute deadline. A previously observed deadline remains
/// authoritative even if the database execution clock later moves backward; the caller has already validated the
/// active invocation and exact permit before relying on this record.
/// </remarks>
/// <param name="LeaseExpiresAtUtc">The authoritative current lease expiry from the locked Work row.</param>
/// <param name="DeadlineUtc">The configured immutable execution deadline for opted-in Work, when present.</param>
/// <param name="DeadlineReached">Whether opted-in Work permanently recorded that its deadline had elapsed.</param>
/// <param name="UsesExecutionClock">Whether opted-in Work uses the configured execution clock instead of the legacy PostgreSQL clock.</param>
internal sealed record PostgreSqlDurableHandoffFacts(
    DateTimeOffset LeaseExpiresAtUtc,
    DateTimeOffset? DeadlineUtc,
    bool DeadlineReached,
    bool UsesExecutionClock)
{
    /// <summary>
    /// Reads and validates the exact native authority for a caller-transaction handoff, without changing protocol state.
    /// </summary>
    /// <remarks>
    /// The caller must already have validated schema and active epoch, bound the transaction-local scope, and verified
    /// that <paramref name="transaction"/> belongs to <paramref name="connection"/>. Reads lock in the protocol order:
    /// active scope <c>FOR SHARE</c>, exact Work <c>FOR UPDATE</c>, its dispatch row <c>FOR UPDATE</c>, then only the
    /// supplied permit ID <c>FOR UPDATE</c>. It samples authoritative time immediately after the Work checks and stops
    /// before dispatch/permit reads if the lease or deadline has elapsed, then samples again after dispatch and permit
    /// lock waits. The dispatch row must match the claim's exact dispatch ID, remain leased, and project the locked Work
    /// revision; this compares current database rows, not the possibly older revision in a permit snapshot. The lookup
    /// uses existing scope, Work, dispatch, and permit keys and never scans Work history or prior permits. It supports
    /// legacy rows explicitly and does not create a scope, consume invocation admission, mutate a permit, or complete
    /// the caller's transaction. Legacy Work uses <c>pg_catalog.clock_timestamp()</c>; opted-in Work uses its configured
    /// execution clock. Refusal precedence is missing/disabled/stale scope, missing Work, Work identity,
    /// terminal state, cancellation, lease then deadline, dispatch/permit status, exact permit identity, and finally
    /// the existing invocation-admission marker for opted-in Work.
    /// </remarks>
    /// <param name="connection">The already-open PostgreSQL connection for the caller-owned transaction.</param>
    /// <param name="transaction">The active caller-owned transaction used for every lock and read.</param>
    /// <param name="permit">The exact effect permit and invocation identity being authorized.</param>
    /// <param name="cancellationToken">Cancels database reads while the caller retains resource ownership.</param>
    /// <returns>
    /// Current lease/deadline facts with a null problem code when valid, or a null facts value and the first applicable
    /// bounded Durable problem code when native authority is refused. Database and cancellation exceptions propagate.
    /// </returns>
    internal static async ValueTask<(PostgreSqlDurableHandoffFacts? Facts, string? ProblemCode)> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlEffectPermit permit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(permit);

        var claim = permit.Claim;

        const string scopeSql = """
            SELECT generation, state
            FROM appsurface_durable.scope
            WHERE scope_id = @scope_id
            FOR SHARE;
            """;
        long scopeGeneration;
        string scopeState;
        await using (var scopeCommand = new NpgsqlCommand(scopeSql, connection, transaction))
        {
            scopeCommand.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
            await using var reader = await scopeCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return (null, DurableProblemCodes.ScopeNotFound);
            }

            scopeGeneration = reader.GetInt64(0);
            scopeState = reader.GetString(1);
        }

        if (!string.Equals(scopeState, "active", StringComparison.Ordinal))
        {
            return (null, DurableProblemCodes.ScopeDisabled);
        }

        if (scopeGeneration != claim.ScopeGeneration)
        {
            return (null, DurableProblemCodes.ScopeGenerationConflict);
        }

        const string workSql = """
            SELECT activity_id, attempt_number, lease_generation, lease_owner, scope_generation, runtime_epoch,
                   state, revision, cancellation_requested_at IS NOT NULL, lease_expires_at,
                   execution_policy_schema IS NOT NULL, execution_not_after,
                   execution_deadline_reached_at IS NOT NULL
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id AND work_id = @work_id
            FOR UPDATE;
            """;
        string activityId;
        int attemptNumber;
        long leaseGeneration;
        string? leaseOwner;
        long workScopeGeneration;
        Guid runtimeEpoch;
        string workState;
        long workRevision;
        bool cancellationRequested;
        DateTimeOffset? leaseExpiresAtUtc;
        bool isOptedIn;
        DateTimeOffset? deadlineUtc;
        bool deadlineReached;
        await using (var workCommand = new NpgsqlCommand(workSql, connection, transaction))
        {
            workCommand.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
            workCommand.Parameters.AddWithValue("work_id", claim.WorkId.Value);
            await using var reader = await workCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return (null, DurableProblemCodes.WorkNotFound);
            }

            activityId = reader.GetString(0);
            attemptNumber = reader.GetInt32(1);
            leaseGeneration = reader.GetInt64(2);
            leaseOwner = reader.IsDBNull(3) ? null : reader.GetString(3);
            workScopeGeneration = reader.GetInt64(4);
            runtimeEpoch = reader.GetGuid(5);
            workState = reader.GetString(6);
            workRevision = reader.GetInt64(7);
            cancellationRequested = reader.GetBoolean(8);
            leaseExpiresAtUtc = reader.IsDBNull(9) ? null : ReadUtc(reader, 9);
            isOptedIn = reader.GetBoolean(10);
            deadlineUtc = isOptedIn && !reader.IsDBNull(11) ? ReadUtc(reader, 11) : null;
            deadlineReached = isOptedIn && reader.GetBoolean(12);
        }

        if (!string.Equals(activityId, claim.ActivityId, StringComparison.Ordinal)
            || attemptNumber != claim.AttemptNumber
            || leaseGeneration != claim.LeaseGeneration
            || !string.Equals(leaseOwner, claim.LeaseOwner, StringComparison.Ordinal)
            || workScopeGeneration != claim.ScopeGeneration
            || runtimeEpoch != claim.RuntimeEpoch)
        {
            return (null, DurableProblemCodes.ClaimLost);
        }

        if (IsTerminal(workState))
        {
            return (null, DurableProblemCodes.AlreadyTerminal);
        }

        if (cancellationRequested || string.Equals(workState, "cancel_pending", StringComparison.Ordinal))
        {
            return (null, DurableProblemCodes.ClaimLost);
        }

        if (leaseExpiresAtUtc is not { } leaseExpiry)
        {
            return (null, DurableProblemCodes.LeaseLost);
        }

        var facts = new PostgreSqlDurableHandoffFacts(leaseExpiry, deadlineUtc, deadlineReached, isOptedIn);
        var afterWorkNowUtc = await SampleTimeAsync(
            connection, transaction, facts.UsesExecutionClock, cancellationToken).ConfigureAwait(false);
        var timeProblem = TimeProblem(facts, afterWorkNowUtc);
        if (timeProblem is not null)
        {
            return (null, timeProblem);
        }

        const string dispatchSql = """
            SELECT dispatch_id, state, expected_revision
            FROM appsurface_durable.dispatch
            WHERE scope_id = @scope_id AND aggregate_kind = 'work' AND aggregate_id = @work_id
            FOR UPDATE;
            """;
        Guid? dispatchId = null;
        string? dispatchState = null;
        long? dispatchExpectedRevision = null;
        await using (var dispatchCommand = new NpgsqlCommand(dispatchSql, connection, transaction))
        {
            dispatchCommand.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
            dispatchCommand.Parameters.AddWithValue("work_id", claim.WorkId.Value);
            await using var reader = await dispatchCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                dispatchId = reader.GetGuid(0);
                dispatchState = reader.GetString(1);
                dispatchExpectedRevision = reader.GetInt64(2);
            }
        }

        const string permitSql = """
            SELECT scope_id, work_id, activity_id, attempt_number, lease_generation, scope_generation,
                   runtime_epoch, status, invocation_admitted_at
            FROM appsurface_durable.effect_permit
            WHERE permit_id = @permit_id
            FOR UPDATE;
            """;
        string? permitScopeId = null;
        string? permitWorkId = null;
        string? permitActivityId = null;
        var permitAttemptNumber = 0;
        long permitLeaseGeneration = 0;
        long permitScopeGeneration = 0;
        Guid permitRuntimeEpoch = default;
        string? permitStatus = null;
        var hasInvocationAdmission = false;
        await using (var permitCommand = new NpgsqlCommand(permitSql, connection, transaction))
        {
            permitCommand.Parameters.AddWithValue("permit_id", permit.PermitId);
            await using var reader = await permitCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                permitScopeId = reader.GetString(0);
                permitWorkId = reader.GetString(1);
                permitActivityId = reader.GetString(2);
                permitAttemptNumber = reader.GetInt32(3);
                permitLeaseGeneration = reader.GetInt64(4);
                permitScopeGeneration = reader.GetInt64(5);
                permitRuntimeEpoch = reader.GetGuid(6);
                permitStatus = reader.GetString(7);
                hasInvocationAdmission = !reader.IsDBNull(8);
            }
        }

        // Recheck after dispatch and permit lock waits; time can expire while either row is being acquired.
        var afterPermitNowUtc = await SampleTimeAsync(
            connection, transaction, facts.UsesExecutionClock, cancellationToken).ConfigureAwait(false);
        timeProblem = TimeProblem(facts, afterPermitNowUtc);
        if (timeProblem is not null)
        {
            return (null, timeProblem);
        }

        if (!string.Equals(workState, "effect_permitted", StringComparison.Ordinal)
            || dispatchId != claim.DispatchId
            || !string.Equals(dispatchState, "leased", StringComparison.Ordinal)
            || dispatchExpectedRevision != workRevision
            || permitStatus is null
            || !string.Equals(permitStatus, "granted", StringComparison.Ordinal))
        {
            return (null, DurableProblemCodes.ClaimLost);
        }

        if (!string.Equals(permitScopeId, claim.ScopeId.Value, StringComparison.Ordinal)
            || !string.Equals(permitWorkId, claim.WorkId.Value, StringComparison.Ordinal)
            || !string.Equals(permitActivityId, claim.ActivityId, StringComparison.Ordinal)
            || permitAttemptNumber != claim.AttemptNumber
            || permitLeaseGeneration != claim.LeaseGeneration
            || permitScopeGeneration != claim.ScopeGeneration
            || permitRuntimeEpoch != claim.RuntimeEpoch)
        {
            return (null, DurableProblemCodes.ClaimLost);
        }

        if (isOptedIn && !hasInvocationAdmission)
        {
            return (null, DurableProblemCodes.ClaimLost);
        }

        return (facts, null);
    }

    /// <summary>Samples the clock already used by the matching legacy or opted-in admission path.</summary>
    /// <param name="connection">The open connection holding the caller-owned transaction.</param>
    /// <param name="transaction">The caller-owned transaction after the required native locks.</param>
    /// <param name="usesExecutionClock">True for opted-in Work; false to preserve legacy PostgreSQL clock semantics.</param>
    /// <param name="cancellationToken">Cancels the clock query.</param>
    /// <returns>The current UTC time from the matching execution protocol clock.</returns>
    internal static async ValueTask<DateTimeOffset> SampleTimeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool usesExecutionClock,
        CancellationToken cancellationToken)
    {
        if (usesExecutionClock)
        {
            return await PostgreSqlDurableWorkStore.SampleExecutionTimeAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand("SELECT pg_catalog.clock_timestamp();", connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The PostgreSQL clock returned null.");
        return new DateTimeOffset((DateTime)value, TimeSpan.Zero);
    }

    /// <summary>Returns lease expiry before an elapsed or permanently observed opted-in deadline.</summary>
    /// <param name="facts">The freshly locked lease and optional immutable deadline.</param>
    /// <param name="nowUtc">Authoritative database time sampled after native lock acquisition.</param>
    /// <returns><see cref="DurableProblemCodes.LeaseLost"/>, <see cref="DurableProblemCodes.ExecutionDeadlineReached"/>, or null.</returns>
    internal static string? TimeProblem(PostgreSqlDurableHandoffFacts facts, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.LeaseExpiresAtUtc <= nowUtc)
        {
            return DurableProblemCodes.LeaseLost;
        }

        return facts.DeadlineReached || facts.DeadlineUtc is { } deadline && deadline <= nowUtc
            ? DurableProblemCodes.ExecutionDeadlineReached
            : null;
    }

    private static bool IsTerminal(string state) => state is
        "succeeded" or
        "succeeded_after_cancel_requested" or
        "failed" or
        "canceled_before_effect";

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);
}
