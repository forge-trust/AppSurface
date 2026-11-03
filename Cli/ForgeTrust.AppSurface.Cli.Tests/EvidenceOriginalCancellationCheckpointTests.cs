using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Detached callback/token/I/O ordering controls; no native identity, lease or root authority.</summary>
public sealed class EvidenceOriginalCancellationCheckpointTests
{
    [Fact]
    public async Task OriginalCallerCancellationReleasesActualCheckpointCallback()
    {
        using var caller = new CancellationTokenSource();
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        using var error = new MemoryStream();
        var checkpoint = new EvidenceOriginalCancellationCheckpoint();
        var callback = checkpoint.WaitAtAsync(EvidenceOriginalCancellationCheckpoint.SelectedPhase, error, stage.Token, caller.Token).AsTask();
        Assert.False(callback.IsCompleted);
        Assert.Equal(EvidenceOriginalCancellationCheckpoint.Frame, error.ToArray());
        caller.Cancel();
        await callback;
        Assert.True(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task StageOnlyCancellationDoesNotCancelOriginalCaller()
    {
        using var caller = new CancellationTokenSource();
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        using var error = new MemoryStream();
        var callback = new EvidenceOriginalCancellationCheckpoint().WaitAtAsync(
            EvidenceOriginalCancellationCheckpoint.SelectedPhase, error, stage.Token, caller.Token).AsTask();
        stage.Cancel();
        await callback;
        Assert.False(caller.IsCancellationRequested);
        Assert.True(stage.IsCancellationRequested);
    }

    [Fact]
    public async Task NonselectedLocationDoesNotWriteOrWait()
    {
        using var error = new MemoryStream();
        var other = EvidenceOriginalCancellationCheckpoint.SelectedPhase == EvidenceOriginalCancellationPhase.BeforeAllocation
            ? EvidenceOriginalCancellationPhase.BeforeActivation : EvidenceOriginalCancellationPhase.BeforeAllocation;
        await new EvidenceOriginalCancellationCheckpoint().WaitAtAsync(other, error, default, default);
        Assert.Equal(0, error.Length);
    }

    [Fact]
    public async Task DuplicateSelectedLocationCannotReopenCheckpoint()
    {
        using var stage = new CancellationTokenSource();
        using var error = new MemoryStream();
        var checkpoint = new EvidenceOriginalCancellationCheckpoint();
        var original = checkpoint.WaitAtAsync(EvidenceOriginalCancellationCheckpoint.SelectedPhase, error, stage.Token, default).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => checkpoint.WaitAtAsync(
            EvidenceOriginalCancellationCheckpoint.SelectedPhase, error, stage.Token, default).AsTask());
        stage.Cancel();
        await original;
    }

    [Fact]
    public async Task IgnoredWriteCancellationKeepsCallbackAndBorrowedStreamOwnedUntilActualJoin()
    {
        using var caller = new CancellationTokenSource();
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        using var error = new HeldWriteStream();
        var callback = new EvidenceOriginalCancellationCheckpoint().WaitAtAsync(
            EvidenceOriginalCancellationCheckpoint.SelectedPhase, error, stage.Token, caller.Token).AsTask();
        await error.Entered.Task;
        caller.Cancel();
        Assert.False(callback.IsCompleted); // Cancelled waits never replace the original write task.
        Assert.False(error.Closed); // Checkpoint borrows the stream; lifecycle controls actual close.
        error.Release.TrySetResult();
        await callback;
        Assert.False(error.Closed);
    }

    [Fact]
    public void ClosedProjectionCannotClaimUnsettledOwnershipOrUnknownCase()
    {
        var actual = new EvidenceOriginalCancellationObservation(EvidenceOriginalCancellationCheckpoint.Case,
            EvidenceOriginalCancellationCheckpoint.SelectedPhase.ToString(), true, false, true);
        var line = EvidenceOriginalCancellationCheckpoint.Encode(actual);
        Assert.Contains("\"callerTokenCancelled\":true", line);
        Assert.Contains("\"lifecycleCallerCancelled\":false", line);
        Assert.Contains("\"nativeAuthority\":false", line);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) + 1 <= 1024);
        Assert.Throws<InvalidOperationException>(() => EvidenceOriginalCancellationCheckpoint.Encode(actual with { OwnWorkStopped = false }));
        Assert.Throws<InvalidOperationException>(() => EvidenceOriginalCancellationCheckpoint.Encode(actual with { Case = "secret-canary" }));
        Assert.DoesNotContain("secret-canary", line);
    }

    [Fact]
    public async Task ObservationRequiresActualJoinEvenAfterTheTransferredCallbackCompletes()
    {
        var execution = Execution();
        var checkpoint = new EvidenceOriginalCancellationCheckpoint();
        await CompleteCheckpointAsync(checkpoint, execution);
        Assert.False(execution.OwnWorkStopped);
        AssertRejected(() => checkpoint.ObserveJoined(execution));

        Assert.True(await execution.StopAndDisposeAsync());
        var observation = checkpoint.ObserveJoined(execution);
        Assert.True(observation.OwnWorkStopped);
        Assert.True(observation.CallerTokenCancelled);
        Assert.False(observation.LifecycleCallerCancelled); // Token observation does not manufacture a terminal cause.
    }

    [Fact]
    public async Task JoinedLifecycleDoesNotReplaceAnUnclaimedCheckpointOrAFailedTransfer()
    {
        var execution = Execution();
        var unclaimed = new EvidenceOriginalCancellationCheckpoint();
        var failedTransfer = new EvidenceOriginalCancellationCheckpoint();
        await CompleteCheckpointAsync(failedTransfer, execution, failWrite: true);
        Assert.True(await execution.StopAndDisposeAsync());
        Assert.True(execution.OwnWorkStopped);
        AssertRejected(() => unclaimed.ObserveJoined(execution));
        AssertRejected(() => failedTransfer.ObserveJoined(execution));
        var sinkCalls = 0;
        failedTransfer.ReportJoined(execution, _ => sinkCalls++);
        Assert.Equal(0, sinkCalls);
    }

    [Fact]
    public async Task CompletedWriteRemainsObservableWhenItsFlushFaultsBeforeCancellation()
    {
        var execution = Execution();
        var checkpoint = new EvidenceOriginalCancellationCheckpoint();
        await CompleteCheckpointAsync(checkpoint, execution, failFlush: true);
        await execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.CallerCancelled);
        Assert.False(await execution.StopAndDisposeAsync());
        var observation = checkpoint.ObserveJoined(execution);
        Assert.True(observation.CallerTokenCancelled);
        Assert.True(observation.LifecycleCallerCancelled);
        Assert.True(observation.OwnWorkStopped);
        Assert.Equal(EvidenceWorkerTerminalCode.CallerCancelled, execution.TerminalCode);
    }

    [Fact]
    public async Task BestEffortSinkCannotReplaceTheActualLifecycleState()
    {
        var execution = Execution();
        var checkpoint = new EvidenceOriginalCancellationCheckpoint();
        checkpoint.ReportJoined(execution, null); // No sink means no observation of an unsettled execution.
        await CompleteCheckpointAsync(checkpoint, execution);
        Assert.True(await execution.StopAndDisposeAsync());
        var expected = checkpoint.ObserveJoined(execution);
        var calls = 0;
        EvidenceOriginalCancellationObservation? received = null;
        checkpoint.ReportJoined(execution, actual =>
        {
            calls++;
            received = actual;
            throw new IOException("sink-secret-canary");
        });
        Assert.Equal(1, calls);
        Assert.Equal(expected, received);
        Assert.Equal(EvidenceWorkerTerminalCode.None, execution.TerminalCode);
        Assert.True(execution.OwnWorkStopped);
        Assert.Equal(expected, checkpoint.ObserveJoined(execution));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FatalSinkExceptionsEscapeWithoutChangingJoinedOwnership(int kind)
    {
        var execution = Execution();
        var checkpoint = new EvidenceOriginalCancellationCheckpoint();
        await CompleteCheckpointAsync(checkpoint, execution);
        Assert.True(await execution.StopAndDisposeAsync());
        Exception expected = kind switch
        {
            0 => new OutOfMemoryException(),
            1 => new StackOverflowException(),
            _ => new AccessViolationException(),
        };
        var actual = Record.Exception(() => checkpoint.ReportJoined(execution, _ => throw expected));
        Assert.Same(expected, actual);
        Assert.True(execution.OwnWorkStopped);
        Assert.Equal(EvidenceWorkerTerminalCode.None, execution.TerminalCode);
    }

    [Fact]
    public void WrongProjectionPhaseIsRejectedWithoutEchoingUntrustedText()
    {
        var projection = new EvidenceOriginalCancellationObservation(EvidenceOriginalCancellationCheckpoint.Case,
            "phase-secret-canary", false, false, true);
        AssertRejected(() => EvidenceOriginalCancellationCheckpoint.Encode(projection));
    }

    private static void AssertRejected(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("cancellation-checkpoint-rejected", error.Message);
        Assert.Null(error.InnerException);
    }

    private static async Task CompleteCheckpointAsync(EvidenceOriginalCancellationCheckpoint checkpoint,
        EvidenceWorkerExecution execution, bool failWrite = false, bool failFlush = false)
    {
        using var caller = new CancellationTokenSource();
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        using var stream = new SignallingStream(failWrite, failFlush);
        var callback = execution.TrackOwnedWork(token => checkpoint.WaitAtAsync(
            EvidenceOriginalCancellationCheckpoint.SelectedPhase, stream, token, caller.Token), stage.Token);
        Assert.NotNull(callback);
        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(callback.IsCompleted);
        }
        finally
        {
            caller.Cancel();
            await callback.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    // Detached lifecycle procedure only. Timers never advance in these ordering controls; a regression
    // escapes through the existing fatal test seam rather than killing the test host. No native authority.
    private static EvidenceWorkerExecution Execution() => new(new MetadataSupervisor(), new FixedClock(),
        TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2),
        message => new EvidenceWorkerTestInterruptionException(message));

    private sealed class MetadataSupervisor : IEvidenceExecutionSupervisor
    {
        public bool IsArmed => true;
        public string RunId => "metadata-only/1";
        public void CloseAdmission() { }
        public ValueTask RequestStopAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
        public ValueTask WaitForOwnedExitAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FixedTimer();
        private sealed class FixedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SignallingStream(bool failWrite, bool failFlush) : MemoryStream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return failWrite ? ValueTask.FromException(new IOException("write-secret-canary"))
                : base.WriteAsync(buffer, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => failFlush
            ? Task.FromException(new IOException("flush-secret-canary")) : Task.CompletedTask;
    }

    private sealed class HeldWriteStream : Stream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { Entered.TrySetResult(); await Release.Task.ConfigureAwait(false); }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
