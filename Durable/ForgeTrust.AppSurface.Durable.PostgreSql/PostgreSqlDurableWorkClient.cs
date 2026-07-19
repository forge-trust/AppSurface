using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Accepts durable Work in a short provider-owned PostgreSQL transaction.</summary>
public sealed class PostgreSqlDurableWorkClient : IDurableWorkClient
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlDurableWorkTransactionWriter _transactionWriter;

    /// <summary>Initializes a client for one validated store and runtime epoch.</summary>
    /// <param name="dataSource">Runtime-role PostgreSQL data source.</param>
    /// <param name="workRegistry">Immutable Work registrations.</param>
    /// <param name="options">Validated StoreId, epoch, and notification behavior.</param>
    public PostgreSqlDurableWorkClient(
        NpgsqlDataSource dataSource,
        IDurableWorkRegistry workRegistry,
        PostgreSqlDurableWorkOptions options)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _transactionWriter = new PostgreSqlDurableWorkTransactionWriter(dataSource, workRegistry, options);
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableWorkAcceptance>> EnqueueAsync(
        DurableWorkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _transactionWriter.EnqueueAsync(transaction, request, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the original database or transport failure; disposal owns transaction cleanup.
            }

            throw;
        }
    }
}
