using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ForgeTrust.RazorWire.IntegrationTests;

internal static partial class AppSurfaceDocsScreenshotBaseline
{
    internal static (int Width, int Height, int BytesPerPixel, byte[] Pixels) DecodePngForTests(byte[] bytes)
    {
        var image = DecodePng(bytes, "test PNG");
        return (image.Width, image.Height, image.BytesPerPixel, image.Pixels);
    }
}

public sealed class AppSurfaceDocsScreenshotBaselineTests
{
    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)6)]
    public void DecodePng_DecodesSupportedColorTypes_WithAllSupportedFilters(byte colorType)
    {
        const int width = 4;
        const int height = 5;
        var rowFilters = new byte[] { 0, 1, 2, 3, 4 };
        var bytesPerPixel = colorType == 2 ? 3 : 4;
        var expectedPixels = BuildExpectedPixels(width, height, bytesPerPixel);
        var png = BuildPngFromRawPixels(width, height, colorType, expectedPixels, rowFilters);

        var decoded = AppSurfaceDocsScreenshotBaseline.DecodePngForTests(png);

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(bytesPerPixel, decoded.BytesPerPixel);
        Assert.Equal(expectedPixels, decoded.Pixels);
    }

    [Fact]
    public void DecodePng_RejectsInvalidPngSignature()
    {
        var validPng = BuildPngFromRawPixels(1, 1, 2, BuildExpectedPixels(1, 1, 3), new byte[] { 0 });
        validPng[0] = 0;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceDocsScreenshotBaseline.DecodePngForTests(validPng));

        Assert.Contains("The test PNG is not a PNG file.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePng_RejectsTruncatedPngChunk()
    {
        var truncatedChunk = new byte[10];
        Array.Copy(PngSignature, truncatedChunk, PngSignature.Length);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceDocsScreenshotBaseline.DecodePngForTests(truncatedChunk));

        Assert.Contains("has a truncated PNG chunk.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePng_RejectsInvalidPngChunkLength()
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);
        WriteChunk(output, "IHDR", BuildIhdr(1, 1, bitDepth: 8, colorType: 2));
        WriteInvalidChunkLength(output, "IDAT", declaredLength: 8, providedDataLength: 0);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceDocsScreenshotBaseline.DecodePngForTests(output.ToArray()));

        Assert.Contains("has an invalid PNG chunk length.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePng_RejectsInvalidHeaderWhenIHDRAppearsTwice()
    {
        const int width = 2;
        const int height = 1;
        var ihdr = BuildIhdr(width, height, bitDepth: 8, colorType: 2);
        var png = BuildPngFromChunks(
            ("IHDR", ihdr),
            ("IHDR", ihdr),
            ("IDAT", BuildFilteredScanlines(
                width,
                height,
                3,
                BuildExpectedPixels(width, height, 3),
                new byte[] { 0 })),
            ("IEND", Array.Empty<byte>()));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceDocsScreenshotBaseline.DecodePngForTests(png));

        Assert.Contains("has an invalid PNG header.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePng_RejectsUnsupportedFormat()
    {
        var png = BuildPngFromChunks(
            ("IHDR", BuildIhdr(1, 1, bitDepth: 16, colorType: 2)),
            ("IDAT", Array.Empty<byte>()),
            ("IEND", Array.Empty<byte>()));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceDocsScreenshotBaseline.DecodePngForTests(png));

        Assert.Contains("uses an unsupported PNG format", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePng_RejectsInvalidDimensions()
    {
        var png = BuildPngFromRawPixels(0, 1, 2, BuildExpectedPixels(0, 1, 3), new byte[] { 0 });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceDocsScreenshotBaseline.DecodePngForTests(png));

        Assert.Contains("has invalid PNG dimensions.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePng_RejectsMissingImageData()
    {
        var png = BuildPngFromChunks(
            ("IHDR", BuildIhdr(1, 1, bitDepth: 8, colorType: 2)),
            ("IEND", Array.Empty<byte>()));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceDocsScreenshotBaseline.DecodePngForTests(png));

        Assert.Contains("is missing required PNG image data.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePng_RejectsUnexpectedPixelDataLengthAfterInflation()
    {
        var png = BuildPngFromChunks(
            ("IHDR", BuildIhdr(2, 1, bitDepth: 8, colorType: 2)),
            ("IDAT", Compress(new byte[] { 1, 2, 3, 4 })),
            ("IEND", Array.Empty<byte>()));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceDocsScreenshotBaseline.DecodePngForTests(png));

        Assert.Contains("has an unexpected PNG pixel-data length.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodePng_RejectsUnsupportedFilter()
    {
        const int width = 3;
        const int height = 1;
        const int bytesPerPixel = 3;
        var rowBytes = width * bytesPerPixel;
        var filteredRow = new byte[1 + rowBytes];
        filteredRow[0] = 9;
        for (var i = 0; i < rowBytes; i++)
        {
            filteredRow[i + 1] = (byte)(i + 1);
        }

        var png = BuildPngFromFilteredScanlines(width, height, bytesPerPixel, bitDepth: 8, colorType: 2, filteredRow);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceDocsScreenshotBaseline.DecodePngForTests(png));

        Assert.Contains("uses unsupported PNG filter", ex.Message, StringComparison.Ordinal);
    }

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static byte[] BuildPngFromRawPixels(
        int width,
        int height,
        byte colorType,
        byte[] pixels,
        byte[] rowFilters,
        byte bitDepth = 8)
    {
        var bytesPerPixel = colorType == 2 ? 3 : 4;
        var filtered = BuildFilteredScanlines(width, height, bytesPerPixel, pixels, rowFilters);
        return BuildPngFromFilteredScanlines(width, height, bytesPerPixel, bitDepth, colorType, filtered);
    }

    private static byte[] BuildPngFromFilteredScanlines(int width, int height, int bytesPerPixel, byte bitDepth, byte colorType, byte[] filteredScanlines)
    {
        return BuildPngFromChunks(
            ("IHDR", BuildIhdr(width, height, bitDepth, colorType)),
            ("IDAT", Compress(filteredScanlines)),
            ("IEND", Array.Empty<byte>()));
    }

    private static byte[] BuildPngFromChunks(params (string Type, byte[] Data)[] chunks)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);
        foreach (var (type, data) in chunks)
        {
            WriteChunk(output, type, data);
        }

        return output.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height, byte bitDepth, byte colorType)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, sizeof(int)), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, sizeof(int)), height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        return ihdr;
    }

    private static byte[] BuildFilteredScanlines(int width, int height, int bytesPerPixel, byte[] pixels, byte[] rowFilters)
    {
        if (rowFilters.Length != height)
        {
            throw new ArgumentException("Row filters must match image height.", nameof(rowFilters));
        }

        var scanlines = new byte[height * (1 + width * bytesPerPixel)];
        var rowBytes = width * bytesPerPixel;
        ReadOnlySpan<byte> previousRow = ReadOnlySpan<byte>.Empty;

        var sourceOffset = 0;
        var scanlineOffset = 0;
        for (var row = 0; row < height; row++)
        {
            var filter = (byte)rowFilters[row];
            scanlines[scanlineOffset++] = filter;

            var rawRow = pixels.AsSpan(sourceOffset, rowBytes);
            var filteredRow = scanlines.AsSpan(scanlineOffset, rowBytes);
            ApplyPngFilterForward(rawRow, previousRow, bytesPerPixel, filter, filteredRow);

            sourceOffset += rowBytes;
            scanlineOffset += rowBytes;
            previousRow = rawRow.ToArray();
        }

        return scanlines;
    }

    private static void ApplyPngFilterForward(
        ReadOnlySpan<byte> rawRow,
        ReadOnlySpan<byte> previousRow,
        int bytesPerPixel,
        byte filter,
        Span<byte> filteredRow)
    {
        for (var index = 0; index < rawRow.Length; index++)
        {
            var left = index >= bytesPerPixel ? rawRow[index - bytesPerPixel] : 0;
            var up = previousRow.IsEmpty ? 0 : previousRow[index];
            var upperLeft = index >= bytesPerPixel && !previousRow.IsEmpty
                ? previousRow[index - bytesPerPixel]
                : 0;

            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => PaethPredictor(left, up, upperLeft),
                _ => throw new ArgumentOutOfRangeException(nameof(filter))
            };

            filteredRow[index] = unchecked((byte)(rawRow[index] - predictor));
        }
    }

    private static int PaethPredictor(int left, int up, int upperLeft)
    {
        var estimate = left + up - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);

        return leftDistance <= upDistance && leftDistance <= upperLeftDistance
            ? left
            : upDistance <= upperLeftDistance
                ? up
                : upperLeft;
    }

    private static byte[] BuildExpectedPixels(int width, int height, int bytesPerPixel)
    {
        var pixels = new byte[width * height * bytesPerPixel];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width + x) * bytesPerPixel);
                pixels[offset] = (byte)(x + (y * 11));
                pixels[offset + 1] = (byte)(x * 7 + y * 13 + 10);
                pixels[offset + 2] = (byte)(x * 17 + y * 19 + 20);
                if (bytesPerPixel == 4)
                {
                    pixels[offset + 3] = (byte)(255 - (x * 23 + y * 31));
                }
            }

        return pixels;
    }

    private static void WriteChunk(MemoryStream output, string type, byte[] data, int? chunkLength = null)
    {
        var length = chunkLength ?? data.Length;
        Span<byte> chunkLengthBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(chunkLengthBytes, length);
        output.Write(chunkLengthBytes);
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(data);
        output.Write(new byte[4]);
    }

    private static void WriteInvalidChunkLength(MemoryStream output, string type, int declaredLength, int providedDataLength)
    {
        Span<byte> chunkLengthBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(chunkLengthBytes, declaredLength);
        output.Write(chunkLengthBytes);
        output.Write(Encoding.ASCII.GetBytes(type));
        var payload = new byte[providedDataLength + 4];
        output.Write(payload);
    }

    private static byte[] Compress(byte[] input)
    {
        using var memory = new MemoryStream();
        using (var compressor = new ZLibStream(memory, CompressionMode.Compress, leaveOpen: true))
        {
            compressor.Write(input, 0, input.Length);
        }

        return memory.ToArray();
    }
}
