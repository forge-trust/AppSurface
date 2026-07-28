// docs:snippet durable-postgresql-accept-work:start
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace DurablePostgreSqlConsumer;

internal static class PostgreSqlReadmeProof
{
    internal static async ValueTask<DurableOperationResult<DurableWorkAcceptance>> AcceptAsync(
        NpgsqlDataSource dataSource,
        IDurableWorkRegistry workRegistry,
        Guid runtimeEpoch,
        Guid expectedStoreId,
        NpgsqlTransaction transaction,
        DurableWorkRequest request,
        CancellationToken cancellationToken)
    {
        var options = new PostgreSqlDurableWorkOptions(
            runtimeEpoch,
            expectedStoreId,
            PostgreSqlDurableWakeNotificationMode.Disabled);

        var writer = new PostgreSqlDurableWorkTransactionWriter(dataSource, workRegistry, options);
        var accepted = await writer.EnqueueAsync(transaction, request, cancellationToken);
        if (!accepted.IsSuccess)
        {
            await transaction.RollbackAsync(cancellationToken);
            return accepted;
        }

        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }
}
// docs:snippet durable-postgresql-accept-work:end
