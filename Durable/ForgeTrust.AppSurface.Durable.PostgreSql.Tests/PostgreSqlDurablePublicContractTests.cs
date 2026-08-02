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

    [Fact]
    public async Task ScheduleTypes_RequireExplicitDependenciesAndValidateBoundedPasses()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var registry = new DurableWorkRegistry([]);
        var workOptions = new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid());
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("durable");

        _ = new PostgreSqlDurableScheduleClient(dataSource, registry, workOptions, scheduleOptions);
        _ = new PostgreSqlDurableScheduleProcessor(dataSource, dataSource, registry, workOptions, scheduleOptions);
        Assert.Equal("durable", scheduleOptions.RuntimeRole);
        Assert.Equal(TimeSpan.FromDays(31), scheduleOptions.MaximumClockAdvance);
        Assert.Equal(TimeSpan.FromMinutes(2), scheduleOptions.LeaseDuration);
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableScheduleOptions(" "));
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableScheduleOptions(new string('r', 64)));
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableScheduleOptions("durable\u0001"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleOptions("durable", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleOptions("durable", TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleOptions("durable", leaseDuration: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleOptions("durable", leaseDuration: TimeSpan.FromMinutes(11)));
        var maximumOptions = new PostgreSqlDurableScheduleOptions(new string('r', 63), leaseDuration: TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), maximumOptions.LeaseDuration);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleProcessRequest("pass", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleProcessRequest("pass", 129));
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableScheduleProcessRequest(" "));
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableScheduleProcessRequest(new string('p', 201)));
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableScheduleProcessRequest("pass\u0001"));
        var maximumRequest = new PostgreSqlDurableScheduleProcessRequest(new string('p', 200), 128);
        Assert.Equal(128, maximumRequest.MaximumSchedules);

        var empty = new PostgreSqlDurableScheduleProcessResult(0, 0, 0, 0);
        Assert.Equal(0, empty.ClaimedSchedules);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleProcessResult(-1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleProcessResult(0, -1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleProcessResult(0, 0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlDurableScheduleProcessResult(0, 0, 0, -1));
        var client = new PostgreSqlDurableScheduleClient(dataSource, registry, workOptions, scheduleOptions);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.CreateAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.UpdateAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.ApplyLifecycleCommandAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.ListAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.ExplainNextOccurrencesAsync(null!));
        var processor = new PostgreSqlDurableScheduleProcessor(dataSource, dataSource, registry, workOptions, scheduleOptions);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await processor.ProcessDueAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.ListAsync(new DurableScheduleListRequest(
            new DurableScopeId("schedule-contract-scope"),
            continuationToken: "not-a-schedule-continuation-token")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.ListAsync(new DurableScheduleListRequest(
            new DurableScopeId("schedule-contract-scope"),
            continuationToken: "eyJWZXJzaW9uIjoyLCJTY2hlZHVsZUlkIjoic2NoZWR1bGUtY29udHJhY3QifQ")));

        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableScheduleClient(null!, registry, workOptions, scheduleOptions));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableScheduleClient(dataSource, null!, workOptions, scheduleOptions));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableScheduleClient(dataSource, registry, null!, scheduleOptions));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableScheduleClient(dataSource, registry, workOptions, null!));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableScheduleProcessor(null!, dataSource, registry, workOptions, scheduleOptions));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableScheduleProcessor(dataSource, null!, registry, workOptions, scheduleOptions));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableScheduleProcessor(dataSource, dataSource, null!, workOptions, scheduleOptions));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableScheduleProcessor(dataSource, dataSource, registry, null!, scheduleOptions));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableScheduleProcessor(dataSource, dataSource, registry, workOptions, null!));
    }

    [Fact]
    public void FlowClient_RequiresExplicitDependencies()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var payloads = new DurablePayloadCodecRegistry();
        var work = new DurableWorkRegistry([]);
        var flows = new DurableFlowRegistry([], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid());

        _ = new PostgreSqlDurableFlowClient(dataSource, flows, payloads, options);

        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowClient(null!, flows, payloads, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowClient(dataSource, null!, payloads, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowClient(dataSource, flows, null!, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowClient(dataSource, flows, payloads, null!));
    }

    [Fact]
    public async Task FlowClient_RejectsNullRequestsBeforeOpeningConnection()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var payloads = new DurablePayloadCodecRegistry();
        var work = new DurableWorkRegistry([]);
        var client = new PostgreSqlDurableFlowClient(
            dataSource,
            new DurableFlowRegistry([], work, payloads),
            payloads,
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()));

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.GetAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.ListAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.StartAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.RaiseEventAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.CancelAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.ReleaseSuspensionAsync(null!));
    }

    [Fact]
    public async Task FlowProcessor_RejectsDiscoveryBoundsBeforeOpeningConnection()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var payloads = new DurablePayloadCodecRegistry();
        var work = new DurableWorkRegistry([]);
        var flows = new DurableFlowRegistry([], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid());
        var processor = new PostgreSqlDurableFlowProcessor(
            dataSource,
            dataSource,
            flows,
            work,
            payloads,
            options);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await processor.DiscoverAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await processor.DiscoverAsync(1_001));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PostgreSqlDurableFlowProcessorSettings(TimeSpan.FromMilliseconds(999)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PostgreSqlDurableFlowProcessorSettings(TimeSpan.FromMinutes(16)));
    }

    [Fact]
    public void FlowProcessor_RequiresExplicitDependenciesAndAcceptsExplicitConfiguration()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var payloads = new DurablePayloadCodecRegistry();
        var work = new DurableWorkRegistry([]);
        var flows = new DurableFlowRegistry([], work, payloads);
        var options = new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid());
        var settings = new PostgreSqlDurableFlowProcessorSettings(TimeSpan.FromMinutes(1));

        _ = new PostgreSqlDurableFlowProcessor(
            dataSource,
            dataSource,
            flows,
            work,
            payloads,
            options,
            settings,
            NoOpPostgreSqlDurableFlowBarrierObserver.Instance);

        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowProcessor(
            null!, dataSource, flows, work, payloads, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowProcessor(
            dataSource, null!, flows, work, payloads, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowProcessor(
            dataSource, dataSource, null!, work, payloads, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowProcessor(
            dataSource, dataSource, flows, null!, payloads, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowProcessor(
            dataSource, dataSource, flows, work, null!, options));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowProcessor(
            dataSource, dataSource, flows, work, payloads, null!));
    }

    [Theory]
    [InlineData("LOCALHOST", "localhost")]
    [InlineData(" localhost, 127.0.0.1 ", "localhost,127.0.0.1")]
    public void StoreTarget_NormalizesConfiguredHosts(string configuredHost, string expectedHost)
    {
        var target = PostgreSqlDurableStoreTarget.Create(
            $"Host={configuredHost};Port=5432;Database=durable_contracts;Username=durable");

        Assert.Equal(expectedHost, target.Host);
        Assert.Equal(5432, target.Port);
        Assert.Equal("durable_contracts", target.Database);
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
        var request = new DurableWorkRequest(
            new DurableScopeId("scope"),
            new DurableCommandId("command"),
            "request-command",
            "tests.work",
            "v1",
            new DurableEncodedPayload(
                "tests.payload",
                "v1",
                DurableDataClassification.ApprovedApplication,
                new byte[] { 1 }),
            DurableProviderSafety.Idempotent);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await writer.EnqueueAsync(null!, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await writer.EnqueueAsync(null!, request));
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
