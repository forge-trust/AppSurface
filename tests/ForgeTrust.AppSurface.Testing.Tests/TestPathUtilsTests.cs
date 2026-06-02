using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Testing.Tests;

public sealed class TestPathUtilsTests : IDisposable
{
    private readonly string _tempRoot = Path.Join(Path.GetTempPath(), "appsurface-test-path-utils", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void PathUnder_ShouldAppendRelativeSegmentsWithoutRootedReset()
    {
        var basePath = Path.Join(_tempRoot, "appsurface");
        var path = TestPathUtils.PathUnder(basePath, "Web", "Docs", "Docs.csproj");

        Assert.Equal(
            Path.Join(basePath, $"Web{Path.DirectorySeparatorChar}Docs{Path.DirectorySeparatorChar}Docs.csproj"),
            path);
    }

    [Fact]
    public void PathUnder_ShouldAllowSamePathWhenSegmentIsDot()
    {
        var basePath = Path.Join(_tempRoot, "appsurface");
        var path = TestPathUtils.PathUnder(basePath, ".");

        Assert.Equal(Path.GetFullPath(basePath), path);
    }

    [Fact]
    public void PathUnder_ShouldRejectTraversalOutsideBasePath()
    {
        var basePath = Path.Join(_tempRoot, "appsurface");

        Assert.Throws<ArgumentException>(() => TestPathUtils.PathUnder(basePath, "..", "outside.txt"));
    }

    [Fact]
    public void PathUnder_ShouldRejectSiblingPrefixEscape()
    {
        var basePath = Path.Join(_tempRoot, "app");

        Assert.Throws<ArgumentException>(() => TestPathUtils.PathUnder(basePath, "..", "app-other", "file.txt"));
    }

    [Fact]
    public void PathUnder_ShouldRejectNullSegmentsArray()
    {
        Assert.Throws<ArgumentNullException>(() => TestPathUtils.PathUnder(_tempRoot, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PathUnder_ShouldRejectBlankBasePath(string basePath)
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.PathUnder(basePath, "Web", "Docs.csproj"));
    }

    [Fact]
    public void RelativePath_ShouldTrimSeparatorNoise()
    {
        var relativePath = TestPathUtils.RelativePath(
            $"Web{Path.DirectorySeparatorChar}",
            $"Docs{Path.AltDirectorySeparatorChar}",
            "Docs.csproj");

        Assert.Equal($"Web{Path.DirectorySeparatorChar}Docs{Path.DirectorySeparatorChar}Docs.csproj", relativePath);
    }

    [Fact]
    public void RelativePath_ShouldRejectEmptySegments()
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath());
    }

    [Fact]
    public void RelativePath_ShouldRejectNullSegment()
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath("Web", null!, "Docs.csproj"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RelativePath_ShouldRejectBlankSegment(string segment)
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath("Web", segment, "Docs.csproj"));
    }

    [Fact]
    public void RelativePath_ShouldRejectSeparatorOnlySegment()
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath(Path.DirectorySeparatorChar.ToString()));
    }

    [Fact]
    public void RelativePath_ShouldRejectCurrentPlatformRootedSegments()
    {
        var rootedSegment = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath("Web", rootedSegment, "Docs.csproj"));
    }

    [Theory]
    [InlineData(@"\repo\file.txt")]
    [InlineData("/repo/file.txt")]
    [InlineData("C:/repo/file.txt")]
    [InlineData(@"C:\repo\file.txt")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData("C:relative")]
    public void RelativePath_ShouldRejectWindowsAbsoluteLookingSegmentsOnAllPlatforms(string segment)
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath("Web", segment, "Docs.csproj"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../file.txt")]
    [InlineData(@"..\file.txt")]
    [InlineData("src/../file.txt")]
    public void RelativePath_ShouldRejectParentTraversalSegments(string segment)
    {
        Assert.Throws<ArgumentException>(() => TestPathUtils.RelativePath("Web", segment));
    }

    [Fact]
    public void PathUnder_ShouldNotResolveSymlinkTargets()
    {
        var basePath = Path.Join(_tempRoot, "root");
        var externalPath = Path.Join(_tempRoot, "external");
        Directory.CreateDirectory(basePath);
        Directory.CreateDirectory(externalPath);

        var linkPath = Path.Join(basePath, "linked");
        if (!TryCreateDirectorySymbolicLink(linkPath, externalPath))
        {
            return;
        }

        Assert.Equal(Path.GetFullPath(Path.Join(linkPath, "file.txt")), TestPathUtils.PathUnder(basePath, "linked", "file.txt"));
    }

    [Fact]
    public void FindRepoRoot_ShouldFindRootFromDirectoryStartPath()
    {
        var repoRoot = CreateRepositoryRoot();
        var nested = Directory.CreateDirectory(Path.Join(repoRoot, "src", "tests"));

        Assert.Equal(repoRoot, TestPathUtils.FindRepoRoot(nested.FullName));
    }

    [Fact]
    public void FindRepoRoot_ShouldFindRootFromFileStartPath()
    {
        var repoRoot = CreateRepositoryRoot();
        var nested = Directory.CreateDirectory(Path.Join(repoRoot, "src", "tests"));
        var filePath = Path.Join(nested.FullName, "SampleTests.cs");
        File.WriteAllText(filePath, "test");

        Assert.Equal(repoRoot, TestPathUtils.FindRepoRoot(filePath));
    }

    [Fact]
    public void FindRepoRoot_ShouldThrowWhenSolutionFileIsMissing()
    {
        Directory.CreateDirectory(_tempRoot);

        Assert.Throws<InvalidOperationException>(() => TestPathUtils.FindRepoRoot(_tempRoot));
    }

    private string CreateRepositoryRoot()
    {
        var repoRoot = Path.Join(_tempRoot, "repo");
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Join(repoRoot, "ForgeTrust.AppSurface.slnx"), "<Solution />");
        return repoRoot;
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
