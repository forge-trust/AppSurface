using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Accepts durable work through the caller's exact active PostgreSQL transaction.
/// </summary>
/// <remarks>
/// The writer never opens, commits, rolls back, replaces, or disposes the supplied transaction. The application domain
/// mutation and durable acceptance must target the same physical PostgreSQL database. The accepted work row is the
/// transactional outbox record; no second outbox or dispatch copy is required.
/// </remarks>
public interface IDurableWorkTransactionWriter
{
    /// <summary>
    /// Writes a durable acceptance through <paramref name="transaction"/> without taking ownership of it.
    /// </summary>
    /// <param name="transaction">The caller-owned active Npgsql transaction.</param>
    /// <param name="request">Validated durable work request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stable acceptance or an actionable conflict problem.</returns>
    ValueTask<DurableOperationResult<DurableWorkAcceptance>> EnqueueAsync(
        NpgsqlTransaction transaction,
        DurableWorkRequest request,
        CancellationToken cancellationToken = default);
}
