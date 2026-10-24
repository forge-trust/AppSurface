namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Ordinary byte-transfer procedure controls, never native report custody or authenticated coverage.</summary>
public sealed class SupervisionReportChunkTransferTests
{
    [Theory]
    [InlineData(0, 131073, 131072)]
    [InlineData(65536, 131073, 131072)]
    [InlineData(0, 1, 1)]
    public void OriginalFileBytesTransferAtExactOffsetsThroughBoundedPieces(int offset, int size, int maximum)
    {
        var path = Path.Combine(Path.GetTempPath(), "report-transfer-" + Guid.NewGuid().ToString("N"));
        var bytes = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(path, bytes);
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
            var events = new List<string>();
            var readRanges = new List<(long Position, int Count)>();
            var actual = SupervisionReportChunkTransfer.ReadValidated(size, offset, maximum,
                () => events.Add("check"), (position, count) =>
                {
                    events.Add("read"); readRanges.Add((position, count));
                    var piece = new byte[count];
                    Assert.Equal(count, RandomAccess.Read(handle, piece, position));
                    return piece;
                });
            var expected = bytes.AsSpan(offset, Math.Min(maximum, size - offset)).ToArray();
            Assert.Equal(expected, actual);
            Assert.All(readRanges, range => Assert.InRange(range.Count, 1, 65536));
            Assert.Equal(offset, readRanges[0].Position);
            Assert.Equal(expected.Length, readRanges.Sum(range => range.Count));
            for (var i = 1; i < readRanges.Count; i++)
                Assert.Equal(readRanges[i - 1].Position + readRanges[i - 1].Count, readRanges[i].Position);
            Assert.Equal(Enumerable.Range(0, readRanges.Count).SelectMany(_ => new[] { "check", "read" }).Append("check"), events);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(131072, 65536)]
    public void EmptyReportStillReadsOriginalEofOnceBeforeFinalCheck(int maximum, int requested)
    {
        var events = new List<string>();
        var bytes = SupervisionReportChunkTransfer.ReadValidated(0, 0, maximum,
            () => events.Add("check"), (offset, count) =>
            {
                events.Add("read"); Assert.Equal(0, offset); Assert.Equal(requested, count);
                return [];
            });
        Assert.Empty(bytes);
        Assert.Equal(new[] { "read", "check" }, events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AShortOriginalReadRejectsWithoutRetryOrLaterWorkChecks(int shortIndex)
    {
        var checks = 0; var reads = 0;
        var error = Assert.Throws<IOException>(() => SupervisionReportChunkTransfer.ReadValidated(131072, 0, 131072,
            () => checks++, (_, count) => new byte[reads++ == shortIndex ? count - 1 : count]));
        Assert.Equal("The producer report set is unavailable or changed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(shortIndex + 1, reads);
        Assert.Equal(reads, checks);
    }

    [Fact]
    public void ReaderFailureRemainsTheSameExceptionAndCannotDispatchTheNextPiece()
    {
        var original = new IOException("private-reader-canary");
        var reads = 0; var checks = 0;
        var error = Assert.Throws<IOException>(() => SupervisionReportChunkTransfer.ReadValidated(131072, 0, 131072,
            () => checks++, (_, _) => { reads++; throw original; }));
        Assert.Same(original, error);
        Assert.Equal(1, reads);
        Assert.Equal(1, checks);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void OriginalWorkCancellationBeforeBetweenOrAfterReadsReturnsNoBytes(int failingCheck, int expectedReads)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var reads = 0; var checks = 0;
        var error = Assert.Throws<OperationCanceledException>(() => SupervisionReportChunkTransfer.ReadValidated(131072, 0, 131072,
            () => { if (checks++ == failingCheck) cancellation.Token.ThrowIfCancellationRequested(); },
            (_, count) => { reads++; return new byte[count]; }));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(expectedReads, reads);
        Assert.Equal(failingCheck + 1, checks);
    }

    [Fact]
    public void EmptyReportFinalFailureIsNotReplacedByAnEofResult()
    {
        var original = new InvalidOperationException("private-final-check");
        var reads = 0;
        var error = Assert.Throws<InvalidOperationException>(() => SupervisionReportChunkTransfer.ReadValidated(0, 0, 1,
            () => throw original, (_, _) => { reads++; return []; }));
        Assert.Same(original, error);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(true, "checkWork")]
    [InlineData(false, "read")]
    public void MissingOriginalOperationRejectsBeforeAnyDispatch(bool missingCheck, string parameter)
    {
        var calls = 0;
        Action check = () => calls++;
        Func<long, int, byte[]> read = (_, _) => { calls++; return []; };
        var error = Assert.Throws<ArgumentNullException>(() => SupervisionReportChunkTransfer.ReadValidated(0, 0, 1,
            missingCheck ? null! : check, missingCheck ? read : null!));
        Assert.Equal(parameter, error.ParamName);
        Assert.Equal(0, calls);
    }
}
