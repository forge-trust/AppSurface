namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Verifies the worker cleanup procedure with local operations and completion barriers only.</summary>
public sealed class SupervisionWorkerCleanupTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AllOperationsRunInOriginalOrderAndAbsentOptionalBindingsAreSkipped(bool monitor, bool pipes)
    {
        var events = new List<string>();
        var original = Actions(events);
        var actions = original with { JoinMonitor = monitor ? original.JoinMonitor : null, Pipes = pipes ? original.Pipes : null };

        await SupervisionWorkerCleanup.RunAsync(actions, stage => events.Add($"failure:{stage}"));

        var expected = new List<string> { "unit" };
        if (monitor) expected.Add("monitor");
        if (pipes) expected.AddRange(["pipe-write", "pumps", "pipe-close"]);
        expected.AddRange(["observation", "inspection", "start-close"]);
        Assert.Equal(expected, events);
    }

    [Theory]
    [InlineData((int)SupervisionWorkerCleanupStage.UnitAndGroup, "io", true)]
    [InlineData((int)SupervisionWorkerCleanupStage.PipeWriteCopies, "cancel", true)]
    [InlineData((int)SupervisionWorkerCleanupStage.Pumps, "oom", false)]
    [InlineData((int)SupervisionWorkerCleanupStage.PipeClose, "access", false)]
    [InlineData((int)SupervisionWorkerCleanupStage.ObservationClose, "io", true)]
    [InlineData((int)SupervisionWorkerCleanupStage.FinalInspection, "cancel", false)]
    [InlineData((int)SupervisionWorkerCleanupStage.StartConnectionClose, "oom", true)]
    public async Task EveryCaughtStageCapturesSyncOrAsyncFailureBeforeContinuing(
        int failedStageValue, string kind, bool synchronous)
    {
        var failedStage = (SupervisionWorkerCleanupStage)failedStageValue;
        var events = new List<string>();
        var error = Error(kind);
        var actions = Actions(events, failedStage, error, synchronous);

        await SupervisionWorkerCleanup.RunAsync(actions, stage => events.Add($"failure:{stage}"));

        var operation = OperationName(failedStage);
        var operationIndex = events.IndexOf(operation);
        Assert.True(operationIndex >= 0);
        Assert.Equal($"failure:{failedStage}", events[operationIndex + 1]);
        Assert.DoesNotContain($"failure:{failedStage}", events.Skip(operationIndex + 2));
        Assert.Equal(
            new[] { "unit", "monitor", "pipe-write", "pumps", "pipe-close", "observation", "inspection", "start-close" }
                .Where(name => name != "monitor" || actions.JoinMonitor is not null),
            events.Where(name => !name.StartsWith("failure:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MonitorFailureEscapesWithoutReclassificationOrLaterCleanup()
    {
        var events = new List<string>();
        var monitorFailure = new InvalidOperationException("monitor");
        var actions = Actions(events, joinMonitor: () =>
        {
            events.Add("monitor");
            return Task.FromException(monitorFailure);
        });

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SupervisionWorkerCleanup.RunAsync(actions, stage => events.Add($"failure:{stage}")));

        Assert.Same(monitorFailure, actual);
        Assert.Equal(["unit", "monitor"], events);
    }

    [Fact]
    public async Task UnitMonitorPumpsAndInspectionAreAwaitedBeforeLaterCleanup()
    {
        var events = new List<string>();
        var unitGate = NewGate();
        var monitorGate = NewGate();
        var pumpsGate = NewGate();
        var pipeCloseGate = NewGate();
        var inspectionGate = NewGate();
        var unitStarted = NewGate();
        var monitorStarted = NewGate();
        var pumpsStarted = NewGate();
        var pipeCloseStarted = NewGate();
        var inspectionStarted = NewGate();

        async Task JoinUnit()
        {
            events.Add("unit-entered");
            unitStarted.TrySetResult();
            await unitGate.Task;
            events.Add("unit-complete");
        }

        async Task JoinMonitor()
        {
            events.Add("monitor-entered");
            monitorStarted.TrySetResult();
            await monitorGate.Task;
            events.Add("monitor-complete");
        }

        async Task JoinPumps()
        {
            events.Add("pumps-entered");
            pumpsStarted.TrySetResult();
            await pumpsGate.Task;
            events.Add("pumps-complete");
        }

        async ValueTask DisposePipes()
        {
            events.Add("pipe-close-entered");
            pipeCloseStarted.TrySetResult();
            await pipeCloseGate.Task;
            events.Add("pipe-close-complete");
        }

        async Task Inspect()
        {
            events.Add("inspection-entered");
            inspectionStarted.TrySetResult();
            await inspectionGate.Task;
            events.Add("inspection-complete");
        }

        var actions = new SupervisionWorkerCleanupActions(
            JoinUnit,
            JoinMonitor,
            new SupervisionWorkerPipeCleanup(() => events.Add("pipe-write"), JoinPumps, DisposePipes),
            () => events.Add("observation"),
            Inspect,
            () => events.Add("start-close"));

        var running = SupervisionWorkerCleanup.RunAsync(actions, stage => events.Add($"failure:{stage}"));
        try
        {
            await unitStarted.Task.WaitAsync(Guard);
            Assert.False(running.IsCompleted);
            Assert.DoesNotContain("monitor-entered", events);
            unitGate.TrySetResult();

            await monitorStarted.Task.WaitAsync(Guard);
            Assert.False(running.IsCompleted);
            Assert.DoesNotContain("pipe-write", events);
            monitorGate.TrySetResult();

            await pumpsStarted.Task.WaitAsync(Guard);
            Assert.False(running.IsCompleted);
            Assert.DoesNotContain("pipe-close-entered", events);
            pumpsGate.TrySetResult();

            await pipeCloseStarted.Task.WaitAsync(Guard);
            Assert.False(running.IsCompleted);
            Assert.DoesNotContain("observation", events);
            pipeCloseGate.TrySetResult();

            await inspectionStarted.Task.WaitAsync(Guard);
            Assert.False(running.IsCompleted);
            Assert.DoesNotContain("start-close", events);
            inspectionGate.TrySetResult();
        }
        finally
        {
            unitGate.TrySetResult();
            monitorGate.TrySetResult();
            pumpsGate.TrySetResult();
            pipeCloseGate.TrySetResult();
            inspectionGate.TrySetResult();
            await running.WaitAsync(Guard);
        }

        Assert.Contains("start-close", events);
    }

    [Fact]
    public async Task EachOperationIsAttemptedOnceAndNoCancellationProxyIsIntroduced()
    {
        var events = new List<string>();
        var actions = Actions(events, SupervisionWorkerCleanupStage.Pumps,
            new OperationCanceledException("operation"), synchronous: false);

        await SupervisionWorkerCleanup.RunAsync(actions, stage => events.Add($"failure:{stage}"));

        Assert.Equal(1, events.Count(name => name == "unit"));
        Assert.Equal(1, events.Count(name => name == "monitor"));
        Assert.Equal(1, events.Count(name => name == "pipe-write"));
        Assert.Equal(1, events.Count(name => name == "pumps"));
        Assert.Equal(1, events.Count(name => name == "pipe-close"));
        Assert.Equal(1, events.Count(name => name == "observation"));
        Assert.Equal(1, events.Count(name => name == "inspection"));
        Assert.Equal(1, events.Count(name => name == "start-close"));
    }

    [Theory]
    [InlineData("actions")]
    [InlineData("recordFailure")]
    public async Task NullInputsAreRejectedBeforeAnyOperation(string input)
    {
        var attempts = 0;
        var events = new List<string>();
        var actions = Actions(events);

        var error = await Assert.ThrowsAsync<ArgumentNullException>(() => input == "actions"
            ? SupervisionWorkerCleanup.RunAsync(null!, _ => attempts++)
            : SupervisionWorkerCleanup.RunAsync(actions, null!));

        Assert.Equal(input, error.ParamName);
        Assert.Equal(0, attempts);
        Assert.Empty(events);
    }

    private static SupervisionWorkerCleanupActions Actions(List<string> events,
        SupervisionWorkerCleanupStage? failedStage = null, Exception? error = null,
        bool synchronous = true, Func<Task>? joinMonitor = null,
        SupervisionWorkerPipeCleanup? pipes = null)
    {
        return new SupervisionWorkerCleanupActions(
            () => Run("unit", SupervisionWorkerCleanupStage.UnitAndGroup),
            joinMonitor ?? (() => Run("monitor", null)),
            pipes ?? new SupervisionWorkerPipeCleanup(
                () => Throw("pipe-write", SupervisionWorkerCleanupStage.PipeWriteCopies),
                () => Run("pumps", SupervisionWorkerCleanupStage.Pumps),
                () => new ValueTask(Run("pipe-close", SupervisionWorkerCleanupStage.PipeClose))),
            () => Throw("observation", SupervisionWorkerCleanupStage.ObservationClose),
            () => Run("inspection", SupervisionWorkerCleanupStage.FinalInspection),
            () => Throw("start-close", SupervisionWorkerCleanupStage.StartConnectionClose));

        Task Run(string name, SupervisionWorkerCleanupStage? stage)
        {
            events.Add(name);
            if (stage is not null && stage == failedStage)
            {
                if (synchronous) throw error!;
                return Task.FromException(error!);
            }
            return Task.CompletedTask;
        }

        void Throw(string name, SupervisionWorkerCleanupStage stage)
        {
            events.Add(name);
            if (stage == failedStage && synchronous)
                throw error!;
        }
    }

    private static Exception Error(string kind) => kind switch
    {
        "io" => new IOException("io"),
        "cancel" => new OperationCanceledException("cancel"),
        "oom" => new OutOfMemoryException("oom"),
        "access" => new AccessViolationException("access"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string OperationName(SupervisionWorkerCleanupStage stage) => stage switch
    {
        SupervisionWorkerCleanupStage.UnitAndGroup => "unit",
        SupervisionWorkerCleanupStage.PipeWriteCopies => "pipe-write",
        SupervisionWorkerCleanupStage.Pumps => "pumps",
        SupervisionWorkerCleanupStage.PipeClose => "pipe-close",
        SupervisionWorkerCleanupStage.ObservationClose => "observation",
        SupervisionWorkerCleanupStage.FinalInspection => "inspection",
        SupervisionWorkerCleanupStage.StartConnectionClose => "start-close",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
