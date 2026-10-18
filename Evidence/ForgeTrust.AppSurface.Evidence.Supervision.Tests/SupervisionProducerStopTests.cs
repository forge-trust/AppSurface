namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Procedure-only stop ordering controls; detached callbacks never establish native settlement.</summary>
public sealed class SupervisionProducerStopTests
{
    /// <summary>Matching stopped observations complete without forcing an otherwise cooperative producer.</summary>
    [Fact]
    public async Task CooperativeStopJoinsRequestAndObservationWithoutKill()
    {
        var order = new List<string>();
        using var cleanup = new CancellationTokenSource();
        await SupervisionProducerStop.RunAsync(TimeSpan.FromSeconds(1),
            token => { Assert.NotEqual(cleanup.Token, token); order.Add("stop"); return Task.CompletedTask; },
            _ => { order.Add("observe"); return Task.CompletedTask; },
            _ => { order.Add("kill"); return Task.CompletedTask; }, cleanup.Token);
        Assert.Equal(new[] { "stop", "observe" }, order);
        Assert.False(cleanup.IsCancellationRequested);
    }

    /// <summary>An actual expired cooperative slice forces under the original token and joins the force task.</summary>
    [Fact]
    public async Task UnconfirmedGraceForcesAndJoinsOriginalContainment()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanup = new CancellationTokenSource();
        var run = SupervisionProducerStop.RunAsync(TimeSpan.FromMilliseconds(10),
            _ => Task.CompletedTask,
            async token => { observed.TrySetResult(); await Task.Delay(Timeout.Infinite, token); },
            async token => { Assert.Equal(cleanup.Token, token); forcing.SetResult(); await release.Task; }, cleanup.Token);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await forcing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.False(cleanup.IsCancellationRequested);
    }

    /// <summary>Stop or observation errors force the selected containment rather than returning an exit fact.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadOrRequestFailureStillRequiresForce(bool requestFails)
    {
        var forced = 0;
        await SupervisionProducerStop.RunAsync(TimeSpan.FromSeconds(1),
            _ => requestFails ? Task.FromException(new IOException("request-canary")) : Task.CompletedTask,
            _ => Task.FromException(new IOException("observation-canary")),
            _ => { forced++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, forced);
    }

    /// <summary>Cleanup cancellation cannot be mislabeled as a confirmed stop or dispatch further operations.</summary>
    [Fact]
    public async Task CallerCancellationRejectsBeforeDispatch()
    {
        using var cleanup = new CancellationTokenSource(); cleanup.Cancel();
        var calls = 0;
        Task Operation(CancellationToken _) { calls++; return Task.CompletedTask; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SupervisionProducerStop.RunAsync(
            TimeSpan.FromSeconds(1), Operation, Operation, Operation, cleanup.Token));
        Assert.Equal(0, calls);
    }

    /// <summary>Cancellation while the original request ignores it never detaches that operation.</summary>
    [Fact]
    public async Task IgnoredCancellationRequestRemainsOwnedUntilReleased()
    {
        using var cleanup = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = 0;
        var run = SupervisionProducerStop.RunAsync(TimeSpan.FromSeconds(1),
            async _ => { entered.SetResult(); await release.Task; },
            _ => { later++; return Task.CompletedTask; }, _ => { later++; return Task.CompletedTask; }, cleanup.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cleanup.Cancel();
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, later);
    }

    /// <summary>A failed force is preserved and late cancellation after a force still forbids completion.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForceFailureOrLateCancellationCannotPass(bool cancel)
    {
        using var cleanup = new CancellationTokenSource();
        var failure = new IOException("force-canary");
        var run = SupervisionProducerStop.RunAsync(TimeSpan.FromSeconds(1),
            _ => Task.FromException(new IOException()), _ => Task.CompletedTask,
            _ => { if (cancel) { cleanup.Cancel(); return Task.CompletedTask; } return Task.FromException(failure); }, cleanup.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => run));
    }

    /// <summary>Missing callbacks and invalid slices reject before invoking a procedure.</summary>
    [Theory]
    [InlineData("request")]
    [InlineData("observe")]
    [InlineData("force")]
    [InlineData("zero")]
    [InlineData("large")]
    public async Task InvalidArgumentsCannotDispatch(string invalid)
    {
        var calls = 0;
        Task Operation(CancellationToken _) { calls++; return Task.CompletedTask; }
        var run = SupervisionProducerStop.RunAsync(invalid == "zero" ? TimeSpan.Zero
                : invalid == "large" ? TimeSpan.FromSeconds(16) : TimeSpan.FromSeconds(1),
            invalid == "request" ? null! : Operation, invalid == "observe" ? null! : Operation,
            invalid == "force" ? null! : Operation, CancellationToken.None);
        if (invalid is "zero" or "large") await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run);
        else await Assert.ThrowsAsync<ArgumentNullException>(() => run);
        Assert.Equal(0, calls);
    }
}
