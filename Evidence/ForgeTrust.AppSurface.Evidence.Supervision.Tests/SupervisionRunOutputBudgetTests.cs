using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual stream/barrier controls for aggregate accounting, without process or admission assertions.</summary>
public sealed class SupervisionRunOutputBudgetTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CompleteAsync_ReconcilesAllPairsAndDiscardedBytesAtExactLimit()
    {
        using var budget = new SupervisionRunOutputBudget(7);
        using var first = new MemoryStream([1, 2, 3, 4]);
        using var second = new MemoryStream([5, 6, 7]);
        using var empty1 = new MemoryStream();
        using var empty2 = new MemoryStream();
        var one = budget.CollectAsync(new(7, 1), first, empty1);
        var two = budget.CollectAsync(new(7, 1), second, empty2);
        await Task.WhenAll(one, two).WaitAsync(Guard);
        var complete = budget.CompleteAsync();
        Assert.Same(complete, budget.CompleteAsync());
        var result = await complete.WaitAsync(Guard);
        Assert.True(result.Successful);
        Assert.Equal(7, result.ReceivedBytes);
        Assert.Equal(2, result.RegisteredPairs);
        Assert.Equal(5, result.Pairs.Sum(static pair => pair.DiscardedBytes));
        Assert.All(result.Pairs, static pair => Assert.True(pair.Stdout.EndOfStream && pair.Stderr.EndOfStream));
        Assert.True(first.CanRead);
        Assert.True(second.CanRead);
    }

    [Fact]
    public async Task CompleteAsync_LaterSiblingOverflowCannotReuseEarlierSuccessfulPairAsRunSuccess()
    {
        using var budget = new SupervisionRunOutputBudget(7);
        using var first = new ControlledStream([1, 2, 3, 4]);
        using var second = new ControlledStream([5, 6, 7, 8], ignoreCancellation: true);
        using var empty1 = new MemoryStream();
        using var empty2 = new MemoryStream();
        var one = budget.CollectAsync(new(7, 7), first, empty1);
        var two = budget.CollectAsync(new(7, 7), second, empty2);
        try
        {
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(Guard);
            first.Release();
            Assert.True((await one.WaitAsync(Guard)).Successful);
            second.Release();
            await two.WaitAsync(Guard);
            var final = await budget.CompleteAsync().WaitAsync(Guard);
            Assert.False(final.Successful);
            Assert.Equal(8, final.ReceivedBytes);
            Assert.True(final.QuotaExceeded);
            Assert.Equal(SupervisionRunOutputFailure.QuotaExceeded, final.Failure);
            Assert.True(final.Pairs[0].Successful);
            Assert.False(final.Pairs[1].Successful);
        }
        finally
        {
            first.Release(); second.Release();
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CompleteAsync_OverflowJoinsIgnoringReadAndCountsItsLateDiscardedBytes()
    {
        using var budget = new SupervisionRunOutputBudget(5);
        using var first = new ControlledStream([1, 2, 3, 4, 5, 6]);
        using var second = new ControlledStream([7, 8, 9, 10], ignoreCancellation: true);
        using var empty1 = new MemoryStream();
        using var empty2 = new MemoryStream();
        var one = budget.CollectAsync(new(16, 16), first, empty1);
        var two = budget.CollectAsync(new(16, 16), second, empty2);
        try
        {
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(Guard);
            first.Release();
            await one.WaitAsync(Guard);
            await second.CancellationObserved.Task.WaitAsync(Guard);
            var complete = budget.CompleteAsync();
            Assert.False(complete.IsCompleted);
            Assert.Throws<InvalidOperationException>(budget.Dispose);
            second.Release();
            await two.WaitAsync(Guard);
            var result = await complete.WaitAsync(Guard);
            Assert.Equal(10, result.ReceivedBytes);
            Assert.Equal(10, result.Pairs.Sum(static pair => pair.DiscardedBytes));
            Assert.False(result.Successful);
            Assert.Equal(SupervisionRunOutputFailure.QuotaExceeded, result.Failure);
        }
        finally
        {
            first.Release(); second.Release();
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CompleteAsync_ReadFailureRemainsFirstWhenLateBytesAlsoOverflow()
    {
        using var budget = new SupervisionRunOutputBudget(3);
        using var first = new ControlledStream([], throwOnRead: true);
        using var second = new ControlledStream([1, 2, 3, 4], ignoreCancellation: true);
        using var empty1 = new MemoryStream();
        using var empty2 = new MemoryStream();
        var one = budget.CollectAsync(new(8, 8), first, empty1);
        var two = budget.CollectAsync(new(8, 8), second, empty2);
        try
        {
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(Guard);
            first.Release();
            await one.WaitAsync(Guard);
            await second.CancellationObserved.Task.WaitAsync(Guard);
            second.Release();
            await two.WaitAsync(Guard);
            var result = await budget.CompleteAsync().WaitAsync(Guard);
            Assert.Equal(SupervisionRunOutputFailure.PairFailed, result.Failure);
            Assert.True(result.QuotaExceeded);
            Assert.Equal(4, result.ReceivedBytes);
            Assert.False(result.Successful);
            Assert.DoesNotContain("private-read-canary", result.ToString());
        }
        finally
        {
            first.Release(); second.Release();
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CompleteAsync_RetainsSharedCancellationCallbackFailureAndStillJoinsLateReads()
    {
        using var budget = new SupervisionRunOutputBudget(5);
        using var first = new ControlledStream([1, 2, 3, 4, 5, 6]);
        using var second = new ControlledStream([7, 8], ignoreCancellation: true, throwOnCancellation: true);
        using var empty1 = new MemoryStream();
        using var empty2 = new MemoryStream();
        var one = budget.CollectAsync(new(16, 16), first, empty1);
        var two = budget.CollectAsync(new(16, 16), second, empty2);
        try
        {
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(Guard);
            first.Release();
            await one.WaitAsync(Guard);
            await second.CancellationObserved.Task.WaitAsync(Guard);
            var final = budget.CompleteAsync();
            Assert.False(final.IsCompleted);
            second.Release();
            await two.WaitAsync(Guard);
            var receipt = await final.WaitAsync(Guard);
            Assert.False(receipt.Successful);
            Assert.True(receipt.StopSignalFailed);
            Assert.True(receipt.QuotaExceeded);
            Assert.Equal(8, receipt.ReceivedBytes);
            Assert.DoesNotContain("private-stop-canary", receipt.ToString());
        }
        finally
        {
            first.Release(); second.Release();
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollectAsync_RejectsDuplicateCollectorOrStreamBeforeSecondDispatch(bool reuseCollector)
    {
        using var budget = new SupervisionRunOutputBudget();
        using var first = new ControlledStream([]);
        using var empty = new MemoryStream();
        using var rejected = new MemoryStream([1, 2, 3]);
        using var rejectedError = new MemoryStream([4]);
        var collector = new SupervisionOutputCollector();
        var accepted = budget.CollectAsync(collector, first, empty);
        try
        {
            await first.Entered.Task.WaitAsync(Guard);
            Assert.Throws<InvalidOperationException>(() => { _ = budget.CollectAsync(
                reuseCollector ? collector : new(), reuseCollector ? rejected : first, rejectedError); });
            Assert.Equal(0, rejected.Position);
            Assert.Equal(0, rejectedError.Position);
            await accepted.WaitAsync(Guard);
            var result = await budget.CompleteAsync().WaitAsync(Guard);
            Assert.Equal(1, result.RegisteredPairs);
            Assert.Equal(SupervisionRunOutputFailure.InvalidSequence, result.Failure);
            Assert.False(result.Successful);
        }
        finally
        {
            first.Release();
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CompleteAsync_ClosesAdmissionBeforeJoiningOriginalRead()
    {
        using var budget = new SupervisionRunOutputBudget();
        using var first = new ControlledStream([], ignoreCancellation: true);
        using var empty = new MemoryStream();
        using var rejected = new MemoryStream([1]);
        using var rejectedError = new MemoryStream();
        var task = budget.CollectAsync(new(), first, empty);
        try
        {
            await first.Entered.Task.WaitAsync(Guard);
            var final = budget.CompleteAsync();
            Assert.False(final.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = budget.CollectAsync(new(), rejected, rejectedError); });
            Assert.False(final.IsCompleted);
            Assert.Equal(0, rejected.Position);
            first.Release();
            await task.WaitAsync(Guard);
            Assert.False((await final.WaitAsync(Guard)).Successful);
        }
        finally
        {
            first.Release();
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CollectAsync_CancelledCallerNeverReadsAndCannotCompleteSuccessfully()
    {
        using var budget = new SupervisionRunOutputBudget();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var first = new MemoryStream([1]);
        using var second = new MemoryStream([2]);
        await budget.CollectAsync(new(), first, second, cancellation.Token).WaitAsync(Guard);
        var result = await budget.CompleteAsync().WaitAsync(Guard);
        Assert.False(result.Successful);
        Assert.Equal(0, result.ReceivedBytes);
        Assert.Equal(0, first.Position);
        Assert.Equal(0, second.Position);
    }

    [Fact]
    public async Task CollectAsync_EnforcesBoundedRegistrationAndDoesNotReadRejectedStreams()
    {
        using var budget = new SupervisionRunOutputBudget();
        var streams = new List<MemoryStream>();
        try
        {
            for (var index = 0; index < SupervisionWorkRegistry.MaximumActiveWorkloads; index++)
            {
                var first = new MemoryStream(); var second = new MemoryStream();
                streams.Add(first); streams.Add(second);
                await budget.CollectAsync(new(), first, second).WaitAsync(Guard);
            }
            using var rejected = new MemoryStream([1]);
            using var empty = new MemoryStream();
            Assert.Throws<InvalidOperationException>(() => { _ = budget.CollectAsync(new(), rejected, empty); });
            Assert.Equal(0, rejected.Position);
            var result = await budget.CompleteAsync().WaitAsync(Guard);
            Assert.Equal(64, result.RegisteredPairs);
            Assert.False(result.Successful);
        }
        finally
        {
            await budget.CompleteAsync().WaitAsync(Guard);
            foreach (var stream in streams) stream.Dispose();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16L * 1024 * 1024 + 1)]
    public void Constructor_RejectsLimitsOutsideExistingContract(long limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SupervisionRunOutputBudget(limit));

    private sealed class ControlledStream(byte[] bytes, bool ignoreCancellation = false,
        bool throwOnRead = false, bool throwOnCancellation = false) : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _read;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) return 0;
            using var registration = cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult();
                if (throwOnCancellation) throw new IOException("private-stop-canary");
            });
            Entered.TrySetResult();
            if (ignoreCancellation) await _release.Task.ConfigureAwait(false);
            else await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (throwOnRead) throw new IOException("private-read-canary");
            bytes.AsMemory().CopyTo(buffer);
            _read = true;
            return bytes.Length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
