using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core.Tests;

public class PathUtilsTests : IDisposable
{
    private readonly string _testRoot;

    public PathUtilsTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PathUtilsTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testRoot);
    }

    [Fact]
    public void FindRepositoryRoot_ShouldFindRootWithGitFolder()
    {
        // Arrange
        var repoRoot = Path.Combine(_testRoot, "repo");
        var gitDir = Path.Combine(repoRoot, ".git");
        var nestedDir = Path.Combine(repoRoot, "src", "sub");

        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(nestedDir);

        // Act
        var result = PathUtils.FindRepositoryRoot(nestedDir);

        // Assert
        Assert.Equal(repoRoot, result);
    }

    [Fact]
    public void FindRepositoryRoot_ShouldReturnStartPath_WhenNoGitFolderFound()
    {
        // Arrange
        var someDir = Path.Combine(_testRoot, "no-git", "sub");
        Directory.CreateDirectory(someDir);

        // Act
        var result = PathUtils.FindRepositoryRoot(someDir);

        // Assert
        Assert.Equal(someDir, result);
    }

    [Fact]
    public void FindRepositoryRoot_ShouldReturnRootPath_ForRootInput()
    {
        var rootPath = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrEmpty(rootPath));

        var result = PathUtils.FindRepositoryRoot(rootPath);

        Assert.Equal(rootPath, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FindRepositoryRoot_ShouldThrow_OnNullOrWhitespace(string? path)
    {
        Assert.Throws<ArgumentException>(() => PathUtils.FindRepositoryRoot(path!));
    }

    [Fact]
    public void FindRepositoryRoot_ShouldWalkUpToExistingDirectory_WhenPathDoesNotExist()
    {
        // Arrange
        var repoRoot = Path.Combine(_testRoot, "existing-repo");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        var nonExistentPath = Path.Combine(repoRoot, "non-existent", "child");

        // Act: repo exists, child does not. It should walk up to repoRoot then find .git.
        var result = PathUtils.FindRepositoryRoot(nonExistentPath);

        // Assert
        Assert.Equal(repoRoot, result);
    }

    [Fact]
    public void FindRepositoryRoot_ShouldLogWarning_WhenPathDoesNotExist()
    {
        // Arrange
        var repoRoot = Path.Combine(_testRoot, "logged-existing-repo");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        var nonExistentPath = Path.Combine(repoRoot, "missing", "child");
        var logger = new TestLogger();

        // Act
        var result = PathUtils.FindRepositoryRoot(nonExistentPath, logger);

        // Assert
        Assert.Equal(repoRoot, result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1001, entry.EventId.Id);
        Assert.Contains(nonExistentPath, entry.Message);
        Assert.Contains(repoRoot, entry.Message);
    }

    [Fact]
    public void FindRepositoryRoot_WithLoggerOverload_ThrowsWhenLoggerIsNull()
    {
        // Arrange
        var someDir = Path.Combine(_testRoot, "null-logger");
        Directory.CreateDirectory(someDir);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => PathUtils.FindRepositoryRoot(someDir, logger: null!));
    }

    [Fact]
    public void FindRepositoryRoot_WithLoggerOverload_ReturnsRepositoryRoot_WhenPathExists()
    {
        // Arrange
        var repoRoot = Path.Combine(_testRoot, "explicit-logger-repo");
        var nestedDir = Path.Combine(repoRoot, "src");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        Directory.CreateDirectory(nestedDir);
        var logger = new TestLogger();

        // Act
        var result = PathUtils.FindRepositoryRoot(nestedDir, logger);

        // Assert
        Assert.Equal(repoRoot, result);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void FindRepositoryRoot_WithLoggerOverload_DoesNotWarn_WhenStartPathIsFile()
    {
        // Arrange
        var repoRoot = Path.Combine(_testRoot, "file-start-repo");
        var nestedDir = Path.Combine(repoRoot, "src");
        var startFile = Path.Combine(nestedDir, "Program.cs");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(startFile, string.Empty);
        var logger = new TestLogger();

        // Act
        var result = PathUtils.FindRepositoryRoot(startFile, logger);

        // Assert
        Assert.Equal(repoRoot, result);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void FindRepositoryRoot_ShouldFindRootWithGitFile()
    {
        // Arrange
        var repoRoot = Path.Combine(_testRoot, "git-file-repo");
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, ".git"), "gitdir: ../something");
        var nestedDir = Path.Combine(repoRoot, "src");
        Directory.CreateDirectory(nestedDir);

        // Act
        var result = PathUtils.FindRepositoryRoot(nestedDir);

        // Assert
        Assert.Equal(repoRoot, result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, true);
            }
            catch
            {
                // Best effort
            }
        }
    }
}
