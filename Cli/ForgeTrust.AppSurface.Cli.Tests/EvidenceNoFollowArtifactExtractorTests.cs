using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;
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
    public async Task ExtractAsync_ShouldRejectContentChangedDuringStreamingBeforePromotion()
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
        var sourcePath = Path.Join(temp.ScratchRoot, "subject", "large.bin");
        await using (var created = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
        {
            created.SetLength(64L * 1024 * 1024);
        }

        using var root = OpenTrustedRoot(temp.ScratchRoot);
        var writer = CreateWriter(temp.ArtifactRoot);
        var extraction = EvidenceNoFollowArtifactExtractor.ExtractAsync(
            root,
            writer,
            [new EvidenceNoFollowArtifact("report", "subject/large.bin", "reports/report.bin")]);

        var changed = false;
        await using (var mutator = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            while (!extraction.IsCompleted && !changed)
            {
                mutator.Position = 0;
                await mutator.WriteAsync(new byte[] { 0xff });
                await mutator.FlushAsync();
                changed = true;
            }
        }

        if (!changed)
        {
            await extraction;
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => extraction);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(File.Exists(Path.Join(temp.ArtifactRoot, "reports", "report.bin")));
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

    private static EvidenceArtifactWriter CreateWriter(string artifactRoot)
    {
        var producer = new EvidenceProducerDeclaration(
            "extractor-test",
            "test",
            "1.0.0",
            [],
            [],
            [new EvidenceArtifactSlot("report", "reports", "application/octet-stream", Required: false, EvidenceArtifactWriter.MaximumTotalArtifactBytes)],
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
