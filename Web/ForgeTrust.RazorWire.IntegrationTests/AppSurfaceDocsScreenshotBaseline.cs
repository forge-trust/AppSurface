using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Core;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>
/// Compares a prepared AppSurface Docs page with a committed Graphite visual baseline.
/// </summary>
/// <remarks>
/// Call this helper only after the test has navigated and waited for its route-specific ready selector. Baselines are
/// deliberately updated only when <c>APPSURFACE_UPDATE_VISUAL_BASELINES=1</c> is set, so ordinary test runs cannot
/// silently accept a visual regression.
/// </remarks>
internal static partial class AppSurfaceDocsScreenshotBaseline
{
    /// <summary>Gets the explicit opt-in environment variable for refreshing committed visual baselines.</summary>
    internal const string UpdateEnvironmentVariable = "APPSURFACE_UPDATE_VISUAL_BASELINES";

    private const string BaselineDirectory = "Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite";

    // Chromium can vary a handful of antialiased edge pixels by one channel value between otherwise identical renders.
    private const double MaximumRasterizationNoiseRatio = 0.0001;

    private const int MaximumRasterizationNoiseChannelDelta = 4;

    /// <summary>
    /// Disables non-deterministic visual effects and compares the current page with its named committed baseline.
    /// </summary>
    /// <param name="page">The page that has reached its route-specific ready state.</param>
    /// <param name="baselineFileName">A PNG file name below the Graphite baseline directory.</param>
    /// <param name="testResultsDirectory">Directory where mismatch artifacts are written.</param>
    /// <param name="cancellationToken">Cancellation token for screenshot and file operations.</param>
    /// <returns>A task that completes when the baseline matches or is explicitly refreshed.</returns>
    /// <exception cref="ArgumentException">The baseline name is not a safe PNG file name.</exception>
    /// <exception cref="InvalidOperationException">The rendered screenshot differs from its committed baseline.</exception>
    public static async Task AssertMatchesAsync(
        IPage page,
        string baselineFileName,
        string testResultsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(testResultsDirectory);

        var baselinePath = ResolveBaselinePath(baselineFileName);
        var actual = await CaptureAsync(page, cancellationToken);

        if (IsUpdateRequested())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            await File.WriteAllBytesAsync(baselinePath, actual, cancellationToken);
            return;
        }

        var expected = File.Exists(baselinePath)
            ? await File.ReadAllBytesAsync(baselinePath, cancellationToken)
            : null;
        var difference = expected is null
            ? PngVisualDifference.Missing
            : ComparePngPixels(expected, actual);
        if (difference.IsMatch)
        {
            return;
        }

        Directory.CreateDirectory(testResultsDirectory);
        var actualPath = Path.Join(
            testResultsDirectory,
            $"{Path.GetFileNameWithoutExtension(baselineFileName)}.actual.png");
        await File.WriteAllBytesAsync(actualPath, actual, cancellationToken);

