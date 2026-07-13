using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Accepts durable work in a short runtime-owned PostgreSQL transaction.
/// </summary>
public sealed class PostgreSqlDurableWorkClient : IDurableWorkClient
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDurableWorkTransactionWriter _transactionWriter;

    /// <summary>
    /// Initializes a durable work client.
    /// </summary>
    /// <param name="dataSource">Runtime-role PostgreSQL data source.</param>
    /// <param name="transactionWriter">Writer configured with the same runtime epoch.</param>
    public PostgreSqlDurableWorkClient(
        NpgsqlDataSource dataSource,
        IDurableWorkTransactionWriter transactionWriter)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _transactionWriter = transactionWriter ?? throw new ArgumentNullException(nameof(transactionWriter));
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
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
