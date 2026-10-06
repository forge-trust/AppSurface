using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Real stream and barrier controls for paired output accounting; no native ownership proof is supplied.</summary>
public sealed class SupervisionOutputCollectorTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CollectAsync_JoinsTwoEofsAndCopiesPrefixesWithoutClosingCallerStreams()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        using var stdout = new MemoryStream(bytes);
        using var stderr = new MemoryStream(new byte[] { 5, 6, 7 });
        var result = await new SupervisionOutputCollector(7, 2).CollectAsync(stdout, stderr, default);

        Assert.True(result.Successful);
        Assert.True(result.Stdout.EndOfStream);
        Assert.True(result.Stderr.EndOfStream);
        Assert.Equal(7L, result.ReceivedBytes);
        Assert.Equal(4L, result.Stdout.ReceivedBytes);
        Assert.Equal(3L, result.Stderr.ReceivedBytes);
        Assert.Equal(2L, result.Stdout.DiscardedBytes);
        Assert.Equal(1L, result.Stderr.DiscardedBytes);
        Assert.Equal(3L, result.DiscardedBytes);
        Assert.Equal(new byte[] { 1, 2 }, result.Stdout.Prefix.ToArray());
        Assert.Equal(new byte[] { 5, 6 }, result.Stderr.Prefix.ToArray());
        bytes[0] = 99;
        Assert.Equal((byte)1, result.Stdout.Prefix[0]);
        Assert.True(stdout.CanRead);
        Assert.True(stderr.CanRead);
    }

    [Fact]
    public async Task CollectAsync_CountsDiscardedBytesAcrossBuffersAndBothStreamsAtExactBoundary()
    {
        var first = Enumerable.Repeat((byte)11, 20_000).ToArray();
        var second = Enumerable.Repeat((byte)22, 12_001).ToArray();
        using var stdout = new MemoryStream(first);
        using var stderr = new MemoryStream(second);
        var result = await new SupervisionOutputCollector(first.Length + second.Length, 7)
            .CollectAsync(stdout, stderr, default);

        Assert.True(result.Successful);
        Assert.Equal(32_001L, result.ReceivedBytes);
        Assert.Equal(result.ReceivedBytes, result.Stdout.ReceivedBytes + result.Stderr.ReceivedBytes);
        Assert.Equal(first.Take(7), result.Stdout.Prefix);
        Assert.Equal(second.Take(7), result.Stderr.Prefix);
        Assert.Equal(31_987L, result.DiscardedBytes);
        Assert.False(result.QuotaExceeded);
    }

    [Fact]
    public async Task CollectAsync_DefaultsRetainOneMiBButChargeAllSixteenMiB()
    {
        var bytes = Enumerable.Repeat((byte)37, 16 * 1024 * 1024).ToArray();
        using var stdout = new MemoryStream(bytes);
        using var stderr = new MemoryStream();
        var result = await new SupervisionOutputCollector().CollectAsync(stdout, stderr, default);

        Assert.True(result.Successful);
        Assert.Equal(16L * 1024 * 1024, result.ReceivedBytes);
        Assert.Equal(1024 * 1024, result.Stdout.Prefix.Length);
        Assert.Equal(bytes.Take(1024 * 1024), result.Stdout.Prefix);
        Assert.Equal(15L * 1024 * 1024, result.DiscardedBytes);
        Assert.Empty(result.Stderr.Prefix);
    }

    [Fact]
    public async Task CollectAsync_OneSharedBudgetRejectsAnOverLimitChargeAndRetainsActualCount()
    {
        using var stdout = new GatedStream(new byte[] { 1, 2, 3 });
        using var stderr = new GatedStream(new byte[] { 4, 5, 6 });
        var collection = new SupervisionOutputCollector(5, 5).CollectAsync(stdout, stderr, default);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stdout.Release();
            await stdout.EofObserved.Task.WaitAsync(Guard);
            stderr.Release();
            var result = await collection.WaitAsync(Guard);
            Assert.False(result.Successful);
            Assert.True(result.QuotaExceeded);
            Assert.Equal(SupervisionOutputFailure.QuotaExceeded, result.Failure);
            Assert.Equal(6L, result.ReceivedBytes);
            Assert.Equal(6L, result.Stdout.ReceivedBytes + result.Stderr.ReceivedBytes);
            Assert.Equal(3, result.Stdout.Prefix.Length + result.Stderr.Prefix.Length);
            Assert.Equal(3L, result.DiscardedBytes);
        }
        finally
        {
            stdout.Release();
            stderr.Release();
            await collection.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CollectAsync_OverflowCancelsAndJoinsCooperativeSiblingWithoutCallingItEof()
    {
        using var stdout = new GatedStream(new byte[] { 1, 2, 3, 4, 5, 6 });
        using var stderr = new GatedStream();
        var collection = new SupervisionOutputCollector(5, 5).CollectAsync(stdout, stderr, default);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stdout.Release();
            await stderr.CancellationObserved.Task.WaitAsync(Guard);
            var result = await collection.WaitAsync(Guard);
            Assert.False(result.Successful);
            Assert.Equal(SupervisionOutputFailure.QuotaExceeded, result.Failure);
            Assert.Equal(6L, result.ReceivedBytes);
            Assert.Equal(SupervisionOutputFailure.Cancelled, result.Stderr.Failure);
            Assert.False(result.Stderr.EndOfStream);
            Assert.Empty(result.Stdout.Prefix);
        }
        finally
        {
            stdout.Release();
            stderr.Release();
            await collection.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CollectAsync_ReadFailureIsClosedAndCancelsOwnedSiblingWithoutExposingCanary()
    {
        using var stdout = new GatedStream(readFailure: true);
        using var stderr = new GatedStream();
        var collection = new SupervisionOutputCollector().CollectAsync(stdout, stderr, default);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stdout.Release();
            var result = await collection.WaitAsync(Guard);
            Assert.False(result.Successful);
            Assert.False(result.Stdout.EndOfStream);
            Assert.False(result.Stderr.EndOfStream);
            Assert.Equal(SupervisionOutputFailure.ReadFailed, result.Failure);
            Assert.Equal(SupervisionOutputFailure.Cancelled, result.Stderr.Failure);
            Assert.DoesNotContain("private-read-canary", result.ToString());
        }
        finally
        {
            stdout.Release();
            stderr.Release();
            await collection.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CollectAsync_CancellationDoesNotDetachAnIgnoringReadOrUpgradeItsLateEof()
    {
        using var cancellation = new CancellationTokenSource();
        using var stdout = new GatedStream(ignoreCancellation: true);
        using var stderr = new MemoryStream();
        var collection = new SupervisionOutputCollector().CollectAsync(stdout, stderr, cancellation.Token);
        try
        {
            await stdout.Entered.Task.WaitAsync(Guard);
            cancellation.Cancel();
            await stdout.CancellationObserved.Task.WaitAsync(Guard);
            Assert.False(collection.IsCompleted);
            stdout.Release();
            var result = await collection.WaitAsync(Guard);
            Assert.True(result.Stdout.EndOfStream);
            Assert.False(result.Successful);
            Assert.Equal(SupervisionOutputFailure.Cancelled, result.Failure);
        }
        finally
        {
            stdout.Release();
            await collection.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CollectAsync_ReadFailureStillJoinsIgnoringSiblingAndChargesItsInFlightBytes()
    {
        using var stdout = new GatedStream(readFailure: true);
        using var stderr = new GatedStream(new byte[] { 1, 2, 3, 4 }, ignoreCancellation: true);
        var collection = new SupervisionOutputCollector(3, 3).CollectAsync(stdout, stderr, default);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stdout.Release();
            await stderr.CancellationObserved.Task.WaitAsync(Guard);
            Assert.False(collection.IsCompleted);
            stderr.Release();
            var result = await collection.WaitAsync(Guard);
            Assert.Equal(SupervisionOutputFailure.ReadFailed, result.Failure);
            Assert.True(result.QuotaExceeded);
            Assert.False(result.Successful);
            Assert.Equal(4L, result.Stderr.ReceivedBytes);
            Assert.Equal(4L, result.ReceivedBytes);
            Assert.Empty(result.Stderr.Prefix);
        }
        finally
        {
            stdout.Release();
            stderr.Release();
            await collection.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CollectAsync_PreCancelledCallerPerformsNoReadsAndReportsNoEof()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stdout = new GatedStream();
        using var stderr = new GatedStream();
        var result = await new SupervisionOutputCollector().CollectAsync(stdout, stderr, cancellation.Token);

        Assert.False(result.Successful);
        Assert.Equal(SupervisionOutputFailure.Cancelled, result.Failure);
        Assert.False(stdout.Entered.Task.IsCompleted);
        Assert.False(stderr.Entered.Task.IsCompleted);
        Assert.False(result.Stdout.EndOfStream);
        Assert.False(result.Stderr.EndOfStream);
        Assert.Equal(0L, result.ReceivedBytes);
    }

    [Fact]
    public async Task CollectAsync_StopCallbackFailureCannotEscapeOrUpgradeTheFirstReadFailure()
    {
        using var stdout = new GatedStream(readFailure: true);
        using var stderr = new GatedStream(cancellationFailure: true);
        var collection = new SupervisionOutputCollector().CollectAsync(stdout, stderr, default);
        try
        {
            await Task.WhenAll(stdout.Entered.Task, stderr.Entered.Task).WaitAsync(Guard);
            stdout.Release();
            var result = await collection.WaitAsync(Guard);
            Assert.False(result.Successful);
            Assert.True(result.StopSignalFailed);
            Assert.Equal(SupervisionOutputFailure.ReadFailed, result.Failure);
            Assert.DoesNotContain("private-cancel-canary", result.ToString());
        }
        finally
        {
            stdout.Release();
            stderr.Release();
            await collection.WaitAsync(Guard);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(16 * 1024 * 1024 + 1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 1024 * 1024 + 1)]
    public void ConstructorRejectsLimitsOutsideProtectedBounds(long bytes, int prefix) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SupervisionOutputCollector(bytes, prefix));

    [Fact]
    public async Task CollectAsync_AllowsZeroRetentionButRejectsMissingSameOrUnreadableStreams()
    {
        var collector = new SupervisionOutputCollector(1, 0);
        using var stdout = new MemoryStream(new byte[] { 1 });
        using var stderr = new MemoryStream();
        var result = await collector.CollectAsync(stdout, stderr, default);
        Assert.True(result.Successful);
        Assert.Empty(result.Stdout.Prefix);
        await Assert.ThrowsAsync<ArgumentNullException>(() => collector.CollectAsync(null!, stderr, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => collector.CollectAsync(stdout, null!, default));
        await Assert.ThrowsAsync<ArgumentException>(() => collector.CollectAsync(stdout, stdout, default));
        using var unreadable = new MemoryStream();
        unreadable.Dispose();
        await Assert.ThrowsAsync<ArgumentException>(() => collector.CollectAsync(unreadable, stderr, default));
    }

    private sealed class GatedStream(
        byte[]? bytes = null,
        bool ignoreCancellation = false,
        bool readFailure = false,
        bool cancellationFailure = false) : Stream
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _read;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource EofObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _released.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                EofObserved.TrySetResult();
                return 0;
            }
            _read = true;
            // Install the wait first so the later observation callback runs before cancellation releases it.
            Task pendingRead = ignoreCancellation ? _released.Task : _released.Task.WaitAsync(cancellationToken);
            using var registration = cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult();
                if (cancellationFailure) throw new IOException("private-cancel-canary");
            });
            Entered.TrySetResult();
            await pendingRead.ConfigureAwait(false);

            if (readFailure) throw new IOException("private-read-canary");
            var value = bytes ?? [];
            value.AsMemory().CopyTo(buffer);
            return value.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Release();
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