        var expectedSha256 = expected is null ? "missing" : ComputeSha256(expected);
        throw new InvalidOperationException(
            $"Graphite visual baseline mismatch. Baseline: '{baselinePath}'. Actual: '{actualPath}'. "
            + $"Expected SHA-256: {expectedSha256}. Actual SHA-256: {ComputeSha256(actual)}. "
            + $"{difference}. "
            + $"Review the artifact, then refresh deliberately with {UpdateEnvironmentVariable}=1.");
    }

    private static async Task<byte[]> CaptureAsync(IPage page, CancellationToken cancellationToken)
    {
        await page.EvaluateAsync(
            """
            () => {
              const id = 'appsurface-graphite-visual-baseline-stability';
              if (document.getElementById(id)) return;
              const style = document.createElement('style');
              style.id = id;
              style.textContent = '*,*::before,*::after{animation:none!important;transition:none!important;caret-color:transparent!important;}';
              document.head.append(style);
              for (const time of document.querySelectorAll('time.docs-provenance-time')) {
                time.textContent = 'Updated';
              }
            }
            """);

        return await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Animations = ScreenshotAnimations.Disabled,
            Caret = ScreenshotCaret.Hide,
            FullPage = true,
            Timeout = 30_000
        });
    }

    private static string ResolveBaselinePath(string baselineFileName)
    {
        if (!string.Equals(Path.GetFileName(baselineFileName), baselineFileName, StringComparison.Ordinal)
            || !baselineFileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Graphite visual baseline names must be a PNG file name without directory segments.",
                nameof(baselineFileName));
        }

        var repoRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var baselineRoot = PathUtils.PathUnder(repoRoot, BaselineDirectory);
        return PathUtils.PathUnder(baselineRoot, baselineFileName);
    }

    private static bool IsUpdateRequested() =>
        string.Equals(Environment.GetEnvironmentVariable(UpdateEnvironmentVariable), "1", StringComparison.Ordinal);

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static PngVisualDifference ComparePngPixels(byte[] expected, byte[] actual)
    {
        var expectedImage = DecodePng(expected, "expected baseline");
        var actualImage = DecodePng(actual, "actual screenshot");
        if (expectedImage.Width != actualImage.Width
            || expectedImage.Height != actualImage.Height
            || expectedImage.BytesPerPixel != actualImage.BytesPerPixel)
        {
            return PngVisualDifference.DimensionMismatch(
                expectedImage.Width,
                expectedImage.Height,
                expectedImage.BytesPerPixel,
                actualImage.Width,
                actualImage.Height,
                actualImage.BytesPerPixel);
        }

        var differingPixels = 0;
        var maximumChannelDelta = 0;
        var bytesPerPixel = expectedImage.BytesPerPixel;
        for (var offset = 0; offset < expectedImage.Pixels.Length; offset += bytesPerPixel)
        {
            var pixelDiffers = false;
            for (var channel = 0; channel < bytesPerPixel; channel++)
            {
                var delta = Math.Abs(expectedImage.Pixels[offset + channel] - actualImage.Pixels[offset + channel]);
                maximumChannelDelta = Math.Max(maximumChannelDelta, delta);
                pixelDiffers |= delta != 0;
            }

            if (pixelDiffers)
            {
                differingPixels++;
            }
        }

        return new PngVisualDifference(differingPixels, expectedImage.Width * expectedImage.Height, maximumChannelDelta);
    }

    private static DecodedPng DecodePng(byte[] bytes, string description)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < signature.Length || !bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            throw new InvalidOperationException($"The {description} is not a PNG file.");
        }

        var position = signature.Length;
        var width = 0;
        var height = 0;
        var bytesPerPixel = 0;
        using var compressedPixels = new MemoryStream();
        while (position < bytes.Length)
        {
            if (bytes.Length - position < 12)
            {
                throw new InvalidOperationException($"The {description} has a truncated PNG chunk.");
            }

            var chunkLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position, sizeof(int)));
            if (chunkLength < 0 || bytes.Length - position - 12 < chunkLength)
            {
                throw new InvalidOperationException($"The {description} has an invalid PNG chunk length.");
            }

            var type = bytes.AsSpan(position + 4, 4);
            var data = bytes.AsSpan(position + 8, chunkLength);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (chunkLength != 13 || width != 0 || height != 0)
                {
                    throw new InvalidOperationException($"The {description} has an invalid PNG header.");
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                var bitDepth = data[8];
                var colorType = data[9];
                var compression = data[10];
                var filter = data[11];
                var interlace = data[12];
                bytesPerPixel = (bitDepth, colorType, compression, filter, interlace) switch
                {
                    (8, 2, 0, 0, 0) => 3,
                    (8, 6, 0, 0, 0) => 4,
                    _ => throw new InvalidOperationException(
                        $"The {description} uses an unsupported PNG format (bit depth {bitDepth}, color type {colorType}).")
                };
                if (width <= 0 || height <= 0)
                {
                    throw new InvalidOperationException($"The {description} has invalid PNG dimensions.");
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressedPixels.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            position += chunkLength + 12;
        }

        if (width == 0 || height == 0 || bytesPerPixel == 0 || compressedPixels.Length == 0)
        {
            throw new InvalidOperationException($"The {description} is missing required PNG image data.");
        }

        var bytesPerRow = checked(width * bytesPerPixel);
        var expectedInflatedLength = checked(height * (bytesPerRow + 1));
        compressedPixels.Position = 0;
        using var zlib = new ZLibStream(compressedPixels, CompressionMode.Decompress, leaveOpen: true);
        using var inflatedPixels = new MemoryStream(expectedInflatedLength);
        zlib.CopyTo(inflatedPixels);
        var filteredPixels = inflatedPixels.ToArray();
        if (filteredPixels.Length != expectedInflatedLength)
        {
            throw new InvalidOperationException($"The {description} has an unexpected PNG pixel-data length.");
        }

        var pixels = new byte[checked(width * height * bytesPerPixel)];
        var sourceOffset = 0;
        for (var row = 0; row < height; row++)
        {
            var filter = filteredPixels[sourceOffset++];
            var currentRow = pixels.AsSpan(row * bytesPerRow, bytesPerRow);
            filteredPixels.AsSpan(sourceOffset, bytesPerRow).CopyTo(currentRow);
            sourceOffset += bytesPerRow;
            var previousRow = row == 0 ? ReadOnlySpan<byte>.Empty : pixels.AsSpan((row - 1) * bytesPerRow, bytesPerRow);
            ApplyPngFilter(currentRow, previousRow, bytesPerPixel, filter, description);
        }

        return new DecodedPng(width, height, bytesPerPixel, pixels);
    }

    private static void ApplyPngFilter(
        Span<byte> currentRow,
        ReadOnlySpan<byte> previousRow,
        int bytesPerPixel,
        byte filter,
        string description)
    {
        for (var index = 0; index < currentRow.Length; index++)
        {
            var left = index >= bytesPerPixel ? currentRow[index - bytesPerPixel] : 0;
            var up = previousRow.IsEmpty ? 0 : previousRow[index];
            var upperLeft = index >= bytesPerPixel && !previousRow.IsEmpty ? previousRow[index - bytesPerPixel] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => PaethPredictor(left, up, upperLeft),
                _ => throw new InvalidOperationException($"The {description} uses unsupported PNG filter {filter}.")
            };
            currentRow[index] = unchecked((byte)(currentRow[index] + predictor));
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

    private sealed record DecodedPng(int Width, int Height, int BytesPerPixel, byte[] Pixels);

    private sealed record PngVisualDifference(int DifferingPixels, int TotalPixels, int MaximumChannelDelta)
    {
        public static PngVisualDifference Missing { get; } = new(-1, 0, 0);

        public bool IsMatch => DifferingPixels >= 0
            && MaximumChannelDelta <= MaximumRasterizationNoiseChannelDelta
            && DifferingPixels <= Math.Ceiling(TotalPixels * MaximumRasterizationNoiseRatio);

        public static PngVisualDifference DimensionMismatch(
            int expectedWidth,
            int expectedHeight,
            int expectedBytesPerPixel,
            int actualWidth,
            int actualHeight,
            int actualBytesPerPixel) =>
            new(-2, 0, 0)
            {
                DimensionMessage =
                    $"Expected {expectedWidth}x{expectedHeight} at {expectedBytesPerPixel} bytes/pixel; "
                    + $"actual is {actualWidth}x{actualHeight} at {actualBytesPerPixel} bytes/pixel."
            };

        private string? DimensionMessage { get; init; }

        public override string ToString() => DifferingPixels switch
        {
            -1 => "No committed PNG baseline was found.",
            -2 => $"PNG dimensions differ. {DimensionMessage}",
            _ => $"Decoded pixels differ: {DifferingPixels}/{TotalPixels} pixels; maximum channel delta {MaximumChannelDelta}; "
                + $"allowed rasterization noise is at most {Math.Ceiling(TotalPixels * MaximumRasterizationNoiseRatio)}/{TotalPixels} pixels with a channel delta of {MaximumRasterizationNoiseChannelDelta}."
        };
    }
}
