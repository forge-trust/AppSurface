using Xunit;

namespace ForgeTrust.RazorWire.Cli.Tests;

public sealed class ExportOutputPathGuardsTests
{
    [Fact]
    public async Task WriteTextArtifactAsync_Should_Create_Ordinary_Artifact_Path()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-output-guard-").FullName;
        try
        {
            var artifactPath = Path.Join(tempDir, "docs", "index.html");

            await ExportOutputPathGuards.WriteTextArtifactAsync(
                tempDir,
                artifactPath,
                "HTML route artifact",
                "/docs",
                "<!DOCTYPE html><html></html>",
                encoding: null,
                CancellationToken.None);

            Assert.Equal("<!DOCTYPE html><html></html>", await File.ReadAllTextAsync(artifactPath));
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
        }
    }

    [Fact]
    public void ValidateWritableArtifactPath_Should_Reject_OutputRoot_Reparse()
    {
        var realRoot = Directory.CreateTempSubdirectory("razorwire-output-real-").FullName;
        var linkRoot = Path.Join(Path.GetTempPath(), $"razorwire-output-link-{Guid.NewGuid():N}");
        try
        {
            if (!TryCreateDirectorySymlink(linkRoot, realRoot))
            {
                throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
            }

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportOutputPathGuards.ValidateWritableArtifactPath(
                    linkRoot,
                    Path.Join(linkRoot, "index.html"),
                    "HTML route artifact",
                    "/",
                    "open-write"));

            AssertRwExport009(exception, ExportOutputPathGuards.OutputRootReparse);
        }
        finally
        {
            DeleteDirectoryIfExists(linkRoot);
            DeleteDirectoryIfExists(realRoot);
        }
    }

    [Fact]
    public async Task WriteTextArtifactAsync_Should_Reject_Future_OutputRoot_Below_Reparse_Ancestor()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-output-guard-").FullName;
        var outsideRoot = Directory.CreateTempSubdirectory("razorwire-output-outside-").FullName;
        try
        {
            var linkedParent = Path.Join(tempDir, "linked");
            if (!TryCreateDirectorySymlink(linkedParent, outsideRoot))
            {
                throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
            }

            var outputRoot = Path.Join(linkedParent, "future-output-root");
            var artifactPath = Path.Join(outputRoot, "index.html");
            var exception = await Assert.ThrowsAsync<ExportValidationException>(
                () => ExportOutputPathGuards.WriteTextArtifactAsync(
                    outputRoot,
                    artifactPath,
                    "HTML route artifact",
                    "/",
                    "<!DOCTYPE html><html></html>",
                    encoding: null,
                    CancellationToken.None));

            AssertRwExport009(exception, ExportOutputPathGuards.OutputRootReparse);
            Assert.False(Directory.Exists(Path.Join(outsideRoot, "future-output-root")));
            Assert.False(File.Exists(Path.Join(outsideRoot, "future-output-root", "index.html")));
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
            DeleteDirectoryIfExists(outsideRoot);
        }
    }

    [Fact]
    public async Task WriteTextArtifactAsync_Should_Reject_Nested_Parent_Reparse_Before_External_Write()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-output-guard-").FullName;
        var outsideRoot = Directory.CreateTempSubdirectory("razorwire-output-outside-").FullName;
        try
        {
            var linkPath = Path.Join(tempDir, "linked");
            if (!TryCreateDirectorySymlink(linkPath, outsideRoot))
            {
                throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
            }

            var artifactPath = Path.Join(linkPath, "nested", "index.html");
            var exception = await Assert.ThrowsAsync<ExportValidationException>(
                () => ExportOutputPathGuards.WriteTextArtifactAsync(
                    tempDir,
                    artifactPath,
                    "HTML route artifact",
                    "/linked/nested",
                    "outside",
                    encoding: null,
                    CancellationToken.None));

            AssertRwExport009(exception, ExportOutputPathGuards.ArtifactParentReparse);
            Assert.False(File.Exists(Path.Join(outsideRoot, "nested", "index.html")));
            Assert.False(Directory.Exists(Path.Join(outsideRoot, "nested")));
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
            DeleteDirectoryIfExists(outsideRoot);
        }
    }

    [Fact]
    public async Task WriteTextArtifactAsync_Should_Reject_Existing_Target_File_Reparse()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-output-guard-").FullName;
        var outsideRoot = Directory.CreateTempSubdirectory("razorwire-output-outside-").FullName;
        try
        {
            var outsideFile = Path.Join(outsideRoot, "index.html");
            await File.WriteAllTextAsync(outsideFile, "outside");
            var linkPath = Path.Join(tempDir, "index.html");
            if (!TryCreateFileSymlink(linkPath, outsideFile))
            {
                throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
            }

            var exception = await Assert.ThrowsAsync<ExportValidationException>(
                () => ExportOutputPathGuards.WriteTextArtifactAsync(
                    tempDir,
                    linkPath,
                    "HTML route artifact",
                    "/",
                    "inside",
                    encoding: null,
                    CancellationToken.None));

            AssertRwExport009(exception, ExportOutputPathGuards.ArtifactTargetReparse);
            Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile));
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
            DeleteDirectoryIfExists(outsideRoot);
        }
    }

    [Fact]
    public void ValidateWritableArtifactPath_Should_Reject_Existing_Target_Directory_Reparse()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-output-guard-").FullName;
        var outsideRoot = Directory.CreateTempSubdirectory("razorwire-output-outside-").FullName;
        try
        {
            var linkPath = Path.Join(tempDir, "asset");
            if (!TryCreateDirectorySymlink(linkPath, outsideRoot))
            {
                throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
            }

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportOutputPathGuards.ValidateWritableArtifactPath(
                    tempDir,
                    linkPath,
                    "non-HTML route asset",
                    "/asset",
                    "open-write"));

            AssertRwExport009(exception, ExportOutputPathGuards.ArtifactTargetReparse);
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
            DeleteDirectoryIfExists(outsideRoot);
        }
    }

    [Fact]
    public void ValidateWritableArtifactPath_Should_Reject_Lexical_Outside_Root()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-output-guard-").FullName;
        var outsidePath = Path.Join(Path.GetDirectoryName(tempDir)!, $"outside-{Guid.NewGuid():N}.html");
        try
        {
            var exception = Assert.Throws<ExportValidationException>(
                () => ExportOutputPathGuards.ValidateWritableArtifactPath(
                    tempDir,
                    outsidePath,
                    "HTML route artifact",
                    "/outside",
                    "open-write"));

            AssertRwExport009(exception, ExportOutputPathGuards.ArtifactOutsideRoot);
            Assert.False(File.Exists(outsidePath));
        }
        finally
        {
            DeleteDirectoryIfExists(tempDir);
        }
    }

    [Fact]
    public void GetPathComparison_Should_Match_Current_Platform()
    {
        var expected = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        Assert.Equal(expected, ExportOutputPathGuards.GetPathComparison());
    }

    [Fact]
    public void IsPathUnderRoot_Should_Treat_FileSystem_Root_As_Containing_Descendants()
    {
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetPathRoot(tempPath);

        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(ExportOutputPathGuards.IsPathUnderRoot(
            tempPath,
            root,
            ExportOutputPathGuards.GetPathComparison()));
    }

    private static void AssertRwExport009(ExportValidationException exception, string reason)
    {
        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("RWEXPORT009", diagnostic.Code);
        Assert.Contains($"[{reason}]", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Artifact kind:", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Route:", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Output-relative path:", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Unsafe segment:", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Operation:", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(ExportOutputPathGuards.DocsAnchor, diagnostic.Message, StringComparison.Ordinal);
    }

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
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

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
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

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
