using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Exercises both fence orders, stable snapshots and terminal cleanup using controlled barriers.</summary>
public sealed class DurableDoctorConcurrencyTests
{
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData((int)DurableDoctorStage.Open)]
    [InlineData((int)DurableDoctorStage.Fence)]
    [InlineData((int)DurableDoctorStage.Credential)]
    [InlineData((int)DurableDoctorStage.Schema)]
    [InlineData((int)DurableDoctorStage.Retention)]
    [InlineData((int)DurableDoctorStage.Runtime)]
    public async Task Advancing_the_owned_clock_expires_each_stage_without_wall_clock_waits(int stageValue)
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var clock = new DoctorManualClock();
        var stages = new List<DurableDoctorStage>();
        var service = new DurableDoctorService(clock, (stage, _) =>
        {
            stages.Add(stage);
            if (stage == (DurableDoctorStage)stageValue) clock.Advance(TimeSpan.FromMilliseconds(800));
            return ValueTask.CompletedTask;
        });
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(async () =>
            await service.InspectAsync(MarkSession(fixture.RuntimeConnectionString, "doctor-fake-deadline"),
                fixture.CreateRequest(timeout: TimeSpan.FromSeconds(1)), CancellationToken.None));
        Assert.Equal(DurableDoctorFailureKind.Unavailable, failure.Kind);
        Assert.Equal(["deadline"], failure.Categories);
        Assert.DoesNotContain(DurableDoctorStage.Commit, stages);
        Assert.Equal(DurableDoctorStage.Closed, stages[^1]);
        await AssertReleasedAsync(fixture, "doctor-fake-deadline");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fake_clock_cleanup_exhaustion_discards_success_and_preserves_caller_precedence(bool cancelCaller)
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var clock = new DoctorManualClock();
        using var caller = new CancellationTokenSource();
        var service = new DurableDoctorService(clock, (stage, _) =>
        {
            if (stage == DurableDoctorStage.Unlock)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                if (cancelCaller) caller.Cancel();
            }
            return ValueTask.CompletedTask;
        });
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(async () =>
            await service.InspectAsync(MarkSession(fixture.RuntimeConnectionString, "doctor-fake-cleanup"),
                fixture.CreateRequest(timeout: TimeSpan.FromSeconds(1)), caller.Token));
        Assert.Equal(cancelCaller ? DurableDoctorFailureKind.Canceled : DurableDoctorFailureKind.Unavailable, failure.Kind);
        Assert.Equal(cancelCaller ? new[] { "caller-canceled" } : new[] { "cleanup" }, failure.Categories);
        await AssertReleasedAsync(fixture, "doctor-fake-cleanup");
    }

    [Fact]
    public async Task Minimum_budget_closes_a_real_open_that_never_finishes_server_handshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var testDeadline = new CancellationTokenSource(TestLimit);
        var accepted = listener.AcceptTcpClientAsync(testDeadline.Token);
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Database = "doctor", Username = "doctor", Password = "private-password-sentinel",
            SslMode = SslMode.Disable, Timeout = 5
        };
        var request = new DurableDoctorRequest(Guid.Parse("88164257-2a2f-42b4-9832-18a649888801"),
            DurableDoctorFixture.ConnectionEnvironmentName, DurableDoctorFixture.EpochEnvironmentName,
            null, null, TimeSpan.FromSeconds(1), "json");
        var timer = Stopwatch.StartNew();
        var call = new DurableDoctorService().InspectAsync(settings.ConnectionString, request, CancellationToken.None).AsTask();
        using var socket = await accepted;
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(() => call);
        Assert.Equal(DurableDoctorFailureKind.Unavailable, failure.Kind);
        Assert.Contains("deadline", failure.Categories);
        Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(3));
        // Consume the bounded startup packet and verify that no owned connection survives the returned failure.
        var buffer = new byte[1024];
        var stream = socket.GetStream();
        while (await stream.ReadAsync(buffer, testDeadline.Token) != 0) { }
        Assert.DoesNotContain("private-password-sentinel", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_before_exclusive_writer_holds_fence_through_observation_and_releases_backend()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var reading = Signal();
        var release = Signal();
        var service = new DurableDoctorService(TimeProvider.System, async (stage, token) =>
        {
            if (stage == DurableDoctorStage.Credential)
            {
                reading.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        });
        var connection = MarkSession(fixture.RuntimeConnectionString, "doctor-reader-first");
        var doctor = service.InspectAsync(connection, fixture.CreateRequest(), CancellationToken.None).AsTask();
        await reading.Task.WaitAsync(TestLimit);
        await using var writer = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await writer.OpenAsync();
        await using var fence = FenceCommand(writer, shared: false, unlock: false);
        using var testDeadline = new CancellationTokenSource(TestLimit);
        var acquire = fence.ExecuteNonQueryAsync(testDeadline.Token);
        await WaitForFenceAsync(fixture, writer.ProcessID, granted: false, testDeadline.Token);
        Assert.False(acquire.IsCompleted);
        release.TrySetResult();
        var observation = await doctor.WaitAsync(TestLimit);
        Assert.Empty(observation.CredentialFailures);
        Assert.True(observation.Schema!.IsCompatible);
        await acquire.WaitAsync(TestLimit);
        await using (var unlock = FenceCommand(writer, shared: false, unlock: true))
        {
            Assert.True((bool)(await unlock.ExecuteScalarAsync())!);
        }
        await AssertReleasedAsync(fixture, "doctor-reader-first");
        Assert.True((await fixture.InspectAsync()).Schema!.IsCompatible);
    }

    [Fact]
    public async Task Exclusive_writer_before_doctor_releases_before_its_snapshot_is_created()
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await using var writer = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await writer.OpenAsync();
        await using (var lockCommand = FenceCommand(writer, shared: false, unlock: false))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }
        var waiting = Signal();
        var service = new DurableDoctorService(TimeProvider.System, (stage, _) =>
        {
            if (stage == DurableDoctorStage.Fence) waiting.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var doctor = service.InspectAsync(MarkSession(fixture.RuntimeConnectionString, "doctor-writer-first"),
            fixture.CreateRequest(DurableDoctorFixture.RuntimeWorkerId, DurableDoctorFixture.ReferenceStaleAfter), CancellationToken.None).AsTask();
        await waiting.Task.WaitAsync(TestLimit);
        // This cooperative writer owns the exclusive fence before changing the worker state.
        await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(2));
        await using (var unlock = FenceCommand(writer, shared: false, unlock: true))
        {
            Assert.True((bool)(await unlock.ExecuteScalarAsync())!);
        }
        var observed = await doctor.WaitAsync(TestLimit);
        Assert.True(observed.Heartbeat!.Found);
        await AssertReleasedAsync(fixture, "doctor-writer-first");
        Assert.True((await fixture.InspectAsync()).Schema!.IsCompatible);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Heartbeat_writer_before_or_after_snapshot_observes_consistent_old_or_new_state(bool before)
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(2), draining: false);
        if (before) await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(2), draining: true);
        var service = new DurableDoctorService(TimeProvider.System, async (stage, _) =>
        {
            if (!before && stage == DurableDoctorStage.Runtime)
            {
                // Ordinary heartbeat updates do not take the migration fence. The transaction must retain its snapshot.
                await fixture.SeedHeartbeatAsync(age: TimeSpan.FromSeconds(2), draining: true);
            }
        });
        var observation = await service.InspectAsync(fixture.RuntimeConnectionString,
            fixture.CreateRequest(DurableDoctorFixture.RuntimeWorkerId, DurableDoctorFixture.ReferenceStaleAfter), CancellationToken.None);
        Assert.Equal(before, observation.Heartbeat!.IsDraining);
        Assert.Equal(fixture.StoreId, observation.StoreId);
        Assert.Equal(observation.Schema!.StoreId, observation.StoreId);
        Assert.Equal(observation.Schema.ActiveRuntimeEpoch, observation.ActiveRuntimeEpoch);
        Assert.True((await fixture.ReadHeartbeatAsync())!.IsDraining);
    }

    [Theory]
    [InlineData((int)DurableDoctorStage.Open)]
    [InlineData((int)DurableDoctorStage.Fence)]
    [InlineData((int)DurableDoctorStage.Credential)]
    [InlineData((int)DurableDoctorStage.Schema)]
    [InlineData((int)DurableDoctorStage.Retention)]
    [InlineData((int)DurableDoctorStage.Runtime)]
    public async Task Minimum_budget_cancels_blocked_stage_without_late_work(int stageValue)
    {
        var blockedStage = (DurableDoctorStage)stageValue;
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var stages = new List<DurableDoctorStage>();
        var entered = Signal();
        var never = Signal();
        var service = new DurableDoctorService(TimeProvider.System, async (stage, token) =>
        {
            stages.Add(stage);
            if (stage == blockedStage)
            {
                entered.TrySetResult();
                await never.Task.WaitAsync(token);
            }
        });
        var timer = Stopwatch.StartNew();
        var call = service.InspectAsync(MarkSession(fixture.RuntimeConnectionString, "doctor-budget"),
            fixture.CreateRequest(timeout: TimeSpan.FromSeconds(1)), CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TestLimit);
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(() => call);
        Assert.Equal(DurableDoctorFailureKind.Unavailable, failure.Kind);
        Assert.Contains("deadline", failure.Categories);
        Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(DurableDoctorStage.Commit, stages);
        var completedStages = stages.ToArray();
        never.TrySetResult();
        Assert.Equal(completedStages, stages);
        await AssertReleasedAsync(fixture, "doctor-budget");
        Assert.True((await fixture.InspectAsync()).Schema!.IsCompatible);
    }

    [Theory]
    [InlineData((int)DurableDoctorStage.Credential)]
    [InlineData((int)DurableDoctorStage.Schema)]
    [InlineData((int)DurableDoctorStage.Retention)]
    [InlineData((int)DurableDoctorStage.Runtime)]
    public async Task Incomplete_permission_observation_never_returns_partial_evidence(int stageValue)
    {
        var stageDenied = (DurableDoctorStage)stageValue;
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var service = new DurableDoctorService(TimeProvider.System, (stage, _) =>
        {
            if (stage == stageDenied) throw new PostgresException("secret-denial", "ERROR", "ERROR", "42501");
            return ValueTask.CompletedTask;
        });
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(async () =>
            await service.InspectAsync(fixture.RuntimeConnectionString, fixture.CreateRequest(), CancellationToken.None));
        Assert.Equal(DurableDoctorFailureKind.Unavailable, failure.Kind);
        Assert.Equal(["dependency"], failure.Categories);
        Assert.DoesNotContain("secret-denial", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cleanup_cannot_mask_original_caller_or_unexpected_failure(bool callerCanceled, bool duringCleanup)
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        using var caller = new CancellationTokenSource();
        var service = new DurableDoctorService(TimeProvider.System, (stage, _) =>
        {
            if (stage == DurableDoctorStage.Runtime)
            {
                if (callerCanceled && !duringCleanup) caller.Cancel();
                throw new InvalidDataException("secret-contract");
            }
            if (stage == DurableDoctorStage.Rollback)
            {
                if (callerCanceled && duringCleanup) caller.Cancel();
                throw new InvalidOperationException("secret-cleanup");
            }
            return ValueTask.CompletedTask;
        });
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(async () =>
            await service.InspectAsync(MarkSession(fixture.RuntimeConnectionString, "doctor-precedence"), fixture.CreateRequest(), caller.Token));
        Assert.Equal(callerCanceled ? DurableDoctorFailureKind.Canceled : DurableDoctorFailureKind.Failed, failure.Kind);
        Assert.Equal(callerCanceled ? new[] { "caller-canceled" } : new[] { "catalog-contract" }, failure.Categories);
        Assert.DoesNotContain("secret-", failure.ToString(), StringComparison.Ordinal);
        await AssertReleasedAsync(fixture, "doctor-precedence");
    }

    [Theory]
    [InlineData((int)DurableDoctorStage.Unlock)]
    [InlineData((int)DurableDoctorStage.Close)]
    public async Task Successful_reads_with_uncertain_cleanup_cannot_return_success(int stageValue)
    {
        var brokenStage = (DurableDoctorStage)stageValue;
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        var service = new DurableDoctorService(TimeProvider.System, (stage, _) =>
        {
            if (stage == brokenStage) throw new InvalidOperationException("secret-cleanup");
            return ValueTask.CompletedTask;
        });
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(async () =>
            await service.InspectAsync(MarkSession(fixture.RuntimeConnectionString, "doctor-cleanup"), fixture.CreateRequest(), CancellationToken.None));
        Assert.Equal(DurableDoctorFailureKind.Unavailable, failure.Kind);
        Assert.Equal(["cleanup"], failure.Categories);
        await AssertReleasedAsync(fixture, "doctor-cleanup");
        Assert.True((await fixture.InspectAsync()).Schema!.IsCompatible);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_blocked_fence_or_table_query_expires_and_releases_its_owned_session(bool fenceBlocked)
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        await using var blocker = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await blocker.OpenAsync();
        NpgsqlTransaction? transaction = null;
        if (fenceBlocked)
        {
            await using var hold = FenceCommand(blocker, shared: false, unlock: false);
            await hold.ExecuteNonQueryAsync();
        }
        else
        {
            transaction = await blocker.BeginTransactionAsync();
            await using var hold = new NpgsqlCommand("LOCK TABLE appsurface_durable.schema_migration IN ACCESS EXCLUSIVE MODE", blocker, transaction);
            await hold.ExecuteNonQueryAsync();
        }
        var started = Signal();
        var service = new DurableDoctorService(TimeProvider.System, (stage, _) =>
        {
            if (stage == (fenceBlocked ? DurableDoctorStage.Fence : DurableDoctorStage.Schema)) started.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var timer = Stopwatch.StartNew();
        var call = service.InspectAsync(MarkSession(fixture.RuntimeConnectionString, "doctor-real-blocked"),
            fixture.CreateRequest(timeout: TimeSpan.FromSeconds(1)), CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TestLimit);
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(() => call);
        Assert.Equal(DurableDoctorFailureKind.Unavailable, failure.Kind);
        Assert.Contains("deadline", failure.Categories);
        Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(3));
        if (fenceBlocked)
        {
            await using var release = FenceCommand(blocker, shared: false, unlock: true);
            Assert.True((bool)(await release.ExecuteScalarAsync())!);
        }
        else
        {
            await transaction!.RollbackAsync();
            await transaction.DisposeAsync();
        }
        await AssertReleasedAsync(fixture, "doctor-real-blocked");
        Assert.True((await fixture.InspectAsync()).Schema!.IsCompatible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancel_before_or_after_online_deadline_wins_at_cleanup(bool afterDeadline)
    {
        await using var fixture = await DurableDoctorFixture.CreateAsync();
        using var caller = new CancellationTokenSource();
        var atRuntime = Signal();
        var never = Signal();
        var service = new DurableDoctorService(TimeProvider.System, async (stage, token) =>
        {
            if (stage == DurableDoctorStage.Runtime)
            {
                atRuntime.TrySetResult();
                await never.Task.WaitAsync(token);
            }
            if (afterDeadline && stage == DurableDoctorStage.Rollback) caller.Cancel();
        });
        var call = service.InspectAsync(MarkSession(fixture.RuntimeConnectionString, "doctor-caller-deadline"),
            fixture.CreateRequest(timeout: TimeSpan.FromSeconds(1)), caller.Token).AsTask();
        await atRuntime.Task.WaitAsync(TestLimit);
        if (!afterDeadline) caller.Cancel();
        var failure = await Assert.ThrowsAsync<DurableDoctorFailureException>(() => call);
        Assert.Equal(DurableDoctorFailureKind.Canceled, failure.Kind);
        Assert.Equal(["caller-canceled"], failure.Categories);
        await AssertReleasedAsync(fixture, "doctor-caller-deadline");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string MarkSession(string connection, string name) => new NpgsqlConnectionStringBuilder(connection) { ApplicationName = name }.ConnectionString;
    private static NpgsqlCommand FenceCommand(NpgsqlConnection connection, bool shared, bool unlock)
    {
        var function = unlock ? "pg_advisory_unlock" : "pg_advisory_lock";
        if (shared) function += "_shared";
        var command = new NpgsqlCommand($"SELECT pg_catalog.{function}(@key)", connection);
        command.Parameters.AddWithValue("key", DurableDoctorService.MigrationAdvisoryLock);
        return command;
    }

    private static async Task WaitForFenceAsync(DurableDoctorFixture fixture, int pid, bool granted, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await connection.OpenAsync(token);
        while (true)
        {
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_locks WHERE pid=@pid AND locktype='advisory' AND granted=@granted)", connection);
            command.Parameters.AddWithValue("pid", pid);
            command.Parameters.AddWithValue("granted", granted);
            if (await command.ExecuteScalarAsync(token) is true) return;
            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
        }
    }

    private static async Task AssertReleasedAsync(DurableDoctorFixture fixture, string applicationName)
    {
        await using var connection = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await connection.OpenAsync();
        using var deadline = new CancellationTokenSource(TestLimit);
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT NOT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@name)
                  AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_locks
                      WHERE locktype='advisory'
                        AND database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
                        AND classid::bigint=(@key >> 32) AND objid::bigint=(@key & 4294967295))
                """, connection);
            command.Parameters.AddWithValue("name", applicationName);
            command.Parameters.AddWithValue("key", DurableDoctorService.MigrationAdvisoryLock);
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }

    /// <summary>Uses the repository's manual-timer pattern to expire owned deadlines deterministically.</summary>
    private sealed class DoctorManualClock : TimeProvider
    {
        private readonly List<DoctorTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DoctorTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            _ticks += duration.Ticks;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        private sealed class DoctorTimer(DoctorManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private long _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            internal void FireIfDue()
            {
                if (_disposed || clock.GetTimestamp() < _due) return;
                _due = _period == 0 ? long.MaxValue : clock.GetTimestamp() + _period;
                callback(state);
            }
        }
    }
}
