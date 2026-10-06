using System.IO.Pipes;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable actual-pipe and counted ownership controls; no Linux factory, D-Bus or native acceptance is exercised.</summary>
public sealed class LinuxOutputPipesTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RealAnonymousPipesObserveBothEofsOnlyAfterLocalWritersClose()
    {
        var stdout = PortablePipe();
        var stderr = PortablePipe();
        var stdoutReadHandle = stdout.Read.SafePipeHandle;
        var stdoutWriteHandle = stdout.Write.SafePipeHandle;
        var stderrReadHandle = stderr.Read.SafePipeHandle;
        var stderrWriteHandle = stderr.Write.SafePipeHandle;
        await using var owner = new SupervisionOutputPipeOwnership(stdout.Read, stderr.Read, stdout.Write, stderr.Write);
        var collection = owner.BeginCollectAsync(default, receivedByteLimit: 128 * 1024, prefixByteLimit: 4);
        Assert.Same(collection, owner.JoinAsync());
        await stdout.Write.WriteAsync(Encoding.UTF8.GetBytes("output"));
        await stderr.Write.WriteAsync(Encoding.UTF8.GetBytes("err"));
        await stdout.Write.FlushAsync();
        await stderr.Write.FlushAsync();
        Assert.False(collection.IsCompleted); // At least one writer remains open, so the two EOFs cannot exist yet.
        owner.CloseWriteCopies();
        var receipt = await collection.WaitAsync(Guard);
        Assert.True(receipt.Successful);
        Assert.Equal(9, receipt.ReceivedBytes);
        Assert.Equal(2, receipt.DiscardedBytes);
        Assert.Equal("outp", Encoding.UTF8.GetString(receipt.Stdout.Prefix.AsSpan()));
        Assert.Equal("err", Encoding.UTF8.GetString(receipt.Stderr.Prefix.AsSpan()));
        Assert.True(stdoutWriteHandle.IsClosed);
        Assert.True(stderrWriteHandle.IsClosed);
        Assert.False(stdoutReadHandle.IsClosed);
        Assert.False(stderrReadHandle.IsClosed);
        await owner.DisposeAsync();
        Assert.True(stdoutReadHandle.IsClosed);
        Assert.True(stderrReadHandle.IsClosed);
    }

    [Fact]
    public async Task OneCollectionTaskIsRegisteredBeforeEitherPumpCanObserveEntry()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        await using var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var task = owner.BeginCollectAsync(default);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            Assert.Same(task, owner.JoinAsync());
            AssertClosed(() => owner.BeginCollectAsync(default));
            Assert.Equal(1, stdout.ReadCount);
            Assert.Equal(1, stderr.ReadCount);
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            await task.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CancellationIgnoringReadKeepsCollectionAndDisposalOwnedUntilActualJoin()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        using var stop = new CancellationTokenSource();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var task = owner.BeginCollectAsync(stop.Token);
        Task? dispose = null;
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stop.Cancel();
            dispose = owner.DisposeAsync().AsTask();
            Assert.Same(dispose, owner.DisposeAsync().AsTask());
            Assert.False(task.IsCompleted);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, stdout.DisposeCount);
            Assert.Equal(0, stderr.DisposeCount);
            stdout.ReleaseEof();
            await stdout.Returned.Task.WaitAsync(Guard);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, stdout.DisposeCount); // The other original pump is still alive.
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            await task.WaitAsync(Guard);
            await (dispose ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
        var receipt = await task;
        Assert.False(receipt.Successful);
        Assert.Equal(SupervisionOutputFailure.Cancelled, receipt.Failure);
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
    }

    [Fact]
    public async Task WriteCloseFailureAttemptsBothAndStillJoinsBeforeReadClosure()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var first = new CountedWriteCloser(fail: true);
        var second = new CountedWriteCloser();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, first, second);
        var task = owner.BeginCollectAsync(default);
        Task? disposal = null;
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            var error = Assert.Throws<SupervisionOutputPipeException>(() => owner.CloseWriteCopies());
            Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, error.Failure);
            Assert.Equal(1, first.CloseCount);
            Assert.Equal(1, second.CloseCount);
            AssertClosed(() => owner.CloseWriteCopies());
            disposal = owner.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, stdout.DisposeCount);
            Assert.Equal(0, stderr.DisposeCount);
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            await task.WaitAsync(Guard);
            var error = await Assert.ThrowsAsync<SupervisionOutputPipeException>(async () =>
                await (disposal ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard));
            Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, error.Failure);
        }
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, second.CloseCount);
        await Assert.ThrowsAsync<SupervisionOutputPipeException>(() => owner.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ReadCloseFailureCannotTurnConcurrentDisposalIntoSuccess()
    {
        var stdout = new CloseBarrierStream(fail: true);
        var stderr = new GatedReadStream();
        var writes = Writes();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var collection = owner.BeginCollectAsync(default);
        Task? dispose = null;
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            Assert.True((await collection.WaitAsync(Guard)).Successful);
            dispose = owner.DisposeAsync().AsTask();
            await stdout.CloseEntered.Task.WaitAsync(Guard);
            var concurrent = owner.DisposeAsync().AsTask();
            Assert.Same(dispose, concurrent);
            Assert.False(concurrent.IsCompleted);
            Assert.Equal(0, stderr.DisposeCount);
            AssertClosed(() => owner.BeginCollectAsync(default));
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            stdout.ReleaseClose();
            var error = await Assert.ThrowsAsync<SupervisionOutputPipeException>(async () =>
                await (dispose ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard));
            Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, error.Failure);
        }
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
        var repeated = await Assert.ThrowsAsync<SupervisionOutputPipeException>(() => owner.DisposeAsync().AsTask());
        Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, repeated.Failure);
        Assert.DoesNotContain("private-canary", repeated.ToString());
    }

    [Fact]
    public async Task ReadFailureRetainsNegativeReceiptAndDisposalStillClosesEveryLocalResource()
    {
        var stdout = new GatedReadStream(failRead: true);
        var stderr = new GatedReadStream();
        var writes = Writes();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        var task = owner.BeginCollectAsync(default);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stdout.ReleaseEof();
            await stdout.Returned.Task.WaitAsync(Guard);
            Assert.False(task.IsCompleted);
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            await task.WaitAsync(Guard);
            await owner.DisposeAsync();
        }
        var receipt = await task;
        Assert.False(receipt.Successful);
        Assert.False(receipt.Stdout.EndOfStream);
        Assert.Equal(SupervisionOutputFailure.ReadFailed, receipt.Failure);
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
        Assert.Equal(1, writes.First.CloseCount);
        Assert.Equal(1, writes.Second.CloseCount);
    }

    [Fact]
    public async Task LoweredSharedBudgetCountsOverflowWithoutDetachedSiblingOrRaisedLimits()
    {
        var writes = Writes();
        await using var owner = new SupervisionOutputPipeOwnership(new MemoryStream([1, 2, 3]),
            new MemoryStream(), writes.First, writes.Second);
        var receipt = await owner.BeginCollectAsync(default, receivedByteLimit: 2, prefixByteLimit: 1).WaitAsync(Guard);
        Assert.True(receipt.QuotaExceeded);
        Assert.Equal(3, receipt.ReceivedBytes);
        Assert.Equal(3, receipt.DiscardedBytes);
        Assert.Equal(SupervisionOutputFailure.QuotaExceeded, receipt.Failure);
        Assert.False(receipt.Successful);
        Assert.Same(owner.JoinAsync(), owner.JoinAsync());
    }

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(-1L, 1)]
    [InlineData(16_777_217L, 1)]
    [InlineData(1L, -1)]
    [InlineData(1L, 1_048_577)]
    public async Task InvalidLimitsDoNotDispatchOrConsumeTheOnlyValidAttempt(long total, int prefix)
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        await using var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = owner.BeginCollectAsync(default, total, prefix); });
            Assert.Equal(0, stdout.ReadCount);
            Assert.Equal(0, stderr.ReadCount);
            AssertClosed(() => owner.JoinAsync());
            var task = owner.BeginCollectAsync(default, 1, 0);
            stdout.ReleaseEof();
            stderr.ReleaseEof();
            Assert.True((await task.WaitAsync(Guard)).Successful);
        }
        finally
        {
            stdout.ReleaseEof();
            stderr.ReleaseEof();
        }
    }

    [Fact]
    public async Task DisposalBeforeCollectionClosesUnstartedResourcesAndPermanentlyBlocksDispatch()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        owner.RequireWritesOpen();
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        AssertClosed(() => owner.BeginCollectAsync(default));
        AssertClosed(() => owner.JoinAsync());
        AssertClosed(() => owner.RequireWritesOpen());
        Assert.Equal(0, stdout.ReadCount);
        Assert.Equal(0, stderr.ReadCount);
        Assert.Equal(1, stdout.DisposeCount);
        Assert.Equal(1, stderr.DisposeCount);
        Assert.Equal(1, writes.First.CloseCount);
        Assert.Equal(1, writes.Second.CloseCount);
    }

    [Fact]
    public async Task LocalWriteCloseIsIdempotentAndDoesNotCloseReadsOrRequireAnInventedStartReceipt()
    {
        var stdout = new GatedReadStream();
        var stderr = new GatedReadStream();
        var writes = Writes();
        await using var owner = new SupervisionOutputPipeOwnership(stdout, stderr, writes.First, writes.Second);
        owner.CloseWriteCopies();
        owner.CloseWriteCopies();
        AssertClosed(() => owner.RequireWritesOpen());
        Assert.Equal(0, stdout.DisposeCount);
        Assert.Equal(0, stderr.DisposeCount);
        Assert.Equal(1, writes.First.CloseCount);
        Assert.Equal(1, writes.Second.CloseCount);
    }

    [Fact]
    public void AliasedReadsOrClosersCannotConstructAnOwnedPair()
    {
        using var stream = new MemoryStream();
        using var other = new MemoryStream();
        var writes = Writes();
        AssertClosed(() => new SupervisionOutputPipeOwnership(stream, stream, writes.First, writes.Second));
        AssertClosed(() => new SupervisionOutputPipeOwnership(stream, other, writes.First, writes.First));
        AssertClosed(() => new SupervisionOutputPipeOwnership(stream, other, stream, writes.First));
        Assert.Equal(0, writes.First.CloseCount);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForAnActualWriteCloseAndRetainsItsFailure()
    {
        var first = new WriteBarrierCloser();
        var second = new CountedWriteCloser();
        var owner = new SupervisionOutputPipeOwnership(new MemoryStream(), new MemoryStream(), first, second);
        var close = Task.Run(() => Record.Exception(() => owner.CloseWriteCopies()));
        Task? disposal = null;
        try
        {
            await first.Entered.Task.WaitAsync(Guard);
            disposal = owner.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(close.IsCompleted);
        }
        finally
        {
            first.Release();
            try
            {
                var error = Assert.IsType<SupervisionOutputPipeException>(await close.WaitAsync(Guard));
                Assert.Equal(SupervisionOutputPipeFailure.CloseFailed, error.Failure);
                await Assert.ThrowsAsync<SupervisionOutputPipeException>(async () =>
                    await (disposal ?? owner.DisposeAsync().AsTask()).WaitAsync(Guard));
            }
            finally { first.CloseBarrier(); }
        }
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, second.CloseCount);
    }

    private static (AnonymousPipeServerStream Read, AnonymousPipeClientStream Write) PortablePipe()
    {
        var reader = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        // Same-process use shares one SafePipeHandle object, not two owners of the same raw descriptor.
        // Closing the client write handle produces EOF; the server's read handle stays open until joined.
        return (reader, new AnonymousPipeClientStream(PipeDirection.Out, reader.ClientSafePipeHandle));
    }

    private static (CountedWriteCloser First, CountedWriteCloser Second) Writes() => (new(), new());

    private static void AssertClosed(Action action)
    {
        var error = Assert.Throws<SupervisionOutputPipeException>(action);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-canary", error.ToString());
    }

    private sealed class CountedWriteCloser(bool fail = false) : IDisposable
    {
        internal int CloseCount { get; private set; }
        public void Dispose()
        {
            CloseCount++;
            if (fail) throw new IOException("private-canary");
        }
    }

    private sealed class WriteBarrierCloser : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CloseCount { get; private set; }
        internal void Release() => _release.Set();
        internal void CloseBarrier() => _release.Dispose();
        public void Dispose()
        {
            CloseCount++;
            Entered.TrySetResult();
            if (!_release.Wait(Guard)) throw new IOException("private-canary");
            throw new IOException("private-canary");
        }
    }

    private class GatedReadStream(bool failRead = false) : Stream
    {
        private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal void ReleaseEof() => _eof.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            Entered.TrySetResult();
            await _eof.Task.ConfigureAwait(false); // Deliberately ignores cancellation; actual pump ownership is tested.
            Returned.TrySetResult();
            if (failRead) throw new IOException("private-canary");
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CloseBarrierStream(bool fail) : GatedReadStream
    {
        private readonly TaskCompletionSource _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseClose() => _close.TrySetResult();
        public override async ValueTask DisposeAsync()
        {
            CloseEntered.TrySetResult();
            await _close.Task.ConfigureAwait(false);
            Dispose();
            if (fail) throw new IOException("private-canary");
        }
    }
}
