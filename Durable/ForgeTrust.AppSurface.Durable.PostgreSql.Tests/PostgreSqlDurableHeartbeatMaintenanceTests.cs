using ForgeTrust.AppSurface.Durable.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableHeartbeatMaintenanceTests : IDisposable
{
    private readonly NpgsqlDataSource _dispatcherDataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");
    private readonly NpgsqlDataSource _runtimeDataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Application Name=runtime");

    public void Dispose()
    {
        _runtimeDataSource.Dispose();
        _dispatcherDataSource.Dispose();
    }

    [Fact]
    public void Constructor_RejectsMissingRegistrationAndClock()
    {
        var registration = CreateRegistration();

        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableHeartbeatMaintenance(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableHeartbeatMaintenance(registration, null!));
    }

    [Fact]
    public void Options_DefaultToEnabledAndRejectUnsafeMaintenanceBounds()
    {
        var defaults = new AppSurfaceDurablePostgreSqlOptions().SnapshotAndValidate();
        Assert.True(defaults.EnableHeartbeatMaintenance);
        Assert.Equal(TimeSpan.FromHours(24), defaults.HeartbeatRetention);
        Assert.Equal(TimeSpan.FromHours(24), defaults.HeartbeatMaintenanceCadence);
        Assert.Equal(500, defaults.HeartbeatPruneBatchSize);

        AssertInvalid(options => options.HeartbeatRetention = TimeSpan.FromHours(23));
        AssertInvalid(options => options.HeartbeatRetention = TimeSpan.FromDays(3651));
        AssertInvalid(options => options.HeartbeatRetention = options.HeartbeatStaleAfter);
        AssertInvalid(options => options.HeartbeatMaintenanceCadence = TimeSpan.FromMinutes(59));
        AssertInvalid(options => options.HeartbeatMaintenanceCadence = TimeSpan.FromDays(31));
        AssertInvalid(options => options.HeartbeatPruneBatchSize = 0);
        AssertInvalid(options => options.HeartbeatPruneBatchSize = 5001);
    }

    [Fact]
    public async Task SignalAdmittedPass_QueuesImmediateMaintenanceWithoutAwaitingSql_AndStopsOnCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var registration = CreateRegistration();
        await using var maintenance = new PostgreSqlDurableHeartbeatMaintenance(
            registration,
            TimeProvider.System,
            pruneOperation: async cancellationToken =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            });

        var hosted = (IHostedService)maintenance;
        await hosted.StartAsync(CancellationToken.None);
        maintenance.SignalAdmittedPass();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await hosted.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        maintenance.SignalAdmittedPass();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task DisabledMaintenance_DoesNotQueueDatabaseWork()
    {
        var calls = 0;
        var options = new AppSurfaceDurablePostgreSqlOptions { EnableHeartbeatMaintenance = false }.SnapshotAndValidate();
        await using var maintenance = new PostgreSqlDurableHeartbeatMaintenance(
            CreateRegistration(options),
            TimeProvider.System,
            pruneOperation: _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(0);
            });

        maintenance.SignalAdmittedPass();
        await Task.Delay(30);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task DisposedMaintenance_IgnoresSignalsAndRepeatedDisposal()
    {
        var calls = 0;
        var maintenance = new PostgreSqlDurableHeartbeatMaintenance(
            CreateRegistration(),
            TimeProvider.System,
            pruneOperation: _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(0);
            });

        await maintenance.DisposeAsync();
        await maintenance.DisposeAsync();
        maintenance.SignalAdmittedPass();
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public void SynchronousDisposal_CanBeRepeatedWithoutAStartedRunner()
    {
        var maintenance = new PostgreSqlDurableHeartbeatMaintenance(CreateRegistration(), TimeProvider.System);

        maintenance.Dispose();
        maintenance.Dispose();
        maintenance.SignalAdmittedPass();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDisposal_StopsMaintenanceAndPreventsFurtherCalls(bool asynchronous)
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = CreateRegistration();
        var maintenance = new PostgreSqlDurableHeartbeatMaintenance(
            registration,
            TimeProvider.System,
            pruneOperation: async cancellationToken =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }

                return 0;
            });
        var services = new ServiceCollection();
        services.AddSingleton(_ => maintenance);
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<PostgreSqlDurableHeartbeatMaintenance>().SignalAdmittedPass();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (asynchronous)
        {
            await provider.DisposeAsync();
        }
        else
        {
            provider.Dispose();
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var callsAtDisposal = Volatile.Read(ref calls);
        await Task.Delay(50);
        Assert.Equal(callsAtDisposal, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task PartialBatch_UsesConfiguredCadenceFromCallStart()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new AppSurfaceDurablePostgreSqlOptions
        {
            HeartbeatMaintenanceCadence = TimeSpan.FromHours(1),
        }.SnapshotAndValidate();
        var registration = CreateRegistration(options);
        await using var maintenance = new PostgreSqlDurableHeartbeatMaintenance(
            registration,
            clock,
            pruneOperation: _ =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    secondCall.TrySetResult();
                }

                return Task.FromResult(1);
            });

        maintenance.SignalAdmittedPass();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await maintenance.StopAsync();
    }

    [Fact]
    public async Task PruneExecutionTime_ConsumesConfiguredCadence()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new AppSurfaceDurablePostgreSqlOptions
        {
            HeartbeatMaintenanceCadence = TimeSpan.FromHours(1),
        }.SnapshotAndValidate();
        await using var maintenance = new PostgreSqlDurableHeartbeatMaintenance(
            CreateRegistration(options),
            clock,
            pruneOperation: _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    clock.Advance(TimeSpan.FromHours(1));
                }
                else
                {
                    secondCall.TrySetResult();
                }

                return Task.FromResult(0);
            });

        maintenance.SignalAdmittedPass();
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await maintenance.StopAsync();
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task FullBatch_UsesOneMinuteCatchUpSpacing()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new AppSurfaceDurablePostgreSqlOptions { HeartbeatPruneBatchSize = 1 }.SnapshotAndValidate();
        var registration = CreateRegistration(options);
        await using var maintenance = new PostgreSqlDurableHeartbeatMaintenance(
            registration,
            clock,
            pruneOperation: _ =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    secondCall.TrySetResult();
                }

                return Task.FromResult(1);
            });

        maintenance.SignalAdmittedPass();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await maintenance.StopAsync();
    }

    [Fact]
    public async Task FailedCall_RetriesAfterOneHour()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = CreateRegistration();
        await using var maintenance = new PostgreSqlDurableHeartbeatMaintenance(
            registration,
            clock,
            pruneOperation: _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("private exception text");
                }

                retried.TrySetResult();
                return Task.FromResult(0);
            });

        maintenance.SignalAdmittedPass();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await maintenance.StopAsync();
    }

    [Fact]
    public async Task NonCancellationExceptionAfterStop_IsHandledAsCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = new PostgreSqlDurableHeartbeatMaintenance(
            CreateRegistration(),
            TimeProvider.System,
            pruneOperation: async cancellationToken =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("operation failed while stopping");
                }

                throw new InvalidOperationException("operation failed while stopping");
            });

        maintenance.SignalAdmittedPass();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await maintenance.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await maintenance.DisposeAsync();
    }

    [Fact]
    public async Task CancellingBlockedPrune_ReleasesTransactionAdvisoryLock()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "heartbeat-maintenance-tests", "lock-release");
        var status = await schema.GetStatusAsync();
        await using var runtimeDataSource = database.CreateDataSource();
        var registration = new PostgreSqlDurableRuntimeRegistration(
            database.DataSource,
            runtimeDataSource,
            new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            new AppSurfaceDurablePostgreSqlOptions
            {
                WorkerId = "maintenance-lock-release-worker",
                HeartbeatRetention = TimeSpan.FromHours(24),
            }.SnapshotAndValidate(),
            Guid.NewGuid());
        await using var blocker = await database.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockTable = new NpgsqlCommand(
            "LOCK TABLE appsurface_durable.runtime_heartbeat IN ACCESS EXCLUSIVE MODE;",
            blocker,
            blockerTransaction))
        {
            await lockTable.ExecuteNonQueryAsync();
        }

        await using var maintenance = new PostgreSqlDurableHeartbeatMaintenance(registration, TimeProvider.System);
        maintenance.SignalAdmittedPass();
        await WaitUntilAsync(async () => !await TryAcquirePruneLockAsync(database.DataSource));
        await maintenance.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await blockerTransaction.RollbackAsync();

        Assert.True(await TryAcquirePruneLockAsync(database.DataSource));
    }

    private PostgreSqlDurableRuntimeRegistration CreateRegistration(AppSurfaceDurablePostgreSqlOptions? options = null) =>
        new(
            _dispatcherDataSource,
            _runtimeDataSource,
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            (options ?? new AppSurfaceDurablePostgreSqlOptions()).SnapshotAndValidate(),
            Guid.NewGuid());

    private static void AssertInvalid(Action<AppSurfaceDurablePostgreSqlOptions> configure)
    {
        var options = new AppSurfaceDurablePostgreSqlOptions();
        configure(options);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.SnapshotAndValidate());
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate(), "The expected maintenance call did not start.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(await predicate(), "The expected PostgreSQL advisory lock state was not reached.");
    }

    private static async Task<bool> TryAcquirePruneLockAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_xact_lock(@namespace_one, @namespace_two);",
            connection,
            transaction);
        command.Parameters.AddWithValue("namespace_one", unchecked((int)0x41534455));
        command.Parameters.AddWithValue("namespace_two", unchecked((int)0x52484252));
        var acquired = (bool)(await command.ExecuteScalarAsync() ?? false);
        await transaction.CommitAsync();
        return acquired;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_sync)
            {
                _timers.Add(timer);
            }

            _timerCreated.TrySetResult();

            return timer;
        }

        internal Task WaitForTimerAsync() => _timerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        internal void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref _ticks, amount.Ticks);
            ManualTimer[] due;
            lock (_sync)
            {
                due = _timers.Where(timer => timer.IsDue(GetTimestamp())).ToArray();
            }

            foreach (var timer in due)
            {
                timer.Fire(GetTimestamp());
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private long _period = Timeout.Infinite;
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                _period = period == Timeout.InfiniteTimeSpan ? Timeout.Infinite : period.Ticks;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetTimestamp() + dueTime.Ticks;
                return true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            internal bool IsDue(long now) => Volatile.Read(ref _disposed) == 0 && now >= Interlocked.Read(ref _due);

            internal void Fire(long now)
            {
                _due = _period == Timeout.Infinite ? long.MaxValue : now + _period;

                callback(state);
            }
        }
    }
}
