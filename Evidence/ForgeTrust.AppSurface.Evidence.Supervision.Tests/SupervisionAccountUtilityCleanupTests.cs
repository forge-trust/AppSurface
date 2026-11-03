namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

public sealed class SupervisionAccountUtilityCleanupTests
{
    [Fact]
    public async Task CompletesInOriginalOrderAndPublishesOnlyAfterBackend()
    {
        var trace = new List<string>();
        var settled = false;
        await Run(trace, started: true, pipes: true, () => { trace.Add("physical"); settled = true; });

        Assert.Equal([
            "stop", "first-write", "group", "second-write", "join", "native", "dispose-pipes",
            "backend", "physical"
        ], trace);
        Assert.True(settled);
    }

    [Fact]
    public async Task StopFailureSkipsRemainingCompoundPhaseButContinuesOwnedPipeCleanup()
    {
        var trace = new List<string>();
        var failures = new List<Exception>();
        var actions = Actions(trace, started: true, pipes: true, () => trace.Add("physical"),
            stop: () => Task.FromException(new InvalidOperationException("stop")));

        await SupervisionAccountUtilityCleanup.RunAsync(actions, failures.Add);

        Assert.Equal(["stop", "second-write", "join", "native", "dispose-pipes", "backend", "physical"], trace);
        Assert.Single(failures);
    }

    [Fact]
    public async Task FirstCompoundWriteFailureSkipsGroupButLaterWriteAndJoinStillRun()
    {
        var trace = new List<string>();
        var failures = new List<Exception>();
        var actions = Actions(trace, started: true, pipes: true, () => trace.Add("physical"),
            firstWrite: () => { trace.Add("first-write"); throw new InvalidOperationException("first-write"); },
            secondWrite: () => trace.Add("second-write"));

        await SupervisionAccountUtilityCleanup.RunAsync(actions, failures.Add);

        Assert.Equal(["stop", "first-write", "second-write", "join", "native", "dispose-pipes", "backend", "physical"], trace);
        Assert.Single(failures);
    }

    [Fact]
    public async Task SecondWriteCloseFailureStillJoinsAndDisposesTheOriginalPipeOwner()
    {
        var trace = new List<string>();
        var failures = new List<Exception>();
        var original = new IOException("second-write");
        var actions = Actions(trace, started: true, pipes: true, () => trace.Add("physical"),
            secondWrite: () => { trace.Add("second-write"); throw original; });

        await SupervisionAccountUtilityCleanup.RunAsync(actions, failures.Add);

        Assert.Equal(["stop", "first-write", "group", "second-write", "join", "native", "dispose-pipes", "backend", "physical"], trace);
        Assert.Same(original, Assert.Single(failures));
    }

    [Fact]
    public async Task BackendFailureClearsCallerSettlementAndSkipsPhysicalPublication()
    {
        var trace = new List<string>();
        var failures = new List<Exception>();
        var nativeJoined = true;
        var pipesClosed = true;
        var actions = Actions(trace, started: true, pipes: true, () => trace.Add("physical"),
            backend: () => { nativeJoined = false; throw new InvalidOperationException("backend"); },
            canPhysical: () => nativeJoined && pipesClosed);

        await SupervisionAccountUtilityCleanup.RunAsync(actions, failures.Add);

        Assert.DoesNotContain("physical", trace);
        Assert.False(nativeJoined && pipesClosed);
        Assert.Single(failures);
    }

    [Fact]
    public async Task OutputJoinFailureStillDisposesPipesAndBackendWithoutSettlement()
    {
        var trace = new List<string>();
        var failures = new List<Exception>();
        var actions = Actions(trace, started: true, pipes: true, () => trace.Add("physical"),
            join: () => Task.FromException(new InvalidOperationException("join")));

        await SupervisionAccountUtilityCleanup.RunAsync(actions, failures.Add);

        Assert.Equal(["stop", "first-write", "group", "second-write", "join", "dispose-pipes", "backend"], trace);
        Assert.Single(failures);
    }

    [Fact]
    public async Task PipeDisposeFailureSuppressesSettlementAfterAJoinedOutput()
    {
        var trace = new List<string>();
        var failures = new List<Exception>();
        var actions = Actions(trace, started: true, pipes: true, () => trace.Add("physical"),
            disposePipes: () => throw new InvalidOperationException("dispose-pipes"));

        await SupervisionAccountUtilityCleanup.RunAsync(actions, failures.Add);

        Assert.Equal(["stop", "first-write", "group", "second-write", "join", "native", "dispose-pipes", "backend"], trace);
        Assert.Single(failures);
    }

