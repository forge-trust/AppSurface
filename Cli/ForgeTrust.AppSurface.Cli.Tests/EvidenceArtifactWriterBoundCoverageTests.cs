using System.Buffers;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceArtifactWriterBoundCoverageTests
{
    [Fact]
    public async Task ProducerWideByteLimitRejectsOversizedMemoryBeforeReadingItAndReleasesTheReservation()
    {
        using var directory = TestDirectory.Create();
        var root = TestPathUtils.PathUnder(directory.Path, "artifacts");
        var declaration = new EvidenceProducerDeclaration("producer", "custom", "1", [], [],
            [new("report", "reports", "application/octet-stream", false, long.MaxValue) { MaximumFiles = 2 }], 10);
        var writer = new EvidenceArtifactWriter(declaration, root);
        using var memory = new LengthOnlyMemory(checked((int)EvidenceArtifactWriter.MaximumTotalArtifactBytes + 1));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync("report", "reports/result.bin", memory.Memory).AsTask());

        Assert.Contains("exceeds its evidence size limit", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, memory.SpanRequests);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(root));

        await writer.WriteAsync("report", "reports/result.bin", new byte[] { 42 });

        Assert.Equal(1, Assert.Single(writer.WrittenArtifacts).LengthBytes);
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, "reports/result.bin")));
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    // The public memory abstraction exposes its logical length without allocating an oversized buffer.
    // Any attempt to read it fails independently, so the test proves the size guard runs first.
    private sealed class LengthOnlyMemory(int length) : MemoryManager<byte>
    {
        internal int SpanRequests { get; private set; }
        public override Memory<byte> Memory => CreateMemory(length);
        public override Span<byte> GetSpan()
        {
            SpanRequests++;
            throw new InvalidOperationException("Oversized memory must not be read.");
        }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
