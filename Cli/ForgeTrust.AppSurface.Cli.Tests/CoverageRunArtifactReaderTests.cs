using System.Diagnostics;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.CoverageArtifacts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunArtifactReaderTests
{
    [Fact]
    public void OpenRegularFile_ShouldReadOrdinaryArtifact()
    {
        using var repo = ArtifactTempDirectory.Create("appsurface-coverage-artifact-");
        var projectOutput = Path.Join(repo.Path, "project");
        var raw = Path.Join(projectOutput, "collector-results", "invocation");
        Directory.CreateDirectory(raw);
        var candidate = repo.WriteFile(
            "project/collector-results/invocation/coverage.cobertura.xml",
            "<coverage />");

        using var stream = CoverageRunArtifactReader.OpenRegularFile(projectOutput, raw, candidate);
        using var reader = new StreamReader(stream);

        Assert.Equal("<coverage />", reader.ReadToEnd());
    }

    [Fact]
    public void EnsureWindowsDirectoryIdentityUnchanged_ShouldAcceptMatchingIdentity()
    {
        var expected = new CoverageRunArtifactReader.WindowsDirectoryIdentity(
            Path.Join("root", "attachment"),
            new CoverageRunArtifactReader.WindowsFileIdentity(17, 42));

        CoverageRunArtifactReader.EnsureWindowsDirectoryIdentityUnchanged(expected, expected);
    }

    [Fact]
    public void WindowsDirectoryShareMode_ShouldDenyDeleteWhileValidatingCandidate()
    {
        const uint fileShareDelete = 0x00000004;

        Assert.Equal(0u, CoverageRunArtifactReader.WindowsDirectoryShareMode & fileShareDelete);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../coverage.cobertura.xml")]
    [InlineData("attachment/../coverage.cobertura.xml")]
    public void ValidateUnixRelativeArtifactPath_ShouldRejectNavigationComponents(string relativePath)
    {
        var exception = Assert.Throws<IOException>(() =>
            CoverageRunArtifactReader.ValidateUnixRelativeArtifactPath(relativePath));

        Assert.Contains("artifact path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateUnixRelativeArtifactPath_ShouldAcceptArtifactDescent()
    {
        var components = CoverageRunArtifactReader.ValidateUnixRelativeArtifactPath(
            Path.Join("attachment", "coverage.cobertura.xml"));

        Assert.Equal(["attachment", "coverage.cobertura.xml"], components);
    }

    [Theory]
    [InlineData(18, 42)]
    [InlineData(17, 43)]
    public void EnsureWindowsDirectoryIdentityUnchanged_ShouldRejectReplacedDirectory(
        uint volumeSerialNumber,
        ulong fileId)
    {
        var expected = new CoverageRunArtifactReader.WindowsDirectoryIdentity(
            Path.Join("root", "attachment"),
            new CoverageRunArtifactReader.WindowsFileIdentity(17, 42));
        var actual = new CoverageRunArtifactReader.WindowsDirectoryIdentity(
            expected.Path,
            new CoverageRunArtifactReader.WindowsFileIdentity(volumeSerialNumber, fileId));

        var exception = Assert.Throws<IOException>(() =>
            CoverageRunArtifactReader.EnsureWindowsDirectoryIdentityUnchanged(expected, actual));

        Assert.Contains("changed while the artifact was opened", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRegularFile_ShouldRejectFifoWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = ArtifactTempDirectory.Create("appsurface-coverage-artifact-");
        var projectOutput = Path.Join(repo.Path, "project");
        var raw = Path.Join(projectOutput, "collector-results", "invocation");
        Directory.CreateDirectory(raw);
        var candidate = Path.Join(raw, "coverage.cobertura.xml");
        Assert.Equal(0, MakeFifo(candidate, Convert.ToUInt32("600", 8)));

        var releaseBlockedReader = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var descriptor = OpenReadWriteNonBlocking(candidate, UnixOpenReadWrite | UnixOpenNonBlocking);
            if (descriptor >= 0)
            {
                Close(descriptor);
            }
        });
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<IOException>(() =>
            CoverageRunArtifactReader.OpenRegularFile(projectOutput, raw, candidate));

        stopwatch.Stop();
        Assert.Contains("not a regular file", exception.Message, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(750), $"FIFO rejection took {stopwatch.Elapsed}.");
        await releaseBlockedReader;
    }

    [Fact]
    public void OpenRegularFile_ShouldUseCaseSensitiveContainmentOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = ArtifactTempDirectory.Create("appsurface-coverage-artifact-");
        var projectOutput = Path.Join(repo.Path, "project");
        var raw = Path.Join(projectOutput, "collector-results", "Invocation");
        Directory.CreateDirectory(raw);
        var candidate = repo.WriteFile(
            "project/collector-results/Invocation/coverage.cobertura.xml",
            "<coverage />");
        var differentlyCasedCandidate = candidate.Replace(
            $"{Path.DirectorySeparatorChar}Invocation{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}invocation{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

        var exception = Assert.Throws<IOException>(() => CoverageRunArtifactReader.OpenRegularFile(
            projectOutput,
            raw,
            differentlyCasedCandidate));

        Assert.Contains("escaped its invocation directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenRegularFile_ShouldPreventWindowsParentReplacementDuringCandidateOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = ArtifactTempDirectory.Create("appsurface-coverage-artifact-");
        var projectOutput = Path.Join(repo.Path, "project");
        var raw = Path.Join(projectOutput, "collector-results", "invocation");
        var attachment = Path.Join(raw, "attachment");
        Directory.CreateDirectory(attachment);
        var candidate = repo.WriteFile(
            "project/collector-results/invocation/attachment/coverage.cobertura.xml",
            "<coverage />");
        var moved = Path.Join(raw, "attachment-moved");
        IOException? replacementFailure = null;

        using var stream = CoverageRunArtifactReader.OpenRegularFile(
            projectOutput,
            raw,
            candidate,
            beforeWindowsCandidateOpen: () =>
            {
                try
                {
                    Directory.Move(attachment, moved);
                }
                catch (IOException ex)
                {
                    replacementFailure = ex;
                }
            });
        using var reader = new StreamReader(stream);

        Assert.NotNull(replacementFailure);
        Directory.Move(attachment, moved);
        Assert.Equal("<coverage />", reader.ReadToEnd());
    }

    [Fact]
    public void OpenRegularFile_ShouldRejectWindowsReparsePointArtifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = ArtifactTempDirectory.Create("appsurface-coverage-artifact-");
        var projectOutput = Path.Join(repo.Path, "project");
        var raw = Path.Join(projectOutput, "collector-results", "invocation");
        Directory.CreateDirectory(raw);
        var external = repo.WriteFile("external.xml", "<coverage marker=\"external\" />");
        var candidate = Path.Join(raw, "coverage.cobertura.xml");
        File.CreateSymbolicLink(candidate, external);

        var exception = Assert.Throws<IOException>(() =>
            CoverageRunArtifactReader.OpenRegularFile(projectOutput, raw, candidate));

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenRegularFile_ShouldRejectWindowsReparsePointParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = ArtifactTempDirectory.Create("appsurface-coverage-artifact-");
        var projectOutput = Path.Join(repo.Path, "project");
        var raw = Path.Join(projectOutput, "collector-results", "invocation");
        Directory.CreateDirectory(raw);
        var externalDirectory = Path.Join(repo.Path, "external");
        Directory.CreateDirectory(externalDirectory);
        repo.WriteFile("external/coverage.cobertura.xml", "<coverage marker=\"external\" />");
        var attachment = Path.Join(raw, "attachment");
        Directory.CreateSymbolicLink(attachment, externalDirectory);
        var candidate = Path.Join(attachment, "coverage.cobertura.xml");

        var exception = Assert.Throws<IOException>(() =>
            CoverageRunArtifactReader.OpenRegularFile(projectOutput, raw, candidate));

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalizeDetailedAsync_ShouldRejectArtifactReplacedBySymbolicLinkBeforeOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = ArtifactTempDirectory.Create("appsurface-coverage-artifact-");
        var projectOutput = Path.Join(repo.Path, "project");
        var raw = Path.Join(projectOutput, "collector-results", "invocation");
        Directory.CreateDirectory(raw);
        var candidate = repo.WriteFile(
            "project/collector-results/invocation/coverage.cobertura.xml",
            "<coverage marker=\"original\" />");
        var external = repo.WriteFile("external.xml", "<coverage marker=\"external\" />");
        var invocation = new CoverageRunDriverInvocation(CoverageRunDriver.Collector, projectOutput, raw, []);

        var result = await CoverageRunDriverStrategy.NormalizeDetailedAsync(
            invocation,
            CancellationToken.None,
            beforeArtifactOpen: () =>
            {
                File.Delete(candidate);
                File.CreateSymbolicLink(candidate, external);
            });

        Assert.Equal("unreadable", result.Status);
        Assert.False(File.Exists(Path.Join(projectOutput, "coverage.cobertura.xml")));
    }

    [Fact]
    public async Task NormalizeDetailedAsync_ShouldRejectParentDirectoryReplacedBySymbolicLinkBeforeOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = ArtifactTempDirectory.Create("appsurface-coverage-artifact-");
        var projectOutput = Path.Join(repo.Path, "project");
        var raw = Path.Join(projectOutput, "collector-results", "invocation");
        var attachment = Path.Join(raw, "attachment");
        Directory.CreateDirectory(attachment);
        repo.WriteFile(
            "project/collector-results/invocation/attachment/coverage.cobertura.xml",
            "<coverage marker=\"original\" />");
        var externalDirectory = Path.Join(repo.Path, "external");
        Directory.CreateDirectory(externalDirectory);
        repo.WriteFile("external/coverage.cobertura.xml", "<coverage marker=\"external\" />");
        var invocation = new CoverageRunDriverInvocation(CoverageRunDriver.Collector, projectOutput, raw, []);

        var result = await CoverageRunDriverStrategy.NormalizeDetailedAsync(
            invocation,
            CancellationToken.None,
            beforeArtifactOpen: () =>
            {
                Directory.Move(attachment, attachment + ".original");
                Directory.CreateSymbolicLink(attachment, externalDirectory);
            });

        Assert.Equal("unreadable", result.Status);
        Assert.False(File.Exists(Path.Join(projectOutput, "coverage.cobertura.xml")));
    }

    [Fact]
    public async Task NormalizeDetailedAsync_ShouldRejectRawAncestorReplacedBySymbolicLinkBeforeOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repo = ArtifactTempDirectory.Create("appsurface-coverage-artifact-");
        var projectOutput = Path.Join(repo.Path, "project");
        var collectorResults = Path.Join(projectOutput, "collector-results");
        var raw = Path.Join(collectorResults, "invocation");
        Directory.CreateDirectory(raw);
        repo.WriteFile(
            "project/collector-results/invocation/coverage.cobertura.xml",
            "<coverage marker=\"original\" />");
        var externalCollectorResults = Path.Join(repo.Path, "external-collector-results");
        Directory.CreateDirectory(Path.Join(externalCollectorResults, "invocation"));
        repo.WriteFile(
            "external-collector-results/invocation/coverage.cobertura.xml",
            "<coverage marker=\"external\" />");
        var invocation = new CoverageRunDriverInvocation(CoverageRunDriver.Collector, projectOutput, raw, []);

        var result = await CoverageRunDriverStrategy.NormalizeDetailedAsync(
            invocation,
            CancellationToken.None,
            beforeArtifactOpen: () =>
            {
                Directory.Move(collectorResults, collectorResults + ".original");
                Directory.CreateSymbolicLink(collectorResults, externalCollectorResults);
            });

        Assert.Equal("unreadable", result.Status);
        Assert.False(File.Exists(Path.Join(projectOutput, "coverage.cobertura.xml")));
    }

    private static int UnixOpenNonBlocking => OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;
    private const int UnixOpenReadWrite = 2;

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenReadWriteNonBlocking(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    private sealed class ArtifactTempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static ArtifactTempDirectory Create(string prefix)
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new ArtifactTempDirectory(path);
        }

        public string WriteFile(string relativePath, string content)
        {
            var path = TestPathUtils.PathUnder(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
