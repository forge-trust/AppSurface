using System.Data;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Validates an exact active caller transaction and configured physical target without taking ownership.</summary>
/// <remarks>Shared by enqueue and handoff; preserves enqueue's resource and target exception conventions.</remarks>
internal static class PostgreSqlDurableTransactionResources
{
    /// <summary>Returns the supplied transaction's open connection after validating its physical store target.</summary>
    /// <param name="transaction">The caller-owned, uncommitted transaction; this method never completes or disposes it.</param>
    /// <param name="target">The configured PostgreSQL host, port and database identity.</param>
    /// <returns>The exact connection owned by the supplied transaction.</returns>
    /// <exception cref="InvalidOperationException">The transaction is inactive or does not target the configured store.</exception>
    internal static NpgsqlConnection RequireActive(NpgsqlTransaction transaction, PostgreSqlDurableStoreTarget target)
    {
        NpgsqlConnection? connection;
        try
        {
            connection = transaction.Connection;
        }
        catch (ObjectDisposedException exception)
        {
            throw new InvalidOperationException(
                "The supplied Npgsql transaction is not active on an open connection. Pass the exact uncommitted domain transaction.", exception);
        }

        if (connection is not { State: ConnectionState.Open })
        {
            throw new InvalidOperationException(
                "The supplied Npgsql transaction is not active on an open connection. Pass the exact uncommitted domain transaction.");
        }

        if (!target.Matches(connection))
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.StoreIdentityMismatch}: The caller transaction does not target the configured durable PostgreSQL host, port, and database.");
        }

        return connection;
    }
}
