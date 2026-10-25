using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Tests the application cleanup callback ordering only; these are not native-owner qualification tests.</summary>
public sealed class SupervisionApplicationCleanupTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("lifetime")]
    [InlineData("body")]
    public async Task CaughtFailuresKeepTheRemainingOriginalOrder(string failedOperation)
    {
        var events = new List<string>();
        var passedFailureToBody = false;
        var failed = await SupervisionApplicationCleanup.RunAsync(Actions(
            events,
            cancel: () => ThrowIf("cancel"),
            stop: () => { events.Add("lifetime"); ThrowIf("lifetime"); return Task.CompletedTask; },
            body: priorFailure =>
            {
                events.Add("body");
                passedFailureToBody = priorFailure;
                ThrowIf("body");
                return Task.CompletedTask;
            }));

        Assert.True(failed);
        Assert.Equal(new[] { "begin", "cancel", "lifetime", "body", "complete" }, events);
        Assert.Equal(failedOperation != "body", passedFailureToBody);

        void ThrowIf(string operation)
        {
            if (failedOperation == operation) throw new InvalidOperationException(operation);
        }
    }

    [Fact]
    public async Task LifetimeJoinBarrierPrecedesMonitorBodyAndCompletion()
    {
        var events = new List<string>();
        var joinStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = SupervisionApplicationCleanup.RunAsync(Actions(events, stop: async () =>
        {
            events.Add("join-start");
            joinStarted.SetResult();
            await releaseJoin.Task;
            events.Add("join-end");
        }));

        var failed = false;
        try
        {
            await joinStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(new[] { "begin", "cancel", "join-start" }, events);
        }
        finally
        {
            releaseJoin.TrySetResult();
            failed = await cleanup;
        }

        Assert.False(failed);
        Assert.Equal(new[] { "begin", "cancel", "join-start", "join-end", "body", "complete" }, events);
    }

    [Fact]
    public async Task MonitorIsSampledAfterLifetimeJoinAndLatePublishedTaskIsJoined()
    {
        var events = new List<string>();
        Task? monitor = null;
        var monitorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMonitor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = SupervisionApplicationCleanup.RunAsync(Actions(events,
            stop: () =>
            {
                events.Add("lifetime");
                monitor = Task.Run(async () =>
                {
                    events.Add("monitor-start");
                    monitorStarted.SetResult();
                    await releaseMonitor.Task;
                    events.Add("monitor-end");
                });
                return Task.CompletedTask;
            }, monitor: () => monitor));

        var failed = false;
        try
        {
            await monitorStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(cleanup.IsCompleted);
            Assert.DoesNotContain("body", events);
        }
        finally
        {
            releaseMonitor.TrySetResult();
            if (monitor is not null) await monitor;
            failed = await cleanup;
        }

        Assert.False(failed);
        Assert.True(events.IndexOf("monitor-end") < events.IndexOf("body"));
        Assert.True(events.IndexOf("body") < events.IndexOf("complete"));
    }

    [Fact]
    public async Task MonitorCancellationIsIgnoredOnlyWhenOriginalMonitorTokenIsCanceled()
    {
        var events = new List<string>();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        canceled.SetCanceled();
        var first = await SupervisionApplicationCleanup.RunAsync(Actions(events, monitor: () => canceled.Task,
            cancellationRequested: () => true));
        Assert.False(first);
        Assert.DoesNotContain("monitor-failure", events);

        events.Clear();
        var uncanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uncanceled.SetCanceled();
        var second = await SupervisionApplicationCleanup.RunAsync(Actions(events, monitor: () => uncanceled.Task,
            cancellationRequested: () => false));
        Assert.False(second);
        Assert.Contains("monitor-failure", events);
        Assert.Contains("body", events);
    }

    [Fact]
    public async Task PriorStopFailureReachesBodyAndBodyFailurePreventsPublicationButCompletesWork()
    {
        var events = new List<string>();
        var failed = await SupervisionApplicationCleanup.RunAsync(Actions(events,
            stop: () => Task.FromException(new InvalidOperationException("stop")),
            body: priorFailure =>
            {
                events.Add(priorFailure ? "body-failed" : "body-clean");
                if (!priorFailure) events.Add("publish");
                throw new InvalidOperationException("body");
            }));

        Assert.True(failed);
        Assert.Equal(new[] { "begin", "cancel", "body-failed", "complete" }, events);
    }

    [Fact]
    public async Task ActiveWorkCompletionWaitsForBodyAndBeginStopRemainsOutsideFailureCatch()
    {
        var events = new List<string>();
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = SupervisionApplicationCleanup.RunAsync(Actions(events, body: async _ =>
        {
            events.Add("body-start");
            bodyStarted.SetResult();
            await releaseBody.Task;
            events.Add("body-end");
        }));
        var failed = false;
        try
        {
            await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(cleanup.IsCompleted);
            Assert.DoesNotContain("complete", events);
        }
        finally
        {
            releaseBody.TrySetResult();
            failed = await cleanup;
        }
        Assert.False(failed);
        Assert.True(events.IndexOf("body-end") < events.IndexOf("complete"));

        var finalized = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SupervisionApplicationCleanup.RunAsync(
            Actions(new List<string>(), begin: () => throw new InvalidOperationException("begin"),
                complete: () => { finalized = true; return true; })));
        Assert.False(finalized);
    }

    [Fact]
    public async Task RejectedFinalWorkCompletionKeepsOtherwiseSuccessfulCleanupFailed()
    {
        var events = new List<string>();
        var failed = await SupervisionApplicationCleanup.RunAsync(Actions(events, complete: () => false));

        Assert.True(failed);
        Assert.Equal(new[] { "begin", "cancel", "lifetime", "body", "complete" }, events);
    }

    [Fact]
    public async Task FinalCompletionExceptionPropagatesAfterBodyInsteadOfBeingReportedAsSuccess()
    {
        var events = new List<string>();
        var expected = new InvalidOperationException("work-registration-completion");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SupervisionApplicationCleanup.RunAsync(Actions(events, complete: () => throw expected)));

        Assert.Same(expected, error);
        Assert.Equal(new[] { "begin", "cancel", "lifetime", "body", "complete" }, events);
    }

    [Fact]
    public async Task UnexpectedMonitorFaultIsRecordedBeforeBodyAndFinalCompletion()
    {
        var events = new List<string>();
        var failed = await SupervisionApplicationCleanup.RunAsync(Actions(events,
            monitor: () => Task.FromException(new IOException("monitor-canary"))));

        // The native owner's ledger records the monitor failure independently of this procedure flag.
        Assert.False(failed);
        Assert.Equal(new[] { "begin", "cancel", "lifetime", "monitor-failure", "body", "complete" }, events);
    }

    private static SupervisionApplicationCleanupActions Actions(List<string> events,
        Action? begin = null, Action? cancel = null, Func<Task>? stop = null,
        Func<Task?>? monitor = null, Func<bool>? cancellationRequested = null, Func<bool, Task>? body = null,
        Func<bool>? complete = null) => new(
        () => { events.Add("begin"); begin?.Invoke(); },
        () => { events.Add("cancel"); cancel?.Invoke(); },
        stop ?? (() => { events.Add("lifetime"); return Task.CompletedTask; }),
        monitor ?? (() => null),
        () => { events.Add("monitor-check"); return cancellationRequested?.Invoke() ?? false; },
        () => events.Add("monitor-failure"),
        body ?? (_ => { events.Add("body"); return Task.CompletedTask; }),
        () => { events.Add("complete"); return complete?.Invoke() ?? true; });
}
