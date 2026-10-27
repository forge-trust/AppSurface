using System.IO.Compression;
using ForgeTrust.AppSurface.Durable.CloudTasks;

namespace ForgeTrust.AppSurface.Durable.CloudTasks.Tests;

// protects: decoded byte limit at the compression boundary before protobuf allocation.
// fails_when: synchronous/asynchronous readers bypass the limit or unsupported operations acquire behavior.
// why_new: real SDK uses synchronous decompression today; seam: deliberately internal bounded stream/provider.
// no_claim: this does not prove HTTP/2 protocol correctness or live Google behavior.
public sealed class TransportBoundsTests
{
    // protects=all supported read APIs enforce the decoded byte boundary and dispose the owned input; fails_when=any read shape bypasses the limit or disposal leaks the stream; no_claim=HTTP/2 framing behavior.
    [Fact]
    public async Task All_supported_read_shapes_share_limit_plus_one_and_dispose_the_owned_stream()
    {
        using var input = new MemoryStream(new byte[CloudTasksSdkTransport.MessageLimit + 1]);
        using var bounded = new CloudTasksSdkTransport.LimitedReadStream(input);
        Assert.True(bounded.CanRead);
        Assert.False(bounded.CanWrite);
        Assert.False(bounded.CanSeek);
        var buffer = new byte[CloudTasksSdkTransport.MessageLimit - 3];
        Assert.Equal(buffer.Length, bounded.Read(buffer, 0, buffer.Length));
        Assert.Equal(1, await bounded.ReadAsync(new byte[1], 0, 1, CancellationToken.None));
        Assert.Equal(2, await bounded.ReadAsync(new byte[2].AsMemory()));
        Assert.Throws<CloudTasksResponseLimitException>(() => bounded.Read(new byte[1]));
        Assert.Throws<NotSupportedException>(() => bounded.Length);
        Assert.Throws<NotSupportedException>(() => bounded.Position);
        Assert.Throws<NotSupportedException>(() => bounded.Position = 0);
        Assert.Throws<NotSupportedException>(() => bounded.Flush());
        Assert.Throws<NotSupportedException>(() => bounded.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => bounded.SetLength(0));
        Assert.Throws<NotSupportedException>(() => bounded.Write([], 0, 0));
        bounded.Dispose();
        Assert.False(input.CanRead);
    }

    // protects=gzip decompression enforces the same expansion limit as plain responses; fails_when=compressed input expands past the configured bound; no_claim=SDK integration behavior.
    [Fact]
    public void Bounded_gzip_provider_roundtrips_exact_limit_and_rejects_an_expansion_over_limit()
    {
        var provider = new CloudTasksSdkTransport.BoundedGzipProvider();
        Assert.Equal("gzip", provider.EncodingName);
        using var encoded = new MemoryStream();
        using (var compressor = provider.CreateCompressionStream(encoded, CompressionLevel.Fastest))
            compressor.Write(new byte[CloudTasksSdkTransport.MessageLimit + 1]);
        using var decoded = provider.CreateDecompressionStream(new MemoryStream(encoded.ToArray()));
        var buffer = new byte[CloudTasksSdkTransport.MessageLimit];
        decoded.ReadExactly(buffer);
        Assert.Throws<CloudTasksResponseLimitException>(() => decoded.ReadByte());
    }
}
