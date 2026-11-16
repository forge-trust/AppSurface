using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReceiptStreamContractTests
{
    private const long PayloadBound = 256L * 1024 * 1024;

    [Theory]
    [InlineData(0)]
    [InlineData(150_000)]
    public void ExactReceiptStreamProducesItsDigestUsingBoundedReadsWithoutDisposingTheCaller(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        using var stream = new ObservedStream(bytes, length);

        var result = EvidenceReceiptStore.EvidenceReceiptKeyLease.HashStream(stream, length, CancellationToken.None);

        Assert.Equal(length, result.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Digest);
        Assert.InRange(stream.MaximumReadRequest, 1, 64 * 1024);
        Assert.True(stream.ReadCalls >= (length + 65_535) / 65_536 + 1);
        Assert.Equal(length, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, "length differs")]
    [InlineData(PayloadBound + 1, 0, 0, 0, "length differs")]
    [InlineData(1, 2, 2, 0, "length differs")]
    [InlineData(1, 1, 2, 1, "grew during read")]
    [InlineData(2, 2, 1, 2, "changed during read")]
    public void MalformedReceiptLengthCannotProduceAPartialDigest(long expected, long reported, int actual,
        int readCalls, string diagnostic)
    {
        using var stream = new ObservedStream(new byte[actual], reported);

        var failure = Assert.Throws<InvalidDataException>(() =>
            EvidenceReceiptStore.EvidenceReceiptKeyLease.HashStream(stream, expected, CancellationToken.None));

        Assert.Contains(diagnostic, failure.Message, StringComparison.Ordinal);
        Assert.Equal(readCalls, stream.ReadCalls);
        Assert.Equal(expected is < 0 or > PayloadBound ? 0 : 1, stream.LengthRequests);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeOrAfterAReadPreservesTheCallerTokenAndStreamOwnership(bool cancelBeforeRead)
    {
        using var caller = new CancellationTokenSource();
        using var stream = new ObservedStream(new byte[] { 1, 2, 3 }, 3,
            afterRead: cancelBeforeRead ? null : caller.Cancel);
        if (cancelBeforeRead) caller.Cancel();

        var failure = Assert.ThrowsAny<OperationCanceledException>(() =>
            EvidenceReceiptStore.EvidenceReceiptKeyLease.HashStream(stream, 3, caller.Token));

        Assert.Equal(caller.Token, failure.CancellationToken);
        Assert.Equal(cancelBeforeRead ? 0 : 1, stream.ReadCalls);
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
    }
}
