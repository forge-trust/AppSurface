using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Exercises asynchronous lifetime ownership only; no procedure seam issues native authority.</summary>
public sealed class SupervisionPendingStartTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const string Job = "/org/freedesktop/systemd1/job/42";

    [Fact]
    public async Task ReservationPrecedesStartIoAndSettlementNeedsTheSeparateStopSequence()
    {
        var owner = Create();
        using var startToken = new CancellationTokenSource();
        var startCalls = 0;
        var reply = await owner.StartAsync(token =>
        {
            Assert.Equal(startToken.Token, token);
            Assert.True(owner.Snapshot.StartReserved);
            Assert.False(owner.Snapshot.StartJoined);
            Assert.False(owner.Snapshot.Started);
            Assert.False(owner.Snapshot.IsSettled);
            startCalls++;
            return Task.FromResult(Job);
        }, startToken.Token).WaitAsync(Guard);
        Assert.Equal(Job, reply);
        Assert.Equal(1, startCalls);
        Assert.True(owner.Snapshot.Started);
        Assert.True(owner.Snapshot.StartJoined);
        Assert.False(owner.Snapshot.IsSettled);
        var stops = 0;
        await owner.StopAsync(_ => { stops++; return Task.CompletedTask; }, default).WaitAsync(Guard);
        Assert.Equal(2, stops);
        Assert.True(owner.Snapshot.StartReserved);
        Assert.True(owner.Snapshot.Closed);
        Assert.True(owner.Snapshot.StopJoined);
        Assert.True(owner.Snapshot.IsSettled);
    }

    [Fact]
    public async Task StopBeforeReservationClosesDispatchPermanently()
    {
        var owner = Create();
        var stops = 0;
        await owner.StopAsync(_ => { stops++; return Task.CompletedTask; }, default).WaitAsync(Guard);
        var starts = 0;
        AssertClosed(() => owner.StartAsync(_ => { starts++; return Task.FromResult(Job); }, default));
        Assert.Equal(0, starts);
        Assert.Equal(1, stops);
        Assert.False(owner.Snapshot.StartReserved);
        Assert.True(owner.Snapshot.IsSettled);
        await owner.StopAsync(_ => throw new InvalidOperationException("private-replay-canary"), default);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task LateAcceptedStartIsJoinedBeforeTheSecondStopCanSettle()
    {
        var owner = Create();
        var accept = ReplyBarrier();
        var repeatEntered = Barrier();
        var releaseRepeat = Barrier();
        var start = owner.StartAsync(_ => accept.Task, default);
        var calls = 0;
        var stop = owner.StopAsync(async _ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                Assert.True(owner.Snapshot.StartJoined);
                repeatEntered.TrySetResult();
                await releaseRepeat.Task;
            }
        }, default);
        try
        {
            Assert.Equal(1, calls);
            Assert.True(owner.Snapshot.Closed);
            Assert.False(stop.IsCompleted);
            Assert.False(owner.Snapshot.StopJoined);
            AssertClosed(() => owner.StartAsync(_ => Task.FromResult(Job), default));
            accept.TrySetResult(Job);
            Assert.Equal(Job, await start.WaitAsync(Guard));
            await repeatEntered.Task.WaitAsync(Guard);
            Assert.Equal(2, calls);
            Assert.False(stop.IsCompleted);
            Assert.False(owner.Snapshot.IsSettled);
            releaseRepeat.TrySetResult();
            await stop.WaitAsync(Guard);
            Assert.True(owner.Snapshot.IsSettled);
        }
        finally
        {
            accept.TrySetResult(Job);
            releaseRepeat.TrySetResult();
            await start.WaitAsync(Guard);
            await stop.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CancelledStartStillJoinsIgnoringOperationAndIsRetainedThroughRestop()
    {
        var owner = Create();
        using var cancellation = new CancellationTokenSource();
        var actualStart = ReplyBarrier();
        var start = owner.StartAsync(token =>
        {
            Assert.Equal(cancellation.Token, token);
            return actualStart.Task;
        }, cancellation.Token);
        cancellation.Cancel();
        var calls = 0;
        var stop = owner.StopAsync(_ => { calls++; return Task.CompletedTask; }, default);
        try
        {
            Assert.False(start.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.True(owner.Snapshot.StartReserved);
            Assert.False(owner.Snapshot.StartJoined);
            Assert.Equal(1, calls);
            actualStart.TrySetResult(Job);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Guard));
            var failure = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard));
            AssertSanitized(failure);
            Assert.Equal(2, calls);
            Assert.Equal(SupervisionPendingStartFailure.StartCancelled, owner.Snapshot.FirstFailure);
            Assert.True(owner.Snapshot.StartReserved);
            Assert.True(owner.Snapshot.StartJoined);
            Assert.True(owner.Snapshot.StopJoined);
            Assert.False(owner.Snapshot.IsSettled);
        }
        finally
        {
            actualStart.TrySetResult(Job);
            await IgnoreExpectedFailure(start);
            await IgnoreExpectedFailure(stop);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateFaultOrCancelledStartRemainsAmbiguousAndStillRequiresRestop(bool canceled)
    {
        var owner = Create();
        var actualStart = ReplyBarrier();
        var start = owner.StartAsync(_ => actualStart.Task, default);
        var calls = 0;
        var stop = owner.StopAsync(_ => { calls++; return Task.CompletedTask; }, default);
        try
        {
            Assert.Equal(1, calls);
            Assert.False(stop.IsCompleted);
            if (canceled) actualStart.TrySetCanceled();
            else actualStart.TrySetException(new IOException("private-procedure-canary"));
            if (canceled)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Guard));
            else
                AssertSanitized(await Assert.ThrowsAsync<EvidenceAdmissionException>(() => start.WaitAsync(Guard)));
            AssertSanitized(await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard)));
            Assert.Equal(2, calls);
            Assert.Equal(canceled ? SupervisionPendingStartFailure.StartCancelled : SupervisionPendingStartFailure.StartFailed,
                owner.Snapshot.FirstFailure);
            Assert.True(owner.Snapshot.Closed);
            Assert.True(owner.Snapshot.StartReserved);
            Assert.True(owner.Snapshot.StopJoined);
            Assert.False(owner.Snapshot.IsSettled);
        }
        finally
        {
            actualStart.TrySetException(new IOException("private-procedure-canary"));
            await IgnoreExpectedFailure(start);
            await IgnoreExpectedFailure(stop);
        }
    }

    [Fact]
    public async Task FirstStopFailureCannotBeReplacedByLateStartFailureAndDoesNotSkipRestop()
    {
        var owner = Create();
        var actualStart = ReplyBarrier();
        var start = owner.StartAsync(_ => actualStart.Task, default);
        var calls = 0;
        var stop = owner.StopAsync(_ =>
        {
            calls++;
            return calls == 1 ? Task.FromException(new IOException("private-procedure-canary")) : Task.CompletedTask;
        }, default);
        try
        {
            Assert.Equal(SupervisionPendingStartFailure.StopFailed, owner.Snapshot.FirstFailure);
            Assert.False(stop.IsCompleted);
            actualStart.TrySetException(new IOException("private-procedure-canary"));
            await IgnoreExpectedFailure(start);
            AssertSanitized(await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard)));
            Assert.Equal(2, calls);
            Assert.Equal(SupervisionPendingStartFailure.StopFailed, owner.Snapshot.FirstFailure);
            Assert.True(owner.Snapshot.StopJoined);
            Assert.False(owner.Snapshot.IsSettled);
        }
        finally
        {
            actualStart.TrySetException(new IOException("private-procedure-canary"));
            await IgnoreExpectedFailure(start);
            await IgnoreExpectedFailure(stop);
        }
    }

    [Fact]
    public async Task ConcurrentStopCallersShareTheFirstProcedureTokenAndTask()
    {
        var owner = Create();
        using var firstToken = new CancellationTokenSource();
        using var laterToken = new CancellationTokenSource();
        laterToken.Cancel();
        var actualStop = Barrier();
        var entered = Barrier();
        var calls = 0;
        var stop = owner.StopAsync(token =>
        {
            Assert.Equal(firstToken.Token, token);
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            return actualStop.Task;
        }, firstToken.Token);
        var otherCalls = 0;
        var callers = Enumerable.Range(0, 8).Select(callerIndex => Task.Run(() =>
        {
            var shared = owner.StopAsync(_ => { Interlocked.Increment(ref otherCalls); return Task.CompletedTask; }, laterToken.Token);
            Assert.Same(stop, shared);
        })).ToArray();
        try
        {
            await entered.Task.WaitAsync(Guard);
            await Task.WhenAll(callers).WaitAsync(Guard);
            Assert.False(stop.IsCompleted);
            Assert.Equal(1, calls);
            Assert.Equal(0, otherCalls);
            actualStop.TrySetResult();
            await stop.WaitAsync(Guard);
            Assert.True(owner.Snapshot.IsSettled);
            Assert.Same(stop, owner.StopAsync(_ => Task.CompletedTask, default));
        }
        finally
        {
            actualStop.TrySetResult();
            await Task.WhenAll(callers).WaitAsync(Guard);
            await stop.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CancelledStopCannotDetachAnActualIgnoringStopAndFailureIsSticky()
    {
        var owner = Create();
        using var cancellation = new CancellationTokenSource();
        var actualStop = Barrier();
        var stop = owner.StopAsync(_ => actualStop.Task, cancellation.Token);
        try
        {
            cancellation.Cancel();
            Assert.False(stop.IsCompleted);
            Assert.True(owner.Snapshot.Closed);
            Assert.False(owner.Snapshot.StopJoined);
            actualStop.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(Guard));
            Assert.Equal(SupervisionPendingStartFailure.StopCancelled, owner.Snapshot.FirstFailure);
            Assert.True(owner.Snapshot.StopJoined);
            Assert.False(owner.Snapshot.IsSettled);
            Assert.Same(stop, owner.StopAsync(_ => Task.CompletedTask, default));
            AssertClosed(() => owner.StartAsync(_ => Task.FromResult(Job), default));
        }
        finally
        {
            actualStop.TrySetResult();
            await IgnoreExpectedFailure(stop);
        }
    }

    [Fact]
    public async Task ExpiredCleanupStillJoinsPendingStartWithoutRenewingDispatchAllowance()
    {
        var owner = Create();
        var actualStart = ReplyBarrier();
        var start = owner.StartAsync(_ => actualStart.Task, default);
        using var expired = new CancellationTokenSource();
        expired.Cancel();
        var calls = 0;
        var stop = owner.StopAsync(_ => { calls++; return Task.CompletedTask; }, expired.Token);
        try
        {
            Assert.True(owner.Snapshot.Closed);
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, calls);
            actualStart.TrySetResult(Job);
            Assert.Equal(Job, await start.WaitAsync(Guard));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(Guard));
            Assert.Equal(0, calls);
            Assert.True(owner.Snapshot.StartReserved);
            Assert.True(owner.Snapshot.StopJoined);
            Assert.False(owner.Snapshot.IsSettled);
        }
        finally
        {
            actualStart.TrySetResult(Job);
            await start.WaitAsync(Guard);
            await IgnoreExpectedFailure(stop);
        }
    }

    [Fact]
    public async Task ReplayedStartDoesNotDispatchOrReleaseTheOriginalReservation()
    {
        var owner = Create();
        var actualStart = ReplyBarrier();
        var start = owner.StartAsync(_ => actualStart.Task, default);
        var replays = 0;
        try
        {
            AssertClosed(() => owner.StartAsync(_ => { replays++; return Task.FromResult(Job); }, default));
            Assert.Equal(0, replays);
            Assert.True(owner.Snapshot.StartReserved);
            Assert.False(owner.Snapshot.StartJoined);
            actualStart.TrySetResult(Job);
            await start.WaitAsync(Guard);
            await owner.StopAsync(_ => Task.CompletedTask, default).WaitAsync(Guard);
            Assert.True(owner.Snapshot.IsSettled);
        }
        finally
        {
            actualStart.TrySetResult(Job);
            await start.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task MissingArgumentsRejectWithoutChangingAdmissionOrDispatching()
    {
        Assert.Throws<ArgumentNullException>(() => new SupervisionPendingStart(null!));
        var owner = Create();
        Assert.Throws<ArgumentNullException>(() => { _ = owner.StartAsync(null!, default); });
        Assert.Throws<ArgumentNullException>(() => { _ = owner.StopAsync(null!, default); });
        Assert.False(owner.Snapshot.StartReserved);
        Assert.False(owner.Snapshot.Closed);
        Assert.False(owner.Snapshot.IsFailed);
        await owner.StartAsync(_ => Task.FromResult(Job), default).WaitAsync(Guard);
        await owner.StopAsync(_ => Task.CompletedTask, default).WaitAsync(Guard);
        Assert.True(owner.Snapshot.IsSettled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task InvalidOrSynchronouslyFaultedStartRetainsFailureAndReservation(int kind)
    {
        var owner = Create();
        var start = owner.StartAsync(_ => kind switch
        {
            0 => null!,
            1 => Task.FromResult<string>(null!),
            2 => Task.FromResult(""),
            3 => Task.FromResult(new string('x', 4097)),
            4 => throw new IOException("private-procedure-canary"),
            _ => Task.FromResult(Job + "\n"),
        }, default);
        AssertSanitized(await Assert.ThrowsAsync<EvidenceAdmissionException>(() => start.WaitAsync(Guard)));
        var calls = 0;
        AssertSanitized(await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            owner.StopAsync(_ => { calls++; return Task.CompletedTask; }, default).WaitAsync(Guard)));
        Assert.Equal(2, calls);
        Assert.True(owner.Snapshot.StartReserved);
        Assert.True(owner.Snapshot.StartJoined);
        Assert.True(owner.Snapshot.StopJoined);
        Assert.Equal(SupervisionPendingStartFailure.StartFailed, owner.Snapshot.FirstFailure);
        Assert.False(owner.Snapshot.IsSettled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NullOrFaultedStopCannotPublishSettlement(int kind)
    {
        var owner = Create();
        var stop = owner.StopAsync(_ => kind switch
        {
            0 => null!,
            1 => Task.FromException(new IOException("private-procedure-canary")),
            _ => throw new IOException("private-procedure-canary"),
        }, default);
        AssertSanitized(await Assert.ThrowsAsync<EvidenceAdmissionException>(() => stop.WaitAsync(Guard)));
        Assert.True(owner.Snapshot.Closed);
        Assert.True(owner.Snapshot.StopJoined);
        Assert.Equal(SupervisionPendingStartFailure.StopFailed, owner.Snapshot.FirstFailure);
        Assert.False(owner.Snapshot.IsSettled);
    }

    [Fact]
    public async Task PreCancelledStartOwnsAnUndispatchedReservationUntilCleanup()
    {
        var owner = Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var starts = 0;
        var start = owner.StartAsync(_ => { starts++; return Task.FromResult(Job); }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Guard));
        Assert.Equal(0, starts);
        Assert.True(owner.Snapshot.StartReserved);
        Assert.True(owner.Snapshot.Closed);
        Assert.False(owner.Snapshot.StopJoined);
        AssertSanitized(await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            owner.StopAsync(_ => Task.CompletedTask, default).WaitAsync(Guard)));
        Assert.False(owner.Snapshot.IsSettled);
    }

    [Fact]
    public async Task ProceduresCannotAwaitTheirOwnStartOrStopCoordinator()
    {
        var owner = Create();
        await owner.StartAsync(startToken =>
        {
            AssertClosed(() => owner.StopAsync(_ => Task.CompletedTask, default));
            AssertClosed(() => owner.StartAsync(_ => Task.FromResult(Job), default));
            return Task.FromResult(Job);
        }, default).WaitAsync(Guard);
        var calls = 0;
        await owner.StopAsync(async stopToken =>
        {
            await Task.Yield();
            calls++;
            AssertClosed(() => owner.StopAsync(_ => Task.CompletedTask, default));
            AssertClosed(() => owner.StartAsync(_ => Task.FromResult(Job), default));
        }, default).WaitAsync(Guard);
        Assert.Equal(2, calls);
        Assert.True(owner.Snapshot.IsSettled);
    }

    private static SupervisionPendingStart Create() => new(LinuxUnitName.Create(LinuxUnitRole.Worker, Guid.NewGuid()));
    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<string> ReplyBarrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertClosed(Action action) => AssertSanitized(Assert.Throws<EvidenceAdmissionException>(action));

    private static void AssertSanitized(EvidenceAdmissionException error)
    {
        Assert.Equal("ASEVD410", error.Code);
        Assert.StartsWith("ASEVD410: The protected pending-start procedure was rejected.", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-procedure-canary", error.ToString());
    }

    private static async Task IgnoreExpectedFailure(Task task)
    {
        try { await task.WaitAsync(Guard); }
        catch (EvidenceAdmissionException) { }
        catch (OperationCanceledException) { }
    }
}
