namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Original-task and failure-order controls only; no native owner, connection or receipt is fabricated.</summary>
public sealed class SupervisionControlDrainTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("handlers")]
    [InlineData("actions")]
    [InlineData("recordFailure")]
    public async Task MissingProcedureInputsRejectBeforeAnyOperationDispatch(string missing)
    {
        using var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() => SupervisionControlDrain.RunAsync(true,
            null, missing == "handlers" ? null! : [], missing == "actions" ? null! : fixture.Actions,
            missing == "recordFailure" ? null! : fixture.Failed));
        Assert.Equal(missing, error.ParamName);
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Failures);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("stack")]
    [InlineData("access")]
    public async Task FatalProcedureExceptionsPropagateWithoutBecomingARecoverableJoinResult(string kind)
    {
        using var fixture = new Fixture();
        // Constructed exceptions test the classification rule; this causes no resource exhaustion or native fault.
        Exception fatal = kind switch
        {
            "memory" => new OutOfMemoryException(),
            "stack" => new StackOverflowException(),
            "access" => new AccessViolationException(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var actions = fixture.Actions with { CancelSignal = () => throw fatal };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => SupervisionControlDrain.RunAsync(true,
            null, [], actions, fixture.Failed));
        Assert.Same(fatal, error);
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Failures);
    }

    [Fact]
    public async Task ExitClosesOnlyAdmissionBeforeJoiningBothOriginalHandlersAndThenClosesTheListener()
    {
        using var fixture = new Fixture();
        var pending = Signal(); var first = Signal(); var second = Signal(); var admissionClosed = Signal();
        var actions = fixture.Actions with
        {
            CloseAcceptAdmission = () =>
            {
                fixture.Events.Add("accept-close");
                pending.TrySetCanceled(); admissionClosed.TrySetResult();
                return Task.CompletedTask;
            },
            CloseListener = () =>
            {
                Assert.True(first.Task.IsCompleted); Assert.True(second.Task.IsCompleted);
                return fixture.Record("listener-close");
            }
        };
        var drain = SupervisionControlDrain.RunAsync(true, pending.Task,
            [first.Task, second.Task], actions, fixture.Failed);
        try
        {
            await admissionClosed.Task.WaitAsync(Guard);
            Assert.False(drain.IsCompleted);
            Assert.DoesNotContain("listener-close", fixture.Events);
            first.SetResult();
            Assert.False(drain.IsCompleted); // One completed handler cannot substitute for both original tasks.
        }
        finally { pending.TrySetCanceled(); first.TrySetResult(); second.TrySetResult(); }
        Assert.True(await drain.WaitAsync(Guard));
        Assert.Empty(fixture.Failures);
        Assert.Equal(new[] { "signal-cancel", "accept-cancel", "checkpoints", "accept-close", "listener-close", "stop", "controls" }, fixture.Events);
    }

    [Fact]
    public async Task IncompleteProtocolStartsContainmentBeforeAHandlerThatOnlyTheOriginalStopCanRelease()
    {
        using var fixture = new Fixture();
        var stopStarted = Signal(); var stopFinished = Signal(); var handlerFinished = Signal(); var listenerClosed = Signal();
        var actions = fixture.Actions with
        {
            BeginFailedDrain = () =>
            {
                fixture.Events.Add("begin-stop"); stopStarted.TrySetResult();
                return stopFinished.Task;
            },
            CloseListener = () =>
            {
                Assert.True(stopStarted.Task.IsCompletedSuccessfully);
                fixture.Events.Add("listener-close"); listenerClosed.TrySetResult();
                return Task.CompletedTask;
            },
            StopDescendants = () => throw new InvalidOperationException("The original stop must be joined, not restarted.")
        };
        var drain = SupervisionControlDrain.RunAsync(false, null, [handlerFinished.Task], actions, fixture.Failed);
        try
        {
            await listenerClosed.Task.WaitAsync(Guard);
            Assert.False(drain.IsCompleted);
            handlerFinished.TrySetResult();
            Assert.False(drain.IsCompleted);
            Assert.DoesNotContain("controls", fixture.Events);
        }
        finally { handlerFinished.TrySetResult(); stopFinished.TrySetResult(); }
        Assert.True(await drain.WaitAsync(Guard));
        Assert.Empty(fixture.Failures);
        Assert.Equal(new[] { "begin-stop", "signal-cancel", "accept-cancel", "checkpoints", "listener-close", "controls" }, fixture.Events);
        // True concerns I/O joins only. The native BeginFailedDrain still irreversibly marks protocol failure.
    }

    [Theory]
    [InlineData("begin-sync", false, true, "DescendantsStop")]
    [InlineData("begin-task", false, true, "DescendantsStop")]
    [InlineData("signal-cancel", true, false, "HandlerDispatch")]
    [InlineData("signal-task", true, false, "HandlerDispatch")]
    [InlineData("accept-cancel", true, true, "AcceptCancel")]
    [InlineData("accept-close", true, false, "ListenerClose")]
    [InlineData("listener-close", true, false, "ListenerClose")]
    [InlineData("listener-close", false, false, "ListenerClose")]
    [InlineData("stop-sync", true, true, "DescendantsStop")]
    [InlineData("stop-task", true, true, "DescendantsStop")]
    [InlineData("controls", true, false, "ControlsJoin")]
    public async Task ARecoverableFailureIsLatchedAtItsActualStageWithoutSkippingLaterJoins(
        string fault, bool exitIntent, bool expectedIoJoined, string expectedStage)
    {
        using var fixture = new Fixture();
        var error = new IOException("private-canary");
        var actions = fault switch
        {
            "begin-sync" => fixture.Actions with { BeginFailedDrain = () => throw error },
            "begin-task" => fixture.Actions with { BeginFailedDrain = () => Task.FromException(error) },
            "signal-cancel" => fixture.Actions with { CancelSignal = () => throw error },
            "signal-task" => fixture.Actions with { Signal = Task.FromException(error) },
            "accept-cancel" => fixture.Actions with { CancelAccept = () => throw error },
            "accept-close" => fixture.Actions with { CloseAcceptAdmission = () => Task.FromException(error) },
            "listener-close" => fixture.Actions with { CloseListener = () => throw error },
            "stop-sync" => fixture.Actions with { StopDescendants = () => throw error },
            "stop-task" => fixture.Actions with { StopDescendants = () => Task.FromException(error) },
            "controls" => fixture.Actions with { JoinControls = () => { fixture.Events.Add("controls"); throw error; } },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.Equal(expectedIoJoined, await SupervisionControlDrain.RunAsync(exitIntent, null, [], actions, fixture.Failed).WaitAsync(Guard));
        Assert.Equal(expectedStage, Assert.Single(fixture.Failures).Stage.ToString());
        Assert.Equal("controls", fixture.Events[^1]);
        Assert.DoesNotContain("private-canary", fixture.Latch.First!.ToJson());
        if (fault == "begin-sync") Assert.Contains("stop", fixture.Events);
        if (fault == "begin-task") Assert.DoesNotContain("stop", fixture.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalCancellationIsExpectedOnlyAfterItsOwnLifetimeWasCancelled(bool cancelLifetime)
    {
        using var fixture = new Fixture();
        var cancelled = Task.FromCanceled(new CancellationToken(true));
        var actions = fixture.Actions with
        {
            Signal = cancelled,
            CancelSignal = cancelLifetime ? fixture.SignalLifetime.Cancel : () => { }
        };
        Assert.Equal(cancelLifetime, await SupervisionControlDrain.RunAsync(true, null, [], actions, fixture.Failed).WaitAsync(Guard));
        Assert.Equal(cancelLifetime ? 0 : 1, fixture.Failures.Count);
        Assert.Equal("controls", fixture.Events[^1]);
    }

    [Fact]
    public async Task AnEarlierFaultNeverLetsAnIgnoringHandlerEscapeTheOriginalJoin()
    {
        using var fixture = new Fixture();
        var held = Signal(); var listenerClosed = Signal();
        var fault = Task.FromException(new IOException("private-handler"));
        var actions = fixture.Actions with
        {
            CloseAcceptAdmission = () =>
            {
                listenerClosed.TrySetResult();
                throw new IOException("private-close");
            }
        };
        var drain = SupervisionControlDrain.RunAsync(true, Task.FromException(new IOException("private-accept")),
            [fault, held.Task], actions, fixture.Failed);
        try
        {
            await listenerClosed.Task.WaitAsync(Guard);
            Assert.False(drain.IsCompleted);
            Assert.DoesNotContain("controls", fixture.Events);
        }
        finally { held.TrySetResult(); }
        Assert.False(await drain.WaitAsync(Guard));
        Assert.Equal(new[] { LinuxControlFailureStage.ListenerClose, LinuxControlFailureStage.HandlersJoin },
            fixture.Failures.Select(item => item.Stage));
        Assert.Equal(LinuxControlFailureStage.ListenerClose, fixture.Latch.First!.Stage);
        Assert.Equal("controls", fixture.Events[^1]);
    }

    [Fact]
    public async Task QualificationCheckpointFailureRemainsFailedAfterAllOtherIoJoins()
    {
        using var fixture = new Fixture();
        var actions = fixture.Actions with { CloseCheckpoints = joined =>
        {
            Assert.True(joined); fixture.Events.Add("checkpoints"); return Task.FromResult(false);
        } };
        Assert.False(await SupervisionControlDrain.RunAsync(true, null, [], actions, fixture.Failed).WaitAsync(Guard));
        Assert.Empty(fixture.Failures); // The actual checkpoint closer owns its original failure latch.
        Assert.Equal("controls", fixture.Events[^1]);
    }

    [Fact]
    public async Task ASignalThatIgnoresCancellationMustJoinBeforeAcceptOrListenerClosure()
    {
        using var fixture = new Fixture();
        var held = Signal();
        var drain = SupervisionControlDrain.RunAsync(true, null, [], fixture.Actions with { Signal = held.Task }, fixture.Failed);
        try
        {
            Assert.True(fixture.SignalLifetime.IsCancellationRequested);
            Assert.False(drain.IsCompleted);
            Assert.Equal(new[] { "signal-cancel" }, fixture.Events);
        }
        finally { held.TrySetResult(); }
        Assert.True(await drain.WaitAsync(Guard));
        Assert.Equal("controls", fixture.Events[^1]);
    }

    private sealed class Fixture : IDisposable
    {
        internal List<string> Events { get; } = [];
        internal List<(LinuxControlFailureStage Stage, Exception Error)> Failures { get; } = [];
        internal LinuxControlFailureLatch Latch { get; } = new();
        internal CancellationTokenSource SignalLifetime { get; } = new();
        internal SupervisionControlDrainActions Actions => new(
            () => Record("begin-stop"),
            () => { Events.Add("signal-cancel"); SignalLifetime.Cancel(); },
            Task.CompletedTask, SignalLifetime.Token,
            () => Events.Add("accept-cancel"),
            joined => { Events.Add("checkpoints"); return Task.FromResult(joined); },
            () => Record("accept-close"), () => Record("listener-close"),
            () => Record("stop"), () => Record("controls"), LinuxControlFailureStage.HandlerDispatch);
        internal Task Record(string value) { Events.Add(value); return Task.CompletedTask; }
        internal void Failed(LinuxControlFailureStage stage, Exception error)
        { Failures.Add((stage, error)); Latch.Capture(stage, null, error); }
        public void Dispose() => SignalLifetime.Dispose();
    }
}
