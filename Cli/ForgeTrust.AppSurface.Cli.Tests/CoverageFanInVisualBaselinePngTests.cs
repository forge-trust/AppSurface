using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInVisualBaselinePngTests
{
    private const string Path = "Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/sample-baseline.png";
    private const string VisualPolicyId = "appsurface-visual-baseline-png-v1";
    private const string BinaryMarker = "Binary files a/" + Path + " and b/" + Path + " differ";

    [Theory]
    [InlineData(1, 1)]
    [InlineData(390, 844)]
    [InlineData(1440, 1000)]
    public void ValidatorAcceptsRgb8ScreenshotsAcrossResolutionsAndChargesDecodedBytes(int width, int height)
    {
        var png = BuildPng(2, 64, (uint)width, (uint)height);
        var budget = new CoverageFanInBudget(20L * 1024 * 1024);

        Assert.True(CoverageFanInPngValidator.IsSupportedScreenshot(png, CancellationToken.None, budget.Charge));
        Assert.Equal((long)(width * 3 + 1) * height, budget.Snapshot().UsedBytes);
    }

    [Fact]
    public void ValidatorChecksDecodedRowsFiltersAndCancellation()
    {
        var tooShort = BuildPng(2, 64, decodedSizeAdjustment: -1);
        var tooLong = BuildPng(2, 64, decodedSizeAdjustment: 1);
        var badFilter = BuildPng(2, 64, firstRowFilter: 5);
        using var cancellation = new CancellationTokenSource();

        Assert.False(IsSupported(tooShort));
        Assert.False(IsSupported(tooLong));
        Assert.False(IsSupported(badFilter));
        Assert.Throws<OperationCanceledException>(() => CoverageFanInPngValidator.IsSupportedScreenshot(
            BuildPng(2, 64), cancellation.Token, _ => cancellation.Cancel()));
    }

    [Fact]
    public void ValidatorRejectsDimensionOverflowAndDecodedDataAboveTwentyMebibytes()
    {
        var oversized = HeaderOnlyPng(7_000_000, 1);
        var overflow = HeaderOnlyPng(uint.MaxValue, uint.MaxValue);

        Assert.False(IsSupported(oversized));
        Assert.False(IsSupported(overflow));
    }

    [Fact]
    public void ValidatorRejectsEmptyOversizedAndInvalidHeaderInputs()
    {
        var oversized = new byte[20 * 1024 * 1024 + 1];
        var zeroWidth = HeaderOnlyPng(0, 1);
        var zeroHeight = HeaderOnlyPng(1, 0);
        var invalidBitDepth = Png(Signature, Chunk("IHDR", Header(1, 1, 2, bitDepth: 16)),
            Chunk("IDAT", [1]), Chunk("IEND", []));
        var invalidCompression = Png(Signature, Chunk("IHDR", Header(1, 1, 2, compression: 1)),
            Chunk("IDAT", [1]), Chunk("IEND", []));
        var invalidFilter = Png(Signature, Chunk("IHDR", Header(1, 1, 2, filter: 1)),
            Chunk("IDAT", [1]), Chunk("IEND", []));

        Assert.False(IsSupported([]));
        Assert.False(IsSupported(oversized));
        Assert.False(IsSupported(zeroWidth));
        Assert.False(IsSupported(zeroHeight));
        Assert.False(IsSupported(invalidBitDepth));
        Assert.False(IsSupported(invalidCompression));
        Assert.False(IsSupported(invalidFilter));
    }

    [Fact]
    public void ValidatorStopsWhenDecodedByteBudgetIsExhausted()
    {
        var budget = new CoverageFanInBudget(4);

        var exception = Assert.Throws<CoverageFanInEvidenceException>(() =>
            CoverageFanInPngValidator.IsSupportedScreenshot(BuildPng(2, 64, width: 1, height: 2),
                CancellationToken.None, budget.Charge));

        Assert.Equal("limit-exceeded", exception.Cause);
        Assert.Equal(4, budget.Snapshot().UsedBytes);
    }

    [Fact]
    public void ValidatorRejectsReCrcedTruncatedTrailerAndTrailingIdatPayload()
    {
        var compressed = CompressScanlines(1, 1, 64, 0, 0);
        var truncated = compressed[..^1];
        var trailing = new byte[compressed.Length + 1];
        compressed.CopyTo(trailing, 0);
        trailing[^1] = 0x5a;

        Assert.False(IsSupported(PngWithImageData(1, 1, 2, truncated)));
        Assert.False(IsSupported(PngWithImageData(1, 1, 2, trailing)));
        Assert.True(IsSupported(PngWithImageData(1, 1, 2, compressed, splitImageData: true)));
    }

    [Fact]
    public void ValidatorRejectsBadSignatureCrcLengthAndTrailingPayload()
    {
        var valid = BuildPng(2, 64);
        var badSignature = valid.ToArray();
        badSignature[0] = 0;
        var badCrc = valid.ToArray();
        badCrc[^1] ^= 1;
        var badLength = valid.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(badLength.AsSpan(8, sizeof(uint)), uint.MaxValue);

        Assert.False(IsSupported(badSignature));
        Assert.False(IsSupported(badCrc));
        Assert.False(IsSupported(badLength));
        Assert.False(IsSupported([.. valid, 0]));
        Assert.False(IsSupported([.. valid, .. Chunk("IEND", [])]));
    }

    [Fact]
    public void ValidatorRequiresCompleteHeaderAndEndChunks()
    {
        var compressed = CompressScanlines(1, 1, 64, 0, 0);
        var header = Chunk("IHDR", Header(1, 1, 2));
        var imageData = Chunk("IDAT", compressed);
        var end = Chunk("IEND", []);
        Assert.False(IsSupported(Png(Signature, header, imageData)));
        Assert.False(IsSupported(Png(Signature, imageData, end)));
        Assert.False(IsSupported(Png(Signature, Chunk("IHDR", Header(1, 1, 2)[..12]), imageData, end)));
    }

    [Fact]
    public void ValidatorRejectsUnsupportedHeaderMissingImageDataAndRepeatedHeader()
    {
        var unsupported = BuildPng(6, 64);
        var interlaced = BuildPng(2, 64, interlace: 1);
        var ancillary = BuildPng(2, 64, includeAncillary: true);
        var missingImageData = Png(Signature, Chunk("IHDR", Header(1, 1, 2)), Chunk("IEND", []));
        var repeatedHeader = Png(Signature, Chunk("IHDR", Header(1, 1, 2)), Chunk("IHDR", Header(1, 1, 2)),
            Chunk("IDAT", [1]), Chunk("IEND", []));

        Assert.False(IsSupported(unsupported));
        Assert.False(IsSupported(interlaced));
        Assert.False(IsSupported(ancillary));
        Assert.False(IsSupported(missingImageData));
        Assert.False(IsSupported(repeatedHeader));
    }

    [Theory]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/home-system-light.png", true)]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsLight/home-mobile-390x844.png", true)]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsLight/home_mobile.png", true)]
    [InlineData("web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/home.png", false)]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/nested/home.png", false)]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/home page.png", false)]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsLight/home.PNG", false)]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsLight/../home.png", false)]
    public void PathQualificationIsCaseSensitiveAndRestrictedToSafeImmediateBasenames(string path, bool valid)
    {
        Assert.Equal(valid, CoverageFanInPngValidator.IsQualifiedPath(path));
    }

    [Fact]
    public void ParserPreservesValidatedBinaryMarkerFullObjectIdsAndEmptyRanges()
    {
        var input = BinaryInput(Path);

        var change = Assert.Single(CoverageFanInDiffParser.Parse(input));

        Assert.Equal(Path, change.OldPath);
        Assert.Equal(Path, change.NewPath);
        Assert.Equal(new string('a', 40), change.OldObject);
        Assert.Equal(new string('b', 40), change.NewObject);
        Assert.Contains(BinaryMarker, Encoding.UTF8.GetString(input.Patch), StringComparison.Ordinal);
        Assert.Empty(change.AddedRanges);
    }

    [Fact]
    public void ParserRejectsLegacyUnknownAndUnqualifiedBinaryChanges()
    {
        var strict = BinaryInput(Path) with { DiffPolicyId = CoverageFanInDiffContext.PolicyId, QualifiedPngPaths = EmptyPaths() };
        var unknown = BinaryInput(Path) with { DiffPolicyId = "future-policy" };
        var unsafePath = "Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/nested/home.png";
        var unqualified = BinaryInput(unsafePath);

        Assert.Contains(unsafePath, unqualified.QualifiedPngPaths);
        Assert.Equal("unsupported-diff", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(strict)).Cause);
        Assert.Equal("unsupported-diff", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(unknown)).Cause);
        Assert.Equal("unsupported-diff", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(unqualified)).Cause);
    }

    [Fact]
    public void ParserRejectsTextHunksAndModeChangesForQualifiedBaselinePaths()
    {
        var oldObject = new string('a', 40);
        var newObject = new string('b', 40);
        var textRaw = $":100644 100644 {oldObject} {newObject} M\0{Path}\0";
        var textPatch = $"diff --git a/{Path} b/{Path}\nindex {oldObject}..{newObject} 100644\n--- a/{Path}\n+++ b/{Path}\n@@ -1 +1 @@\n-old\n+new\n";
        var modeRaw = $":100644 100755 {oldObject} {newObject} M\0{Path}\0";
        var modeInput = new CoverageFanInDiffInput(oldObject, oldObject, newObject, 40,
            Encoding.UTF8.GetBytes($"diff --git a/{Path} b/{Path}\nindex {oldObject}..{newObject}\nBinary files a/{Path} and b/{Path} differ\n"), modeRaw)
        {
            DiffPolicyId = VisualPolicyId,
            QualifiedPngPaths = new HashSet<string>([Path], StringComparer.Ordinal)
        };
        var textInput = new CoverageFanInDiffInput(oldObject, oldObject, newObject, 40, Encoding.UTF8.GetBytes(textPatch), textRaw)
        {
            DiffPolicyId = VisualPolicyId,
            QualifiedPngPaths = new HashSet<string>([Path], StringComparer.Ordinal)
        };

        Assert.Equal("unsupported-diff", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(textInput)).Cause);
        Assert.Equal("unsupported-diff", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(modeInput)).Cause);
    }

    [Fact]
    public void RawParserRejectsDuplicatePathsAcrossRecords()
    {
        var oldObject = new string('a', 40);
        var newObject = new string('b', 40);
        var duplicate = $":100644 100644 {oldObject} {newObject} M\0{Path}\0:100644 100644 {new string('c', 40)} {new string('d', 40)} M\0{Path}\0";

        Assert.Equal("malformed-diff", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.ReadRaw(duplicate, 40)).Cause);
    }

    [Fact]
    public async Task FreshReadQualifiesOnlyPinnedSamePathPngBlobs()
    {
        using var fixture = await DiffFixture.CreateAsync();

        var input = await fixture.ReadAsync();
        var change = Assert.Single(CoverageFanInDiffParser.Parse(input));

        Assert.Equal(VisualPolicyId, input.DiffPolicyId);
        Assert.Equal(new HashSet<string>([Path], StringComparer.Ordinal), input.QualifiedPngPaths);
        Assert.Equal(fixture.OldPngObject, change.OldObject);
        Assert.Equal(fixture.NewPngObject, change.NewObject);
        Assert.Contains(BinaryMarker, Encoding.UTF8.GetString(input.Patch), StringComparison.Ordinal);
        Assert.Empty(change.AddedRanges);
        Assert.Single(fixture.Runner.ValidAttributePaths);
    }

    [Theory]
    [InlineData(BlobReadFailure.Truncated, "truncated-diff")]
    [InlineData(BlobReadFailure.NonzeroExit, "unsupported-diff")]
    [InlineData(BlobReadFailure.StandardError, "unsupported-diff")]
    [InlineData(BlobReadFailure.MissingBytes, "malformed-diff")]
    [InlineData(BlobReadFailure.ShortBytes, "malformed-diff")]
    [InlineData(BlobReadFailure.LongBytes, "malformed-diff")]
    [InlineData(BlobReadFailure.InvalidOldPng, "unsupported-diff")]
    [InlineData(BlobReadFailure.InvalidNewPng, "unsupported-diff")]
    public async Task PinnedBinaryBlobReadFailuresAreRejected(BlobReadFailure failure, string cause)
    {
        using var fixture = await DiffFixture.CreateAsync(failure: failure);

        var exception = await Assert.ThrowsAsync<CoverageFanInEvidenceException>(() => fixture.ReadAsync());

        Assert.Equal(cause, exception.Cause);
        Assert.NotEmpty(fixture.Runner.BlobRequests);
        var request = fixture.Runner.BlobRequests[0];
        Assert.Equal(failure == BlobReadFailure.InvalidNewPng ? 2 : 1, fixture.Runner.BlobRequests.Count);
        Assert.Null(request.OutputFile);
        Assert.True(request.CaptureBinaryStandardOutput);
        Assert.Equal((long)fixture.Runner.BlobContents[request.Arguments[^1]].Length, request.StandardOutputLimitBytes);
        Assert.NotNull(request.BudgetCharge);
    }

    [Fact]
    public async Task VisualPolicyRejectsAnIncompletePinnedBlobInventory()
    {
        using var fixture = await DiffFixture.CreateAsync(missingBlobInventory: true);

        var exception = await Assert.ThrowsAsync<CoverageFanInEvidenceException>(() => fixture.ReadAsync());

        Assert.Equal("malformed-diff", exception.Cause);
        Assert.Empty(fixture.Runner.BlobRequests);
    }

    [Fact]
    public async Task EmptyDiffProducesNoQualifiedScreenshotsOrBlobReads()
    {
        using var fixture = await DiffFixture.CreateAsync(changePng: false);

        var input = await fixture.ReadAsync();

        Assert.Empty(CoverageFanInDiffParser.Parse(input));
        Assert.Empty(input.QualifiedPngPaths);
        Assert.Empty(fixture.Runner.BlobRequests);
    }

    [Fact]
    public async Task VisualPolicySkipsBlobReadsWhenNoQualifiedScreenshotChanged()
    {
        using var fixture = await DiffFixture.CreateAsync(changePng: false, changeSource: true);

        var input = await fixture.ReadAsync();

        Assert.Empty(input.QualifiedPngPaths);
        Assert.Empty(fixture.Runner.BlobRequests);
        Assert.Single(CoverageFanInDiffParser.Parse(input));
    }

    [Fact]
    public async Task UnsupportedDiffPolicyIsRejectedBeforeReadingObjects()
    {
        using var fixture = await DiffFixture.CreateAsync(diffPolicyId: "future-policy");

        var exception = await Assert.ThrowsAsync<CoverageFanInEvidenceException>(() => fixture.ReadAsync());

        Assert.Equal("unsupported-diff", exception.Cause);
        Assert.Empty(fixture.Runner.BlobRequests);
    }

    [Theory]
    [InlineData("unexpected\0diff\0unspecified\0")]
    [InlineData("")]
    [InlineData(Path + "\0diff\0unspecified\0unexpected-tail")]
    public async Task VisualPolicyRejectsAnInvalidPinnedDiffAttributeInventory(string attributeResponse)
    {
        using var fixture = await DiffFixture.CreateAsync(attributeResponse: attributeResponse);

        var exception = await Assert.ThrowsAsync<CoverageFanInEvidenceException>(() => fixture.ReadAsync());

        Assert.Equal("malformed-diff", exception.Cause);
        Assert.Empty(fixture.Runner.BlobRequests);
    }

    private static CoverageFanInDiffInput BinaryInput(string path)
    {
        var oldObject = new string('a', 40);
        var newObject = new string('b', 40);
        var raw = $":100644 100644 {oldObject} {newObject} M\0{path}\0";
        var patch = $"diff --git a/{path} b/{path}\nindex {oldObject}..{newObject} 100644\nBinary files a/{path} and b/{path} differ\n";
        return new(oldObject, oldObject, newObject, 40, Encoding.UTF8.GetBytes(patch), raw)
        {
            DiffPolicyId = VisualPolicyId,
            QualifiedPngPaths = new HashSet<string>([path], StringComparer.Ordinal)
        };
    }

    private static IReadOnlySet<string> EmptyPaths() => new HashSet<string>(StringComparer.Ordinal);

    private static bool IsSupported(byte[] bytes) => CoverageFanInPngValidator.IsSupportedScreenshot(bytes, CancellationToken.None, _ => { });

    private static async Task<string> GitAsync(string workingDirectory, string? input, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var error = await stderr;
        Assert.True(process.ExitCode == 0, error);
        return (await stdout).TrimEnd('\r', '\n');
    }

    private static byte[] BuildPng(byte colorType, byte pixel, uint width = 1, uint height = 1,
        int decodedSizeAdjustment = 0, byte firstRowFilter = 0, byte interlace = 0, bool splitImageData = false,
        bool includeAncillary = false)
    {
        var compressed = CompressScanlines(checked((int)width), checked((int)height), pixel,
            decodedSizeAdjustment, firstRowFilter);
        return PngWithImageData(checked((int)width), checked((int)height), colorType, compressed,
            interlace, splitImageData, includeAncillary);
    }

    private static byte[] CompressScanlines(int width, int height, byte pixel, int decodedSizeAdjustment, byte firstRowFilter)
    {
        var scanlineLength = checked(width * 3 + 1);
        var scanlines = new byte[checked(scanlineLength * height + decodedSizeAdjustment)];
        for (var row = 0; row < height; row++)
        {
            var rowOffset = checked(row * scanlineLength);
            if (rowOffset >= scanlines.Length) break;
            scanlines[rowOffset] = row == 0 ? firstRowFilter : (byte)0;
            var pixelOffset = rowOffset + 1;
            if (pixelOffset < scanlines.Length)
                scanlines.AsSpan(pixelOffset, Math.Min(width * 3, scanlines.Length - pixelOffset)).Fill(pixel);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(scanlines);
        return compressed.ToArray();
    }

    private static byte[] Header(uint width, uint height, byte colorType, byte interlace = 0,
        byte bitDepth = 8, byte compression = 0, byte filter = 0)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), height);
        data[8] = bitDepth;
        data[9] = colorType;
        data[10] = compression;
        data[11] = filter;
        data[12] = interlace;
        return data;
    }

    private static byte[] PngWithImageData(int width, int height, byte colorType, byte[] compressed,
        byte interlace = 0, bool splitImageData = false, bool includeAncillary = false)
    {
        var chunks = new List<byte[]> { Signature, Chunk("IHDR", Header((uint)width, (uint)height, colorType, interlace)) };
        if (includeAncillary) chunks.Add(Chunk("tEXt", Encoding.ASCII.GetBytes("key\0value")));
        if (splitImageData)
        {
            var splitAt = compressed.Length / 2;
            chunks.Add(Chunk("IDAT", compressed[..splitAt]));
            chunks.Add(Chunk("IDAT", compressed[splitAt..]));
        }
        else
        {
            chunks.Add(Chunk("IDAT", compressed));
        }
        chunks.Add(Chunk("IEND", []));
        return Png([.. chunks]);
    }

    private static byte[] HeaderOnlyPng(uint width, uint height) => Png(Signature,
        Chunk("IHDR", Header(width, height, 2)), Chunk("IDAT", [1]), Chunk("IEND", []));

    private static byte[] Png(params byte[][] chunks)
    {
        using var output = new MemoryStream();
        foreach (var chunk in chunks) output.Write(chunk);
        return output.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        using var output = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(typeBytes, data));
        output.Write(crc);
        return output.ToArray();
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type) crc = CrcByte(crc, value);
        foreach (var value in data) crc = CrcByte(crc, value);
        return ~crc;
    }

    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320u ^ (crc >> 1);
        return crc;
    }

    public enum BlobReadFailure
    {
        Truncated,
        NonzeroExit,
        StandardError,
        MissingBytes,
        ShortBytes,
        LongBytes,
        InvalidOldPng,
        InvalidNewPng
    }

    private sealed class DiffFixture : IDisposable
    {
        private const string SourcePath = "src/example.cs";
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
        private readonly TestDirectory _temp;
        private readonly CoverageFanInDiffContext _context;

        private DiffFixture(TestDirectory temp, CoverageFanInDiffContext context, VisualBaselineProcessRunner runner,
            string oldPngObject, string newPngObject)
        {
            _temp = temp;
            _context = context;
            Runner = runner;
            OldPngObject = oldPngObject;
            NewPngObject = newPngObject;
        }

        internal VisualBaselineProcessRunner Runner { get; }
        internal string OldPngObject { get; }
        internal string NewPngObject { get; }

        internal static async Task<DiffFixture> CreateAsync(BlobReadFailure? failure = null,
            bool missingBlobInventory = false, bool changePng = true, bool changeSource = false,
            string diffPolicyId = VisualPolicyId, string? attributeResponse = null)
        {
            var temp = TestDirectory.Create();
            var root = temp.Path;
            var screenshotFile = TestPathUtils.PathUnder(root, Path);
            var sourceFile = TestPathUtils.PathUnder(root, SourcePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(screenshotFile)!);
            Directory.CreateDirectory(System.IO.Path.Join(root, "src"));
            await File.WriteAllBytesAsync(screenshotFile, BuildPng(2, 64));
            await File.WriteAllTextAsync(sourceFile, "class Example { }\n");
            await GitAsync(root, null, "init", "-q");
            await GitAsync(root, null, "config", "user.email", "fixture@example.invalid");
            await GitAsync(root, null, "config", "user.name", "Fixture");
            await GitAsync(root, null, "add", "--", Path, SourcePath);
            await GitAsync(root, null, "commit", "-m", "baseline");
            var requestedBase = await GitAsync(root, null, "rev-parse", "HEAD");
            var oldPngObject = await GitAsync(root, null, "rev-parse", $"{requestedBase}:{Path}");

            if (changePng) await File.WriteAllBytesAsync(screenshotFile, BuildPng(2, 65));
            if (changeSource) await File.WriteAllTextAsync(sourceFile, "class Example { int Added => 1; }\n");
            await GitAsync(root, null, "add", "--", Path, SourcePath);
            await GitAsync(root, null, "commit", "--allow-empty", "-m", "fixture change");
            var head = await GitAsync(root, null, "rev-parse", "HEAD");
            var tree = await GitAsync(root, null, "rev-parse", "HEAD^{tree}");
            var newPngObject = await GitAsync(root, null, "rev-parse", $"{head}:{Path}");
            var oldPng = await File.ReadAllBytesAsync(screenshotFile);
            var newPng = changePng ? BuildPng(2, 65) : oldPng;

            var policy = CoverageFanInPolicyTests.Policy() with
            {
                Source = new(head, tree, "10.0.402", "Release"),
                ContractVersion = 3,
                ReportProfileId = "coverlet-collector-cobertura-v1",
                Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: requestedBase)
            };
            var policyObject = JsonNode.Parse(CoveragePolicyEncoding.ToElement(policy).GetRawText())!.AsObject();
            policyObject["diffPolicyId"] = diffPolicyId;
            using var policyDocument = JsonDocument.Parse(policyObject.ToJsonString());
            var expected = new CoverageFanInExpected(policyDocument.RootElement.Clone(), "test-digest");
            var blobContents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [oldPngObject] = BuildPng(2, 64),
                [newPngObject] = newPng
            };
            var runner = new VisualBaselineProcessRunner(blobContents, failure, missingBlobInventory,
                attributeResponse, failure switch
                {
                    BlobReadFailure.InvalidOldPng => oldPngObject,
                    BlobReadFailure.InvalidNewPng => newPngObject,
                    _ => null
                }, oldPngObject, newPngObject);
            var context = new CoverageFanInDiffContext(new(expected, root)
            { IdentityRunner = runner }, new CoverageFanInBudget(1L << 32));
            await context.InitializeAsync(CancellationToken.None);
            return new(temp, context, runner, oldPngObject, newPngObject);
        }

        internal Task<CoverageFanInDiffInput> ReadAsync() => _context.ReadAsync(_deadline.Token);

        public void Dispose()
        {
            _deadline.Dispose();
            _temp.Dispose();
        }
    }

    private sealed class VisualBaselineProcessRunner(
        IReadOnlyDictionary<string, byte[]> blobContents,
        BlobReadFailure? failure,
        bool missingBlobInventory,
        string? attributeResponse,
        string? invalidPngObject,
        string oldPngObject,
        string newPngObject) : ICoverageRunProcessRunner
    {
        private readonly CliWrapCoverageRunProcessRunner _inner = new();
        internal Dictionary<string, byte[]> BlobContents { get; } = new(blobContents, StringComparer.Ordinal);
        internal List<CoverageRunProcessRequest> BlobRequests { get; } = [];
        internal List<string> ValidAttributePaths { get; } = [];

        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Arguments.Contains("check-attr", StringComparer.Ordinal))
            {
                request.Lease.Complete();
                var paths = Encoding.UTF8.GetString(request.StandardInputBytes ?? []).Split('\0',
                    StringSplitOptions.RemoveEmptyEntries);
                if (attributeResponse is not null)
                    return new(0, attributeResponse, StandardOutput: attributeResponse);

                ValidAttributePaths.AddRange(paths);
                var output = string.Concat(paths.Select(path => $"{path}\0diff\0unspecified\0"));
                return new(0, output, StandardOutput: output);
            }

            if (request.Arguments.Count >= 3 && request.Arguments[^3] == "cat-file"
                && request.Arguments[^2] == "blob")
            {
                BlobRequests.Add(request);
                request.Lease.Complete();
                var objectId = request.Arguments[^1];
                var bytes = BlobContents[objectId];
                request.BudgetCharge(bytes.Length);
                var standardOutput = failure switch
                {
                    BlobReadFailure.MissingBytes => null,
                    BlobReadFailure.ShortBytes => bytes[..^1],
                    BlobReadFailure.LongBytes => [.. bytes, 0],
                    _ => bytes
                };
                if (objectId == invalidPngObject) standardOutput = CorruptSignature(bytes);
                return failure switch
                {
                    BlobReadFailure.Truncated => new(0, "truncated", OutputTruncated: true)
                    { StandardError = string.Empty },
                    BlobReadFailure.NonzeroExit => new(2, string.Empty)
                    { StandardError = string.Empty, BinaryStandardOutput = standardOutput },
                    BlobReadFailure.StandardError => new(0, "git diagnostic")
                    { StandardError = "git diagnostic", BinaryStandardOutput = standardOutput },
                    _ => new(0, string.Empty)
                    { StandardError = string.Empty, BinaryStandardOutput = standardOutput }
                };
            }

            var result = await _inner.RunAsync(request, cancellationToken);
            if (missingBlobInventory && request.Arguments.Contains("--raw", StringComparer.Ordinal)
                && request.Arguments.Contains("--find-renames=50%", StringComparer.Ordinal))
            {
                var output = result.StandardOutput ?? result.Output;
                output = output.Replace(oldPngObject, new string('c', oldPngObject.Length), StringComparison.Ordinal)
                    .Replace(newPngObject, new string('d', newPngObject.Length), StringComparison.Ordinal);
                return new(result.ExitCode, output, result.OutputTruncated, output)
                { StandardError = result.StandardError };
            }
            return result;
        }

        private static byte[] CorruptSignature(byte[] bytes)
        {
            var corrupted = bytes.ToArray();
            corrupted[0] ^= 1;
            return corrupted;
        }
    }

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
}
