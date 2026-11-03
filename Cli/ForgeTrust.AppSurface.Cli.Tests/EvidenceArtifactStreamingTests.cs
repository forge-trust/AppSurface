using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceArtifactStreamingTests
{
    [Fact]
    public async Task StreamWrite_UndeclaredSlotCannotCreateOutputOrConsumeTheValidReservation()
    {
        using var directory = TempDirectory.Create();
        var writer = CreateWriter(directory.Path, 128);
        using var contents = new MemoryStream([1, 2, 3]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync("undeclared", "coverage/report.txt", contents).AsTask());

        Assert.Contains("not declared", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, contents.Position);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        var accepted = await writer.WriteAsync("report", "coverage/report.txt", contents);
        Assert.Equal(3, accepted.LengthBytes);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamWrite_ReadFailureBeforePersistencePreservesCallerPathsAndOriginalException(bool replaceParent)
    {
        using var directory = TempDirectory.Create();
        var writer = CreateWriter(directory.Path, 128);
        var prefix = Path.Join(directory.Path, "coverage");
        var destination = Path.Join(prefix, "report.txt");
        var failure = new IOException("original read failure");
        using var contents = new ActionReadStream(() =>
        {
            Assert.False(File.Exists(destination));
            Assert.False(Directory.Exists(prefix));
            Assert.Empty(writer.WrittenArtifacts);
            if (replaceParent)
            {
                File.WriteAllText(prefix, "replacement bytes");
            }
            else
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Join(destination, "caller.txt"), "replacement bytes");
            }
            throw failure;
        });

        var observed = await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync("report", "coverage/report.txt", contents).AsTask());

        Assert.Same(failure, observed);
        Assert.True(contents.CanRead);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Equal("replacement bytes", File.ReadAllText(replaceParent ? prefix : Path.Join(destination, "caller.txt")));
        if (replaceParent) File.Delete(prefix);
        else Directory.Delete(destination, recursive: true);
        var retry = await writer.WriteAsync("report", "coverage/retry.txt", new MemoryStream([42]));
        Assert.Equal(1, retry.LengthBytes);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task WriteAsync_Stream_ShouldSupportNonSeekableInputAndVerifyIncrementally()
    {
        using var directory = TempDirectory.Create();
        var writer = CreateWriter(directory.Path, 128);
        var contents = new NonSeekableStream("streamed evidence"u8.ToArray());

        var artifact = await writer.WriteAsync("report", "coverage/report.txt", contents);

        Assert.Equal(17, artifact.LengthBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("streamed evidence"u8.ToArray())).ToLowerInvariant(), artifact.Sha256);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task WriteAsync_Stream_ShouldAcceptExactSlotLimitAndRejectTheNextByte()
    {
        using var directory = TempDirectory.Create();
        var writer = CreateWriter(directory.Path, 4);

        var exact = await writer.WriteAsync("report", "coverage/exact.txt", new NonSeekableStream([1, 2, 3, 4]));

        Assert.Equal(4, exact.LengthBytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("summary", "coverage/over.txt", new NonSeekableStream([1, 2, 3, 4, 5])).AsTask());
        Assert.False(File.Exists(Path.Join(directory.Path, "coverage", "over.txt")));
    }

    [Fact]
    public async Task WriteAsync_Stream_ShouldDeletePartialOutputAndReleaseReservationOnReadError()
    {
        using var directory = TempDirectory.Create();
        var writer = CreateWriter(directory.Path, 128);
        var path = Path.Join(directory.Path, "coverage", "report.txt");

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync("report", "coverage/report.txt", new ThrowingStream("first attempt"u8.ToArray())).AsTask());

        Assert.False(File.Exists(path));
        var retry = await writer.WriteAsync("report", "coverage/report.txt", new NonSeekableStream("retry"u8.ToArray()));
        Assert.Equal(5, retry.LengthBytes);
    }

    [Fact]
    public async Task WriteAsync_Stream_ShouldDeletePartialOutputAndReleaseReservationOnCancellation()
    {
        using var directory = TempDirectory.Create();
        var writer = CreateWriter(directory.Path, 128);
        using var cancellation = new CancellationTokenSource();
        var contents = new CancellingStream("cancelled evidence"u8.ToArray(), cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync("report", "coverage/report.txt", contents, cancellation.Token).AsTask());

        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(File.Exists(Path.Join(directory.Path, "coverage", "report.txt")));
        await writer.WriteAsync("report", "coverage/report.txt", new NonSeekableStream("retry"u8.ToArray()));
    }

    [Fact]
    public async Task VerifyWrittenArtifactsAsync_ShouldDetectTamperingWithoutLoadingWholeFile()
    {
        using var directory = TempDirectory.Create();
        var writer = CreateWriter(directory.Path, 128);
        await writer.WriteAsync("report", "coverage/report.txt", new NonSeekableStream("original"u8.ToArray()));
        await File.WriteAllTextAsync(Path.Join(directory.Path, "coverage", "report.txt"), "tampered");

        Assert.False(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task StreamWrite_ShouldPreservePreexistingDestinationAndAllowFreshRetry()
    {
        using var directory = TempDirectory.Create();
        var writer = CreateWriter(directory.Path, 128);
        Directory.CreateDirectory(Path.Join(directory.Path, "coverage"));
        var destination = Path.Join(directory.Path, "coverage/report.txt");
        await File.WriteAllTextAsync(destination, "existing-owned-by-caller");

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync("report", "coverage/report.txt", new MemoryStream([1, 2])).AsTask());

        Assert.Equal("existing-owned-by-caller", await File.ReadAllTextAsync(destination));
        Assert.Empty(writer.WrittenArtifacts);
        var retry = await writer.WriteAsync("report", "coverage/fresh.txt", new MemoryStream([1, 2]));
        Assert.Equal(2, retry.LengthBytes);
    }

    [Fact]
    public async Task StreamWrite_ShouldEnforceAggregateBudgetAndReleaseItsFailedReservation()
    {
        using var directory = TempDirectory.Create();
        var writer = CreateWriter(directory.Path, long.MaxValue);
        using var generated = new GeneratedStream(EvidenceArtifactWriter.MaximumTotalArtifactBytes + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("report", "coverage/too-large.bin", generated).AsTask());

        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(File.Exists(Path.Join(directory.Path, "coverage/too-large.bin")));
        var retry = await writer.WriteAsync("report", "coverage/retry.bin", new MemoryStream([42]));
        Assert.Equal(1, retry.LengthBytes);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    private static EvidenceArtifactWriter CreateWriter(string rootPath, long maximumBytes) => new(
        new EvidenceProducerDeclaration(
            "streaming",
            "test",
            "1",
            [],
            [],
            [
                new EvidenceArtifactSlot("report", "coverage", "text/plain", false, maximumBytes),
                new EvidenceArtifactSlot("summary", "coverage", "text/plain", false, maximumBytes),
            ],
            1),
        rootPath);

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "appsurface-evidence-stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    private sealed class GeneratedStream(long remaining) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..count].Fill(42);
            remaining -= count;
            return ValueTask.FromResult(count);
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

    private sealed class ThrowingStream(byte[] bytes) : NonSeekableStream(bytes)
    {
        private bool _thrown;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_thrown)
            {
                throw new IOException("synthetic input failure");
            }

            _thrown = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation) : NonSeekableStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ActionReadStream(Action read) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            read();
            return ValueTask.FromResult(0);
        }
    }
}
