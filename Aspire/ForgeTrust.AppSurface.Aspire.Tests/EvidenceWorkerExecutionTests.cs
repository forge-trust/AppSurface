using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceWorkerExecutionTests
{
    private static readonly TimeSpan Cleanup = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Job = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

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

    [Theory]
    [InlineData(false, "test-run")]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    public void Constructor_RejectsUnarmedOrUnidentifiedSupervisorBeforeAnyStop(bool armed, string runId)
    {
        var supervisor = new TestSupervisor { Armed = armed, RunIdentifier = runId };
        Assert.Throws<ArgumentException>(() => Create(supervisor, new ManualTimeProvider()));
        Assert.Equal(0, supervisor.StopRequests);
    }

    [Theory]
    [InlineData(0, 20, 5, 0)]
    [InlineData(120, 0, 5, 0)]
    [InlineData(120, 601, 5, 0)]
    [InlineData(120, 20, 0, 0)]
    [InlineData(120, 40, 31, 0)]
    [InlineData(120, 4, 5, 0)]
    [InlineData(120, 20, 5, -1)]
    [InlineData(120, 20, 5, 61)]
    [InlineData(5, 20, 5, 5)]
    public void Constructor_RejectsBudgetsThatCannotProtectStopOrCollection(
        int jobSeconds, int cleanupSeconds, int stoppingSeconds, int collectionSeconds)
    {
        var supervisor = new TestSupervisor();
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceWorkerExecution(
            supervisor, new ManualTimeProvider(), TimeSpan.FromSeconds(jobSeconds),
            TimeSpan.FromSeconds(cleanupSeconds), TimeSpan.FromSeconds(stoppingSeconds),
            _ => new EvidenceWorkerTestInterruptionException("fatal"),
            collectionReserve: TimeSpan.FromSeconds(collectionSeconds)));
        Assert.Equal(0, supervisor.StopRequests);
    }

    [Theory]
    [InlineData((int)EvidenceRunStage.Admission, 0)]
    [InlineData((int)EvidenceRunStage.Admission, 31)]
    [InlineData((int)EvidenceRunStage.Start, 121)]
    [InlineData(999, 1)]
    public async Task ExecuteAsync_RejectsInvalidDeclaredDeadlineWithoutStartingWork(int stage, int seconds)
    {
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, new ManualTimeProvider());
        var invoked = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => execution.ExecuteAsync((EvidenceRunStage)stage,
            TimeSpan.FromSeconds(seconds), _ =>
            {
                invoked = true;
                return ValueTask.FromResult(true);
            }).AsTask());
        Assert.False(invoked);
        Assert.Equal(0, supervisor.StopRequests);
        Assert.Equal(EvidenceWorkerTerminalCode.None, execution.TerminalCode);
    }

    [Fact]
    public async Task RequestTerminalStopAsync_RejectsNoneAndUsesFreshStopTokenForCancelledCaller()
    {
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, new ManualTimeProvider());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.None).AsTask());
        Assert.False(execution.IsAdmissionClosed);
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.StageFailed, caller.Token);
        await execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.DeadlineExceeded);

        Assert.Equal(EvidenceWorkerTerminalCode.CallerCancelled, execution.TerminalCode);
        Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(execution.TerminalException).CancellationToken);
        Assert.True(supervisor.SawFreshStoppingToken);
        Assert.True(execution.OwnWorkStopped);
        Assert.Equal(1, supervisor.StopRequests);
        Assert.False(execution.RegisterDisposer(_ => ValueTask.CompletedTask));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_LateSuccessCannotRestoreFailedOrReplacedLease(bool replaceLease)
    {
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, new ManualTimeProvider());
        var result = await execution.ExecuteAsync(EvidenceRunStage.Resource, TimeSpan.FromSeconds(1), _ =>
        {
            if (replaceLease) supervisor.RunIdentifier = "replacement-run";
            else execution.LatchFailure();
            return ValueTask.FromResult("late success");
        });

        Assert.Equal(replaceLease ? EvidenceWorkerStageOutcome.Rejected : EvidenceWorkerStageOutcome.Failed, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal(replaceLease ? EvidenceWorkerTerminalCode.AdmissionClosed : EvidenceWorkerTerminalCode.StageFailed, execution.TerminalCode);
        Assert.True(execution.OwnWorkStopped);
        Assert.Null(execution.TrackOwnedWork(_ => ValueTask.CompletedTask));
        Assert.False(execution.RegisterDisposer(_ => ValueTask.CompletedTask));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CallbackFaultIsSettledAndCannotProduceAPassedValue(bool cancelledCaller)
    {
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, new ManualTimeProvider());
        using var caller = new CancellationTokenSource();
        var fault = new OperationCanceledException("safe fixture cancellation", caller.Token);
        var result = await execution.ExecuteAsync<string>(EvidenceRunStage.Producer, TimeSpan.FromSeconds(1), _ =>
        {
            if (cancelledCaller) caller.Cancel();
            return ValueTask.FromException<string>(fault);
        }, caller.Token);

        Assert.Equal(cancelledCaller ? EvidenceWorkerStageOutcome.Cancelled : EvidenceWorkerStageOutcome.Failed, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal(cancelledCaller ? EvidenceWorkerTerminalCode.CallerCancelled : EvidenceWorkerTerminalCode.StageFailed, execution.TerminalCode);
        Assert.True(execution.OwnWorkStopped);
        Assert.True(supervisor.ExitAcknowledged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_WaitsForOwnedChildAfterCallbackAndJoinsItOnTerminalSignal(bool callerCancels)
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, clock);
        using var caller = new CancellationTokenSource();
        var childStarted = NewSignal();
        var childRelease = NewSignal();
        var callbackReturned = NewSignal();
        var run = execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), _ =>
        {
            Assert.NotNull(execution.TrackOwnedWork(async _ =>
            {
                childStarted.TrySetResult();
                await childRelease.Task;
            }));
            callbackReturned.TrySetResult();
            return ValueTask.FromResult("callback completed");
        }, caller.Token).AsTask();
        await childStarted.Task;
        await callbackReturned.Task;
        await clock.WaitForTimerCreationsAsync(1);
        Assert.False(run.IsCompleted);
        if (callerCancels) caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(10));
        await supervisor.StopRequested.Task;
        Assert.False(run.IsCompleted);
        childRelease.TrySetResult();

        var result = await run;
        Assert.Equal(callerCancels ? EvidenceWorkerStageOutcome.Cancelled : EvidenceWorkerStageOutcome.TimedOut, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal(callerCancels ? EvidenceWorkerTerminalCode.CallerCancelled : EvidenceWorkerTerminalCode.DeadlineExceeded, execution.TerminalCode);
        Assert.True(execution.OwnWorkStopped);
        Assert.True(supervisor.ExitAcknowledged);
    }

    [Fact]
    public async Task StopAndDisposeAsync_FaultedDisposerDoesNotSkipEarlierOwnersOrRestoreEligibility()
    {
        var execution = Create(new TestSupervisor(), new ManualTimeProvider());
        var order = new List<string>();
        var fault = new InvalidOperationException("safe fixture disposer failure");
        Assert.True(execution.RegisterDisposer(_ => { order.Add("parent"); return ValueTask.CompletedTask; }));
        Assert.True(execution.RegisterDisposer(_ => { order.Add("child"); return ValueTask.FromException(fault); }));

        Assert.False(await execution.StopAndDisposeAsync());
        Assert.Equal(["child", "parent"], order);
        Assert.Same(fault, execution.TerminalException);
        Assert.Equal(EvidenceWorkerTerminalCode.CleanupFailed, execution.TerminalCode);
        Assert.True(execution.OwnWorkStopped);
        Assert.False(execution.CleanupCompleted);
        var collected = await execution.CollectAsync(TimeSpan.FromSeconds(1), _ => ValueTask.FromResult("failure manifest"));
        Assert.Equal(EvidenceWorkerStageOutcome.Passed, collected.Outcome);
        Assert.Equal("failure manifest", collected.Value);
        Assert.Equal(EvidenceWorkerTerminalCode.CleanupFailed, execution.TerminalCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndDisposeAsync_SupervisorFaultFailsStopBeforeDisposal(bool duringStopRequest)
    {
        var supervisor = new TestSupervisor();
        var fault = new InvalidOperationException("safe fixture supervisor failure");
        if (duringStopRequest) supervisor.OnStopRequest = _ => ValueTask.FromException(fault);
        else supervisor.OnClose = () => throw fault;
        var execution = Create(supervisor, new ManualTimeProvider());
        var disposed = false;
        execution.RegisterDisposer(_ => { disposed = true; return ValueTask.CompletedTask; });

        var fatal = await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() => execution.StopAndDisposeAsync().AsTask());
        Assert.Contains(duringStopRequest ? "request owned-work stop" : "closed safely", fatal.Message, StringComparison.Ordinal);
        Assert.False(disposed);
        Assert.False(execution.OwnWorkStopped);
        Assert.False(execution.CleanupCompleted);
        Assert.Equal(duringStopRequest ? EvidenceWorkerTerminalCode.CleanupFailed : EvidenceWorkerTerminalCode.None, execution.TerminalCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndDisposeAsync_StalledSupervisorCannotConsumeStoppingGraceOrRunDisposers(bool duringExit)
    {
        var clock = new ManualTimeProvider();
        var entered = NewSignal();
        var settled = NewSignal();
        async ValueTask Stall(CancellationToken token)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { settled.TrySetResult(); }
        }
        var supervisor = new TestSupervisor();
        if (duringExit) supervisor.OnWaitForExit = Stall;
        else supervisor.OnStopRequest = Stall;
        var execution = Create(supervisor, clock);
        var disposed = false;
        execution.RegisterDisposer(_ => { disposed = true; return ValueTask.CompletedTask; });
        var cleanup = execution.StopAndDisposeAsync().AsTask();
        await entered.Task;
        clock.Advance(Grace);

        var fatal = await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() => cleanup);
        await settled.Task;
        Assert.Contains(duringExit ? "acknowledge owned-work exit" : "stop request", fatal.Message, StringComparison.Ordinal);
        Assert.False(disposed);
        Assert.False(execution.OwnWorkStopped);
        Assert.False(execution.CleanupCompleted);
    }

    [Fact]
    public async Task StopAndDisposeAsync_CannotSpendTheProtectedCollectionReserve()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new TestSupervisor();
        var execution = new EvidenceWorkerExecution(supervisor, clock, TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(10), Grace, _ => new EvidenceWorkerTestInterruptionException("fatal"),
            collectionReserve: TimeSpan.FromSeconds(5));
        var disposed = false;
        execution.RegisterDisposer(_ => { disposed = true; return ValueTask.CompletedTask; });
        clock.Advance(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() => execution.StopAndDisposeAsync().AsTask());
        Assert.False(disposed);
        Assert.Equal(0, supervisor.StopRequests);
        Assert.False(execution.OwnWorkStopped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public async Task CollectAsync_RejectsUnboundedDeadlineBeforeInvokingCollector(int seconds)
    {
        var execution = Create(new TestSupervisor(), new ManualTimeProvider());
        var invoked = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => execution.CollectAsync(
            TimeSpan.FromSeconds(seconds), _ => { invoked = true; return ValueTask.FromResult(true); }).AsTask());
        Assert.False(invoked);
        Assert.False(execution.CollectionCompleted);
    }

    [Fact]
    public async Task CollectAsync_RequiresStoppedOwnersAndRejectsAReplacedLeaseOrRepeatedCollection()
    {
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, new ManualTimeProvider());
        var calls = 0;
        ValueTask<string> Collect(CancellationToken _) { calls++; return ValueTask.FromResult("manifest"); }
        Assert.Equal(EvidenceWorkerStageOutcome.Rejected, (await execution.CollectAsync(TimeSpan.FromSeconds(1), Collect)).Outcome);
        Assert.True(await execution.StopAndDisposeAsync());
        supervisor.RunIdentifier = "replaced-run";
        Assert.Equal(EvidenceWorkerStageOutcome.Rejected, (await execution.CollectAsync(TimeSpan.FromSeconds(1), Collect)).Outcome);
        supervisor.RunIdentifier = "test-run";
        Assert.Equal(EvidenceWorkerStageOutcome.Passed, (await execution.CollectAsync(TimeSpan.FromSeconds(1), Collect)).Outcome);
        Assert.Equal(EvidenceWorkerStageOutcome.Rejected, (await execution.CollectAsync(TimeSpan.FromSeconds(1), Collect)).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CollectAsync_CancelledBeforeAdmissionCanStillCollectAFailureManifestLater()
    {
        var execution = Create(new TestSupervisor(), new ManualTimeProvider());
        Assert.True(await execution.StopAndDisposeAsync());
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var invoked = false;
        var cancelled = await execution.CollectAsync(TimeSpan.FromSeconds(1), _ =>
        {
            invoked = true;
            return ValueTask.FromResult("should not run");
        }, caller.Token);
        Assert.Equal(EvidenceWorkerStageOutcome.Cancelled, cancelled.Outcome);
        Assert.False(invoked);
        Assert.False(execution.CollectionCompleted);
        var failure = await execution.CollectAsync(TimeSpan.FromSeconds(1), _ => ValueTask.FromResult("failure manifest"));
        Assert.Equal(EvidenceWorkerStageOutcome.Passed, failure.Outcome);
        Assert.Equal("failure manifest", failure.Value);
        Assert.Equal(EvidenceWorkerTerminalCode.CallerCancelled, execution.TerminalCode);
    }

    [Fact]
    public async Task CollectAsync_RejectsWhenTheWholeDeadlineCannotFitCollection()
    {
        var clock = new ManualTimeProvider();
        var execution = Create(new TestSupervisor(), clock);
        Assert.True(await execution.StopAndDisposeAsync());
        clock.Advance(Job - TimeSpan.FromTicks(1));
        var invoked = false;
        var result = await execution.CollectAsync(TimeSpan.FromSeconds(1), _ =>
        {
            invoked = true;
            return ValueTask.FromResult("must not run");
        });
        Assert.Equal(EvidenceWorkerStageOutcome.Rejected, result.Outcome);
        Assert.False(invoked);
        Assert.False(execution.CollectionCompleted);
    }

    [Fact]
    public async Task TrackOwnedWork_ExpiredLeaseCannotLaunchAChild()
    {
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, new ManualTimeProvider());
        supervisor.Armed = false;
        var invoked = false;
        Assert.Null(execution.TrackOwnedWork(_ =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        }));
        Assert.False(invoked);
        await execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.AdmissionClosed);
        Assert.True(execution.OwnWorkStopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectAsync_DiscardsAValueWhenCollectorInvalidatesLeaseOrLatchesFailure(bool replaceLease)
    {
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, new ManualTimeProvider());
        Assert.True(await execution.StopAndDisposeAsync());
        var collected = await execution.CollectAsync(TimeSpan.FromSeconds(1), _ =>
        {
            if (replaceLease) supervisor.RunIdentifier = "replacement-run";
            else execution.LatchFailure();
            return ValueTask.FromResult("must not be returned");
        });
        Assert.Equal(EvidenceWorkerStageOutcome.Failed, collected.Outcome);
        Assert.Null(collected.Value);
        Assert.False(execution.CollectionCompleted);
        Assert.Equal(replaceLease ? EvidenceWorkerTerminalCode.AdmissionClosed : EvidenceWorkerTerminalCode.StageFailed, execution.TerminalCode);
    }

    [Fact]
    public async Task CollectAsync_CollectorFaultBecomesFailedWithoutExposingAPassedValue()
    {
        var execution = Create(new TestSupervisor(), new ManualTimeProvider());
        Assert.True(await execution.StopAndDisposeAsync());
        var fault = new InvalidOperationException("safe fixture collector failure");
        var collected = await execution.CollectAsync<string>(TimeSpan.FromSeconds(1), _ => ValueTask.FromException<string>(fault));
        Assert.Equal(EvidenceWorkerStageOutcome.Failed, collected.Outcome);
        Assert.Null(collected.Value);
        Assert.Same(fault, execution.TerminalException);
        Assert.Equal(EvidenceWorkerTerminalCode.StageFailed, execution.TerminalCode);
        Assert.False(execution.CollectionCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectAsync_CancellationOrTimeoutJoinsLateCollectorAndRejectsItsValue(bool callerCancels)
    {
        var clock = new ManualTimeProvider();
        var execution = Create(new TestSupervisor(), clock);
        Assert.True(await execution.StopAndDisposeAsync());
        using var caller = new CancellationTokenSource();
        var baselineTimers = clock.TimerCreations;
        var started = NewSignal();
        var release = NewSignal();
        var run = execution.CollectAsync(TimeSpan.FromSeconds(1), async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return "late manifest";
        }, caller.Token).AsTask();
        await started.Task;
        await clock.WaitForTimerCreationsAsync(baselineTimers + 1);
        if (callerCancels) caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(1));
        await clock.WaitForTimerCreationsAsync(baselineTimers + 2);
        Assert.False(run.IsCompleted);
        Assert.False(execution.OwnWorkStopped);
        release.TrySetResult();
        var result = await run;
        Assert.Equal(callerCancels ? EvidenceWorkerStageOutcome.Cancelled : EvidenceWorkerStageOutcome.TimedOut, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal(callerCancels ? EvidenceWorkerTerminalCode.CallerCancelled : EvidenceWorkerTerminalCode.DeadlineExceeded, execution.TerminalCode);
        Assert.True(execution.OwnWorkStopped);
        Assert.False(execution.CollectionCompleted);
    }

    [Fact]
    public async Task CollectAsync_UnsettledCollectorFailsStopAtGraceBoundary()
    {
        var clock = new ManualTimeProvider();
        var execution = Create(new TestSupervisor(), clock);
        Assert.True(await execution.StopAndDisposeAsync());
        var baselineTimers = clock.TimerCreations;
        var started = NewSignal();
        var release = NewSignal();
        var settled = NewSignal();
        var run = execution.CollectAsync(TimeSpan.FromSeconds(1), async _ =>
        {
            started.TrySetResult();
            await release.Task;
            settled.TrySetResult();
            return "untrusted late manifest";
        }).AsTask();
        await started.Task;
        await clock.WaitForTimerCreationsAsync(baselineTimers + 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await clock.WaitForTimerCreationsAsync(baselineTimers + 2);
        clock.Advance(Grace);
        try
        {
            var fatal = await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() => run);
            Assert.Contains("collection did not settle", fatal.Message, StringComparison.Ordinal);
            Assert.False(execution.CollectionCompleted);
        }
        finally
        {
            release.TrySetResult();
            await settled.Task;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeadlineExpiresBetweenPreliminaryAndLockedAcceptance()
    {
        var innerClock = new ManualTimeProvider();
        var clock = new TimestampGateTimeProvider(innerClock);
        var supervisor = new TestSupervisor();
        var execution = new EvidenceWorkerExecution(supervisor, clock, Job, Cleanup, Grace,
            message => new EvidenceWorkerTestInterruptionException(message));
        var started = NewSignal();
        var releaseCallback = NewSignal();
        TimestampReadGate? gate = null;
        var run = Task.Run(async () => await execution.ExecuteAsync(EvidenceRunStage.Producer,
            TimeSpan.FromSeconds(10), async _ =>
            {
                started.TrySetResult();
                await releaseCallback.Task;
                return "completed callback value";
            }));
        try
        {
            await started.Task.WaitAsync(BarrierTimeout);
            await innerClock.WaitForTimerCreationsAsync(1).WaitAsync(BarrierTimeout);
            // Capture the preliminary stage and job samples before moving time. The next
            // sample belongs to acceptance under the execution lock and must reject the value.
            gate = clock.GateAfterTimestampReads(1);
            releaseCallback.TrySetResult();
            await gate.Reached.WaitAsync(BarrierTimeout);
            innerClock.Advance(TimeSpan.FromSeconds(10), fireTimers: false);
            gate.Release();

            var result = await run.WaitAsync(BarrierTimeout);
            Assert.Equal(EvidenceWorkerStageOutcome.TimedOut, result.Outcome);
            Assert.Null(result.Value);
            Assert.Equal(EvidenceWorkerTerminalCode.DeadlineExceeded, execution.TerminalCode);
            Assert.IsType<TimeoutException>(execution.TerminalException);
            Assert.True(execution.OwnWorkStopped);
            Assert.True(supervisor.ExitAcknowledged);
            Assert.Equal(1, supervisor.StopRequests);
            Assert.Null(execution.TrackOwnedWork(_ => ValueTask.CompletedTask));
        }
        finally
        {
            gate?.Release();
            releaseCallback.TrySetResult();
            await run.WaitAsync(BarrierTimeout);
        }
    }

    [Fact]
    public async Task CollectAsync_DeadlineExpiresBetweenPreliminaryAndLockedAcceptance()
    {
        var innerClock = new ManualTimeProvider();
        var clock = new TimestampGateTimeProvider(innerClock);
        var execution = new EvidenceWorkerExecution(new TestSupervisor(), clock, Job, Cleanup, Grace,
            message => new EvidenceWorkerTestInterruptionException(message));
        Assert.True(await execution.StopAndDisposeAsync());
        var baselineTimers = innerClock.TimerCreations;
        var started = NewSignal();
        var releaseCollector = NewSignal();
        TimestampReadGate? gate = null;
        var run = Task.Run(async () => await execution.CollectAsync(TimeSpan.FromSeconds(1), async _ =>
        {
            started.TrySetResult();
            await releaseCollector.Task;
            return "completed collector value";
        }));
        try
        {
            await started.Task.WaitAsync(BarrierTimeout);
            await innerClock.WaitForTimerCreationsAsync(baselineTimers + 1).WaitAsync(BarrierTimeout);
            gate = clock.GateAfterTimestampReads(1);
            releaseCollector.TrySetResult();
            await gate.Reached.WaitAsync(BarrierTimeout);
            innerClock.Advance(TimeSpan.FromSeconds(1), fireTimers: false);
            gate.Release();

            var result = await run.WaitAsync(BarrierTimeout);
            Assert.Equal(EvidenceWorkerStageOutcome.TimedOut, result.Outcome);
            Assert.Null(result.Value);
            Assert.Equal(EvidenceWorkerTerminalCode.DeadlineExceeded, execution.TerminalCode);
            Assert.True(execution.CleanupCompleted);
            Assert.True(execution.OwnWorkStopped);
            Assert.False(execution.CollectionCompleted);
        }
        finally
        {
            gate?.Release();
            releaseCollector.TrySetResult();
            await run.WaitAsync(BarrierTimeout);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TestInterruptionEscapesWithoutBecomingAStageFailure()
    {
        var supervisor = new TestSupervisor();
        var execution = Create(supervisor, new ManualTimeProvider());
        var interruption = new EvidenceWorkerTestInterruptionException("deliberate callback interruption");

        var observed = await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(() =>
            execution.ExecuteAsync<string>(EvidenceRunStage.Producer, TimeSpan.FromSeconds(1),
                _ => ValueTask.FromException<string>(interruption)).AsTask());

        Assert.Same(interruption, observed);
        Assert.Equal(EvidenceWorkerTerminalCode.None, execution.TerminalCode);
        Assert.Equal(0, supervisor.StopRequests);
        Assert.False(execution.CleanupCompleted);
        Assert.False(execution.CollectionCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_FailureRevokesOrdinaryObservationAdmissionAndKeepsFirstCause()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        using var admissionRun = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan,
            consumerAcceptanceMatches: false);
        var supervisor = new TestSupervisor { RunIdentifier = admissionRun.Context.RunId };
        var admission = await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan,
            admissionRun.Context, supervisor, verifier: null, CancellationToken.None);
        // This is the existing internal Observation lifecycle seam. No verifier assertion,
        // protected worker, native output handle or consumer acceptance is fabricated.
        Assert.Equal(EvidenceExecutionMode.Observation, admission.Mode);
        Assert.Null(admission.Assertion);
        admission.Activate("ordinary-observation-test-output");
        admission.ValidateActive(plan);
        var execution = new EvidenceWorkerExecution(supervisor, new ManualTimeProvider(), Job, Cleanup, Grace,
            message => new EvidenceWorkerTestInterruptionException(message), admission);
        var fault = new InvalidOperationException("ordinary observation callback failure");

        var result = await execution.ExecuteAsync<string>(EvidenceRunStage.Producer, TimeSpan.FromSeconds(1),
            _ => ValueTask.FromException<string>(fault));
        Assert.Equal(EvidenceWorkerStageOutcome.Failed, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() => admission.ValidateActive(plan)).Code);
        Assert.False(await execution.StopAndDisposeAsync());
        await execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.CleanupFailed);
        Assert.Equal(EvidenceWorkerTerminalCode.StageFailed, execution.TerminalCode);
        Assert.Same(fault, execution.TerminalException);
        Assert.True(execution.CleanupCompleted);
        Assert.True(execution.OwnWorkStopped);
        Assert.Null(admission.Assertion);
        Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() => admission.ValidateActive(plan)).Code);
    }

    [Fact]
    public async Task ExecuteAsync_OuterCancellationAfterCallbackCompletionRejectsLateSuccess()
    {
        var innerClock = new ManualTimeProvider();
        var clock = new TimestampGateTimeProvider(innerClock);
        var supervisor = new TestSupervisor();
        var execution = new EvidenceWorkerExecution(supervisor, clock, Job, Cleanup, Grace,
            message => new EvidenceWorkerTestInterruptionException(message));
        using var outer = new CancellationTokenSource();
        var started = NewSignal();
        var releaseCallback = NewSignal();
        TimestampReadGate? gate = null;
        Task? stop = null;
        var run = Task.Run(async () => await execution.ExecuteAsync(EvidenceRunStage.Producer,
            TimeSpan.FromSeconds(10), async _ =>
            {
                started.TrySetResult();
                await releaseCallback.Task;
                return "completed callback value";
            }));
        try
        {
            await started.Task.WaitAsync(BarrierTimeout);
            await innerClock.WaitForTimerCreationsAsync(1).WaitAsync(BarrierTimeout);
            // Pause the preliminary job sample outside the execution lock, after the
            // callback has joined. Outer cancellation must win before value acceptance.
            gate = clock.GateAfterTimestampReads(1);
            releaseCallback.TrySetResult();
            await gate.Reached.WaitAsync(BarrierTimeout);
            outer.Cancel();
            stop = execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.StageFailed, outer.Token).AsTask();
            await stop.WaitAsync(BarrierTimeout);
            var originalCause = Assert.IsType<OperationCanceledException>(execution.TerminalException);
            Assert.Equal(outer.Token, originalCause.CancellationToken);
            Assert.True(execution.OwnWorkStopped);
            Assert.True(supervisor.ExitAcknowledged);
            Assert.Equal(1, supervisor.StopRequests);
            gate.Release();

            var result = await run.WaitAsync(BarrierTimeout);
            Assert.Equal(EvidenceWorkerStageOutcome.Cancelled, result.Outcome);
            Assert.Null(result.Value);
            Assert.Equal(EvidenceWorkerTerminalCode.CallerCancelled, execution.TerminalCode);
            Assert.Same(originalCause, execution.TerminalException);
            Assert.True(execution.OwnWorkStopped);
            Assert.Equal(1, supervisor.StopRequests);
        }
        finally
        {
            gate?.Release();
            releaseCallback.TrySetResult();
            if (stop is not null) await Task.WhenAll(stop, run).WaitAsync(BarrierTimeout);
            else await run.WaitAsync(BarrierTimeout);
        }
    }

    [Fact]
    public async Task CollectAsync_CancellationJoinsLateFaultWithoutReplacingFirstCause()
    {
        var clock = new ManualTimeProvider();
        var execution = Create(new TestSupervisor(), clock);
        Assert.True(await execution.StopAndDisposeAsync());
        using var caller = new CancellationTokenSource();
        var baselineTimers = clock.TimerCreations;
        var started = NewSignal();
        var cancellationObserved = NewSignal();
        var releaseCollector = NewSignal();
        var lateFault = new InvalidOperationException("safe fixture late collector failure");
        var run = execution.CollectAsync<string>(TimeSpan.FromSeconds(1), async token =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            started.TrySetResult();
            await releaseCollector.Task;
            throw lateFault;
        }, caller.Token).AsTask();
        try
        {
            await started.Task.WaitAsync(BarrierTimeout);
            await clock.WaitForTimerCreationsAsync(baselineTimers + 1).WaitAsync(BarrierTimeout);
            caller.Cancel();
            await cancellationObserved.Task.WaitAsync(BarrierTimeout);
            // Grace timer creation confirms cancellation has latched and collection is
            // joining the still-owned collector rather than accepting its completion.
            await clock.WaitForTimerCreationsAsync(baselineTimers + 2).WaitAsync(BarrierTimeout);
            var originalCause = Assert.IsType<OperationCanceledException>(execution.TerminalException);
            Assert.Equal(caller.Token, originalCause.CancellationToken);
            Assert.False(run.IsCompleted);
            Assert.False(execution.OwnWorkStopped);
            releaseCollector.TrySetResult();

            var result = await run.WaitAsync(BarrierTimeout);
            Assert.Equal(EvidenceWorkerStageOutcome.Cancelled, result.Outcome);
            Assert.Null(result.Value);
            Assert.Equal(EvidenceWorkerTerminalCode.CallerCancelled, execution.TerminalCode);
            Assert.Same(originalCause, execution.TerminalException);
            Assert.True(execution.OwnWorkStopped);
            Assert.False(execution.CollectionCompleted);
        }
        finally
        {
            releaseCollector.TrySetResult();
            await run.WaitAsync(BarrierTimeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndDisposeAsync_BlockedAdmissionClosureCannotSpendStoppingGrace(bool exhaustGrace)
    {
        var clock = new ManualTimeProvider();
        var closeGate = new TimestampReadGate();
        var closeSettled = NewSignal();
        var closeCalls = 0;
        var exitCalls = 0;
        var disposerCalls = 0;
        var supervisor = new TestSupervisor
        {
            OnClose = () =>
            {
                Interlocked.Increment(ref closeCalls);
                try { closeGate.Pause(); }
                finally { closeSettled.TrySetResult(); }
            },
            OnExit = () => Interlocked.Increment(ref exitCalls),
        };
        var execution = Create(supervisor, clock);
        Assert.True(execution.RegisterDisposer(_ =>
        {
            Interlocked.Increment(ref disposerCalls);
            return ValueTask.CompletedTask;
        }));
        Task<bool>? cleanup = null;
        try
        {
            cleanup = execution.StopAndDisposeAsync().AsTask();
            await closeGate.Reached.WaitAsync(BarrierTimeout);
            // Capture both the stopping cancellation timer and the closure wait timer
            // before advancing the actual allowance supplied to this lifecycle.
            await clock.WaitForTimerCreationsAsync(2).WaitAsync(BarrierTimeout);
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(0, supervisor.StopRequests);
            Assert.Equal(0, Volatile.Read(ref exitCalls));
            Assert.Equal(0, Volatile.Read(ref disposerCalls));

            if (exhaustGrace)
            {
                clock.Advance(Grace);
                var fatal = await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(
                    () => cleanup.WaitAsync(BarrierTimeout));
                Assert.Contains("admission did not close within its bounded stopping grace", fatal.Message, StringComparison.Ordinal);
                Assert.Equal(0, supervisor.StopRequests);
                Assert.Equal(0, Volatile.Read(ref exitCalls));
                Assert.Equal(0, Volatile.Read(ref disposerCalls));
                Assert.False(execution.OwnWorkStopped);
                Assert.False(execution.CleanupCompleted);
            }
            else
            {
                closeGate.Release();
                Assert.True(await cleanup.WaitAsync(BarrierTimeout));
                Assert.Equal(1, supervisor.StopRequests);
                Assert.Equal(1, Volatile.Read(ref exitCalls));
                Assert.Equal(1, Volatile.Read(ref disposerCalls));
                Assert.True(supervisor.ExitAcknowledged);
                Assert.True(execution.OwnWorkStopped);
                Assert.True(execution.CleanupCompleted);
            }
            Assert.Equal(1, Volatile.Read(ref closeCalls));
        }
        finally
        {
            closeGate.Release();
            if (cleanup is not null)
            {
                await closeSettled.Task.WaitAsync(BarrierTimeout);
                try { await cleanup.WaitAsync(BarrierTimeout); }
                catch (EvidenceWorkerTestInterruptionException) { }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndDisposeAsync_FaultedOwnedExitAcknowledgementCannotPermitDisposal(bool faultAcknowledgement)
    {
        var closeCalls = 0;
        var exitCalls = 0;
        var disposerCalls = 0;
        var exitEntered = NewSignal();
        var exitAcknowledgement = NewSignal();
        var supervisor = new TestSupervisor
        {
            OnClose = () => Interlocked.Increment(ref closeCalls),
            OnWaitForExit = _ =>
            {
                Interlocked.Increment(ref exitCalls);
                exitEntered.TrySetResult();
                return new ValueTask(exitAcknowledgement.Task);
            },
        };
        var execution = Create(supervisor, new ManualTimeProvider());
        Assert.True(execution.RegisterDisposer(_ =>
        {
            Interlocked.Increment(ref disposerCalls);
            return ValueTask.CompletedTask;
        }));
        Task<bool>? cleanup = null;
        try
        {
            cleanup = execution.StopAndDisposeAsync().AsTask();
            await exitEntered.Task.WaitAsync(BarrierTimeout);
            Assert.Equal(1, Volatile.Read(ref closeCalls));
            Assert.Equal(1, supervisor.StopRequests);
            Assert.False(cleanup.IsCompleted);
            Assert.False(execution.OwnWorkStopped);
            Assert.Equal(0, Volatile.Read(ref disposerCalls));

            if (faultAcknowledgement)
            {
                exitAcknowledgement.TrySetException(new InvalidOperationException("private-exit-ack-canary"));
                var fatal = await Assert.ThrowsAsync<EvidenceWorkerTestInterruptionException>(
                    () => cleanup.WaitAsync(BarrierTimeout));
                Assert.Contains("failed to acknowledge owned-work exit", fatal.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("private-exit-ack-canary", fatal.Message, StringComparison.Ordinal);
                Assert.Equal(0, Volatile.Read(ref disposerCalls));
                Assert.False(execution.OwnWorkStopped);
                Assert.False(execution.CleanupCompleted);
            }
            else
            {
                exitAcknowledgement.TrySetResult();
                Assert.True(await cleanup.WaitAsync(BarrierTimeout));
                Assert.Equal(1, Volatile.Read(ref disposerCalls));
                Assert.True(execution.OwnWorkStopped);
                Assert.True(execution.CleanupCompleted);
            }
            Assert.Equal(1, Volatile.Read(ref exitCalls));
        }
        finally
        {
            exitAcknowledgement.TrySetResult();
            if (cleanup is not null)
            {
                try { await cleanup.WaitAsync(BarrierTimeout); }
                catch (EvidenceWorkerTestInterruptionException) { }
            }
        }
    }

    /// <summary>Gates one captured clock sample while the underlying monotonic clock advances.</summary>
    /// <remarks>Timer notifications remain under the existing ManualTimeProvider's independent control.</remarks>
    private sealed class TimestampGateTimeProvider(ManualTimeProvider inner) : TimeProvider
    {
        private readonly object _gate = new();
        private TimestampReadGate? _pending;
        private int _remainingReads;

        public override long TimestampFrequency => inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);

        internal TimestampReadGate GateAfterTimestampReads(int precedingReads)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(precedingReads);
            lock (_gate)
            {
                if (_pending is not null) throw new InvalidOperationException("A timestamp gate is already pending.");
                _remainingReads = checked(precedingReads + 1);
                return _pending = new TimestampReadGate();
            }
        }

        public override long GetTimestamp()
        {
            var captured = inner.GetTimestamp();
            TimestampReadGate? pending = null;
            lock (_gate)
            {
                if (_pending is not null && --_remainingReads == 0)
                {
                    pending = _pending;
                    _pending = null;
                }
            }
            pending?.Pause();
            return captured;
        }
    }

    /// <summary>Owns a finite test barrier; callers release it and join their lifecycle task in finally.</summary>
    private sealed class TimestampReadGate
    {
        private readonly TaskCompletionSource _reached = NewSignal();
        private readonly TaskCompletionSource _released = NewSignal();

        internal Task Reached => _reached.Task;
        internal void Release() => _released.TrySetResult();

        internal void Pause()
        {
            _reached.TrySetResult();
            _released.Task.WaitAsync(BarrierTimeout).GetAwaiter().GetResult();
        }
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
        internal Action? OnClose { get; set; }
        internal Action? OnExit { get; init; }
        internal string RunIdentifier { get; set; } = "test-run";
        internal Func<CancellationToken, ValueTask>? OnStopRequest { get; set; }
        internal Func<CancellationToken, ValueTask>? OnWaitForExit { get; set; }
        public bool IsArmed => Armed;
        public string RunId => RunIdentifier;

        public void CloseAdmission() => OnClose?.Invoke();

        public ValueTask RequestStopAsync(CancellationToken stoppingToken)
        {
            StopRequests++;
            SawFreshStoppingToken = !stoppingToken.IsCancellationRequested;
            StopRequested.TrySetResult();
            return OnStopRequest?.Invoke(stoppingToken) ?? ValueTask.CompletedTask;
        }

        public ValueTask WaitForOwnedExitAsync(CancellationToken stoppingToken)
        {
            OnExit?.Invoke();
            if (OnWaitForExit is not null) return OnWaitForExit(stoppingToken);
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
        internal int TimerCreations { get { lock (_gate) return _createdTimers; } }
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
