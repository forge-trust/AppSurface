using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class AppSurfaceDurablePostgreSqlRegistrationTests
{
    [Fact]
    public async Task StorageRegistration_IsPassiveAndResolvesEveryPublicRuntimeBoundary()
    {
        await using var dataSource = CreateUnopenedDataSource();
        var services = new ServiceCollection();

        var builder = services.AddAppSurfaceDurablePostgreSql(
            dataSource,
            Guid.NewGuid(),
            options =>
            {
                options.WorkerId = "test-worker";
                options.SendWakeNotifications = false;
            });

        Assert.Same(services, builder.Services);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        await using var provider = services.BuildServiceProvider();
        Assert.IsType<PostgreSqlDurableRuntimeSchemaManager>(provider.GetRequiredService<IDurableRuntimeSchemaManager>());
        Assert.IsType<PostgreSqlDurableWorkClient>(provider.GetRequiredService<IDurableWorkClient>());
        Assert.IsType<PostgreSqlDurableFlowClient>(provider.GetRequiredService<IDurableFlowClient>());
        Assert.IsType<PostgreSqlDurableScheduleClient>(provider.GetRequiredService<IDurableScheduleClient>());
        Assert.IsType<PostgreSqlDurableControlClient>(provider.GetRequiredService<IDurableWorkControlClient>());
        Assert.IsType<PostgreSqlDurableControlClient>(provider.GetRequiredService<IDurableScopeControlClient>());
        Assert.IsType<PostgreSqlDurableRuntimePump>(provider.GetRequiredService<IDurableRuntimePump>());
        Assert.IsType<PostgreSqlDurableRuntimeHealth>(provider.GetRequiredService<IDurableRuntimeHealth>());
        Assert.IsType<PostgreSqlDurableRuntimeHealth>(provider.GetRequiredService<IDurableRuntimeDrainControl>());
        Assert.IsType<PostgreSqlDurableWorkTransactionWriter>(
            provider.GetRequiredService<IDurableWorkTransactionWriter>());
    }

    [Fact]
    public async Task WorkerHost_IsExplicitIdempotentAndRequiresStorageRegistration()
    {
        var unconfigured = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => unconfigured.AddAppSurfaceDurableWorkerHost());

        await using var dataSource = CreateUnopenedDataSource();
        var services = new ServiceCollection();
        var builder = services.AddAppSurfaceDurablePostgreSql(dataSource, Guid.NewGuid());

        Assert.Same(builder, builder.AddWorkerHost());
        builder.AddWorkerHost();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PostgreSqlDurableHostedServiceMarker));
    }

    [Fact]
    public async Task StorageRegistration_RejectsAmbiguousOrInvalidConfiguration()
    {
        await using var dataSource = CreateUnopenedDataSource();
        Assert.Throws<ArgumentNullException>(() =>
            AppSurfaceDurablePostgreSqlServiceCollectionExtensions.AddAppSurfaceDurablePostgreSql(
                null!, dataSource, Guid.NewGuid()));
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddAppSurfaceDurablePostgreSql(null!, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddAppSurfaceDurablePostgreSql(dataSource, Guid.Empty));

        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(dataSource, Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() =>
            services.AddAppSurfaceDurablePostgreSql(dataSource, Guid.NewGuid()));

        AssertInvalidOptions(dataSource, options => options.WorkerId = " ", typeof(ArgumentException));
        AssertInvalidOptions(dataSource, options => options.WorkerId = "worker@example.com", typeof(ArgumentException));
        AssertInvalidOptions(dataSource, options => options.WorkerId = new string('w', 201), typeof(ArgumentException));
        AssertInvalidOptions(dataSource, options => options.MaximumItemsPerPass = 0, typeof(ArgumentOutOfRangeException));
        AssertInvalidOptions(dataSource, options => options.TimeBudgetPerPass = TimeSpan.Zero, typeof(ArgumentOutOfRangeException));
        AssertInvalidOptions(dataSource, options => options.HostedSurfaces = DurableRuntimeSurface.None, typeof(ArgumentOutOfRangeException));
        AssertInvalidOptions(dataSource, options => options.IdlePollingInterval = TimeSpan.Zero, typeof(ArgumentOutOfRangeException));
        AssertInvalidOptions(
            dataSource,
            options => options.HeartbeatStaleAfter = options.IdlePollingInterval,
            typeof(ArgumentOutOfRangeException));
        AssertInvalidOptions(
            dataSource,
            options => options.TransientFailureDelay = TimeSpan.FromMinutes(6),
            typeof(ArgumentOutOfRangeException));
    }

    [Fact]
    public void PostgreSqlModule_DeclaresDurableDependencyWithoutRuntimeSideEffects()
    {
        var builder = new ModuleDependencyBuilder();

        builder.AddModule<AppSurfaceDurablePostgreSqlModule>();

        Assert.Contains(builder.Modules, module => module is AppSurfaceDurablePostgreSqlModule);
        Assert.Contains(builder.Modules, module => module is AppSurfaceDurableModule);
    }

    [Fact]
    public void HostedDelay_UsesPollingBoundAndImmediatelyRechecksOverdueWork()
    {
        var maximum = TimeSpan.FromSeconds(10);

        Assert.Equal(maximum, PostgreSqlDurableHostedService.CalculateIdleDelay(null, maximum));
        Assert.Equal(
            TimeSpan.FromMilliseconds(1),
            PostgreSqlDurableHostedService.CalculateIdleDelay(DateTimeOffset.UtcNow.AddSeconds(-1), maximum));
        var shortDelay = PostgreSqlDurableHostedService.CalculateIdleDelay(
            DateTimeOffset.UtcNow.AddMilliseconds(500),
            maximum);
        Assert.InRange(shortDelay, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(500));
        Assert.Equal(
            maximum,
            PostgreSqlDurableHostedService.CalculateIdleDelay(DateTimeOffset.UtcNow.AddMinutes(1), maximum));
    }

    private static NpgsqlDataSource CreateUnopenedDataSource() => NpgsqlDataSource.Create(
        "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");

    private static void AssertInvalidOptions(
        NpgsqlDataSource dataSource,
        Action<AppSurfaceDurablePostgreSqlOptions> configure,
        Type exceptionType)
    {
        var exception = Record.Exception(() =>
            new ServiceCollection().AddAppSurfaceDurablePostgreSql(dataSource, Guid.NewGuid(), configure));
        Assert.NotNull(exception);
        Assert.IsType(exceptionType, exception);
    }
}
