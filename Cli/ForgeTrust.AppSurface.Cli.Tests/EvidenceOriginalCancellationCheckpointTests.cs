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
