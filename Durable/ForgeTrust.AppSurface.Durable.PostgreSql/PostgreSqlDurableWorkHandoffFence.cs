using System.Data;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Scoped, invocation-bound savepoint protocol; owns no caller resources or native transitions.</summary>
internal sealed class PostgreSqlDurableWorkHandoffFence : IDurableWorkHandoffFence
{
    private static readonly AsyncLocal<Invocation?> Current = new();
    private static readonly Uri Documentation = new("https://github.com/forge-trust/AppSurface/blob/main/Durable/handoff-fence.md");
    private readonly PostgreSqlDurableStoreTarget _target;
    private readonly PostgreSqlDurableWorkOptions _options;
    private readonly PostgreSqlDurableHandoffCheckpointHook _checkpoints;
    private Invocation? _invocation;
    private int _executing;

    internal PostgreSqlDurableWorkHandoffFence(NpgsqlDataSource source, PostgreSqlDurableWorkOptions options,
        PostgreSqlDurableHandoffCheckpointHook? checkpoints = null)
    {
        _target = PostgreSqlDurableStoreTarget.Create(source.ConnectionString);
        _options = options;
        _checkpoints = checkpoints ?? PostgreSqlDurableHandoffCheckpointHook.NoOp;
    }

    /// <summary>Enters authority only around executor invocation after permit commit and existing admission.</summary>
    /// <remarks>The immutable permit identity does not cache renewal-sensitive expiry or revision. Dispose deactivates inherited contexts.</remarks>
    internal IDisposable EnterInvocation(PostgreSqlEffectPermit permit)
    {
        var invocation = new Invocation(permit, Current.Value);
        if (Interlocked.CompareExchange(ref _invocation, invocation, null) is not null)
        {
            throw new InvalidOperationException("The scoped handoff fence is already bound to an invocation.");
        }

        Current.Value = invocation;
        return invocation;
    }

    private bool IsActive(Invocation invocation) => ReferenceEquals(Current.Value, invocation)
        && ReferenceEquals(_invocation, invocation) && Volatile.Read(ref invocation._active) == 1;

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableWorkHandoffResult>> ExecuteAsync(NpgsqlTransaction transaction,
        Func<NpgsqlTransaction, CancellationToken, ValueTask<bool>> authorizeAndRecordHandoff,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(authorizeAndRecordHandoff);
        var invocation = _invocation;
        if (invocation is null || !IsActive(invocation))
        {
            return Refused(DurableProblemCodes.ClaimLost);
        }

        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("Handoff operations on one scoped instance cannot overlap or reenter.");
        }

