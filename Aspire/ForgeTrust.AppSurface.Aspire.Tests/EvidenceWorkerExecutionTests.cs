using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceWorkerExecutionTests
{
    private static readonly TimeSpan Cleanup = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Job = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData(99_999_999L, false, true)]
    [InlineData(100_000_000L, false, false)]
    [InlineData(100_000_001L, false, false)]
    [InlineData(99_999_999L, true, true)]
    [InlineData(100_000_000L, true, false)]
    [InlineData(100_000_001L, true, false)]
    public async Task ExecuteAsync_UsesElapsedDeadlineWhenTimerDeliveryIsDelayed(
        long elapsedTicks, bool ownedWrite, bool passes)
    {
        var expected = passes ? EvidenceWorkerStageOutcome.Passed : EvidenceWorkerStageOutcome.TimedOut;
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var result = await execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), _ =>
        {
            if (ownedWrite)
            {
                Assert.NotNull(execution.TrackOwnedWork(_ =>
                {
                    clock.Advance(TimeSpan.FromTicks(elapsedTicks), fireTimers: false);
                    return ValueTask.CompletedTask;
                }));
            }
            else
            {
                clock.Advance(TimeSpan.FromTicks(elapsedTicks), fireTimers: false);
            }
            return ValueTask.FromResult("callback result");
        });

        Assert.Equal(expected, result.Outcome);
        if (expected == EvidenceWorkerStageOutcome.Passed)
        {
            Assert.Equal("callback result", result.Value);
            Assert.Equal(EvidenceWorkerTerminalCode.None, execution.TerminalCode);
            Assert.Equal(0, supervisor.StopRequests);
        }
        else
        {
            Assert.Null(result.Value);
            Assert.Equal(EvidenceWorkerTerminalCode.DeadlineExceeded, execution.TerminalCode);
            Assert.True(execution.OwnWorkStopped);
            Assert.Equal(1, supervisor.StopRequests);
        }
    }

    [Theory]
    [InlineData(9_999_999L, true)]
    [InlineData(10_000_000L, false)]
    [InlineData(10_000_001L, false)]
    public async Task CollectAsync_UsesElapsedDeadlineWhenTimerDeliveryIsDelayed(long elapsedTicks, bool passes)
    {
        var expected = passes ? EvidenceWorkerStageOutcome.Passed : EvidenceWorkerStageOutcome.TimedOut;
        var clock = new ManualTimeProvider();
        var execution = Create(new TestSupervisor(), clock);
        Assert.True(await execution.StopAndDisposeAsync());

        var result = await execution.CollectAsync(TimeSpan.FromSeconds(1), _ =>
        {
            clock.Advance(TimeSpan.FromTicks(elapsedTicks), fireTimers: false);
            return ValueTask.FromResult("manifest");
        });

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected == EvidenceWorkerStageOutcome.Passed, execution.CollectionCompleted);
        Assert.Equal(expected == EvidenceWorkerStageOutcome.Passed ? "manifest" : null, result.Value);
        Assert.Equal(expected == EvidenceWorkerStageOutcome.Passed
            ? EvidenceWorkerTerminalCode.None : EvidenceWorkerTerminalCode.DeadlineExceeded, execution.TerminalCode);
    }

    [Theory]
    [InlineData(199_999_999L, false)]
    [InlineData(200_000_000L, true)]
    [InlineData(200_000_001L, true)]
    public async Task StopAndDisposeAsync_RejectsLateDisposalDespiteDelayedTimer(long elapsedTicks, bool fatal)
    {
        var clock = new ManualTimeProvider();
        var execution = Create(new TestSupervisor(), clock);
        var nextDisposed = false;
        Assert.True(execution.RegisterDisposer(_ => { nextDisposed = true; return ValueTask.CompletedTask; }));
        Assert.True(execution.RegisterDisposer(_ =>
        {
            clock.Advance(TimeSpan.FromTicks(elapsedTicks), fireTimers: false);
            return ValueTask.CompletedTask;
        }));

        if (fatal)
        {
            await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() => execution.StopAndDisposeAsync().AsTask());
            Assert.False(nextDisposed);
            Assert.False(execution.CleanupCompleted);
            Assert.False(execution.CollectionCompleted);
        }
        else
        {
            Assert.True(await execution.StopAndDisposeAsync());
            Assert.True(nextDisposed);
            Assert.True(execution.CleanupCompleted);
        }
    }

    [Theory]
    [InlineData(49_999_999L, false, false)]
    [InlineData(50_000_000L, false, true)]
    [InlineData(50_000_001L, false, true)]
    [InlineData(49_999_999L, true, false)]
    [InlineData(50_000_000L, true, true)]
    [InlineData(50_000_001L, true, true)]
    public async Task StopAndDisposeAsync_RejectsLateSupervisorDespiteDelayedTimer(long elapsedTicks, bool duringExit, bool fatal)
    {
        var clock = new ManualTimeProvider();
        Action advance = () => clock.Advance(TimeSpan.FromTicks(elapsedTicks), fireTimers: false);
        var supervisor = new TestSupervisor { OnClose = duringExit ? null : advance, OnExit = duringExit ? advance : null };
        var execution = Create(supervisor, clock);
        var disposed = false;
        Assert.True(execution.RegisterDisposer(_ => { disposed = true; return ValueTask.CompletedTask; }));

        if (fatal)
        {
            await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() => execution.StopAndDisposeAsync().AsTask());
            Assert.False(disposed);
            Assert.False(execution.OwnWorkStopped);
            Assert.False(execution.CleanupCompleted);
        }
        else
        {
            Assert.True(await execution.StopAndDisposeAsync());
            Assert.True(disposed);
            Assert.True(execution.OwnWorkStopped);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsWorkThatWouldConsumeCollectionReserve()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = new EvidenceWorkerExecution(supervisor, clock, TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5),
            _ => new EvidenceWorkerTestInterruptionException("fatal"), collectionReserve: TimeSpan.FromSeconds(5));
        var invoked = false;
        var result = await execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(6), _ =>
        {
            invoked = true;
            return ValueTask.FromResult(true);
        });
        Assert.Equal(EvidenceWorkerStageOutcome.Rejected, result.Outcome);
        Assert.False(invoked);
        Assert.True(execution.OwnWorkStopped);
        Assert.Equal(EvidenceWorkerTerminalCode.AdmissionClosed, execution.TerminalCode);
    }

    [Fact]
    public async Task ExecuteAsync_CooperativelyCancelsAndRetainsOriginalDeadlineCause()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var started = NewSignal();
        var run = execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }).AsTask();

        await started.Task;
        await clock.WaitForTimerCreationsAsync(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await run;

        Assert.Equal(EvidenceWorkerStageOutcome.TimedOut, result.Outcome);
        Assert.Equal(EvidenceWorkerTerminalCode.DeadlineExceeded, execution.TerminalCode);
        Assert.Equal(1, supervisor.StopRequests);
        Assert.True(supervisor.SawFreshStoppingToken);
        Assert.True(supervisor.ExitAcknowledged);
    }

    [Fact]
    public async Task CollectAsync_AfterCooperativeTimeoutCanReturnFailureManifest()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var started = NewSignal();
        var run = execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }).AsTask();

        await started.Task;
        await clock.WaitForTimerCreationsAsync(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await run;
        Assert.Equal(EvidenceWorkerStageOutcome.TimedOut, result.Outcome);
        Assert.True(execution.OwnWorkStopped);

        Assert.False(await execution.StopAndDisposeAsync());
        Assert.True(execution.CleanupCompleted);
        var collected = await execution.CollectAsync(TimeSpan.FromSeconds(1), _ =>
            ValueTask.FromResult("bounded failure manifest"));

        Assert.Equal(EvidenceWorkerStageOutcome.Passed, collected.Outcome);
        Assert.Equal("bounded failure manifest", collected.Value);
        Assert.Equal(EvidenceWorkerTerminalCode.DeadlineExceeded, execution.TerminalCode);
        Assert.True(execution.CollectionCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_LatePassedResultDuringGraceCannotUpgradeTimeout()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var started = NewSignal();
        var release = NewSignal();
        var run = execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return true;
        }).AsTask();

        await started.Task;
        await clock.WaitForTimerCreationsAsync(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        await supervisor.StopRequested.Task;
        release.TrySetResult();

        var result = await run;
        Assert.Equal(EvidenceWorkerStageOutcome.TimedOut, result.Outcome);
        Assert.Equal(EvidenceWorkerTerminalCode.DeadlineExceeded, execution.TerminalCode);
    }

    [Fact]
    public async Task ExecuteAsync_UnjoinedCallbackUsesFatalPathBeforeAnyDisposer()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor { HoldExitAcknowledgement = true };
        var execution = Create(supervisor, clock);
        var disposed = false;
        execution.RegisterDisposer(_ => { disposed = true; return ValueTask.CompletedTask; });
        var started = NewSignal();
        var run = execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), _ =>
        {
            started.TrySetResult();
            return new ValueTask<bool>(new TaskCompletionSource<bool>().Task);
        }).AsTask();
        await started.Task;
        await clock.WaitForTimerCreationsAsync(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        await supervisor.StopRequested.Task;
        await AdvanceUntilCompleteAsync(clock, run, Grace);

        var exception = await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() => run);
        Assert.Contains("Owned Evidence", exception.Message, StringComparison.Ordinal);
        Assert.False(disposed);
    }

    [Fact]
    public async Task ExecuteAsync_JoinsInFlightWriteBeforeReturningTimedOut()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var callbackStarted = NewSignal();
        var writeStarted = NewSignal();
        var writeRelease = NewSignal();
        var writeSettled = false;
        var run = execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), async token =>
        {
            var write = execution.TrackOwnedWork(async _ =>
            {
                writeStarted.TrySetResult();
                await writeRelease.Task;
                writeSettled = true;
            }, token);
            callbackStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            await write!;
            return true;
        }).AsTask();

        await callbackStarted.Task;
        await writeStarted.Task;
        await clock.WaitForTimerCreationsAsync(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        await supervisor.StopRequested.Task;
        Assert.False(run.IsCompleted);
        writeRelease.TrySetResult();

        var result = await run;
        Assert.True(writeSettled);
        Assert.Equal(EvidenceWorkerStageOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task StopAndDisposeAsync_StalledDisposerPreventsNextDisposer()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var secondDisposerRan = false;
        execution.RegisterDisposer(_ => { secondDisposerRan = true; return ValueTask.CompletedTask; });
        execution.RegisterDisposer(_ => new ValueTask(new TaskCompletionSource().Task));

        var cleanup = execution.StopAndDisposeAsync().AsTask();
        await clock.WaitForTimerCreationsAsync(3);
        clock.Advance(Cleanup);

        await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() => cleanup);
        Assert.False(secondDisposerRan);
    }

    [Fact]
    public async Task StopAndDisposeAsync_ConcurrentCallersShareOneDisposalPass()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var disposerStarted = NewSignal();
        var releaseDisposer = NewSignal();
        var disposerCalls = 0;
        execution.RegisterDisposer(async _ =>
        {
            Interlocked.Increment(ref disposerCalls);
            disposerStarted.TrySetResult();
            await releaseDisposer.Task;
        });

        var firstCleanup = execution.StopAndDisposeAsync().AsTask();
        await disposerStarted.Task;
        var concurrentCleanup = execution.StopAndDisposeAsync().AsTask();
        releaseDisposer.TrySetResult();

        var results = await Task.WhenAll(firstCleanup, concurrentCleanup);
        Assert.Same(firstCleanup, concurrentCleanup);
        Assert.True(results[0]);
        Assert.True(results[1]);
        Assert.Equal(1, disposerCalls);
        Assert.Equal(1, supervisor.StopRequests);
        Assert.True(execution.CleanupCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_SynchronousPreTaskStallDoesNotBlockDeadlineControl()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var entered = NewSignal();
        var release = NewSignal();
        var run = execution.ExecuteAsync(EvidenceRunStage.Admission, TimeSpan.FromSeconds(10), _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return ValueTask.FromResult(true);
        }).AsTask();

        await entered.Task;
        await clock.WaitForTimerCreationsAsync(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        await supervisor.StopRequested.Task;
        release.TrySetResult();
        Assert.Equal(EvidenceWorkerStageOutcome.TimedOut, (await run).Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsFurtherCallbacksAfterTerminalLatch()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        var result = await execution.ExecuteAsync(EvidenceRunStage.Admission, TimeSpan.FromSeconds(1), _ => ValueTask.FromResult(true), caller.Token);
        var invoked = false;
        var next = await execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(1), _ =>
        {
            invoked = true;
            return ValueTask.FromResult(true);
        });

        Assert.Equal(EvidenceWorkerStageOutcome.Cancelled, result.Outcome);
        Assert.NotEqual(EvidenceWorkerStageOutcome.Passed, next.Outcome);
        Assert.False(invoked);
    }

    [Fact]
    public async Task TrackOwnedWork_QuotaFailureClosesRegistrationImmediatelyAndKeepsFirstCause()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var quota = EvidenceRunByteQuota.CreateArtifact(limit: 1, onExceeded: execution.LatchFailure);
        var invoked = false;

        Assert.False(quota.TryReserve(2, out _));
        var ownedWork = execution.TrackOwnedWork(_ =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        });

        Assert.True(execution.IsAdmissionClosed);
        Assert.Equal(EvidenceWorkerTerminalCode.StageFailed, execution.TerminalCode);
        Assert.Null(ownedWork);
        Assert.False(invoked);

        await execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.DeadlineExceeded);
        Assert.Equal(EvidenceWorkerTerminalCode.StageFailed, execution.TerminalCode);
    }

    [Fact]
    public async Task ExecuteAsync_StaleArmedLeaseRejectsBeforeInvokingCallback()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        supervisor.Armed = false;
        var invoked = false;

        var result = await execution.ExecuteAsync(EvidenceRunStage.Admission, TimeSpan.FromSeconds(1), _ =>
        {
            invoked = true;
            return ValueTask.FromResult(true);
        });

        Assert.Equal(EvidenceWorkerStageOutcome.Rejected, result.Outcome);
        Assert.Equal(EvidenceWorkerTerminalCode.AdmissionClosed, execution.TerminalCode);
        Assert.False(invoked);
    }

    [Fact]
    public async Task StopAndDisposeAsync_ExposesJoinAndCleanupBeforeCollection()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        var stage = await execution.ExecuteAsync(EvidenceRunStage.Admission, TimeSpan.FromSeconds(1), _ => ValueTask.FromResult(true));
        Assert.Equal(EvidenceWorkerStageOutcome.Passed, stage.Outcome);

        Assert.True(await execution.StopAndDisposeAsync());
        Assert.True(execution.OwnWorkStopped);
        Assert.True(execution.CleanupCompleted);

        var collected = await execution.CollectAsync(TimeSpan.FromSeconds(1), _ => ValueTask.FromResult("complete"));
        Assert.Equal(EvidenceWorkerStageOutcome.Passed, collected.Outcome);
        Assert.Equal("complete", collected.Value);
        Assert.True(execution.CollectionCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_ChildWithoutExitAcknowledgementUsesFatalPath()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor { HoldExitAcknowledgement = true };
        var execution = Create(supervisor, clock);
        var childStarted = NewSignal();
        var childRelease = NewSignal();
        var run = execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), _ =>
        {
            execution.TrackOwnedWork(async _ =>
            {
                childStarted.TrySetResult();
                await childRelease.Task;
            });
            return ValueTask.FromResult(true);
        }).AsTask();
        await childStarted.Task;
        await clock.WaitForTimerCreationsAsync(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        await supervisor.StopRequested.Task;
        childRelease.TrySetResult();
        await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() => run);
    }

    private static EvidenceWorkerExecution Create(TestSupervisor supervisor, ManualTimeProvider clock) =>
        new(supervisor, clock, Job, Cleanup, Grace, message => new EvidenceWorkerTestInterruptionException(message));

    private static async Task AdvanceUntilCompleteAsync(ManualTimeProvider clock, Task task, TimeSpan interval)
    {
        for (var attempt = 0; attempt < 32 && !task.IsCompleted; attempt++)
        {
            clock.Advance(interval);
            await Task.Yield();
        }
        Assert.True(task.IsCompleted, "The lifecycle did not reach a terminal result under the advanced fake clock.");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TestSupervisor : IEvidenceExecutionSupervisor
    {
        internal TaskCompletionSource StopRequested { get; } = NewSignal();
        internal bool HoldExitAcknowledgement { get; init; }
        internal int StopRequests { get; private set; }
        internal bool ExitAcknowledged { get; private set; }
        internal bool SawFreshStoppingToken { get; private set; }
        internal bool Armed { get; set; } = true;
        internal Action? OnClose { get; init; }
        internal Action? OnExit { get; init; }
        public bool IsArmed => Armed;
        public string RunId => "test-run";

        public void CloseAdmission() => OnClose?.Invoke();

        public ValueTask RequestStopAsync(CancellationToken stoppingToken)
        {
            StopRequests++;
            SawFreshStoppingToken = !stoppingToken.IsCancellationRequested;
            StopRequested.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask WaitForOwnedExitAsync(CancellationToken stoppingToken)
        {
            OnExit?.Invoke();
            if (HoldExitAcknowledgement)
            {
                return ValueTask.FromException(new InvalidOperationException("Exit acknowledgement was not received."));
            }

            ExitAcknowledged = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private long _timestamp;
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private int _createdTimers;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp() { lock (_gate) return _timestamp; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer;
            List<TaskCompletionSource> ready;
            lock (_gate)
            {
                timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                _createdTimers++;
                timer.ChangeUnderLock(dueTime, period);
                ready = _waiters.Where(waiter => _createdTimers >= waiter.Count).Select(waiter => waiter.Signal).ToList();
                _waiters.RemoveAll(waiter => _createdTimers >= waiter.Count);
            }
            foreach (var signal in ready) signal.TrySetResult();
            return timer;
        }

        internal Task WaitForTimerCreationsAsync(int count)
        {
            lock (_gate)
            {
                if (_createdTimers >= count) return Task.CompletedTask;
                var signal = NewSignal();
                _waiters.Add((count, signal));
                return signal.Task;
            }
        }

        internal void Advance(TimeSpan amount, bool fireTimers = true)
        {
            if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            List<ManualTimer> due;
            lock (_gate)
            {
                _timestamp += amount.Ticks;
                _utcNow += amount;
                if (!fireTimers) return;
                due = _timers.Where(timer => timer.IsDue(_timestamp)).ToList();
                foreach (var timer in due) timer.AdvanceUnderLock(_timestamp);
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long? _dueAt;
            private TimeSpan _period;
            private bool _disposed;

            internal bool IsDue(long now) => !_disposed && _dueAt is long due && due <= now;
            internal void AdvanceUnderLock(long now) => _dueAt = _period > TimeSpan.Zero ? now + _period.Ticks : null;
            internal void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate) return ChangeUnderLock(dueTime, period);
            }

            internal bool ChangeUnderLock(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._timestamp + dueTime.Ticks;
                return true;
            }

            public void Dispose()
            {
                lock (owner._gate) { _disposed = true; _dueAt = null; }
            }

            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
