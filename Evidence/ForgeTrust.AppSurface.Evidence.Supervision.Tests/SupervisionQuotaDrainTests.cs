using System.Collections.Concurrent;
using System.IO.Pipes;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable pipe controls for opt-in quota drainage; no process or native acceptance is asserted.</summary>
public sealed class SupervisionQuotaDrainTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task QuotaSignalsStopButBothActualPipesDrainAndChargeLateBytes()
    {
        using var stdout = PortableOutputPipe.Create();
        using var stderr = PortableOutputPipe.Create();
        using var ownerDrain = new CancellationTokenSource();
        using var budget = new SupervisionRunOutputBudget(receivedByteLimit: 2, drainOnQuotaExceeded: true);
        var quotaSignalled = Signal();
        using var stopRegistration = budget.StopToken.Register(() => quotaSignalled.TrySetResult());
        var collection = budget.CollectAsync(new(receivedByteLimit: 2, prefixByteLimit: 1),
            stdout.Reader, stderr.Reader, ownerDrain.Token);

        try
        {
            await Task.WhenAll(stdout.Reader.WaitForReadRequestAsync(1), stderr.Reader.WaitForReadRequestAsync(1)).WaitAsync(Guard);
            await stdout.WriteAsync([0x41]);
            await stdout.Reader.WaitForReadRequestAsync(2); // The first byte was charged before the next read began.
            await stdout.WriteAsync([0x42]);
            await stdout.Reader.WaitForReadRequestAsync(3); // The aggregate limit is now exactly reached.
            await stderr.WriteAsync([0x58]);
            await quotaSignalled.Task.WaitAsync(Guard);

            Assert.True(budget.StopToken.IsCancellationRequested);
            Assert.False(collection.IsCompleted);
            await stdout.WriteAsync([0x43, 0x44]);
            await stderr.WriteAsync([0x59]);

            stdout.CloseWriter();
            await stdout.Reader.EndOfStream.WaitAsync(Guard);
            Assert.False(collection.IsCompleted); // stderr still has a live writer, so its EOF is not fabricated.
            stderr.CloseWriter();

            var pair = await collection.WaitAsync(Guard);
            var ledger = await budget.CompleteAsync().WaitAsync(Guard);
            Assert.True(pair.Drained);
            Assert.False(pair.Successful);
            Assert.True(pair.QuotaExceeded);
            Assert.True(pair.Stdout.EndOfStream);
            Assert.True(pair.Stderr.EndOfStream);
            Assert.Equal(4, pair.Stdout.ReceivedBytes);
            Assert.Equal(2, pair.Stderr.ReceivedBytes);
            Assert.Equal(6, pair.ReceivedBytes);
            Assert.Equal(1, pair.Stdout.Prefix.Length);
            Assert.Equal(new byte[] { 0x41 }, pair.Stdout.Prefix.ToArray());
            Assert.Empty(pair.Stderr.Prefix);
            Assert.Equal(5, pair.DiscardedBytes);

            Assert.True(ledger.Drained);
            Assert.False(ledger.Successful);
            Assert.True(ledger.QuotaExceeded);
            Assert.Equal(1, ledger.RegisteredPairs);
            Assert.Equal(6, ledger.ReceivedBytes);
            Assert.Equal(pair.DiscardedBytes, ledger.Pairs.Sum(static item => item.DiscardedBytes));
        }
        finally
        {
            stdout.CloseWriter();
            stderr.CloseWriter();
            await collection.WaitAsync(Guard);
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task AggregateQuotaDrainsAndAccountsEveryRegisteredPair()
    {
        using var firstOut = PortableOutputPipe.Create();
        using var firstError = PortableOutputPipe.Create();
        using var secondOut = PortableOutputPipe.Create();
        using var secondError = PortableOutputPipe.Create();
        using var ownerDrain = new CancellationTokenSource();
        using var budget = new SupervisionRunOutputBudget(receivedByteLimit: 2, drainOnQuotaExceeded: true);
        var quotaSignalled = Signal();
        using var stopRegistration = budget.StopToken.Register(() => quotaSignalled.TrySetResult());
        var first = budget.CollectAsync(new(receivedByteLimit: 16, prefixByteLimit: 1),
            firstOut.Reader, firstError.Reader, ownerDrain.Token);
        var second = budget.CollectAsync(new(receivedByteLimit: 16, prefixByteLimit: 1),
            secondOut.Reader, secondError.Reader, ownerDrain.Token);

        try
        {
            await Task.WhenAll(firstOut.Reader.WaitForReadRequestAsync(1), firstError.Reader.WaitForReadRequestAsync(1),
                secondOut.Reader.WaitForReadRequestAsync(1), secondError.Reader.WaitForReadRequestAsync(1)).WaitAsync(Guard);
            await firstOut.WriteAsync([0x31]);
            await firstOut.Reader.WaitForReadRequestAsync(2);
            await firstOut.WriteAsync([0x32]);
            await firstOut.Reader.WaitForReadRequestAsync(3); // Two bytes charged, still within the run limit.
            await secondError.WriteAsync([0x33]);
            await quotaSignalled.Task.WaitAsync(Guard);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);

            await firstError.WriteAsync([0x34]);
            await secondOut.WriteAsync([0x35, 0x36]);
            firstOut.CloseWriter();
            firstError.CloseWriter();
            secondOut.CloseWriter();
            secondError.CloseWriter();

            var pairs = await Task.WhenAll(first, second).WaitAsync(Guard);
            var ledger = await budget.CompleteAsync().WaitAsync(Guard);
            Assert.Equal(2, ledger.RegisteredPairs);
            Assert.True(ledger.Drained);
            Assert.False(ledger.Successful);
            Assert.True(ledger.QuotaExceeded);
            Assert.Equal(6, ledger.ReceivedBytes);
            Assert.Equal(ledger.ReceivedBytes, pairs.Sum(static item => item.ReceivedBytes));
            Assert.All(pairs, static item =>
            {
                Assert.True(item.Drained);
                Assert.False(item.Successful);
                Assert.True(item.Stdout.EndOfStream);
                Assert.True(item.Stderr.EndOfStream);
            });
        }
        finally
        {
            firstOut.CloseWriter();
            firstError.CloseWriter();
            secondOut.CloseWriter();
            secondError.CloseWriter();
            await Task.WhenAll(first, second).WaitAsync(Guard);
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task OwnerDrainCancellationAfterQuotaDoesNotInventEitherEof()
    {
        using var stdout = PortableOutputPipe.Create();
        using var stderr = PortableOutputPipe.Create();
        using var ownerDrain = new CancellationTokenSource();
        using var budget = new SupervisionRunOutputBudget(receivedByteLimit: 1, drainOnQuotaExceeded: true);
        var quotaSignalled = Signal();
        using var stopRegistration = budget.StopToken.Register(() => quotaSignalled.TrySetResult());
        var collection = budget.CollectAsync(new(receivedByteLimit: 1, prefixByteLimit: 1),
            stdout.Reader, stderr.Reader, ownerDrain.Token);

        try
        {
            await Task.WhenAll(stdout.Reader.WaitForReadRequestAsync(1), stderr.Reader.WaitForReadRequestAsync(1)).WaitAsync(Guard);
            await stdout.WriteAsync([0x51]);
            await stdout.Reader.WaitForReadRequestAsync(2);
            await stdout.WriteAsync([0x52]);
            await quotaSignalled.Task.WaitAsync(Guard);
            await stdout.Reader.WaitForReadRequestAsync(3);
            Assert.False(collection.IsCompleted);

            ownerDrain.Cancel();
            var pair = await collection.WaitAsync(Guard);
            var ledger = await budget.CompleteAsync().WaitAsync(Guard);
            Assert.True(pair.QuotaExceeded);
            Assert.False(pair.Drained);
            Assert.False(pair.Successful);
            Assert.False(pair.Stdout.EndOfStream);
            Assert.False(pair.Stderr.EndOfStream);
            Assert.False(ledger.Drained);
            Assert.False(ledger.Successful);
        }
        finally
        {
            ownerDrain.Cancel();
            stdout.CloseWriter();
            stderr.CloseWriter();
            await collection.WaitAsync(Guard);
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task ReadFailureAfterQuotaKeepsDrainedFalse()
    {
        using var stdout = PortableOutputPipe.Create(failOnReadRequest: 3);
        using var stderr = PortableOutputPipe.Create();
        using var ownerDrain = new CancellationTokenSource();
        using var budget = new SupervisionRunOutputBudget(receivedByteLimit: 1, drainOnQuotaExceeded: true);
        var quotaSignalled = Signal();
        using var stopRegistration = budget.StopToken.Register(() => quotaSignalled.TrySetResult());
        var collection = budget.CollectAsync(new(receivedByteLimit: 1, prefixByteLimit: 1),
            stdout.Reader, stderr.Reader, ownerDrain.Token);

        try
        {
            await Task.WhenAll(stdout.Reader.WaitForReadRequestAsync(1), stderr.Reader.WaitForReadRequestAsync(1)).WaitAsync(Guard);
            stderr.CloseWriter();
            await stderr.Reader.EndOfStream.WaitAsync(Guard);
            await stdout.WriteAsync([0x61]);
            await stdout.Reader.WaitForReadRequestAsync(2);
            await stdout.WriteAsync([0x62]);
            await Task.WhenAll(quotaSignalled.Task, stdout.Reader.ReadFailure).WaitAsync(Guard);
            stdout.CloseWriter();

            var pair = await collection.WaitAsync(Guard);
            var ledger = await budget.CompleteAsync().WaitAsync(Guard);
            Assert.True(pair.QuotaExceeded);
            Assert.False(pair.Drained);
            Assert.False(pair.Successful);
            Assert.False(pair.Stdout.EndOfStream);
            Assert.Equal(SupervisionOutputFailure.ReadFailed, pair.Stdout.Failure);
            Assert.True(pair.Stderr.EndOfStream);
            Assert.False(ledger.Drained);
            Assert.False(ledger.Successful);
        }
        finally
        {
            stdout.CloseWriter();
            stderr.CloseWriter();
            await collection.WaitAsync(Guard);
            await budget.CompleteAsync().WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task LaterCleanupFailureKeepsQuotaFirstAndInvalidatesDrainPredicate()
    {
        using var budget = new SupervisionRunOutputBudget(receivedByteLimit: 1, drainOnQuotaExceeded: true);
        using var stdout = new MemoryStream([0x71, 0x72]);
        using var stderr = new MemoryStream();
        var pair = await budget.CollectAsync(new(receivedByteLimit: 1, prefixByteLimit: 1), stdout, stderr)
            .WaitAsync(Guard);
        Assert.True(pair.Drained);
        Assert.False(pair.Successful);

        budget.Fail(SupervisionRunOutputFailure.InvalidSequence);
        var ledger = await budget.CompleteAsync().WaitAsync(Guard);
        Assert.Equal(SupervisionRunOutputFailure.QuotaExceeded, ledger.Failure);
        Assert.True(ledger.DrainFailed);
        Assert.False(ledger.Drained);
        Assert.False(ledger.Successful);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PortableOutputPipe : IDisposable
    {
        private readonly AnonymousPipeServerStream _server;
        internal OneBytePipeReader Reader { get; }
        internal AnonymousPipeClientStream Writer { get; }

        private PortableOutputPipe(AnonymousPipeServerStream server, AnonymousPipeClientStream writer, int failOnReadRequest)
        {
            _server = server;
            Writer = writer;
            Reader = new(server, failOnReadRequest);
        }

        internal static PortableOutputPipe Create(int failOnReadRequest = 0)
        {
            var server = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
            try
            {
                var writer = new AnonymousPipeClientStream(PipeDirection.Out, server.ClientSafePipeHandle);
                return new(server, writer, failOnReadRequest);
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }

        internal Task WriteAsync(byte[] bytes) => Writer.WriteAsync(bytes.AsMemory()).AsTask();
        internal void CloseWriter() => Writer.Dispose();

        public void Dispose()
        {
            Writer.Dispose();
            Reader.Dispose();
            _server.Dispose();
        }
    }

    private sealed class OneBytePipeReader(Stream inner, int failOnReadRequest = 0) : Stream
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _requests = new();
        private readonly TaskCompletionSource _eof = Signal();
        private readonly TaskCompletionSource _readFailure = Signal();
        private int _readRequest;

        internal Task EndOfStream => _eof.Task;
        internal Task ReadFailure => _readFailure.Task;

        internal Task WaitForReadRequestAsync(int ordinal) => _requests
            .GetOrAdd(ordinal, static _ => Signal()).Task.WaitAsync(Guard);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref _readRequest);
            _requests.GetOrAdd(ordinal, static _ => Signal()).TrySetResult();
            if (ordinal == failOnReadRequest)
            {
                _readFailure.TrySetResult();
                throw new IOException("controlled pipe read failure");
            }

            var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken).ConfigureAwait(false);
            if (count == 0) _eof.TrySetResult();
            return count;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
