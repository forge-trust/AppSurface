using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReceiptStreamContractTests
{
    private const long PayloadBound = 256L * 1024 * 1024;

    [Fact]
    public void BoundedReceiptDocumentRejectsGrowthAfterAnInitiallyValidLength()
    {
        using var stream = new ObservedStream([1, 2, 3, 4, 5, 6], reportedLength: 2);

        var actual = BoundedEvidenceDocument.Read(stream, 3);

        Assert.Null(actual);
        Assert.Equal(1, stream.LengthRequests);
        Assert.Equal(4, stream.MaximumReadRequest);
        Assert.Equal(4, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void BoundedReceiptDocumentAcceptsAnExactLimitAndPreservesCallerOwnership()
    {
        byte[] expected = [1, 2, 3];
        using var stream = new ObservedStream(expected, reportedLength: expected.Length);

        var actual = BoundedEvidenceDocument.Read(stream, expected.Length);

        Assert.Equal(expected, actual);
        Assert.Equal(2, stream.ReadCalls);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void CoverageMetadataRejectsGrowthWithTheInventoryLimitReason()
    {
        var limit = CoverageEvidenceInventory.MaximumMetadataBytes;
        using var stream = new ObservedStream(new byte[limit + 2], reportedLength: limit);

        var failure = Assert.Throws<CoverageEvidenceInventoryException>(
            () => CoverageReportSet.ReadReceiptMetadata(stream, CancellationToken.None));

        Assert.Equal("inventory-metadata-limit", failure.Reason);
        Assert.Equal(limit + 1, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150_000)]
    public async Task ExactReceiptStreamProducesItsDigestUsingBoundedReadsWithoutDisposingTheCaller(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        using var stream = new ObservedStream(bytes, length);

        var result = await EvidenceReceiptPayload.HashStreamAsync(stream, length, CancellationToken.None);

        Assert.Equal(length, result.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Digest);
        Assert.InRange(stream.MaximumReadRequest, 1, 64 * 1024);
        Assert.True(stream.ReadCalls >= (length + 65_535) / 65_536 + 1);
        Assert.Equal(length, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ExactReceiptStreamCopiesToOptionalDestinationWhileHashing()
    {
        byte[] bytes = [1, 2, 3, 4, 5];
        using var source = new ObservedStream(bytes, bytes.Length);
        using var destination = new MemoryStream();

        var result = await EvidenceReceiptPayload.HashStreamAsync(
            source, bytes.Length, CancellationToken.None, destination);

        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Digest);
        Assert.Equal(bytes.Length, result.Length);
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task ExtraByteIsDetectedWithoutCopyingItToStaging()
    {
        using var source = new ObservedStream([1, 2, 3], reportedLength: 2);
        using var destination = new MemoryStream();

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EvidenceReceiptPayload.HashStreamAsync(source, 2, CancellationToken.None, destination));

        Assert.Contains("grew during read", failure.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2 }, destination.ToArray());
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, "length differs")]
    [InlineData(PayloadBound + 1, 0, 0, 0, "length differs")]
    [InlineData(1, 2, 2, 0, "length differs")]
    [InlineData(1, 1, 2, 2, "grew during read")]
    [InlineData(2, 2, 1, 2, "changed during read")]
    public async Task MalformedReceiptLengthCannotProduceAPartialDigest(long expected, long reported, int actual,
        int readCalls, string diagnostic)
    {
        using var stream = new ObservedStream(new byte[actual], reported);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EvidenceReceiptPayload.HashStreamAsync(stream, expected, CancellationToken.None));

        Assert.Contains(diagnostic, failure.Message, StringComparison.Ordinal);
        Assert.Equal(readCalls, stream.ReadCalls);
        Assert.Equal(expected is < 0 or > PayloadBound ? 0 : 1, stream.LengthRequests);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CancellationAtReadBoundariesPreservesTheCallerTokenAndStreamOwnership(
        bool cancelBeforeRead, bool cancelOnEofRead)
    {
        using var caller = new CancellationTokenSource();
        var observedReads = 0;
        using var stream = new ObservedStream(new byte[] { 1, 2, 3 }, 3, afterRead: () =>
        {
            if (!cancelBeforeRead && (cancelOnEofRead ? observedReads == 1 : observedReads == 0)) caller.Cancel();
            observedReads++;
        });
        if (cancelBeforeRead) caller.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EvidenceReceiptPayload.HashStreamAsync(stream, 3, caller.Token));

        Assert.Equal(caller.Token, failure.CancellationToken);
        Assert.Equal(cancelBeforeRead ? 0 : cancelOnEofRead ? 2 : 1, stream.ReadCalls);
        Assert.True(stream.CanRead);
    }

    private sealed class ObservedStream(byte[] bytes, long reportedLength, Action? afterRead = null) : MemoryStream(bytes)
    {
        internal int ReadCalls { get; private set; }
        internal int MaximumReadRequest { get; private set; }
        internal int LengthRequests { get; private set; }
        public override long Length { get { LengthRequests++; return reportedLength; } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            MaximumReadRequest = Math.Max(MaximumReadRequest, count);
            var observed = base.Read(buffer, offset, count);
            afterRead?.Invoke();
            return observed;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment))
                return ValueTask.FromResult(Read(segment.Array!, segment.Offset, segment.Count));

            var temporary = new byte[buffer.Length];
            var count = Read(temporary, 0, temporary.Length);
            temporary.AsMemory(0, count).CopyTo(buffer);
            return ValueTask.FromResult(count);
        }
    }
}
