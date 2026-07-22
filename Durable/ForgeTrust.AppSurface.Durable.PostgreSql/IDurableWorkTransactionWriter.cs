using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Accepts durable Work through the caller's exact active PostgreSQL transaction.</summary>
/// <remarks>
/// The writer never opens, commits, rolls back, replaces, or disposes the supplied transaction. Domain state and Work
/// acceptance therefore commit or roll back together in the same database.
/// </remarks>
public interface IDurableWorkTransactionWriter
{
    /// <summary>Writes one durable acceptance without taking ownership of the transaction.</summary>
    /// <param name="transaction">Caller-owned active Npgsql transaction.</param>
    /// <param name="request">Validated Work request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stable new or duplicate acceptance, or an actionable domain problem.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transaction"/> or <paramref name="request"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the transaction is disposed, inactive, closed, or targets a different PostgreSQL store.</exception>
    /// <exception cref="DurableRuntimeSchemaException">Thrown with safe schema status when the durable schema is absent or incompatible. When a PostgreSQL error exposed the missing schema, <see cref="Exception.InnerException"/> retains that original failure.</exception>
    /// <exception cref="NpgsqlException">The original PostgreSQL or transport failure. The caller must roll back the transaction.</exception>
    ValueTask<DurableOperationResult<DurableWorkAcceptance>> EnqueueAsync(
        NpgsqlTransaction transaction,
        DurableWorkRequest request,
        CancellationToken cancellationToken = default);
}
