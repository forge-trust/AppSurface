using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

await using var dataSource = NpgsqlDataSource.Create(
    "Host=127.0.0.1;Port=5432;Database=durable_consumer;Username=durable");
var registry = new DurableWorkRegistry([]);
var options = new PostgreSqlDurableWorkOptions(
    runtimeEpoch: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
    expectedStoreId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

IDurableWorkTransactionWriter writer = new PostgreSqlDurableWorkTransactionWriter(
    dataSource,
    registry,
    options);
IDurableWorkClient client = new PostgreSqlDurableWorkClient(dataSource, registry, options);

if (options.WakeNotificationMode != PostgreSqlDurableWakeNotificationMode.Disabled)
{
    throw new InvalidOperationException("PostgreSQL wake notifications must remain explicit and default off.");
}

Console.WriteLine($"{writer.GetType().Name}|{client.GetType().Name}|{options.WakeNotificationMode}");
