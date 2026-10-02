using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceNoFollowArtifactExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ShouldStreamRegularFileIntoItsDeclaredArtifactSlot()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        var expected = new byte[] { 0, 1, 2, 0xff, 0x80 };
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "report.bin"), expected);
        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(temp.ArtifactRoot);

        var results = await EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            writer,
            [new EvidenceNoFollowArtifact("report", "subject/report.bin", "reports/report.bin")]);

        var result = Assert.Single(results);
        Assert.Equal("report", result.LogicalName);
        Assert.Equal(expected.LongLength, result.LengthBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task ExtractAsync_ShouldWriteAnEmptyArtifactWithTheEmptyContentDigest()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(TestPathUtils.PathUnder(temp.ScratchRoot, "subject"));
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(temp.ScratchRoot, "subject", "empty.bin"), []);
        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(temp.ArtifactRoot);

        var results = await EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            writer,
            [new EvidenceNoFollowArtifact("report", "subject/empty.bin", "reports/empty.bin")]);

        var result = Assert.Single(results);
        Assert.Equal(0, result.LengthBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), result.Sha256);
        Assert.Empty(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(temp.ArtifactRoot, "reports", "empty.bin")));
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task ExtractAsync_ShouldPreserveInputOrderAcrossDeclaredArtifactSlots()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        var summary = new byte[] { 4, 5, 6 };
        var report = new byte[] { 1, 2 };
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "summary.bin"), summary);
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "report.bin"), report);
        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(
            temp.ArtifactRoot,
            new EvidenceArtifactSlot("summary", "summaries", "application/octet-stream", Required: false, EvidenceArtifactWriter.MaximumTotalArtifactBytes));

        var results = await EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            writer,
            [
                new EvidenceNoFollowArtifact("summary", "subject/summary.bin", "summaries/summary.bin"),
                new EvidenceNoFollowArtifact("report", "subject/report.bin", "reports/report.bin"),
            ]);

        Assert.Equal(new[] { "summary", "report" }, results.Select(static result => result.LogicalName));
        Assert.Equal(summary, await File.ReadAllBytesAsync(Path.Join(temp.ArtifactRoot, "summaries", "summary.bin")));
        Assert.Equal(report, await File.ReadAllBytesAsync(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("subject/../outside")]
    [InlineData("subject/./report.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("//outside")]
    [InlineData("subject//report.bin")]
    [InlineData("subject\\report.bin")]
    [InlineData("./report.bin")]
    public async Task ExtractAsync_ShouldRejectNonCanonicalOrTraversingSourcePaths(string sourcePath)
    {
        using var invalidRoot = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            EvidenceNoFollowArtifactExtractor.ExtractAsync(
                invalidRoot,
                CreateWriter(Path.GetTempPath()),
                [new EvidenceNoFollowArtifact("report", sourcePath, "reports/report.bin")]));
        Assert.NotNull(exception.ParamName);
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectNullInputsAndInvalidArtifactRecordsBeforeOpeningRoot()
    {
        using var invalidRoot = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        var writer = CreateWriter(Path.GetTempPath());

        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            null!,
            writer,
            []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            null!,
            []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            writer,
            null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            writer,
            [null!]));
        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            writer,
            [new EvidenceNoFollowArtifact("  ", "subject/report.bin", "reports/report.bin")]));
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectMalformedOrOverlongSourceAndDestinationPaths()
    {
        using var invalidRoot = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        var writer = CreateWriter(Path.GetTempPath());
        string?[] invalidPaths =
        [
            null,
            "",
            " ",
            "subject/\0report.bin",
            "subject/\u0001report.bin",
            "\ud800",
            new string('a', 256),
            new string('a', 4_097),
        ];

        foreach (var invalidPath in invalidPaths)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
                invalidRoot,
                writer,
                [new EvidenceNoFollowArtifact("report", invalidPath!, "reports/report.bin")]));
            await Assert.ThrowsAsync<ArgumentException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
                invalidRoot,
                writer,
                [new EvidenceNoFollowArtifact("report", "subject/report.bin", invalidPath!)]));
        }
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectDuplicateLogicalSourceAndDestinationEntriesBeforeOpeningRoot()
    {
        using var invalidRoot = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        var writer = CreateWriter(Path.GetTempPath());

        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            writer,
            [
                new EvidenceNoFollowArtifact("report", "subject/one.bin", "reports/one.bin"),
                new EvidenceNoFollowArtifact("report", "subject/two.bin", "reports/two.bin"),
            ]));
        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            writer,
            [
                new EvidenceNoFollowArtifact("first", "subject/one.bin", "reports/one.bin"),
                new EvidenceNoFollowArtifact("second", "subject/one.bin", "reports/two.bin"),
            ]));
        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            writer,
            [
                new EvidenceNoFollowArtifact("first", "subject/one.bin", "reports/same.bin"),
                new EvidenceNoFollowArtifact("second", "subject/two.bin", "reports/same.bin"),
            ]));
    }

    [Fact]
    public async Task ExtractAsync_ShouldEnforceFileCountAndCallerLimitCeilings()
    {
        using var invalidRoot = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        var tooManyFiles = Enumerable.Range(0, EvidenceNoFollowArtifactExtractionLimits.MaximumAllowedFileCount + 1)
            .Select(index => new EvidenceNoFollowArtifact($"artifact-{index}", $"subject/{index}.bin", $"reports/{index}.bin"))
            .ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            CreateWriter(Path.GetTempPath()),
            tooManyFiles));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            CreateWriter(Path.GetTempPath()),
            [],
            new EvidenceNoFollowArtifactExtractionLimits { MaximumTotalBytes = EvidenceArtifactWriter.MaximumTotalArtifactBytes + 1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            CreateWriter(Path.GetTempPath()),
            [],
            new EvidenceNoFollowArtifactExtractionLimits { MaximumDuration = TimeSpan.FromMinutes(3) }));
    }

    [Fact]
    public async Task ExtractAsync_ShouldAcceptTheMaximumAllowedArtifactCount()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        var maximumFileCount = EvidenceNoFollowArtifactExtractionLimits.MaximumAllowedFileCount;
        var additionalSlots = Enumerable.Range(1, maximumFileCount - 1)
            .Select(index => new EvidenceArtifactSlot(
                $"artifact-{index}",
                $"artifacts/{index}",
                "application/octet-stream",
                Required: false,
                MaximumBytes: 1))
            .ToArray();
        var writer = CreateWriter(Path.Join(temp.ArtifactRoot, "maximum-count"), additionalSlots);
        var artifacts = new List<EvidenceNoFollowArtifact>(maximumFileCount)
        {
            new("report", "subject/0.bin", "reports/0.bin"),
        };

        for (var index = 0; index < maximumFileCount; index++)
        {
            await File.WriteAllBytesAsync(
                Path.Join(temp.ScratchRoot, "subject", $"{index}.bin"),
                [(byte)index]);
            if (index > 0)
            {
                artifacts.Add(new EvidenceNoFollowArtifact(
                    $"artifact-{index}",
                    $"subject/{index}.bin",
                    $"artifacts/{index}/{index}.bin"));
            }
        }

        using var root = OpenTrustedRoot(temp.ScratchRoot);

        var results = await EvidenceNoFollowArtifactExtractor.ExtractAsync(root, writer, artifacts);

        Assert.Equal(maximumFileCount, results.Count);
        Assert.Equal(maximumFileCount, writer.WrittenArtifacts.Count);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectInvalidMinimumLimitsAndCallerFileCountBeforeOpeningRoot()
    {
        using var invalidRoot = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        var writer = CreateWriter(Path.GetTempPath());
        var invalidLimits = new[]
        {
            new EvidenceNoFollowArtifactExtractionLimits { MaximumFileCount = 0 },
            new EvidenceNoFollowArtifactExtractionLimits { MaximumFileCount = -1 },
            new EvidenceNoFollowArtifactExtractionLimits { MaximumTotalBytes = 0 },
            new EvidenceNoFollowArtifactExtractionLimits { MaximumTotalBytes = -1 },
            new EvidenceNoFollowArtifactExtractionLimits { MaximumDuration = TimeSpan.Zero },
            new EvidenceNoFollowArtifactExtractionLimits { MaximumDuration = TimeSpan.FromTicks(-1) },
        };

        foreach (var limits in invalidLimits)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
                invalidRoot,
                writer,
                [],
                limits));
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            writer,
            [
                new EvidenceNoFollowArtifact("report", "subject/one.bin", "reports/one.bin"),
                new EvidenceNoFollowArtifact("summary", "subject/two.bin", "summaries/two.bin"),
            ],
            new EvidenceNoFollowArtifactExtractionLimits { MaximumFileCount = 1 }));
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectSymlinksAndFilesOutsideTheScratchRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        Directory.CreateDirectory(Path.Join(temp.OutsideRoot, "private"));
        var outsideFile = Path.Join(temp.OutsideRoot, "private", "secret.bin");
        await File.WriteAllTextAsync(outsideFile, "outside");
        File.CreateSymbolicLink(Path.Join(temp.ScratchRoot, "subject", "linked.bin"), outsideFile);
        Directory.CreateSymbolicLink(Path.Join(temp.ScratchRoot, "linked-directory"), Path.Join(temp.OutsideRoot, "private"));
        using var root = OpenTrustedRoot(temp.ScratchRoot);

        await Assert.ThrowsAnyAsync<IOException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            CreateWriter(temp.ArtifactRoot),
            [new EvidenceNoFollowArtifact("report", "subject/linked.bin", "reports/report.bin")]));
        await Assert.ThrowsAnyAsync<IOException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            CreateWriter(temp.ArtifactRoot),
            [new EvidenceNoFollowArtifact("report", "linked-directory/secret.bin", "reports/report.bin")]));
        Assert.False(File.Exists(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectHardlinksDirectoriesAndAggregateByteOverflow()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        await File.WriteAllTextAsync(Path.Join(temp.ScratchRoot, "subject", "one.bin"), "hard-linked");
        if (Link(
                Path.Join(temp.ScratchRoot, "subject", "one.bin"),
                Path.Join(temp.ScratchRoot, "subject", "second-name.bin")) != 0)
        {
            throw new IOException($"Could not create the Linux hard-link fixture (errno {Marshal.GetLastPInvokeError()}).");
        }
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject", "directory"));
        using var root = OpenTrustedRoot(temp.ScratchRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            CreateWriter(temp.ArtifactRoot),
            [new EvidenceNoFollowArtifact("report", "subject/one.bin", "reports/report.bin")]));
        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            CreateWriter(temp.ArtifactRoot),
            [new EvidenceNoFollowArtifact("report", "subject/directory", "reports/report.bin")]));

        File.Delete(Path.Join(temp.ScratchRoot, "subject", "second-name.bin"));
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "small.bin"), [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            CreateWriter(temp.ArtifactRoot),
            [new EvidenceNoFollowArtifact("report", "subject/small.bin", "reports/report.bin")],
            new EvidenceNoFollowArtifactExtractionLimits { MaximumTotalBytes = 2 }));
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectAggregateOverflowBeforePromotingAnyArtifact()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "one.bin"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "two.bin"), [3, 4]);
        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(
            temp.ArtifactRoot,
            new EvidenceArtifactSlot("summary", "summaries", "application/octet-stream", Required: false, EvidenceArtifactWriter.MaximumTotalArtifactBytes));

        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            writer,
            [
                new EvidenceNoFollowArtifact("report", "subject/one.bin", "reports/one.bin"),
                new EvidenceNoFollowArtifact("summary", "subject/two.bin", "summaries/two.bin"),
            ],
            new EvidenceNoFollowArtifactExtractionLimits { MaximumTotalBytes = 3 }));

        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(File.Exists(Path.Join(temp.ArtifactRoot, "reports", "one.bin")));
        Assert.False(File.Exists(Path.Join(temp.ArtifactRoot, "summaries", "two.bin")));
    }

    [Fact]
    public async Task ExtractAsync_ShouldAcceptArtifactsAtCallerFileCountAndByteLimits()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        var report = new byte[] { 1, 2 };
        var summary = new byte[] { 3, 4 };
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "report.bin"), report);
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "summary.bin"), summary);
        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(
            temp.ArtifactRoot,
            new EvidenceArtifactSlot("summary", "summaries", "application/octet-stream", Required: false, EvidenceArtifactWriter.MaximumTotalArtifactBytes));

        var results = await EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            writer,
            [
                new EvidenceNoFollowArtifact("report", "subject/report.bin", "reports/report.bin"),
                new EvidenceNoFollowArtifact("summary", "subject/summary.bin", "summaries/summary.bin"),
            ],
            new EvidenceNoFollowArtifactExtractionLimits { MaximumFileCount = 2, MaximumTotalBytes = 4 });

        Assert.Equal(new[] { "report", "summary" }, results.Select(static result => result.LogicalName));
        Assert.Equal(report, await File.ReadAllBytesAsync(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
        Assert.Equal(summary, await File.ReadAllBytesAsync(Path.Join(temp.ArtifactRoot, "summaries", "summary.bin")));
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task ExtractAsync_ShouldHonorTheArtifactSlotSizeAtItsExactBoundary()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        var contents = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "report.bin"), contents);
        using var root = OpenTrustedRoot(temp.ScratchRoot);
        EvidenceArtifactWriter CreateSlotLimitedWriter(string artifactRoot, long maximumBytes) => CreateWriter(
            artifactRoot,
            new EvidenceArtifactSlot(
                "bounded-report",
                "bounded-reports",
                "application/octet-stream",
                Required: false,
                maximumBytes));

        var exactLimitRoot = Path.Join(temp.ArtifactRoot, "exact-limit");
        var exactLimitWriter = CreateSlotLimitedWriter(exactLimitRoot, contents.Length);
        var results = await EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            exactLimitWriter,
            [new EvidenceNoFollowArtifact("bounded-report", "subject/report.bin", "bounded-reports/report.bin")]);

        Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Join(exactLimitRoot, "bounded-reports", "report.bin")));
        Assert.Equal(contents.LongLength, Assert.Single(results).LengthBytes);
        Assert.True(await exactLimitWriter.VerifyWrittenArtifactsAsync());

        var smallerLimitRoot = Path.Join(temp.ArtifactRoot, "smaller-limit");
        var smallerLimitWriter = CreateSlotLimitedWriter(smallerLimitRoot, contents.Length - 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            smallerLimitWriter,
            [new EvidenceNoFollowArtifact("bounded-report", "subject/report.bin", "bounded-reports/report.bin")]));

        Assert.Empty(smallerLimitWriter.WrittenArtifacts);
        Assert.False(File.Exists(Path.Join(smallerLimitRoot, "bounded-reports", "report.bin")));
    }

    [Fact]
    public async Task ExtractAsync_ShouldKeepEarlierArtifactWhenLaterArtifactExceedsItsSlotLimit()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(TestPathUtils.PathUnder(temp.ScratchRoot, "subject"));
        var reportContents = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(temp.ScratchRoot, "subject", "report.bin"), reportContents);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(temp.ScratchRoot, "subject", "summary.bin"), [4, 5]);
        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(
            temp.ArtifactRoot,
            new EvidenceArtifactSlot("summary", "summaries", "application/octet-stream", Required: false, MaximumBytes: 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            writer,
            [
                new EvidenceNoFollowArtifact("report", "subject/report.bin", "reports/report.bin"),
                new EvidenceNoFollowArtifact("summary", "subject/summary.bin", "summaries/summary.bin"),
            ]));

        Assert.Equal("report", Assert.Single(writer.WrittenArtifacts).LogicalName);
        Assert.Equal(reportContents, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(temp.ArtifactRoot, "reports", "report.bin")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(temp.ArtifactRoot, "summaries", "summary.bin")));
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectNonDirectoryRootAndMissingSourcesWithoutWriting()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        var rootFile = Path.Join(temp.ScratchRoot, "not-a-directory");
        await File.WriteAllTextAsync(rootFile, "not a directory");
        using var nonDirectoryRoot = File.OpenHandle(rootFile);
        var writer = CreateWriter(temp.ArtifactRoot);
        var request = new[] { new EvidenceNoFollowArtifact("report", "subject/missing.bin", "reports/report.bin") };

        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            nonDirectoryRoot,
            writer,
            request));

        using var root = OpenTrustedRoot(temp.ScratchRoot);
        await Assert.ThrowsAnyAsync<IOException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            writer,
            request));

        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(File.Exists(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectInvalidRootDescriptorBeforeOpeningSources()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var invalidRoot = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            CreateWriter(Path.GetTempPath()),
            [new EvidenceNoFollowArtifact("report", "subject/report.bin", "reports/report.bin")]));
    }

    [Fact]
    public async Task ExtractAsync_ShouldResolveSourcesBeneathTheAlreadyOpenedRootDescriptor()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        var expected = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "report.bin"), expected);
        using var root = OpenTrustedRoot(temp.ScratchRoot);

        Directory.Move(temp.ScratchRoot, temp.OutsideRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "report.bin"), [9, 8, 7]);

        var results = await EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            CreateWriter(temp.ArtifactRoot),
            [new EvidenceNoFollowArtifact("report", "subject/report.bin", "reports/report.bin")]);

        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
        Assert.Equal(expected.LongLength, Assert.Single(results).LengthBytes);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "report.bin")));
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("append")]
    [InlineData("touch")]
    public async Task ExtractAsyncForTesting_ShouldRejectSourceMutationBeforeTheVerificationPass(string mutation)
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        var sourcePath = Path.Join(temp.ScratchRoot, "subject", "report.bin");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(temp.ArtifactRoot);

        void MutateSource(string sourceRelativePath)
        {
            Assert.Equal("subject/report.bin", sourceRelativePath);
            if (mutation == "append")
            {
                using var changedSource = new FileStream(sourcePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                changedSource.WriteByte(4);
                return;
            }

            if (mutation == "touch")
            {
                File.SetLastWriteTimeUtc(sourcePath, File.GetLastWriteTimeUtc(sourcePath).AddSeconds(-2));
                return;
            }

            Assert.Equal("replace", mutation);
            File.WriteAllBytes(sourcePath, [9, 8, 7]);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsyncForTesting(
            root,
            writer,
            [new EvidenceNoFollowArtifact("report", "subject/report.bin", "reports/report.bin")],
            MutateSource));

        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(File.Exists(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
    }

    [Fact]
    public async Task ExtractAsyncForTesting_ShouldPropagateCallerCancellationDuringVerification()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        await File.WriteAllBytesAsync(Path.Join(temp.ScratchRoot, "subject", "report.bin"), [1, 2, 3]);
        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(temp.ArtifactRoot);
        using var cancellation = new CancellationTokenSource();

        void CancelAfterFirstPass(string sourceRelativePath)
        {
            Assert.Equal("subject/report.bin", sourceRelativePath);
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsyncForTesting(
            root,
            writer,
            [new EvidenceNoFollowArtifact("report", "subject/report.bin", "reports/report.bin")],
            CancelAfterFirstPass,
            cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(File.Exists(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectFifoWithoutBlockingOrWriting()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.ScratchRoot);
        Directory.CreateDirectory(temp.ArtifactRoot);
        Directory.CreateDirectory(Path.Join(temp.ScratchRoot, "subject"));
        var fifoPath = Path.Join(temp.ScratchRoot, "subject", "pipe");
        if (MakeFifo(fifoPath, 0x180) != 0)
        {
            throw new IOException($"Could not create the Linux FIFO fixture (errno {Marshal.GetLastPInvokeError()}).");
        }

        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(temp.ArtifactRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            writer,
            [new EvidenceNoFollowArtifact("report", "subject/pipe", "reports/report.bin")]));

        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(File.Exists(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
    }

    [Fact]
    public async Task ExtractAsync_ShouldRejectPreCanceledRequestsBeforeOpeningRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedPlatformFailsClosedAsync();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var invalidRoot = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            CreateWriter(Path.GetTempPath()),
            [],
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ExtractAsync_OnUnsupportedPlatform_ShouldFailClosed()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        await AssertUnsupportedPlatformFailsClosedAsync();
    }

    private static async Task AssertUnsupportedPlatformFailsClosedAsync()
    {
        using var invalidRoot = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => EvidenceNoFollowArtifactExtractor.ExtractAsync(
            invalidRoot,
            CreateWriter(Path.GetTempPath()),
            []));
    }

    private static EvidenceArtifactWriter CreateWriter(string artifactRoot, params EvidenceArtifactSlot[] additionalSlots)
    {
        var slots = new[]
        {
            new EvidenceArtifactSlot("report", "reports", "application/octet-stream", Required: false, EvidenceArtifactWriter.MaximumTotalArtifactBytes),
        }.Concat(additionalSlots);
        var producer = new EvidenceProducerDeclaration(
            "extractor-test",
            "test",
            "1.0.0",
            [],
            [],
            slots.ToArray(),
            TimeoutSeconds: 120);
        return new EvidenceArtifactWriter(producer, artifactRoot);
    }

    private static SafeFileHandle OpenTrustedRoot(string path)
    {
        // The test controls this fixture path; the extractor itself verifies the opened handle is a directory.
        var descriptor = Open(path, flags: 0);
        if (descriptor < 0)
        {
            throw new IOException($"Could not open the Linux scratch root (errno {Marshal.GetLastPInvokeError()}).");
        }

        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Join(Path.GetTempPath(), "evidence-nofollow-" + Guid.NewGuid().ToString("N"));

        public string ScratchRoot => Path.Join(_path, "scratch");

        public string ArtifactRoot => Path.Join(_path, "artifacts");

        public string OutsideRoot => Path.Join(_path, "outside");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_path, recursive: true);
            }
            catch (IOException)
            {
                // Preserve the test result if the operating system still has a transient handle open.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the test result if the operating system still has a transient handle open.
            }
        }
    }
}
