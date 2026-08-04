using System.Threading.Channels;
using ForgeTrust.AppSurface.Core;
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
        Assert.IsAssignableFrom<IDurableWorkTransactionWriter>(provider.GetRequiredService<IDurableWorkTransactionWriter>());
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
    public void Module_IsHostNeutralAndDeclaresTheDurableCoreDependency()
    {
        var module = new AppSurfaceDurablePostgreSqlModule();
        var services = new ServiceCollection();
        var dependencies = new ModuleDependencyBuilder();

        module.ConfigureServices(new StartupContext([], new TestStartupModule()), services);
        module.RegisterDependentModules(dependencies);

        Assert.Empty(services);
        Assert.Contains(dependencies.Modules, dependency => dependency is AppSurfaceDurableModule);
        Assert.Throws<ArgumentNullException>(() => module.ConfigureServices(null!, services));
        Assert.Throws<ArgumentNullException>(() => module.ConfigureServices(new StartupContext([], new TestStartupModule()), null!));
        Assert.Throws<ArgumentNullException>(() => module.RegisterDependentModules(null!));
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
        gate.Reopen();
        Assert.True(gate.TryEnter());
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
    public async Task HostedWait_CancelsTheLosingPollDelayWhenAWakeSignalArrives()
    {
        var wakeSignals = Channel.CreateBounded<bool>(1);
        Assert.True(wakeSignals.Writer.TryWrite(true));
        CancellationToken timerCancellation = default;

        await PostgreSqlDurableHostedService.WaitForWakeOrPollAsync(
            wakeSignals.Reader,
            TimeSpan.FromMinutes(5),
            (_, cancellationToken) =>
            {
                timerCancellation = cancellationToken;
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            CancellationToken.None);

        Assert.True(timerCancellation.IsCancellationRequested);
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
    public async Task HostedStart_ClosesAdmissionAndPersistsDrainWhenSchemaValidationFails()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        var admission = new DurableRuntimeAdmissionGate();
        var drain = new RecordingDrainControl();
        var schema = new FailingSchemaManager();
        using var hosted = new PostgreSqlDurableHostedService(
            schema,
            new EmptyPump(),
            drain,
            new PostgreSqlDurableRuntimeRegistration(
                dispatcher,
                runtime,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                new PostgreSqlDurableScheduleOptions("durable_runtime"),
                new AppSurfaceDurablePostgreSqlOptions
                {
                    WorkerId = "hosted-schema-failure-worker",
                    SendWakeNotifications = false,
                }.SnapshotAndValidate(),
                Guid.NewGuid()),
            admission,
            new TestHostApplicationLifetime(),
            Options.Create(new HostOptions()),
            NullLogger<PostgreSqlDurableHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        await schema.Validated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await drain.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(admission.TryEnter());
        Assert.Equal(1, drain.DrainCount);
        await hosted.StopAsync(CancellationToken.None);
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

    [Fact]
    public async Task HostedLifecycle_ValidatesResumesSchedulesAndPersistsDrain()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "hosted-lifecycle", "initial");
        var options = new AppSurfaceDurablePostgreSqlOptions
        {
            WorkerId = "hosted-lifecycle-worker",
            SendWakeNotifications = false,
            IdlePollingInterval = TimeSpan.FromMilliseconds(20),
        }.SnapshotAndValidate();
        var registration = new PostgreSqlDurableRuntimeRegistration(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options,
            Guid.NewGuid());
        var health = new PostgreSqlDurableRuntimeHealth(registration, schema);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hosted = new PostgreSqlDurableHostedService(
            schema,
            new SignalingPump(invoked),
            health,
            registration,
            new DurableRuntimeAdmissionGate(),
            new TestHostApplicationLifetime(),
            Options.Create(new HostOptions()),
            NullLogger<PostgreSqlDurableHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await health.GetAsync()).State);

        await hosted.StopAsync(CancellationToken.None);
        Assert.Equal(DurableRuntimeHealthState.Draining, (await health.GetAsync()).State);
    }

    [Fact]
    public async Task HostedLifecycle_UsesPostgreSqlWakeHintsToAccelerateItsNextAuthoritativePass()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "hosted-wake", "initial");
        var options = new AppSurfaceDurablePostgreSqlOptions
        {
            WorkerId = "hosted-wake-worker",
            SendWakeNotifications = true,
            IdlePollingInterval = TimeSpan.FromMinutes(5),
            HeartbeatStaleAfter = TimeSpan.FromMinutes(6),
        }.SnapshotAndValidate();
        var registration = new PostgreSqlDurableRuntimeRegistration(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(
                epoch,
                (await schema.GetStatusAsync()).StoreId,
                PostgreSqlDurableWakeNotificationMode.Enabled),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options,
            Guid.NewGuid());
        var lifetime = new TestHostApplicationLifetime();
        var pump = new CountingPump();
        using var hosted = new PostgreSqlDurableHostedService(
            schema,
            pump,
            new RecordingDrainControl(),
            registration,
            new DurableRuntimeAdmissionGate(),
            lifetime,
            Options.Create(new HostOptions()),
            NullLogger<PostgreSqlDurableHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        await pump.FirstPass.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; !pump.SecondPass.Task.IsCompleted && attempt < 100; attempt++)
        {
            await using var notify = database.DataSource.CreateCommand("SELECT pg_notify('appsurface_durable_wake', 'test');");
            await notify.ExecuteNonQueryAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        await pump.SecondPass.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.StopApplication();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostedLifecycle_RetriesTransientPumpFailureBeforeReturningToAuthoritativePolling()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        var options = new AppSurfaceDurablePostgreSqlOptions
        {
            WorkerId = "hosted-transient-worker",
            SendWakeNotifications = false,
            IdlePollingInterval = TimeSpan.FromMinutes(5),
            HeartbeatStaleAfter = TimeSpan.FromMinutes(6),
            TransientFailureDelay = TimeSpan.FromMilliseconds(20),
        }.SnapshotAndValidate();
        var pump = new TransientThenSignalingPump();
        var drain = new RecordingDrainControl();
        using var hosted = new PostgreSqlDurableHostedService(
            new NoOpSchemaManager(),
            pump,
            drain,
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

        await hosted.StartAsync(CancellationToken.None);
        await pump.RetryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(1, drain.ResumeCount);
        Assert.Equal(1, drain.DrainCount);
    }

    [Fact]
    public async Task HostedLifecycle_StopsBeforeResumingWhenApplicationShutdownHasAlreadyBegun()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        var lifetime = new TestHostApplicationLifetime();
        lifetime.StopApplication();
        var schema = new NoOpSchemaManager();
        var drain = new RecordingDrainControl();
        using var hosted = CreateHostedService(
            dispatcher,
            runtime,
            schema,
            new EmptyPump(),
            drain,
            lifetime,
            "hosted-prestopped-worker");

        await hosted.StartAsync(CancellationToken.None);
        await schema.Validated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, drain.ResumeCount);
        Assert.Equal(1, drain.DrainCount);
    }

    [Fact]
    public async Task HostedLifecycle_CancelsAnActivePassAndPreservesDrainFailureDiagnosticsDuringShutdown()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        var lifetime = new TestHostApplicationLifetime();
        var pump = new CancellationAwarePump();
        using var hosted = CreateHostedService(
            dispatcher,
            runtime,
            new NoOpSchemaManager(),
            pump,
            new FailingDrainControl(),
            lifetime,
            "hosted-cancel-worker");

        await hosted.StartAsync(CancellationToken.None);
        await pump.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.StopApplication();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostedLifecycle_YieldsImmediatelyWhenThePumpReportsMoreAuthoritativeWork()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        var lifetime = new TestHostApplicationLifetime();
        var pump = new HasMoreThenCancellationPump();
        using var hosted = CreateHostedService(
            dispatcher,
            runtime,
            new NoOpSchemaManager(),
            pump,
            new RecordingDrainControl(),
            lifetime,
            "hosted-has-more-worker");

        await hosted.StartAsync(CancellationToken.None);
        await pump.SecondPassStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.StopApplication();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostedStop_CancelsTheWakeListenerWithoutRequiringApplicationStopping()
    {
        using var dispatcher = CreateDataSource();
        using var runtime = CreateDataSource();
        using var hosted = CreateHostedService(
            dispatcher,
            runtime,
            new NoOpSchemaManager(),
            new EmptyPump(),
            new RecordingDrainControl(),
            new TestHostApplicationLifetime(),
            "hosted-direct-stop-worker",
            sendWakeNotifications: true);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static PostgreSqlDurableHostedService CreateHostedService(
        NpgsqlDataSource dispatcher,
        NpgsqlDataSource runtime,
        IDurableRuntimeSchemaManager schema,
        IDurableRuntimePump pump,
        IDurableRuntimeDrainControl drain,
        IHostApplicationLifetime lifetime,
        string workerId,
        bool sendWakeNotifications = false)
    {
        var options = new AppSurfaceDurablePostgreSqlOptions
        {
            WorkerId = workerId,
            SendWakeNotifications = sendWakeNotifications,
            IdlePollingInterval = TimeSpan.FromMinutes(5),
            HeartbeatStaleAfter = TimeSpan.FromMinutes(6),
            TimeBudgetPerPass = TimeSpan.FromMilliseconds(100),
            ShutdownReserve = TimeSpan.FromMilliseconds(100),
        }.SnapshotAndValidate();
        return new PostgreSqlDurableHostedService(
            schema,
            pump,
            drain,
            new PostgreSqlDurableRuntimeRegistration(
                dispatcher,
                runtime,
                new PostgreSqlDurableWorkOptions(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    sendWakeNotifications
                        ? PostgreSqlDurableWakeNotificationMode.Enabled
                        : PostgreSqlDurableWakeNotificationMode.Disabled),
                new PostgreSqlDurableScheduleOptions("durable_runtime"),
                options,
                Guid.NewGuid()),
            new DurableRuntimeAdmissionGate(),
            lifetime,
            Options.Create(new HostOptions { ShutdownTimeout = TimeSpan.FromSeconds(1) }),
            NullLogger<PostgreSqlDurableHostedService>.Instance);
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

    private sealed class SignalingPump(TaskCompletionSource invoked) : IDurableRuntimePump
    {
        public ValueTask<DurableRuntimePumpResult> RunOnceAsync(
            DurableRuntimePumpRequest request,
            CancellationToken cancellationToken = default)
        {
            invoked.TrySetResult();
            return ValueTask.FromResult(new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
        }
    }

    private sealed class CountingPump : IDurableRuntimePump
    {
        private int _calls;

        internal TaskCompletionSource FirstPass { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondPass { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<DurableRuntimePumpResult> RunOnceAsync(
            DurableRuntimePumpRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstPass.TrySetResult();
            }
            else
            {
                SecondPass.TrySetResult();
            }

            return ValueTask.FromResult(new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
        }
    }

    private sealed class TransientThenSignalingPump : IDurableRuntimePump
    {
        private int _calls;

        internal TaskCompletionSource RetryCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<DurableRuntimePumpResult> RunOnceAsync(
            DurableRuntimePumpRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return ValueTask.FromException<DurableRuntimePumpResult>(new TimeoutException("Simulated transient store timeout."));
            }

            RetryCompleted.TrySetResult();
            return ValueTask.FromResult(new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
        }
    }

    private sealed class CancellationAwarePump : IDurableRuntimePump
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DurableRuntimePumpResult> RunOnceAsync(
            DurableRuntimePumpRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-aware pump must be canceled before it can return.");
        }
    }

    private sealed class HasMoreThenCancellationPump : IDurableRuntimePump
    {
        private int _calls;

        internal TaskCompletionSource SecondPassStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DurableRuntimePumpResult> RunOnceAsync(
            DurableRuntimePumpRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return new DurableRuntimePumpResult(1, 1, 1, 0, 0, true, null, TimeSpan.Zero);
            }

            SecondPassStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The has-more pump must be canceled before it can return.");
        }
    }

    private sealed class FailingDrainControl : IDurableRuntimeDrainControl
    {
        public ValueTask BeginDrainAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("Simulated drain persistence failure."));

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingDrainControl : IDurableRuntimeDrainControl
    {
        internal int DrainCount { get; private set; }

        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ResumeCount { get; private set; }

        public ValueTask BeginDrainAsync(CancellationToken cancellationToken = default)
        {
            DrainCount++;
            Drained.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
        {
            ResumeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpSchemaManager : IDurableRuntimeSchemaManager
    {
        internal TaskCompletionSource Validated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<DurableRuntimeSchemaStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GenerateScript(int fromVersion = 0) => throw new NotSupportedException();

        public ValueTask<DurableRuntimeSchemaApplyResult> ApplyAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
        {
            Validated.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask<DurableRuntimeEpochActivationResult> InitializeRuntimeEpochAsync(
            Guid initialEpoch,
            string actorId,
            string reasonCode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<DurableRuntimeEpochRotationResult> RotateRuntimeEpochAsync(
            Guid expectedActiveEpoch,
            Guid newActiveEpoch,
            string actorId,
            string reasonCode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FailingSchemaManager : IDurableRuntimeSchemaManager
    {
        internal TaskCompletionSource Validated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<DurableRuntimeSchemaStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GenerateScript(int fromVersion = 0) => throw new NotSupportedException();

        public ValueTask<DurableRuntimeSchemaApplyResult> ApplyAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
        {
            Validated.TrySetResult();
            return ValueTask.FromException(new InvalidOperationException("Simulated schema validation failure."));
        }

        public ValueTask<DurableRuntimeEpochActivationResult> InitializeRuntimeEpochAsync(
            Guid initialEpoch,
            string actorId,
            string reasonCode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<DurableRuntimeEpochRotationResult> RotateRuntimeEpochAsync(
            Guid expectedActiveEpoch,
            Guid newActiveEpoch,
            string actorId,
            string reasonCode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            _stopping.Cancel();
        }
    }

    private sealed class TestStartupModule : IAppSurfaceHostModule
    {
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }
}