        try
        {
            var connection = PostgreSqlDurableTransactionResources.RequireActive(transaction, _target);
            if (transaction.IsolationLevel != IsolationLevel.ReadCommitted)
            {
                throw new InvalidOperationException("The handoff fence requires a caller-owned ReadCommitted transaction.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteBoundAsync(connection, transaction, invocation, authorizeAndRecordHandoff, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _executing, 0); }
    }

    private async ValueTask<DurableOperationResult<DurableWorkHandoffResult>> ExecuteBoundAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Invocation invocation,
        Func<NpgsqlTransaction, CancellationToken, ValueTask<bool>> callback, CancellationToken cancellationToken)
    {
        var operation = "ash_op_" + Guid.NewGuid().ToString("N");
        var callbackSavepoint = "ash_cb_" + Guid.NewGuid().ToString("N");
        // If SAVE fails, no established savepoint is available and the caller must assess its transaction.
        await transaction.SaveAsync(operation, cancellationToken).ConfigureAwait(false);
        string? priorScope = null;
        var captured = false;
        var operationReleased = false;
        var callbackCreated = false;
        var callbackReleased = false;
        try
        {
            await using (var capture = new NpgsqlCommand("SELECT current_setting('appsurface_durable.scope_id', true);", connection, transaction))
            {
                priorScope = await capture.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            }

            captured = true;
            await PostgreSqlDurableWorkStore.AcquireEpochFenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableWorkStore.ValidateSchemaAsync(connection, transaction, _options.ExpectedStoreId, cancellationToken).ConfigureAwait(false);
            await PostgreSqlDurableWorkStore.ValidateCurrentEpochAsync(connection, transaction, _options.RuntimeEpoch, cancellationToken).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, invocation._permit.Claim.ScopeId.Value, cancellationToken).ConfigureAwait(false);
            var (facts, problem) = await PostgreSqlDurableHandoffFacts.ReadAsync(connection, transaction, invocation._permit, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                await UndoOperationAsync(connection, transaction, operation, priorScope, captured).ConfigureAwait(false);
                operationReleased = true;
                return Refused(problem);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActive(invocation))
            {
                await UndoOperationAsync(connection, transaction, operation, priorScope, captured).ConfigureAwait(false);
                operationReleased = true;
                return Refused(DurableProblemCodes.ClaimLost);
            }

            await transaction.SaveAsync(callbackSavepoint, cancellationToken).ConfigureAwait(false);
            callbackCreated = true;
            var prepared = await callback(transaction, cancellationToken).ConfigureAwait(false);
            // Npgsql retains the exact connection on an active transaction; completion or disposal fails this check.
            _ = PostgreSqlDurableTransactionResources.RequireActive(transaction, _target);

            using (var cleanup = CleanupToken())
            {
                if (!prepared)
                {
                    await _checkpoints.ReachAsync(PostgreSqlDurableHandoffCheckpoint.BeforeRollback, cleanup.Token).ConfigureAwait(false);
                    await transaction.RollbackAsync(callbackSavepoint, cleanup.Token).ConfigureAwait(false);
                    await ReleaseSavepointAsync(transaction, callbackSavepoint, cleanup.Token).ConfigureAwait(false);
                    callbackReleased = true;
                }
                await RestoreScopeAsync(connection, transaction, priorScope, cleanup.Token).ConfigureAwait(false);
            }
            await _checkpoints.ReachAsync(PostgreSqlDurableHandoffCheckpoint.BeforeFinalCheck, cancellationToken).ConfigureAwait(false);
            var now = await PostgreSqlDurableHandoffFacts.SampleTimeAsync(connection, transaction, facts!.UsesExecutionClock, cancellationToken).ConfigureAwait(false);
            var finalProblem = !IsActive(invocation) ? DurableProblemCodes.ClaimLost : PostgreSqlDurableHandoffFacts.TimeProblem(facts!, now);
            cancellationToken.ThrowIfCancellationRequested();
            if (finalProblem is not null)
            {
                // The still-open callback savepoint removes application writes while retaining native locks.
                using var cleanup = CleanupToken();
                if (!callbackReleased)
                {
                    await _checkpoints.ReachAsync(PostgreSqlDurableHandoffCheckpoint.BeforeRollback, cleanup.Token).ConfigureAwait(false);
                    await transaction.RollbackAsync(callbackSavepoint, cleanup.Token).ConfigureAwait(false);
                    await ReleaseSavepointAsync(transaction, callbackSavepoint, cleanup.Token).ConfigureAwait(false);
                    callbackReleased = true;
                }
                await RestoreScopeAsync(connection, transaction, priorScope, cleanup.Token).ConfigureAwait(false);
                await ReleaseSavepointAsync(transaction, operation, cleanup.Token).ConfigureAwait(false);
                operationReleased = true;
                return Refused(finalProblem);
            }
            // The last lifetime/token checks above are the success linearization point. Later signals cannot reverse it.
            using (var cleanup = CleanupToken())
            {
                await _checkpoints.ReachAsync(PostgreSqlDurableHandoffCheckpoint.AfterFinalCheck, cleanup.Token).ConfigureAwait(false);
                // PostgreSQL RELEASE of the enclosing savepoint also releases its nested callback savepoint.
                await ReleaseSavepointAsync(transaction, operation, cleanup.Token).ConfigureAwait(false);
            }
            operationReleased = true;
            return DurableOperationResult<DurableWorkHandoffResult>.Success(new(prepared));
        }
        catch (Exception original) when (!operationReleased)
        {
            try
            {
                if (callbackCreated && !callbackReleased && !cancellationToken.IsCancellationRequested)
                {
                    // Recover callback errors, including an aborted SQL transaction, without releasing native locks.
                    using var cleanup = CleanupToken();
                    await _checkpoints.ReachAsync(PostgreSqlDurableHandoffCheckpoint.BeforeRollback, cleanup.Token).ConfigureAwait(false);
                    await transaction.RollbackAsync(callbackSavepoint, cleanup.Token).ConfigureAwait(false);
                    await ReleaseSavepointAsync(transaction, callbackSavepoint, cleanup.Token).ConfigureAwait(false);
                    await RestoreScopeAsync(connection, transaction, priorScope, cleanup.Token).ConfigureAwait(false);
                    await ReleaseSavepointAsync(transaction, operation, cleanup.Token).ConfigureAwait(false);
                }
                else
                {
                    await UndoOperationAsync(connection, transaction, operation, priorScope, captured).ConfigureAwait(false);
                }
            }
            catch (Exception cleanup) { throw new AggregateException("Handoff cleanup failed; roll back and dispose the caller transaction.", original, cleanup); }
            throw;
        }
    }

