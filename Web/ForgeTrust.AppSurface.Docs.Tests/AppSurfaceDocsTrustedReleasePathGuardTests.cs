using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Unit")]
public sealed class AppSurfaceDocsTrustedReleasePathGuardTests : IDisposable
{
    private readonly string _tempDirectory;

    public AppSurfaceDocsTrustedReleasePathGuardTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), "appsurfacedocs-trusted-release-path-guard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void ResolveContentRootRelativePath_ShouldResolveRelativePathsAgainstContentRoot()
    {
        var result = AppSurfaceDocsTrustedReleasePathGuard.ResolveContentRootRelativePath(_tempDirectory, "docs/releases");

        Assert.Equal(
            Path.GetFullPath(Path.Join(_tempDirectory, "docs", "releases")),
            result);
    }

    [Fact]
    public void ResolveContentRootRelativePath_ShouldPreserveRootedPaths()
    {
        var rootedPath = Path.Join(_tempDirectory, "external");

        var result = AppSurfaceDocsTrustedReleasePathGuard.ResolveContentRootRelativePath(Path.GetTempPath(), rootedPath);

        Assert.Equal(Path.GetFullPath(rootedPath), result);
    }

    [Fact]
    public void TryResolveCatalogTreePath_ShouldResolveRelativePathUnderTrustedRoot()
    {
        var trustedRoot = Directory.CreateDirectory(Path.Join(_tempDirectory, "releases")).FullName;

        var resolved = AppSurfaceDocsTrustedReleasePathGuard.TryResolveCatalogTreePath(
            trustedRoot,
            "  1.2.3  ",
            out var exactTreePath,
            out var publicIssue,
            out var internalDetail);

        Assert.True(resolved);
        Assert.Equal(Path.Join(trustedRoot, "1.2.3"), exactTreePath);
        Assert.Null(publicIssue);
        Assert.Null(internalDetail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../release")]
    public void TryResolveCatalogTreePath_ShouldRejectUnsafeConfiguredPaths(string configuredPath)
    {
        var trustedRoot = Directory.CreateDirectory(Path.Join(_tempDirectory, "releases")).FullName;

        var resolved = AppSurfaceDocsTrustedReleasePathGuard.TryResolveCatalogTreePath(
            trustedRoot,
            configuredPath,
            out var exactTreePath,
            out var publicIssue,
            out var internalDetail);

        Assert.False(resolved);
        Assert.Null(exactTreePath);
        Assert.NotNull(publicIssue);
        Assert.NotNull(internalDetail);
    }

    [Fact]
    public void TryValidateDirectory_ShouldReportMissingDirectoryWithoutLeakingPath()
    {
        var result = AppSurfaceDocsTrustedReleasePathGuard.TryValidateDirectory(
            Path.Join(_tempDirectory, "missing"),
            "missing",
            "unsafe",
            out var publicIssue,
            out var internalDetail);

        Assert.False(result);
        Assert.Equal("missing", publicIssue);
        Assert.Contains("does not exist", internalDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateDirectory_ShouldReportUnsafeDirectory_WhenPathMetadataCannotBeRead()
    {
        var result = AppSurfaceDocsTrustedReleasePathGuard.TryValidateDirectory(
            "bad\0directory",
            "missing",
            "unsafe",
            out var publicIssue,
            out var internalDetail);

        Assert.False(result);
        Assert.Equal("unsafe", publicIssue);
        Assert.Contains("could not be inspected", internalDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../index.html")]
    public void TryValidateFileCandidate_ShouldRejectUnsafeRelativeFilePaths(string relativeFilePath)
    {
        var result = AppSurfaceDocsTrustedReleasePathGuard.TryValidateFileCandidate(
            _tempDirectory,
            relativeFilePath,
            out var physicalFilePath,
            out var denialReason);

        Assert.False(result);
        Assert.Equal(string.Empty, physicalFilePath);
        Assert.Equal("candidate path is not a safe relative path.", denialReason);
    }

    [Fact]
    public void TryValidateFileCandidate_ShouldRejectMissingLeafFile()
    {
        var result = AppSurfaceDocsTrustedReleasePathGuard.TryValidateFileCandidate(
            _tempDirectory,
            "missing.html",
            out var physicalFilePath,
            out var denialReason);

        Assert.False(result);
        Assert.Equal(AppSurfaceDocsTrustedReleasePathGuard.NormalizePhysicalPath(Path.Join(_tempDirectory, "missing.html")), physicalFilePath);
        Assert.Contains("does not exist", denialReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateFileCandidate_ShouldReportInspectionFailure_WhenPathMetadataCannotBeRead()
    {
        var result = AppSurfaceDocsTrustedReleasePathGuard.TryValidateFileCandidate(
            _tempDirectory,
            "bad\0file.html",
            out var physicalFilePath,
            out var denialReason);

        Assert.False(result);
        Assert.Equal(string.Empty, physicalFilePath);
        Assert.Contains("could not be inspected", denialReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateNoReparseSegments_ShouldRejectMissingTrustedRoot()
    {
        var missingRoot = Path.Join(_tempDirectory, "missing-root");
        var candidatePath = Path.Join(missingRoot, "index.html");

        var result = AppSurfaceDocsTrustedReleasePathGuard.TryValidateNoReparseSegments(
            missingRoot,
            candidatePath,
            expectLeafFile: true,
            out var denialReason);

        Assert.False(result);
        Assert.Contains("does not exist", denialReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateNoReparseSegments_ShouldRejectCandidateOutsideTrustedRoot()
    {
        var outsidePath = Path.Join(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");

        var result = AppSurfaceDocsTrustedReleasePathGuard.TryValidateNoReparseSegments(
            _tempDirectory,
            outsidePath,
            expectLeafFile: false,
            out var denialReason);

        Assert.False(result);
        Assert.Equal("path resolves outside the trusted root.", denialReason);
    }

    [Fact]
    public void TryValidateNoReparseSegments_ShouldAcceptTrustedRootItself()
    {
        var result = AppSurfaceDocsTrustedReleasePathGuard.TryValidateNoReparseSegments(
            _tempDirectory,
            _tempDirectory,
            expectLeafFile: false,
            out var denialReason);

        Assert.True(result);
        Assert.Null(denialReason);
    }

    [Fact]
    public void IsSameOrDescendant_ShouldRejectSiblingDirectories()
    {
        var root = Path.Join(_tempDirectory, "release");
        var sibling = Path.Join(_tempDirectory, "release-neighbor");

        Assert.False(AppSurfaceDocsTrustedReleasePathGuard.IsSameOrDescendant(root, sibling));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
