using System.Text;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable counted framing controls only; none authenticate SO_PEERCRED or construct a Linux connection.</summary>
public sealed class LinuxControlConnectionTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly byte[] Ready = Encoding.UTF8.GetBytes("{\"op\":\"ready\"}\n");

    [Fact]
    public async Task ValidFrameUsesExistingCodecAndAllowsExactlyOneReply()
    {
        using var stream = new ScriptedStream(Ready);
        using var framing = new SupervisionControlLineFraming(stream);
        Assert.IsType<EvidenceReadyControlRequest>(await framing.ReadRequestAsync(default));
        await framing.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), default);
        Assert.Equal("{\"ok\":true}\n", Encoding.UTF8.GetString(stream.Written.ToArray()));
        Assert.Equal(Ready.Length, stream.ReadBytes);
        Assert.Equal(1, stream.DisposeCount);
        var replay = await Assert.ThrowsAsync<ControlLineException>(() =>
            framing.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), default));
        Assert.Equal(ControlLineFailure.InvalidSequence, replay.Failure);
        Assert.Equal(1, stream.WriteCalls);
    }

    [Fact]
    public async Task FragmentedFrameCountsEveryByteAndHandlesSplitLf()
    {
        using var stream = new ScriptedStream(Ready, readChunk: 1);
        using var framing = new SupervisionControlLineFraming(stream);
        Assert.IsType<EvidenceReadyControlRequest>(await framing.ReadRequestAsync(default));
        Assert.Equal(Ready.Length, stream.ReadBytes);
        Assert.Equal(Ready.Length, stream.ReadCalls);
    }

    [Fact]
    public async Task ExactRequestLimitIncludesWhitespaceAndLfWithoutReadingBeyondIt()
    {
        var bytes = new byte[SupervisionControlLineFraming.MaximumRequestLineBytes];
        Array.Fill(bytes, (byte)' ');
        Ready.CopyTo(bytes, bytes.Length - Ready.Length);
        using var stream = new ScriptedStream(bytes);
        using var framing = new SupervisionControlLineFraming(stream);
        Assert.IsType<EvidenceReadyControlRequest>(await framing.ReadRequestAsync(default));
        Assert.Equal(bytes.Length, stream.ReadBytes);
        Assert.True(stream.MaximumReadCapacity <= 4096);
    }

    [Fact]
    public async Task OversizeRequestRejectsAtCapWithoutScanningRemainingInput()
    {
        var bytes = Enumerable.Repeat((byte)' ', SupervisionControlLineFraming.MaximumRequestLineBytes + 7).ToArray();
        bytes[^1] = (byte)'\n';
        using var stream = new ScriptedStream(bytes);
        using var framing = new SupervisionControlLineFraming(stream);
        var error = await Assert.ThrowsAsync<ControlLineException>(() => framing.ReadRequestAsync(default));
        Assert.Equal(ControlLineFailure.InvalidFrame, error.Failure);
        Assert.Equal(SupervisionControlLineFraming.MaximumRequestLineBytes, stream.ReadBytes);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("{\"op\":\"ready\"}")]
    [InlineData("{\"op\":\"ready\"}\r\n")]
    [InlineData("{\"op\":\"ready\"}\nextra")]
    [InlineData("{\"op\":\"ready\"}\n{\"op\":\"stop\"}\n")]
    public async Task MissingEmptyCrLfAndTrailingFramesRejectBeforeDispatch(string input)
    {
        using var stream = new ScriptedStream(Encoding.UTF8.GetBytes(input));
        using var framing = new SupervisionControlLineFraming(stream);
        var error = await Assert.ThrowsAsync<ControlLineException>(() => framing.ReadRequestAsync(default));
        Assert.Equal(ControlLineFailure.InvalidFrame, error.Failure);
        Assert.Equal(0, stream.WriteCalls);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory]
    [InlineData("{\"op\":\"private-canary\"}\n")]
    [InlineData("{\"op\":\"ready\",\"OP\":\"ready\"}\n")]
    [InlineData("[]\n")]
    public async Task InvalidCodecInputReturnsOnlyClosedFailure(string input)
    {
        using var stream = new ScriptedStream(Encoding.UTF8.GetBytes(input));
        using var framing = new SupervisionControlLineFraming(stream);
        var error = await Assert.ThrowsAsync<ControlLineException>(() => framing.ReadRequestAsync(default));
        Assert.Equal(ControlLineFailure.InvalidJson, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-canary", error.ToString());
    }

    [Fact]
    public async Task ConcurrentRequestReplayCannotStartAnotherRead()
    {
        using var stream = new ScriptedStream(Ready, gateRead: true);
        using var framing = new SupervisionControlLineFraming(stream);
        var first = framing.ReadRequestAsync(default);
        try
        {
            await stream.Entered.Task.WaitAsync(Guard);
            var error = await Assert.ThrowsAsync<ControlLineException>(() => framing.ReadRequestAsync(default));
            Assert.Equal(ControlLineFailure.InvalidSequence, error.Failure);
            Assert.Equal(1, stream.ReadCalls);
            Assert.False(first.IsCompleted);
            stream.Release();
            await Assert.ThrowsAsync<ControlLineException>(() => first.WaitAsync(Guard));
        }
        finally
        {
            stream.Release();
            try { await first.WaitAsync(Guard); } catch (ControlLineException) { }
        }
    }

    [Fact]
    public async Task ResponseBeforeRequestRejectsWithoutWriting()
    {
        using var stream = new ScriptedStream(Ready);
        using var framing = new SupervisionControlLineFraming(stream);
        var error = await Assert.ThrowsAsync<ControlLineException>(() =>
            framing.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), default));
        Assert.Equal(ControlLineFailure.InvalidSequence, error.Failure);
        Assert.Equal(0, stream.WriteCalls);
        Assert.Equal(0, stream.ReadCalls);
    }

    [Fact]
    public async Task ExactReplyLimitAcceptsAndNextByteRejectsBeforeWrite()
    {
        var reply = Enumerable.Repeat((byte)' ', SupervisionControlLineFraming.MaximumResponseLineBytes - 1).ToArray();
        Encoding.UTF8.GetBytes("{\"ok\":true}").CopyTo(reply, 0);
        using var stream = new ScriptedStream(Ready);
        using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        await framing.WriteResponseAsync(reply, default);
        Assert.Equal((long)SupervisionControlLineFraming.MaximumResponseLineBytes, stream.Written.Length);
        Assert.Equal((byte)'\n', stream.Written.ToArray()[^1]);
        using var tooLargeStream = new ScriptedStream(Ready);
        using var tooLarge = new SupervisionControlLineFraming(tooLargeStream);
        await tooLarge.ReadRequestAsync(default);
        await Assert.ThrowsAsync<ControlLineException>(() => tooLarge.WriteResponseAsync(new byte[reply.Length + 1], default));
        Assert.Equal(0, tooLargeStream.WriteCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("private-canary")]
    [InlineData("{\"ok\":true}\n")]
    public async Task InvalidReplyNeverWritesSuppliedBytes(string json)
    {
        using var stream = new ScriptedStream(Ready);
        using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        var error = await Assert.ThrowsAsync<ControlLineException>(() =>
            framing.WriteResponseAsync(Encoding.UTF8.GetBytes(json), default));
        Assert.DoesNotContain("private-canary", error.ToString());
        Assert.Equal(0, stream.WriteCalls);
    }

    [Fact]
    public async Task CancellationDisposesButStillJoinsIgnoringRead()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ScriptedStream(Ready, gateRead: true);
        using var framing = new SupervisionControlLineFraming(stream);
        var read = framing.ReadRequestAsync(cancellation.Token);
        try
        {
            await stream.Entered.Task.WaitAsync(Guard);
            cancellation.Cancel();
            Assert.Equal(1, stream.DisposeCount);
            Assert.False(read.IsCompleted);
            stream.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Guard));
            Assert.Equal(1, stream.DisposeCount);
        }
        finally
        {
            stream.Release();
            try { await read.WaitAsync(Guard); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CancellationDisposesButStillJoinsIgnoringWriteAndSnapshotsReply()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ScriptedStream(Ready, gateWrite: true);
        using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var write = framing.WriteResponseAsync(bytes, cancellation.Token);
        try
        {
            await stream.Entered.Task.WaitAsync(Guard);
            Array.Fill(bytes, (byte)'x');
            cancellation.Cancel();
            Assert.False(write.IsCompleted);
            Assert.Equal(1, stream.DisposeCount);
            stream.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Guard));
            Assert.Equal("{\"ok\":true}\n", Encoding.UTF8.GetString(stream.Written.ToArray()));
        }
        finally
        {
            stream.Release();
            try { await write.WaitAsync(Guard); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ReadAndWriteErrorsAreSanitizedAndDoNotBecomeEofOrSuccess()
    {
        using var readStream = new ScriptedStream(Ready, failRead: true);
        using var readFraming = new SupervisionControlLineFraming(readStream);
        var readError = await Assert.ThrowsAsync<ControlLineException>(() => readFraming.ReadRequestAsync(default));
        Assert.Equal(ControlLineFailure.IoFailed, readError.Failure);
        Assert.DoesNotContain("private-io-canary", readError.ToString());
        using var writeStream = new ScriptedStream(Ready, failWrite: true);
        using var writeFraming = new SupervisionControlLineFraming(writeStream);
        await writeFraming.ReadRequestAsync(default);
        var writeError = await Assert.ThrowsAsync<ControlLineException>(() =>
            writeFraming.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), default));
        Assert.Equal(ControlLineFailure.IoFailed, writeError.Failure);
        Assert.Null(writeError.InnerException);
        Assert.Equal(1, writeStream.DisposeCount);
    }

    [Fact]
    public async Task PreCancellationHasNoIoAndActualCloseErrorRejectsCompletedReply()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new ScriptedStream(Ready);
        using var framing = new SupervisionControlLineFraming(stream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => framing.ReadRequestAsync(cancellation.Token));
        Assert.Equal(0, stream.ReadCalls);
        using var closeStream = new ScriptedStream(Ready, failClose: true);
        using var closeFraming = new SupervisionControlLineFraming(closeStream);
        await closeFraming.ReadRequestAsync(default);
        var error = await Assert.ThrowsAsync<ControlLineException>(() =>
            closeFraming.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), default));
        Assert.Equal(ControlLineFailure.Closed, error.Failure);
        Assert.Equal(1, closeStream.DisposeCount);
    }

    [Fact]
    public async Task CancellationStillJoinsReadThatFaultsAfterActualDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ScriptedStream(Ready, gateRead: true, failRead: true);
        using var framing = new SupervisionControlLineFraming(stream);
        var read = framing.ReadRequestAsync(cancellation.Token);
        try
        {
            await stream.Entered.Task.WaitAsync(Guard);
            cancellation.Cancel();
            Assert.Equal(1, stream.DisposeCount);
            Assert.False(read.IsCompleted);
            stream.Release();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Guard));
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("private-io-canary", error.ToString());
        }
        finally
        {
            stream.Release();
            try { await read.WaitAsync(Guard); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task InvalidResponseUtf8CannotReachTheOwnedWrite()
    {
        using var stream = new ScriptedStream(Ready);
        using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        var error = await Assert.ThrowsAsync<ControlLineException>(() =>
            framing.WriteResponseAsync(new byte[] { (byte)'{', (byte)'"', 0xff, (byte)'"', (byte)':', (byte)'0', (byte)'}' }, default));
        Assert.Equal(ControlLineFailure.InvalidJson, error.Failure);
        Assert.Equal(0, stream.WriteCalls);
    }

    [Fact]
    public async Task ResponseAndAsyncDisposalJoinConcurrentCloseBeforeReportingItsFailure()
    {
        using var stream = new CloseBarrierStream();
        using var framing = new SupervisionControlLineFraming(stream);
        await framing.ReadRequestAsync(default);
        var closer = Task.Run(framing.Dispose);
        Task? response = null;
        Task? finalClose = null;
        try
        {
            await stream.CloseEntered.Task.WaitAsync(Guard);
            response = framing.WriteResponseAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), default);
            finalClose = framing.DisposeAsync().AsTask();
            // Both calls reached an already-started close synchronously; neither may publish its outcome yet.
            Assert.False(response.IsCompleted);
            Assert.False(finalClose.IsCompleted);
            stream.ReleaseClose();
            await closer.WaitAsync(Guard);
            var responseError = await Assert.ThrowsAsync<ControlLineException>(() => response.WaitAsync(Guard));
            var closeError = await Assert.ThrowsAsync<ControlLineException>(() => finalClose.WaitAsync(Guard));
            Assert.Equal(ControlLineFailure.Closed, responseError.Failure);
            Assert.Equal(ControlLineFailure.Closed, closeError.Failure);
            Assert.DoesNotContain("private-close-canary", responseError.ToString());
            Assert.Equal(1, stream.CloseCalls);
            Assert.False(stream.CloseTimedOut);
        }
        finally
        {
            stream.ReleaseClose();
            await closer.WaitAsync(Guard);
            if (response is not null) try { await response.WaitAsync(Guard); } catch (ControlLineException) { }
            if (finalClose is not null) try { await finalClose.WaitAsync(Guard); } catch (ControlLineException) { }
        }
    }

    [Fact]
    public async Task CancelledReadJoinsBothOriginalOperationAndConcurrentClose()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CloseBarrierStream(gateRead: true);
        using var framing = new SupervisionControlLineFraming(stream);
        var read = framing.ReadRequestAsync(cancellation.Token);
        Task? closer = null;
        try
        {
            await stream.ReadEntered.Task.WaitAsync(Guard);
            closer = Task.Run(framing.Dispose);
            await stream.CloseEntered.Task.WaitAsync(Guard);
            cancellation.Cancel();
            Assert.False(read.IsCompleted);
            stream.ReleaseRead();
            Assert.True(stream.ReadFinished);
            Assert.False(read.IsCompleted);
            stream.ReleaseClose();
            await closer.WaitAsync(Guard);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Guard));
            Assert.Equal(1, stream.CloseCalls);
            Assert.False(stream.CloseTimedOut);
        }
        finally
        {
            stream.ReleaseRead();
            stream.ReleaseClose();
            if (closer is not null) await closer.WaitAsync(Guard);
            try { await read.WaitAsync(Guard); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task AsyncDisposalJoinsSuccessfulActualCloseAndReplayDoesNotCloseAgain()
    {
        using var stream = new CloseBarrierStream(failClose: false);
        using var framing = new SupervisionControlLineFraming(stream);
        var closer = Task.Run(framing.Dispose);
        try
        {
            await stream.CloseEntered.Task.WaitAsync(Guard);
            var joined = framing.DisposeAsync().AsTask();
            Assert.False(joined.IsCompleted);
            stream.ReleaseClose();
            await Task.WhenAll(closer, joined).WaitAsync(Guard);
            await framing.DisposeAsync();
            framing.Dispose();
            Assert.Equal(1, stream.CloseCalls);
            Assert.False(stream.CloseTimedOut);
        }
        finally
        {
            stream.ReleaseClose();
            await closer.WaitAsync(Guard);
        }
    }

    private sealed class CloseBarrierStream(bool gateRead = false, bool failClose = true) : Stream
    {
        private readonly MemoryStream _input = new(Ready, writable: false);
        private readonly ManualResetEventSlim _releaseClose = new(false);
        // Inline continuations deliberately make ReleaseRead drain the completed original read before returning.
        private readonly TaskCompletionSource _releaseRead = new();
        private int _closeCalls;
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ReadFinished { get; private set; }
        internal bool CloseTimedOut { get; private set; }
        internal int CloseCalls => Volatile.Read(ref _closeCalls);
        internal void ReleaseRead() => _releaseRead.TrySetResult();
        internal void ReleaseClose() => _releaseClose.Set();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult();
            if (gateRead) await _releaseRead.Task.ConfigureAwait(false);
            var count = _input.Read(buffer.Span);
            ReadFinished = true;
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.CompareExchange(ref _closeCalls, 1, 0) == 0)
            {
                CloseEntered.TrySetResult();
                CloseTimedOut = !_releaseClose.Wait(Guard);
                _input.Dispose();
                if (CloseTimedOut || failClose) throw new IOException("private-close-canary");
            }
            base.Dispose(disposing);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ScriptedStream(
        byte[] input, int readChunk = 4096, bool gateRead = false, bool gateWrite = false,
        bool failRead = false, bool failWrite = false, bool failClose = false) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal MemoryStream Written { get; } = new();
        internal int ReadBytes { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        internal int DisposeCount { get; private set; }
        internal int MaximumReadCapacity { get; private set; }
        internal void Release() => _released.TrySetResult();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            MaximumReadCapacity = Math.Max(MaximumReadCapacity, buffer.Length);
            if (gateRead)
            {
                Entered.TrySetResult();
                await _released.Task.ConfigureAwait(false);
            }
            if (failRead) throw new IOException("private-io-canary");
            var count = _input.Read(buffer.Span[..Math.Min(buffer.Length, readChunk)]);
            ReadBytes += count;
            return count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (gateWrite)
            {
                Entered.TrySetResult();
                await _released.Task.ConfigureAwait(false);
            }
            if (failWrite) throw new IOException("private-io-canary");
            Written.Write(buffer.Span);
        }
        protected override void Dispose(bool disposing)
        {
            // Deliberately do not release a gated operation: the controls must observe that it remains owned.
            if (disposing && DisposeCount == 0)
            {
                DisposeCount++;
                if (failClose) throw new IOException("private-close-canary");
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
