using System.Buffers.Binary;
using System.IO.Compression;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Recognizes the bounded static screenshot PNG subset used by AppSurface Docs visual baselines.</summary>
internal static class CoverageFanInPngValidator
{
    private const string GraphiteDirectory = "Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/";
    private const string LightDirectory = "Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsLight/";
    private const int MaximumBlobBytes = 20 * 1024 * 1024;
    private const long MaximumDecodedBytes = 20L * 1024 * 1024;
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Matches only an immediate, safe ASCII PNG basename in one of the two pinned baseline directories.</summary>
    internal static bool IsQualifiedPath(string path)
    {
        var fileName = path.StartsWith(GraphiteDirectory, StringComparison.Ordinal)
            ? path[GraphiteDirectory.Length..]
            : path.StartsWith(LightDirectory, StringComparison.Ordinal)
                ? path[LightDirectory.Length..]
                : string.Empty;
        if (fileName.Length <= 4 || !fileName.EndsWith(".png", StringComparison.Ordinal)) return false;
        foreach (var value in fileName.AsSpan(0, fileName.Length - 4))
            if (!char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_')) return false;
        return true;
    }

    /// <summary>Validates the exact bounded RGB8 PNG profile and charges each decoded byte as it is consumed.</summary>
    internal static bool IsSupportedScreenshot(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken,
        Action<long> chargeDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(chargeDecodedBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumBlobBytes || bytes.Length < Signature.Length
            || !bytes.Span[..Signature.Length].SequenceEqual(Signature)) return false;

        var position = Signature.Length;
        var scanlineLength = 0;
        uint height = 0;
        using var imageData = new ExactZlibInput(bytes.Length);
        while (bytes.Length - position >= 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = bytes.Span[position..];
            var length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
            if (length > (uint)(chunk.Length - 12)) return false;

            var body = chunk.Slice(4, 4 + (int)length);
            var type = body[..4];
            var data = body[4..];
            var crc = BinaryPrimitives.ReadUInt32BigEndian(chunk[(8 + data.Length)..]);
            if (ComputeCrc(body, cancellationToken) != crc) return false;
            position += data.Length + 12;

            if (scanlineLength == 0)
            {
                if (!type.SequenceEqual("IHDR"u8) || data.Length != 13) return false;
                var width = BinaryPrimitives.ReadUInt32BigEndian(data);
                height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                if (width == 0 || height == 0 || !data[8..].SequenceEqual(Rgb8Format)) return false;
                var rowBytes = 1L + 3L * width;
                if (rowBytes > MaximumDecodedBytes / height) return false;
                scanlineLength = (int)rowBytes;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                imageData.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return data.IsEmpty && position == bytes.Length && imageData.Length > 0
                    && HasExpectedImageData(imageData, scanlineLength, height, cancellationToken, chargeDecodedBytes);
            }
            else
            {
                return false;
            }
        }
        return false;
    }

    private static ReadOnlySpan<byte> Rgb8Format => [8, 2, 0, 0, 0];

    private static bool HasExpectedImageData(ExactZlibInput compressed, int scanlineLength, uint height,
        CancellationToken cancellationToken, Action<long> chargeDecodedBytes)
    {
        compressed.Position = 0;
        var scanline = new byte[scanlineLength];
        using var decoded = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        try
        {
            for (var row = 0; row < height; row++)
            {
                var offset = 0;
                while (offset < scanline.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = decoded.Read(scanline.AsSpan(offset));
                    if (read == 0) return false;
                    chargeDecodedBytes(read);
                    offset += read;
                }
                if (scanline[0] > 4) return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (decoded.ReadByte() < 0) return compressed.Position == compressed.Length;
            chargeDecodedBytes(1);
            return false;
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException)
        {
            return false;
        }
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> body, CancellationToken cancellationToken)
    {
        var crc = uint.MaxValue;
        for (var index = 0; index < body.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            crc = CrcTable[(crc ^ body[index]) & 0xff] ^ (crc >> 8);
        }
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : 0xedb88320u ^ (value >> 1);
            table[(int)index] = value;
        }
        return table;
    }

    /// <summary>
    /// Deliver the last compressed byte separately so inflater read-ahead cannot conceal trailing data.
    /// A further read means the inflater still needs input and the zlib stream was truncated.
    /// </summary>
    private sealed class ExactZlibInput(int capacity) : MemoryStream(capacity)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Limit(count));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Limit(buffer.Length)]);

        private int Limit(int requested)
        {
            if (requested == 0) return 0;
            var remaining = Length - Position;
            if (remaining == 0) throw new EndOfStreamException();
            return (int)Math.Min(requested, Math.Max(1L, remaining - 1));
        }
    }
}
