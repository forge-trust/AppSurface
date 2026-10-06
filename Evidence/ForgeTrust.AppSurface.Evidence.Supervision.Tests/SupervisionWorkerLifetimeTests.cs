using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual task/barrier controls for procedure ownership, without a native worker or admission.</summary>
public sealed class SupervisionWorkerLifetimeTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task StopBeforeStartupForbidsDispatchAndSharesCompletion()
    {
        var owner = new SupervisionWorkerLifetime();
        var calls = new List<string>();
        var stop = owner.StopAsync(() => { calls.Add("contain"); return Task.CompletedTask; },
            () => { calls.Add("finalize"); return Task.CompletedTask; });
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(stop, owner.StopAsync(() => throw new InvalidOperationException(), () => throw new InvalidOperationException()));
        Assert.ThrowsAny<Exception>(() => { _ = owner.StartAsync(_ => throw new InvalidOperationException(), default); });
        Assert.Equal(new[] { "contain", "finalize" }, calls);
        Assert.True(owner.IsClosed);
        Assert.False(owner.StartJoined);
        Assert.True(owner.StopJoined);
        Assert.False(owner.Failed);
    }

    [Fact]
    public async Task StopContainsBeforeJoiningIgnoredStartupAndFinalizesOnlyAfterItsRealCompletion()
    {
        var owner = new SupervisionWorkerLifetime();
        var entered = Signal(); var release = Signal(); var contained = Signal();
        var finalized = false;
        var start = owner.StartAsync(async token =>
        {
            entered.TrySetResult();
            await release.Task; // Deliberately ignore cancellation; ownership must not detach this task.
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = owner.StopAsync(() => { contained.TrySetResult(); return Task.CompletedTask; },
            () => { Assert.True(start.IsCompleted); finalized = true; return Task.CompletedTask; });
        try
        {
            await contained.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(stop.IsCompleted);
            Assert.False(owner.StartJoined);
            Assert.False(finalized);
            Assert.Same(stop, owner.StopAsync(() => throw new InvalidOperationException(), () => throw new InvalidOperationException()));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<Exception>(() => stop.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(finalized);
        Assert.True(owner.StartJoined);
        Assert.True(owner.StopJoined);
        Assert.True(owner.Failed);
    }

    [Fact]
    public async Task FailureInContainmentDoesNotSkipPendingStartupJoinOrFinalization()
    {
        var owner = new SupervisionWorkerLifetime();
        var entered = Signal(); var release = Signal(); var contained = Signal();
        var finalized = false;
        var start = owner.StartAsync(async _ => { entered.TrySetResult(); await release.Task; }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = owner.StopAsync(() => { contained.TrySetResult(); throw new IOException("private-canary"); },
            () => { finalized = true; return Task.CompletedTask; });
        try
        {
            await contained.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(stop.IsCompleted);
            Assert.False(finalized);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<Exception>(() => start.WaitAsync(TimeSpan.FromSeconds(2)));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => stop.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("private-canary", error.ToString());
        Assert.Null(error.InnerException);
        Assert.True(finalized);
        Assert.True(owner.StopJoined);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StartupFaultCancellationOrMissingTaskCannotBecomeSuccessfulAfterCleanup(int kind)
    {
        var owner = new SupervisionWorkerLifetime();
        var start = owner.StartAsync(_ => kind switch
        {
            0 => throw new IOException("private-canary"),
            1 => Task.FromCanceled(new CancellationToken(true)),
            _ => null!,
        }, default);
        await Assert.ThrowsAnyAsync<Exception>(() => start.WaitAsync(TimeSpan.FromSeconds(2)));
        var calls = 0;
        var stop = owner.StopAsync(() => { calls++; return Task.CompletedTask; }, () => { calls++; return Task.CompletedTask; });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => stop.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, calls);
        Assert.True(owner.StartJoined);
        Assert.True(owner.StopJoined);
        Assert.True(owner.Failed);
        Assert.DoesNotContain("private-canary", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task SuccessfulStartupRetainsCancellationUntilSharedFinalization()
    {
        var owner = new SupervisionWorkerLifetime();
        CancellationToken retained = default;
        await owner.StartAsync(token => { retained = token; return Task.CompletedTask; }, default);
        Assert.True(retained.CanBeCanceled);
        Assert.False(retained.IsCancellationRequested);
        var calls = 0;
        await owner.StopAsync(() => { Assert.True(retained.IsCancellationRequested); calls++; return Task.CompletedTask; },
            () => { calls++; return Task.CompletedTask; });
        Assert.Equal(2, calls);
        Assert.True(owner.StopJoined);
        Assert.False(owner.Failed);
    }

    [Fact]
    public async Task OriginalCallerCancellationAfterStartupClosesUseAndCannotPassCleanFinalization()
    {
        var owner = new SupervisionWorkerLifetime();
        using var caller = new CancellationTokenSource();
        await owner.StartAsync(_ => Task.CompletedTask, caller.Token);
        caller.Cancel();
        Assert.True(owner.IsClosed);
        Assert.True(owner.Failed);
        var calls = 0;
        var stop = owner.StopAsync(() => { calls++; return Task.CompletedTask; },
            () => { calls++; return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<Exception>(() => stop.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, calls);
        Assert.True(owner.StartJoined);
        Assert.True(owner.StopJoined);
        Assert.True(owner.Failed);
    }

    [Fact]
    public async Task CancellationCallbackFailureStillAttemptsBothStopPhasesAndLatchesFailure()
    {
        var owner = new SupervisionWorkerLifetime();
        CancellationTokenRegistration registration = default;
        await owner.StartAsync(token =>
        {
            registration = token.Register(() => throw new IOException("callback-canary"));
            return Task.CompletedTask;
        }, default);
        try
        {
            var calls = 0;
            var stop = owner.StopAsync(() => { calls++; return Task.CompletedTask; }, () => { calls++; return Task.CompletedTask; });
            var error = await Assert.ThrowsAnyAsync<Exception>(() => stop.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, calls);
            Assert.True(owner.StopJoined);
            Assert.DoesNotContain("callback-canary", error.ToString());
        }
        finally { registration.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizationFailureOrMissingTaskRemainsTheSameSharedFailure(bool missing)
    {
        var owner = new SupervisionWorkerLifetime();
        await owner.StartAsync(_ => Task.CompletedTask, default);
        var stop = owner.StopAsync(() => Task.CompletedTask,
            () => missing ? null! : Task.FromException(new IOException("final-canary")));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => stop.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(stop, owner.StopAsync(() => Task.CompletedTask, () => Task.CompletedTask));
        Assert.True(owner.StopJoined);
        Assert.True(owner.Failed);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("final-canary", error.ToString());
    }

    [Fact]
    public async Task ConcurrentStartupAttemptsDispatchOnlyOneProcedure()
    {
        var owner = new SupervisionWorkerLifetime();
        var entered = Signal(); var release = Signal();
        var calls = 0;
        Task? retained = null;
        var attempts = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            try
            {
                var task = owner.StartAsync(async _ => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; }, default);
                Interlocked.Exchange(ref retained, task);
                return true;
            }
            catch { return false; }
        })).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(results, static accepted => accepted);
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
        await retained!.WaitAsync(TimeSpan.FromSeconds(2));
        await owner.StopAsync(() => Task.CompletedTask, () => Task.CompletedTask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReentrantStopRejectsBeforeSelfJoinIncludingAfterAwait(bool asynchronous)
    {
        var owner = new SupervisionWorkerLifetime();
        await owner.StartAsync(async token =>
        {
            if (asynchronous) await Task.Yield();
            Assert.ThrowsAny<Exception>(() => { _ = owner.StopAsync(() => Task.CompletedTask, () => Task.CompletedTask); });
        }, default).WaitAsync(TimeSpan.FromSeconds(2));
        await owner.StopAsync(async () =>
        {
            if (asynchronous) await Task.Yield();
            Assert.ThrowsAny<Exception>(() => { _ = owner.StopAsync(() => Task.CompletedTask, () => Task.CompletedTask); });
        }, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(owner.Failed);
    }

    [Fact]
    public async Task PrecancelledStartupNeverDispatchesAndStillRequiresCleanup()
    {
        var owner = new SupervisionWorkerLifetime();
        var calls = 0;
        var start = owner.StartAsync(_ => { calls++; return Task.CompletedTask; }, new CancellationToken(true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, calls);
        await Assert.ThrowsAnyAsync<Exception>(() => owner.StopAsync(() => Task.CompletedTask, () => Task.CompletedTask));
        Assert.True(owner.StartJoined);
        Assert.True(owner.StopJoined);
    }
}
