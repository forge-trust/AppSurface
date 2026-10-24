namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Ordinary producer cleanup ordering only; no owner, unit, identity, report custody or native receipt.</summary>
public sealed class SupervisionProducerCleanupTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task OriginalAcquisitionControlsTheExactOrder(bool dispatched, bool pipes, bool collector)
    {
        var trace = new List<string>();
        var actions = Actions(trace);
        actions = actions with { Pipes = pipes ? actions.Pipes! with { JoinPumps = collector ? actions.Pipes!.JoinPumps : null } : null };
        await SupervisionProducerCleanup.RunAsync(actions with { DispatchAttempted = () => dispatched }, stage => trace.Add("failure:" + stage));
        var expected = new List<string> { "stop" };
        if (dispatched) expected.Add("observe");
        if (pipes)
        {
            expected.Add("writes");
            if (collector) expected.Add("pumps");
            expected.Add("dispose");
        }
        expected.Add("close");
        Assert.Equal(expected, trace);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public async Task EachCaughtFailureIsLatchedBeforeLaterOriginalCleanup(int failedStage, bool synchronous)
    {
        var trace = new List<string>();
        var selected = (SupervisionProducerCleanupStage)failedStage;
        var original = Actions(trace, selected, synchronous);
        await SupervisionProducerCleanup.RunAsync(original, stage => trace.Add("failure:" + stage));
        string[] names = ["stop", "observe", "writes", "pumps", "dispose"];
        var expected = new List<string>();
        for (var index = 0; index < names.Length; index++)
        {
            expected.Add(names[index]);
            if (index == failedStage) expected.Add("failure:" + selected);
        }
        expected.Add("close");
        Assert.Equal(expected, trace);
    }

    [Fact]
    public async Task OriginalStopJoinsBeforeDispatchIsSampledAndRemainingOperationsStayOwned()
    {
        var trace = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = false;
        var reads = 0;
        var original = Actions(trace);
        var actions = original with
        {
            StopPending = async () =>
            {
                trace.Add("stop");
                started.SetResult();
                await release.Task;
                dispatched = true;
            },
            Pipes = original.Pipes! with
            {
                JoinPumps = async () =>
                {
                    trace.Add("pumps");
                    pumpStarted.SetResult();
                    await pumpRelease.Task;
                }
            }
        };
        var cleanup = SupervisionProducerCleanup.RunAsync(actions with { DispatchAttempted = () => { reads++; return dispatched; } },
            stage => trace.Add("failure:" + stage));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(0, reads);
            Assert.Equal(new[] { "stop" }, trace);
            release.SetResult();
            await pumpStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(cleanup.IsCompleted);
            Assert.DoesNotContain("dispose", trace);
            Assert.DoesNotContain("close", trace);
            pumpRelease.SetResult();
        }
        finally
        {
            release.TrySetResult();
            pumpRelease.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(1, reads);
        Assert.Equal(new[] { "stop", "observe", "writes", "pumps", "dispose", "close" }, trace);
    }

    [Fact]
    public async Task RetainedCloseKeepsItsOriginalExceptionPolicyAfterEarlierFailure()
    {
        var trace = new List<string>();
        var original = Actions(trace, SupervisionProducerCleanupStage.StopPending, true);
        var failure = new IOException("private-close");
        var actions = original with { CloseRetainedOwners = () => { trace.Add("close"); throw failure; } };
        var actual = await Assert.ThrowsAsync<IOException>(() => SupervisionProducerCleanup.RunAsync(actions, stage => trace.Add("failure:" + stage)));
        Assert.Same(failure, actual);
        Assert.Equal(new[] { "stop", "failure:StopPending", "observe", "writes", "pumps", "dispose", "close" }, trace);
    }

    [Theory]
    [InlineData("actions")]
    [InlineData("recordFailure")]
    public async Task MissingRequiredInputRejectsBeforeDispatch(string name)
    {
        var trace = new List<string>();
        var failure = await Assert.ThrowsAsync<ArgumentNullException>(() => SupervisionProducerCleanup.RunAsync(
            name == "actions" ? null! : Actions(trace),
            name == "recordFailure" ? null! : _ => trace.Add("failure")));
        Assert.Equal(name, failure.ParamName);
        Assert.Empty(trace);
    }

    private static SupervisionProducerCleanupActions Actions(List<string> trace,
        SupervisionProducerCleanupStage? failure = null, bool synchronous = true)
    {
        return new(
            StopPending: () => Attempt("stop", SupervisionProducerCleanupStage.StopPending),
            DispatchAttempted: () => true,
            ObserveStopped: () => Attempt("observe", SupervisionProducerCleanupStage.ObserveStopped),
            Pipes: new(() => { _ = Attempt("writes", SupervisionProducerCleanupStage.CloseWriteCopies); },
                () => Attempt("pumps", SupervisionProducerCleanupStage.JoinPumps),
                () => new ValueTask(Attempt("dispose", SupervisionProducerCleanupStage.DisposePipes))),
            CloseRetainedOwners: () => trace.Add("close"));

        Task Attempt(string name, SupervisionProducerCleanupStage stage)
        {
            trace.Add(name);
            if (stage != failure) return Task.CompletedTask;
            Exception error = stage switch
            {
                SupervisionProducerCleanupStage.StopPending => new OperationCanceledException("private-stop"),
                SupervisionProducerCleanupStage.ObserveStopped => new IOException("private-observation"),
                SupervisionProducerCleanupStage.CloseWriteCopies => new OutOfMemoryException("private-write"),
                SupervisionProducerCleanupStage.JoinPumps => new InvalidOperationException("private-pump"),
                SupervisionProducerCleanupStage.DisposePipes => new AccessViolationException("private-pipe"),
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            };
            if (synchronous) throw error;
            return Task.FromException(error);
        }
    }
}
