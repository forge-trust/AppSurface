using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurablePublicContractTests
{
    [Fact]
    public void WorkOptions_RequireStoreAndEpochAndDefaultNotificationsOff()
    {
        var epoch = Guid.NewGuid();
        var storeId = Guid.NewGuid();

        var options = new PostgreSqlDurableWorkOptions(epoch, storeId);

        Assert.Equal(epoch, options.RuntimeEpoch);
        Assert.Equal(storeId, options.ExpectedStoreId);
        Assert.Equal(PostgreSqlDurableWakeNotificationMode.Disabled, options.WakeNotificationMode);
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableWorkOptions(Guid.Empty, storeId));
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableWorkOptions(epoch, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableWorkOptions(
            epoch,
            storeId,
            (PostgreSqlDurableWakeNotificationMode)int.MaxValue));
    }

    [Fact]
    public void WorkWriterAndClient_RequireExplicitDependencies()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var registry = new DurableWorkRegistry([]);
        var options = new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid());

        _ = new PostgreSqlDurableWorkTransactionWriter(dataSource, registry, options);
        _ = new PostgreSqlDurableWorkClient(dataSource, registry, options);

        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableWorkTransactionWriter(null!, registry, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableWorkTransactionWriter(dataSource, null!, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableWorkTransactionWriter(dataSource, registry, null!));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableWorkClient(null!, registry, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableWorkClient(dataSource, null!, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableWorkClient(dataSource, registry, null!));
    }

    [Theory]
    [InlineData("LOCALHOST", "localhost", true)]
    [InlineData("localhost", "127.0.0.1", false)]
    [InlineData("localhost", "localhost", true)]
    public void StoreTarget_UsesExactCanonicalConnectionTarget(string configuredHost, string actualHost, bool expected)
    {
        var target = PostgreSqlDurableStoreTarget.Create(
            $"Host={configuredHost};Port=5432;Database=durable_contracts;Username=durable");
        using var connection = new NpgsqlConnection(
            $"Host={actualHost};Port=5432;Database=durable_contracts;Username=durable");

        Assert.Equal(expected, target.Matches(connection));
    }

    [Fact]
    public async Task WorkStore_RejectsInvalidConstructionAndDiscoveryBoundsBeforeOpeningConnection()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");

        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableWorkStore(null!, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableWorkStore(dataSource, Guid.Empty));

        var store = new PostgreSqlDurableWorkStore(dataSource, Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.DiscoverAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.DiscoverAsync(1_001));
    }

    [Fact]
    public async Task WorkClients_RejectNullRequestsAndTransactionsBeforeOpeningConnection()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var registry = new DurableWorkRegistry([]);
        var options = new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid());
        var writer = new PostgreSqlDurableWorkTransactionWriter(dataSource, registry, options);
        var client = new PostgreSqlDurableWorkClient(dataSource, registry, options);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await writer.EnqueueAsync(null!, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.EnqueueAsync(null!));
    }

    [Fact]
    public void StoreTarget_RejectsMissingCoordinatesAndNullConnections()
    {
        Assert.Throws<ArgumentException>(() => PostgreSqlDurableStoreTarget.Create("Username=durable"));
        Assert.Throws<ArgumentException>(() => PostgreSqlDurableStoreTarget.Create("Host=localhost"));

        var target = PostgreSqlDurableStoreTarget.Create(
            "Host=LOCALHOST, 127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");

        Assert.Equal("localhost,127.0.0.1", target.Host);
        Assert.Throws<ArgumentNullException>(() => target.Matches(null!));
    }

    [Fact]
    public void CleanupFailureFilter_OnlyAcceptsExpectedProviderAndLifecycleFailures()
    {
        Assert.True(PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(new NpgsqlException("provider")));
        Assert.True(PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(new InvalidOperationException("state")));
        Assert.True(PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(new ObjectDisposedException("transaction")));
        Assert.True(PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(new OperationCanceledException("canceled")));
        Assert.False(PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(new ArgumentException("programming error")));
    }
}
