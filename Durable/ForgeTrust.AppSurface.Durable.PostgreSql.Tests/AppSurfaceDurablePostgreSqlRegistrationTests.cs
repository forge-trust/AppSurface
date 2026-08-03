using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class AppSurfaceDurablePostgreSqlRegistrationTests
{
    [Fact]
    public void PassiveRegistration_ResolvesRuntimeKernelWithoutInstallingHostedWork()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        var services = new ServiceCollection();

        var builder = services.AddAppSurfaceDurablePostgreSql(
            dispatcher,
            runtime,
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            new PostgreSqlDurableScheduleOptions("durable_runtime"),
            options => options.SendWakeNotifications = false);

        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.IsAssignableFrom<IDurableWorkClient>(provider.GetRequiredService<IDurableWorkClient>());
        Assert.IsAssignableFrom<IDurableFlowClient>(provider.GetRequiredService<IDurableFlowClient>());
        Assert.IsAssignableFrom<IDurableScheduleClient>(provider.GetRequiredService<IDurableScheduleClient>());
        Assert.IsAssignableFrom<IDurableRuntimeSchemaManager>(provider.GetRequiredService<IDurableRuntimeSchemaManager>());
        Assert.IsAssignableFrom<IDurableRuntimePump>(provider.GetRequiredService<IDurableRuntimePump>());
        Assert.IsAssignableFrom<IDurableRuntimeHealth>(provider.GetRequiredService<IDurableRuntimeHealth>());
        Assert.IsAssignableFrom<IDurableRuntimeDrainControl>(provider.GetRequiredService<IDurableRuntimeDrainControl>());
        Assert.IsAssignableFrom<IDurableWorkControlClient>(provider.GetRequiredService<IDurableWorkControlClient>());
        Assert.IsAssignableFrom<IDurableScopeControlClient>(provider.GetRequiredService<IDurableScopeControlClient>());
        Assert.IsAssignableFrom<IDurableWorkOperatorClient>(provider.GetRequiredService<IDurableWorkOperatorClient>());
        Assert.Same(services, builder.Services);
        Assert.Equal(
            PostgreSqlDurableWakeNotificationMode.Disabled,
            provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>().WorkOptions.WakeNotificationMode);
    }

    [Fact]
    public void WorkerHost_IsExplicitIdempotentAndRequiresPassiveStorage()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAppSurfaceDurableWorkerHost());

        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            dispatcher,
            runtime,
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            new PostgreSqlDurableScheduleOptions("durable_runtime"))
            .AddWorkerHost()
            .AddWorkerHost();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PostgreSqlDurableHostedServiceMarker));
    }

    [Fact]
    public void Registration_RejectsDuplicateStorageAndUnsafeActivationOptions()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        var services = new ServiceCollection();
        var workOptions = new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid());
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("durable_runtime");
        services.AddAppSurfaceDurablePostgreSql(dispatcher, runtime, workOptions, scheduleOptions);

        Assert.Throws<InvalidOperationException>(() =>
            services.AddAppSurfaceDurablePostgreSql(dispatcher, runtime, workOptions, scheduleOptions));
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddAppSurfaceDurablePostgreSql(
                dispatcher,
                runtime,
                workOptions,
                scheduleOptions,
                options => options.WorkerId = "unsafe worker id"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddAppSurfaceDurablePostgreSql(
                dispatcher,
                runtime,
                workOptions,
                scheduleOptions,
                options => options.HeartbeatStaleAfter = options.IdlePollingInterval));
    }

    [Fact]
    public void AdmissionGate_RejectsNewPassesAfterShutdownAdmissionCloses()
    {
        var gate = new DurableRuntimeAdmissionGate();

        Assert.True(gate.TryEnter());
        gate.Close();
        Assert.False(gate.TryEnter());
    }

    [Fact]
    public void TurnScheduler_RotatesEveryEnabledSurfaceAndSkipsDisabledOnes()
    {
        var scheduler = new DurableRuntimeTurnScheduler();

        Assert.Equal(DurableRuntimeSurface.Work, scheduler.Next(DurableRuntimeSurface.All));
        Assert.Equal(DurableRuntimeSurface.Flow, scheduler.Next(DurableRuntimeSurface.All));
        Assert.Equal(DurableRuntimeSurface.Schedule, scheduler.Next(DurableRuntimeSurface.All));
        Assert.Equal(DurableRuntimeSurface.Work, scheduler.Next(DurableRuntimeSurface.All));
        Assert.Equal(DurableRuntimeSurface.Flow, scheduler.Next(DurableRuntimeSurface.Flow | DurableRuntimeSurface.Schedule));
        Assert.Equal(DurableRuntimeSurface.Schedule, scheduler.Next(DurableRuntimeSurface.Flow | DurableRuntimeSurface.Schedule));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Next(DurableRuntimeSurface.None));
    }

    [Fact]
    public void HostedWait_UsesEarliestDueTimeAndNeverReturnsZeroDelay()
    {
        var maximum = TimeSpan.FromSeconds(5);

        Assert.Equal(maximum, PostgreSqlDurableHostedService.CalculateIdleDelay(null, maximum));
        Assert.Equal(TimeSpan.FromMilliseconds(1), PostgreSqlDurableHostedService.CalculateIdleDelay(DateTimeOffset.UtcNow, maximum));
        var nearDue = PostgreSqlDurableHostedService.CalculateIdleDelay(DateTimeOffset.UtcNow.AddMilliseconds(20), maximum);
        Assert.InRange(nearDue, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HostedStart_RejectsAPassBudgetThatCannotLeaveTheShutdownReserve()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
                services.AddAppSurfaceDurablePostgreSql(
                        dispatcher,
                        runtime,
                        new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                        new PostgreSqlDurableScheduleOptions("durable_runtime"),
                        options =>
                        {
                            options.TimeBudgetPerPass = TimeSpan.FromSeconds(10);
                            options.ShutdownReserve = TimeSpan.FromSeconds(5);
                            options.SendWakeNotifications = false;
                        })
                    .AddWorkerHost();
            })
            .Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.StartAsync());

        Assert.Contains("ShutdownReserve", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedStop_DrainFailureDoesNotPreventBackgroundServiceShutdown()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        var options = new AppSurfaceDurablePostgreSqlOptions
        {
            WorkerId = "host-stop-test",
            SendWakeNotifications = false,
        }.SnapshotAndValidate();
        var hosted = new PostgreSqlDurableHostedService(
            new PostgreSqlDurableRuntimeSchemaManager(runtime),
            new EmptyPump(),
            new FailingDrainControl(),
            new PostgreSqlDurableRuntimeRegistration(
                dispatcher,
                runtime,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                new PostgreSqlDurableScheduleOptions("durable_runtime"),
                options,
                Guid.NewGuid()),
            new DurableRuntimeAdmissionGate(),
            new TestHostApplicationLifetime(),
            Options.Create(new HostOptions()),
            NullLogger<PostgreSqlDurableHostedService>.Instance);

        await hosted.StopAsync(CancellationToken.None);
    }

    private static NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(
        "Host=localhost;Port=5432;Database=durable_registration;Username=durable;Password=not-opened");

    private sealed class EmptyPump : IDurableRuntimePump
    {
        public ValueTask<DurableRuntimePumpResult> RunOnceAsync(
            DurableRuntimePumpRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
    }

    private sealed class FailingDrainControl : IDurableRuntimeDrainControl
    {
        public ValueTask BeginDrainAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("Simulated drain persistence failure."));

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
