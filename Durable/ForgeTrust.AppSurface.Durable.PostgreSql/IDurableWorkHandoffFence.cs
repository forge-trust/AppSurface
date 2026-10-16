using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Validates the current native invocation and prepares an application handoff in its caller's transaction.</summary>
/// <remarks>
/// Resolve this scoped service through AddAppSurfaceDurablePostgreSql and use it only inside the actual Work executor.
/// The caller owns an active ReadCommitted transaction targeting the configured store. The callback must use only that
/// transaction and connection, acquire application locks after native locks, and perform database work only. It must not
/// complete or dispose the transaction, perform provider I/O, or escape work past invocation exit. Await the operation
/// and complete and dispose caller resources inside the invocation before provider I/O. Preparation is provisional until
/// caller commit; an uncertain commit requires application-ledger and existing provider-safety reconciliation.
/// See the package's handoff reference for lock ordering, host timeout prerequisites, and cooperation limits.
/// </remarks>
public interface IDurableWorkHandoffFence
{
    /// <summary>Runs one application authorization and conditional handoff write under current native fences.</summary>
    /// <param name="transaction">Exact caller-owned, uncommitted ReadCommitted transaction on an open connection.</param>
    /// <param name="authorizeAndRecordHandoff">Database-only callback, called at most once with the supplied resources and token.</param>
    /// <param name="cancellationToken">Caller cancellation; observed cancellation before the final check removes operation writes.</param>
    /// <returns>A provisional preparation, successful denial after removing callback writes, or a bounded native refusal.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">Resources, target or isolation are invalid, or a call overlaps or reenters.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before the final success check, or the callback throws cancellation.</exception>
    /// <exception cref="AggregateException">Cleanup fails after another failure; both causes are retained and outer rollback/disposal is required.</exception>
    /// <remarks>
    /// Sequential calls are allowed; application conditional writes govern duplicates. There are no callback retries.
    /// SQL and transport exceptions propagate after feasible savepoint cleanup. Cleanup never completes the outer
    /// transaction. A cancellation signal after the last check cannot retroactively change preparation; check the token
    /// before committing and reconcile a failed or ambiguous commit before sending. An improper callback commit cannot
    /// be undone by the framework. The result is not delivery confirmation or authority transferable outside this invocation.
    /// </remarks>
    ValueTask<DurableOperationResult<DurableWorkHandoffResult>> ExecuteAsync(
        NpgsqlTransaction transaction,
        Func<NpgsqlTransaction, CancellationToken, ValueTask<bool>> authorizeAndRecordHandoff,
        CancellationToken cancellationToken = default);
}

/// <summary>Describes whether the callback prepared an application handoff that still requires caller commit.</summary>
/// <param name="HandoffPrepared">True for provisional preparation; false for application denial with callback writes removed.</param>
public sealed record DurableWorkHandoffResult(bool HandoffPrepared);
