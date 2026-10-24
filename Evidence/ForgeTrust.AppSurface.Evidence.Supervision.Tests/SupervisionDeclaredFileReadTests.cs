using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Tests the detached declared-file read loop with ordinary caller-owned file handles.</summary>
public sealed class SupervisionDeclaredFileReadTests
{
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(4096, 17)]
    [InlineData(65536, 257)]
    [InlineData(65537, 31)]
    public void ReadsExactBytesWithSmallBorrowedBuffersAndExpectedHash(int length, int bufferSize)
    {
        var expected = Bytes(length);
        using var fixture = FileFixture.Create(expected);
        var received = new List<byte>();
        var beforeReads = 0;
        var beforeEof = 0;
        var chunks = new List<int>();

        SupervisionDeclaredFileRead.Read(fixture.Handle, (ulong)expected.Length, new byte[bufferSize],
            () => beforeReads++, memory => { chunks.Add(memory.Length); received.AddRange(memory.ToArray()); }, () => beforeEof++, Invalid);

        Assert.Equal(expected, received);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)),
            Convert.ToHexStringLower(SHA256.HashData(received.ToArray())));
        Assert.Equal(chunks.Count, beforeReads);
        Assert.Equal(expected.Length, chunks.Sum());
        Assert.All(chunks, size => Assert.InRange(size, 1, bufferSize));
        Assert.Equal(1, beforeEof);
        Assert.False(fixture.Handle.IsClosed);
    }

    [Fact]
    public void ZeroLengthStillChecksEofOnceWithoutReceivingOrReadingData()
    {
        using var fixture = FileFixture.Create([]);
        var receives = 0;
        var beforeReads = 0;
        var beforeEof = 0;

        SupervisionDeclaredFileRead.Read(fixture.Handle, 0, new byte[1],
            () => beforeReads++, _ => receives++, () => beforeEof++, Invalid);

        Assert.Equal(0, beforeReads);
        Assert.Equal(0, receives);
        Assert.Equal(1, beforeEof);
    }

    [Fact]
    public void UsesPositionalReadsAndLeavesCallerStreamPositionUnchanged()
    {
        var expected = Bytes(4096);
        using var fixture = FileFixture.Create(expected);
        fixture.Stream.Position = 19;
        var received = new List<byte>();

        SupervisionDeclaredFileRead.Read(fixture.Handle, (ulong)expected.Length, new byte[23],
            static () => { }, memory => received.AddRange(memory.ToArray()), static () => { }, Invalid);

        Assert.Equal(19, fixture.Stream.Position);
        Assert.Equal(expected, received);
    }

    [Fact]
    public void TruncationUsesTheCallerExceptionInstanceAndDoesNotReceiveAfterFailure()
    {
        var expected = Bytes(4096);
        using var fixture = FileFixture.Create(expected);
        fixture.Resize(7);
        var failure = LinuxProtectedDeployment.InvalidDeployment();
        var received = new List<byte>();

        var actual = Assert.Throws<EvidenceAdmissionException>(() => SupervisionDeclaredFileRead.Read(
            fixture.Handle, (ulong)expected.Length, new byte[31], static () => { },
            memory => { Assert.False(memory.IsEmpty); received.AddRange(memory.ToArray()); }, static () => { }, () => failure));

        Assert.Same(failure, actual);
        Assert.Equal(expected[..7], received);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void GrowthUsesTheCallerExceptionInstanceAfterAllDeclaredBytes(int length)
    {
        var expected = Bytes(length);
        using var fixture = FileFixture.Create(expected);
        var failure = LinuxProtectedDeployment.InvalidDeployment();
        var received = new List<byte>();
        var eofCalls = 0;
        var actual = Assert.Throws<EvidenceAdmissionException>(() => SupervisionDeclaredFileRead.Read(
            fixture.Handle, (ulong)expected.Length, new byte[7], static () => { },
            memory => received.AddRange(memory.ToArray()), () => { eofCalls++; fixture.Append([0x7f]); }, () => failure));

        Assert.Same(failure, actual);
        Assert.Equal(expected, received);
        Assert.Equal(1, eofCalls);
    }

    [Fact]
    public void SynchronousShrinkBeforeTheNextReadRejectsWithoutAnEofCallback()
    {
        var expected = Bytes(64);
        using var fixture = FileFixture.Create(expected);
        var beforeReads = 0;
        var beforeEof = 0;

        var failure = new InvalidOperationException("changed during check");
        var actual = Assert.Throws<InvalidOperationException>(() => SupervisionDeclaredFileRead.Read(
            fixture.Handle, (ulong)expected.Length, new byte[16],
            () =>
            {
                if (++beforeReads == 2) fixture.Resize(16);
            }, static _ => { },
            () =>
            {
                beforeEof++;
            }, () => failure));

        Assert.Same(failure, actual);
        Assert.Equal(2, beforeReads);
        Assert.Equal(0, beforeEof);
    }

    [Theory]
    [InlineData("before-read")]
    [InlineData("receive")]
    [InlineData("before-eof")]
    public void CallbackExceptionsPropagateByIdentityAndStopFurtherCallbacks(string phase)
    {
        using var fixture = FileFixture.Create(Bytes(3));
        var failure = new OperationCanceledException(phase);
        var receives = 0;
        var beforeEof = 0;

        var actual = Assert.Throws<OperationCanceledException>(() => SupervisionDeclaredFileRead.Read(
            fixture.Handle, 3, new byte[3],
            () => { if (phase == "before-read") throw failure; },
            _ =>
            {
                receives++;
                if (phase == "receive") throw failure;
            },
            () =>
            {
                beforeEof++;
                if (phase == "before-eof") throw failure;
            }, Invalid));

        Assert.Same(failure, actual);
        Assert.Equal(phase == "before-read" ? 0 : 1, receives);
        Assert.Equal(phase == "before-eof" ? 1 : 0, beforeEof);
    }

    [Fact]
    public void CancellationAndExpiredDeadlineRemainCallerChecksBeforeIo()
    {
        using var fixture = FileFixture.Create(Bytes(1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledReads = 0;
        var closedHandle = fixture.Handle;
        fixture.Stream.Dispose(); // An actual read would now fail with a different exception.
        Assert.Throws<OperationCanceledException>(() => SupervisionDeclaredFileRead.Read(
            closedHandle, 1, new byte[1],
            () =>
            {
                cancelledReads++;
                cancelled.Token.ThrowIfCancellationRequested();
            }, _ => { }, static () => { }, Invalid));
        Assert.Equal(1, cancelledReads);

        var expired = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.Zero);
        Assert.Throws<EvidenceAdmissionException>(() => SupervisionDeclaredFileRead.Read(
            closedHandle, 1, new byte[1], () => expired.Check(default), _ => { }, static () => { }, Invalid));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("buffer")]
    [InlineData("empty-buffer")]
    [InlineData("expectedLength")]
    public void NullOrInvalidArgumentsAreRejectedBeforeIo(string argument)
    {
        using var fixture = FileFixture.Create(Bytes(1));
        var closedHandle = fixture.Handle;
        fixture.Stream.Dispose(); // Argument rejection must precede IO on this actual closed file.
        var ex = Assert.ThrowsAny<ArgumentException>(() => SupervisionDeclaredFileRead.Read(
            argument == "file" ? null! : closedHandle,
            argument == "expectedLength" ? (ulong)long.MaxValue + 1 : 1,
            argument == "buffer" ? null! : argument == "empty-buffer" ? [] : [1],
            static () => { }, static _ => { }, static () => { }, Invalid));
        Assert.Equal(argument == "empty-buffer" ? "buffer" : argument, ex.ParamName);
    }

    [Fact]
    public void MissingCallbacksAndRejectFactoryAreRejectedBeforeIo()
    {
        using var fixture = FileFixture.Create(Bytes(1));
        Assert.Throws<ArgumentNullException>(() => SupervisionDeclaredFileRead.Read(
            fixture.Handle, 1, [1], null!, static _ => { }, static () => { }, Invalid));
        Assert.Throws<ArgumentNullException>(() => SupervisionDeclaredFileRead.Read(
            fixture.Handle, 1, [1], static () => { }, null!, static () => { }, Invalid));
        Assert.Throws<ArgumentNullException>(() => SupervisionDeclaredFileRead.Read(
            fixture.Handle, 1, [1], static () => { }, static _ => { }, null!, Invalid));
        Assert.Throws<ArgumentNullException>(() => SupervisionDeclaredFileRead.Read(
            fixture.Handle, 1, [1], static () => { }, static _ => { }, static () => { }, null!));
        Assert.Throws<ArgumentNullException>(() => SupervisionDeclaredFileRead.Read(
            fixture.Handle, 1, null!, static () => { }, static _ => { }, static () => { }, Invalid));
    }

    private static byte[] Bytes(int length)
    {
        var value = new byte[length];
        for (var i = 0; i < value.Length; i++) value[i] = (byte)(i * 31 + 7);
        return value;
    }

    private static Exception Invalid() => new InvalidOperationException("declared file mismatch");

    private sealed class FileFixture : IDisposable
    {
        private FileFixture(string path, FileStream stream)
        {
            Path = path;
            Stream = stream;
        }

        private string Path { get; }
        internal FileStream Stream { get; }
        internal SafeFileHandle Handle => Stream.SafeFileHandle;

        internal static FileFixture Create(byte[] bytes)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "declared-read-" + Guid.NewGuid().ToString("N"));
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(bytes);
            stream.Flush(true);
            stream.Position = 0;
            return new FileFixture(path, stream);
        }

        internal void Resize(long length)
        {
            Stream.SetLength(length);
            Stream.Flush(true);
        }

        internal void Append(byte[] bytes)
        {
            Stream.Position = Stream.Length;
            Stream.Write(bytes);
            Stream.Flush(true);
        }

        public void Dispose()
        {
            Stream.Dispose();
            try { File.Delete(Path); } catch (FileNotFoundException) { }
        }
    }
}