    private static CancellationTokenSource CleanupToken() => new(TimeSpan.FromSeconds(5));

    private async ValueTask UndoOperationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string operation, string? priorScope, bool captured)
    {
        using var cleanup = CleanupToken();
        await _checkpoints.ReachAsync(PostgreSqlDurableHandoffCheckpoint.BeforeRollback, cleanup.Token).ConfigureAwait(false);
        await transaction.RollbackAsync(operation, cleanup.Token).ConfigureAwait(false);
        if (captured)
        {
            await RestoreScopeAsync(connection, transaction, priorScope, cleanup.Token).ConfigureAwait(false);
        }

        await ReleaseSavepointAsync(transaction, operation, cleanup.Token).ConfigureAwait(false);
    }

    /// <summary>Restores caller scope with an intentional cleanup barrier; a failure requires caller recovery.</summary>
    private async ValueTask RestoreScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string? priorScope, CancellationToken token)
    {
        await _checkpoints.ReachAsync(PostgreSqlDurableHandoffCheckpoint.BeforeRestore, token).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, priorScope, token).ConfigureAwait(false);
    }

    /// <summary>Releases an established savepoint with an intentional cleanup barrier; owns no outer transaction.</summary>
    private async ValueTask ReleaseSavepointAsync(NpgsqlTransaction transaction, string savepoint, CancellationToken token)
    {
        await _checkpoints.ReachAsync(PostgreSqlDurableHandoffCheckpoint.BeforeRelease, token).ConfigureAwait(false);
        await transaction.ReleaseAsync(savepoint, token).ConfigureAwait(false);
    }

    /// <summary>Restores effective scope including the empty/no-scope state; never changes the role or session defaults.</summary>
    private static async ValueTask SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string? scope, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope, true);", connection, transaction);
        command.Parameters.AddWithValue("scope", scope ?? string.Empty);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static DurableOperationResult<DurableWorkHandoffResult> Refused(string code) =>
        DurableOperationResult<DurableWorkHandoffResult>.Failure(new(code,
            "The current native invocation did not authorize this handoff.",
            "A required invocation, scope, claim, lease, deadline, dispatch or permit fence was not current.",
            "Stop provider I/O and follow the bounded problem code and existing recovery policy; do not bypass the fence.",
            Documentation, Guid.NewGuid().ToString("N")));

    private sealed class Invocation(PostgreSqlEffectPermit permit, Invocation? previous) : IDisposable
    {
        internal readonly PostgreSqlEffectPermit _permit = permit;
        internal int _active = 1;
        public void Dispose()
        {
            Interlocked.Exchange(ref _active, 0);
            if (ReferenceEquals(Current.Value, this))
            {
                Current.Value = previous;
            }
        }
    }
}

/// <summary>Internal deterministic barriers for the final decision and cleanup; production has no callback.</summary>
internal enum PostgreSqlDurableHandoffCheckpoint { BeforeFinalCheck, AfterFinalCheck, BeforeRollback, BeforeRestore, BeforeRelease }

/// <summary>Intentional internal test seam; carries no payload or authority and exposes no public binder.</summary>
internal sealed class PostgreSqlDurableHandoffCheckpointHook(
    Func<PostgreSqlDurableHandoffCheckpoint, CancellationToken, ValueTask>? callback = null)
{
    internal static PostgreSqlDurableHandoffCheckpointHook NoOp { get; } = new();
    internal ValueTask ReachAsync(PostgreSqlDurableHandoffCheckpoint checkpoint, CancellationToken token) =>
        callback?.Invoke(checkpoint, token) ?? ValueTask.CompletedTask;
}
