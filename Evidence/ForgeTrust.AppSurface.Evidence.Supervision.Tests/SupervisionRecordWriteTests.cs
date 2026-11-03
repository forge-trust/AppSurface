namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Actual stream I/O procedure controls; records remain detached bytes, never native evidence authority.</summary>
public sealed class SupervisionRecordWriteTests
{
    [Fact]
    public async Task ActualFileKeepsBinaryRecordsOrderAndOneLfEachWithoutClosingTheStream()
    {
        using var file = new FileStream(Path.Combine(Path.GetTempPath(), "issue779-records-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        await SupervisionRecordWrite.WriteLinesAsync(file, [new byte[] { 0, 255 }, Array.Empty<byte>(), new byte[] { 65 }], default);
        Assert.Equal(6, file.Length);
        file.Position = 0;
        var bytes = new byte[6];
        await file.ReadExactlyAsync(bytes);
        Assert.Equal(new byte[] { 0, 255, 10, 10, 65, 10 }, bytes);
        Assert.True(file.CanWrite);
        file.WriteByte(66);
    }

    [Fact]
    public async Task OriginalTokenAndOperationOrderReachEveryWriteAndTheSingleFlush()
    {
        using var token = new CancellationTokenSource();
        using var stream = new ControlledStream();
        await SupervisionRecordWrite.WriteLinesAsync(stream, [new byte[] { 1 }, new byte[] { 2, 3 }], token.Token);
        Assert.Equal(["write:01", "write:0A", "write:0203", "write:0A", "flush"], stream.Calls);
        Assert.All(stream.Tokens, actual => Assert.Equal(token.Token, actual));
        Assert.False(stream.Disposed);
    }

    [Fact]
    public async Task EmptyRecordSetStillFlushesOnceWithTheOriginalToken()
    {
        using var token = new CancellationTokenSource();
        using var stream = new ControlledStream();
        await SupervisionRecordWrite.WriteLinesAsync(stream, [], token.Token);
        Assert.Equal(["flush"], stream.Calls);
        Assert.Equal(token.Token, Assert.Single(stream.Tokens));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EachActualWriteOrFlushFailurePropagatesAndStopsLaterOperations(int failAt)
    {
        var expected = new IOException("private I/O failure");
        using var stream = new ControlledStream(failAt, expected);
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() =>
            SupervisionRecordWrite.WriteLinesAsync(stream, [new byte[] { 1 }, new byte[] { 2 }], default)));
        Assert.Equal(failAt + 1, stream.Calls.Count);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public async Task IgnoredCancellationCannotFinishTheWriterBeforeTheOriginalWriteJoins()
    {
        using var token = new CancellationTokenSource();
        using var stream = new ControlledStream(blockFirst: true);
        var writing = SupervisionRecordWrite.WriteLinesAsync(stream, [new byte[] { 1 }], token.Token);
        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            token.Cancel();
            Assert.False(writing.IsCompleted);
            Assert.Equal(["write:01"], stream.Calls);
        }
        finally
        {
            stream.Release.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(["write:01", "write:0A", "flush"], stream.Calls);
        Assert.All(stream.Tokens, actual => Assert.Equal(token.Token, actual));
    }

    [Fact]
    public async Task ActualStreamCancellationAndClosedStreamFailureRemainUnnormalized()
    {
        using var stream = new MemoryStream();
        using var token = new CancellationTokenSource();
        token.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SupervisionRecordWrite.WriteLinesAsync(stream, [new byte[] { 1 }], token.Token));
        Assert.Equal(token.Token, error.CancellationToken);
        Assert.Empty(stream.ToArray());
        stream.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            SupervisionRecordWrite.WriteLinesAsync(stream, [new byte[] { 1 }], default));
    }

    [Fact]
    public async Task NullArgumentsRejectBeforeAnyStreamIo()
    {
        using var stream = new ControlledStream();
        await Assert.ThrowsAsync<ArgumentNullException>(() => SupervisionRecordWrite.WriteLinesAsync(null!, [], default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => SupervisionRecordWrite.WriteLinesAsync(stream, null!, default));
        Assert.Empty(stream.Calls);
    }

    private sealed class ControlledStream(int failAt = -1, Exception? error = null, bool blockFirst = false) : Stream
    {
        internal List<string> Calls { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            At("write:" + Convert.ToHexString(buffer.Span), cancellationToken);
            if (blockFirst && Calls.Count == 1)
            { Entered.TrySetResult(); await Release.Task.ConfigureAwait(false); }
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        { At("flush", cancellationToken); return Task.CompletedTask; }
        private void At(string call, CancellationToken token)
        {
            Calls.Add(call); Tokens.Add(token);
            if (Calls.Count - 1 == failAt) throw error!;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