    [Fact]
    public async Task PhysicalSettlementFailureIsCapturedAfterAllRetainedFactsAreTrue()
    {
        var trace = new List<string>();
        var failures = new List<Exception>();
        var failure = new InvalidOperationException("physical");
        var actions = Actions(trace, started: true, pipes: true, () => throw failure);

        await SupervisionAccountUtilityCleanup.RunAsync(actions, error => failures.Add(error));

        Assert.Equal(["stop", "first-write", "group", "second-write", "join", "native", "dispose-pipes", "backend"], trace);
        Assert.Same(failure, Assert.Single(failures));
    }

    [Fact]
    public async Task NoPipesMarksOnlyCallerFactsAndStillDisposesBackend()
    {
        var trace = new List<string>();
        var marked = false;
        var actions = Actions(trace, started: false, pipes: false, () => trace.Add("physical"),
            markNoPipes: () => marked = true,
            canPhysical: () => marked);

        await SupervisionAccountUtilityCleanup.RunAsync(actions, _ => { });

        Assert.Equal(["stop", "no-pipes", "backend", "physical"], trace);
        Assert.True(marked);
    }

    [Fact]
    public async Task PhysicalSettlementUsesTheRetainedPredicateAtTheLastBoundary()
    {
        var trace = new List<string>();
        var canSettle = false;
        var actions = Actions(trace, started: true, pipes: true, () => trace.Add("physical"),
            canPhysical: () => canSettle);

        await SupervisionAccountUtilityCleanup.RunAsync(actions, _ => { });
        Assert.DoesNotContain("physical", trace);

        canSettle = true;
        trace.Clear();
        await SupervisionAccountUtilityCleanup.RunAsync(actions, _ => { });
        Assert.Contains("physical", trace);
    }

    [Fact]
    public async Task CallbackFailureCaptureIsStickyAndLaterStagesStillExecute()
    {
        var trace = new List<string>();
        var failures = new List<Exception>();
        Exception? first = null;
        var actions = Actions(trace, started: true, pipes: true, () => trace.Add("physical"),
            stop: () => Task.FromException(new InvalidOperationException("first")),
            join: () => Task.FromException(new InvalidOperationException("join")),
            secondWrite: () => trace.Add("second-write"));

        await SupervisionAccountUtilityCleanup.RunAsync(actions, error =>
        {
            first ??= error;
            failures.Add(error);
            trace.Add("captured");
        });

        Assert.Equal(["stop", "captured", "second-write", "join", "captured", "dispose-pipes", "backend"], trace);
        Assert.Equal(2, failures.Count);
        Assert.Equal("first", failures[0].Message);
        Assert.Equal("join", failures[1].Message);
        Assert.Same(failures[0], first);
    }

    [Fact]
    public async Task CancellationFromTheOriginalStopCallbackIsCapturedAndCleanupContinues()
    {
        var trace = new List<string>();
        var failures = new List<Exception>();
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("original-token", cancellation.Token);
        var actions = Actions(trace, started: true, pipes: true, () => trace.Add("physical"),
            stop: () => Task.FromException(original),
            secondWrite: () => trace.Add("second-write"));

        await SupervisionAccountUtilityCleanup.RunAsync(actions, failures.Add);

        Assert.Equal(["stop", "second-write", "join", "native", "dispose-pipes", "backend", "physical"], trace);
        var captured = Assert.Single(failures);
        Assert.Same(original, captured);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(captured).CancellationToken);
    }

    private static async Task Run(List<string> trace, bool started, bool pipes, Action physical)
    {
        await SupervisionAccountUtilityCleanup.RunAsync(
            Actions(trace, started, pipes, physical), _ => { });
    }

    private static SupervisionAccountUtilityCleanupActions Actions(List<string> trace, bool started, bool pipes,
        Action physical, Func<Task>? stop = null, Action? firstWrite = null,
        Action? secondWrite = null, Func<Task>? join = null, Action? backend = null, Func<bool>? canPhysical = null,
        Action? markNoPipes = null, Action? disposePipes = null)
    {
        var nativeJoined = false;
        var pipesClosed = false;
        return new(
            async () =>
            {
                trace.Add("stop");
                if (started)
                {
                    if (stop is not null) await stop();
                    if (pipes) (firstWrite ?? (() => trace.Add("first-write")))();
                    trace.Add("group");
                }
            },
            pipes ? new(
                secondWrite ?? (() => trace.Add("second-write")),
                async () =>
                {
                    trace.Add("join");
                    if (join is not null) await join();
                    nativeJoined = true;
                    trace.Add("native");
                },
                () =>
                {
                    trace.Add("dispose-pipes");
                    if (disposePipes is not null) disposePipes();
                    pipesClosed = true;
                    return ValueTask.CompletedTask;
                }) : null,
            !started ? () =>
            {
                trace.Add("no-pipes");
                nativeJoined = true;
                pipesClosed = true;
                markNoPipes?.Invoke();
            } : null,
            backend ?? (() => trace.Add("backend")),
            canPhysical ?? (() => nativeJoined && pipesClosed),
            physical);
    }
}
