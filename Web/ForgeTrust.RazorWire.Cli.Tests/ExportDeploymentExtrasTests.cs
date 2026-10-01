namespace ForgeTrust.RazorWire.Cli.Tests;

public sealed class ExportDeploymentExtrasTests
{
    [Theory]
    [InlineData(null, "is required")]
    [InlineData("", "is required")]
    [InlineData("CNAME", "must start with one")]
    [InlineData("//host/CNAME", "must start with one")]
    [InlineData("/", "must name a file")]
    [InlineData("/folder/", "must name a file")]
    [InlineData("/folder//CNAME", "unsupported URL")]
    [InlineData("/folder\\CNAME", "unsupported URL")]
    [InlineData("/file?query", "unsupported URL")]
    [InlineData("/file#fragment", "unsupported URL")]
    [InlineData("/file:name", "unsupported URL")]
    [InlineData("/./CNAME", "traversal segments")]
    [InlineData("/../CNAME", "traversal segments")]
    [InlineData("/%2e%2e/CNAME", "encoded traversal")]
    [InlineData("/folder%2fCNAME", "encoded traversal")]
    [InlineData("/folder%5cCNAME", "encoded traversal")]
    [InlineData("/CON.txt", "reserved device name")]
    [InlineData("/folder/LPT9", "reserved device name")]
    public void TryNormalizePublishPath_RejectsUnsafePaths(string? path, string expectedMessage)
    {
        Assert.False(ExportDeploymentExtras.TryNormalizePublishPath(path, out var normalized, out var message));
        Assert.Null(normalized);
        Assert.Contains(expectedMessage, message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRegisteredExtra_RequiresAbsoluteExistingRegularSource()
    {
        using var root = new TemporaryDirectory();
        var relative = Assert.Throws<ExportValidationException>(
            () => ExportDeploymentExtras.CreateRegisteredExtra("relative.txt", "/CNAME"));
        Assert.Contains("[source-outside-root]", Assert.Single(relative.Diagnostics).Message, StringComparison.Ordinal);

        var missing = Assert.Throws<ExportValidationException>(
            () => ExportDeploymentExtras.CreateRegisteredExtra(Path.Join(root.Path, "missing.txt"), "/CNAME"));
        Assert.Contains("[source-missing]", Assert.Single(missing.Diagnostics).Message, StringComparison.Ordinal);

        var directory = Assert.Throws<ExportValidationException>(
            () => ExportDeploymentExtras.CreateRegisteredExtra(root.Path, "/CNAME"));
        Assert.Contains("[source-directory]", Assert.Single(directory.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateManifestExtra_RejectsMissingSourceWithManifestContext()
    {
        using var root = new TemporaryDirectory();
        var manifest = Path.Join(root.Path, "extras.yml");

        var error = Assert.Throws<ExportValidationException>(
            () => ExportDeploymentExtras.CreateManifestExtra(manifest, 2, root.Path, "missing.txt", "/CNAME"));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Contains("[source-missing]", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("entry 2", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapPublishPathToFilePath_RejectsTraversalEvenWhenCalledWithoutNormalization()
    {
        using var root = new TemporaryDirectory();

        var error = Assert.Throws<ExportValidationException>(
            () => ExportDeploymentExtras.MapPublishPathToFilePath(root.Path, "/../outside.txt"));

        Assert.Contains("[target-invalid]", Assert.Single(error.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTargetParentPath_RejectsParentOutsideOutputRoot()
    {
        using var root = new TemporaryDirectory();
        var output = Path.Join(root.Path, "output");
        Directory.CreateDirectory(output);

        var error = Assert.Throws<ExportValidationException>(
            () => ExportDeploymentExtras.ValidateTargetParentPath(output, Path.Join(root.Path, "outside.txt"), "/outside.txt"));

        Assert.Contains("[target-invalid]", Assert.Single(error.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTargetParentPath_RejectsSymlinkedParent()
    {
        using var root = new TemporaryDirectory();
        var output = Path.Join(root.Path, "output");
        var external = Path.Join(root.Path, "external");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(external);
        var linkPath = Path.Join(output, "linked");
        if (!TryCreateDirectorySymlink(linkPath, external))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Symbolic link creation is not available in this environment.");
        }

        var error = Assert.Throws<ExportValidationException>(
            () => ExportDeploymentExtras.ValidateTargetParentPath(
                output, Path.Join(output, "linked", "CNAME"), "/linked/CNAME"));

        Assert.Contains("[target-parent-symlink]", Assert.Single(error.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("relative", "/")]
    [InlineData("  /CNAME  ", "/CNAME")]
    public void CreateDiagnostic_NormalizesRouteForInvalidExtra(string? route, string expectedRoute)
    {
        var diagnostic = ExportDeploymentExtras.CreateDiagnostic("target-invalid", "invalid", route);

        Assert.Equal("RWEXPORT007", diagnostic.Code);
        Assert.Equal(expectedRoute, diagnostic.Route);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("razorwire-extras-").FullName;
        }

        public string Path { get; }

        public void Dispose()
            => Directory.Delete(Path, recursive: true);
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
}
