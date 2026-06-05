namespace ForgeTrust.RazorWire.Cli.Tests;

public class ExportDeploymentExtrasManifestReaderTests
{
    [Fact]
    public async Task Read_Should_Return_Resolved_Extra_For_Valid_Manifest()
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            var deploy = Path.Join(root, "deploy");
            Directory.CreateDirectory(deploy);
            var sourcePath = Path.Join(deploy, "CNAME");
            await File.WriteAllTextAsync(sourcePath, "docs.example.com");
            var manifestPath = Path.Join(deploy, "export-extras.yml");
            await File.WriteAllTextAsync(
                manifestPath,
                """
                version: 1
                extras:
                  - source: CNAME
                    publishPath: /CNAME
                """);

            var extra = Assert.Single(ExportDeploymentExtrasManifestReader.Read(manifestPath));

            Assert.Equal(sourcePath, extra.SourcePath);
            Assert.Equal("/CNAME", extra.PublishPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Read_Should_Allow_WellKnown_SecurityText()
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            var deploy = Path.Join(root, "deploy");
            var wellKnown = Path.Join(deploy, ".well-known");
            Directory.CreateDirectory(wellKnown);
            var sourcePath = Path.Join(wellKnown, "security.txt");
            await File.WriteAllTextAsync(sourcePath, "Contact: mailto:security@example.com");
            var manifestPath = Path.Join(deploy, "export-extras.yml");
            await File.WriteAllTextAsync(
                manifestPath,
                """
                version: 1
                extras:
                  - source: .well-known/security.txt
                    publishPath: /.well-known/security.txt
                """);

            var extra = Assert.Single(ExportDeploymentExtrasManifestReader.Read(manifestPath));

            Assert.Equal(sourcePath, extra.SourcePath);
            Assert.Equal("/.well-known/security.txt", extra.PublishPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Read_Should_Reject_Case_Only_Duplicate_PublishPaths()
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            var deploy = Path.Join(root, "deploy");
            Directory.CreateDirectory(deploy);
            await File.WriteAllTextAsync(Path.Join(deploy, "CNAME"), "docs.example.com");
            await File.WriteAllTextAsync(Path.Join(deploy, "cname"), "docs.example.net");
            var manifestPath = Path.Join(deploy, "export-extras.yml");
            await File.WriteAllTextAsync(
                manifestPath,
                """
                version: 1
                extras:
                  - source: CNAME
                    publishPath: /CNAME
                  - source: cname
                    publishPath: /cname
                """);

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportDeploymentExtrasManifestReader.Read(manifestPath));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[target-duplicate]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Theory]
    [InlineData("/_redirects")]
    [InlineData("/_headers")]
    public async Task Read_Should_Reject_Reserved_Provider_Paths(string publishPath)
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            var deploy = Path.Join(root, "deploy");
            Directory.CreateDirectory(deploy);
            await File.WriteAllTextAsync(Path.Join(deploy, "rules"), "content");
            var manifestPath = Path.Join(deploy, "export-extras.yml");
            await File.WriteAllTextAsync(
                manifestPath,
                $$"""
                version: 1
                extras:
                  - source: rules
                    publishPath: {{publishPath}}
                """);

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportDeploymentExtrasManifestReader.Read(manifestPath));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[target-reserved]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Theory]
    [InlineData("/bad*name.txt")]
    [InlineData("/bad%2Aname.txt")]
    [InlineData("/bad<name.txt")]
    [InlineData("/bad>name.txt")]
    [InlineData("/bad|name.txt")]
    [InlineData("/bad\"name.txt")]
    [InlineData("/badname.")]
    [InlineData("/badname ")]
    public async Task Read_Should_Reject_Invalid_Filesystem_PublishPath_Segments(string publishPath)
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            var deploy = Path.Join(root, "deploy");
            Directory.CreateDirectory(deploy);
            await File.WriteAllTextAsync(Path.Join(deploy, "CNAME"), "docs.example.com");
            var manifestPath = Path.Join(deploy, "export-extras.yml");
            await File.WriteAllTextAsync(
                manifestPath,
                $$"""
                version: 1
                extras:
                  - source: CNAME
                    publishPath: '{{publishPath}}'
                """);

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportDeploymentExtrasManifestReader.Read(manifestPath));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[target-invalid]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Read_Should_Reject_Source_Outside_Manifest_Directory()
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "CNAME"), "docs.example.com");
            var deploy = Path.Join(root, "deploy");
            Directory.CreateDirectory(deploy);
            var manifestPath = Path.Join(deploy, "export-extras.yml");
            await File.WriteAllTextAsync(
                manifestPath,
                """
                version: 1
                extras:
                  - source: ../CNAME
                    publishPath: /CNAME
                """);

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportDeploymentExtrasManifestReader.Read(manifestPath));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[source-outside-root]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Read_Should_Reject_Rooted_Source_Path()
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            var deploy = Path.Join(root, "deploy");
            Directory.CreateDirectory(deploy);
            var sourcePath = Path.Join(deploy, "CNAME");
            await File.WriteAllTextAsync(sourcePath, "docs.example.com");
            var manifestPath = Path.Join(deploy, "export-extras.yml");
            await File.WriteAllTextAsync(
                manifestPath,
                $$"""
                version: 1
                extras:
                  - source: {{sourcePath}}
                    publishPath: /CNAME
                """);

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportDeploymentExtrasManifestReader.Read(manifestPath));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[source-outside-root]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Read_Should_Reject_Source_Directory()
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            var deploy = Path.Join(root, "deploy");
            Directory.CreateDirectory(Path.Join(deploy, "CNAME"));
            var manifestPath = Path.Join(deploy, "export-extras.yml");
            await File.WriteAllTextAsync(
                manifestPath,
                """
                version: 1
                extras:
                  - source: CNAME
                    publishPath: /CNAME
                """);

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportDeploymentExtrasManifestReader.Read(manifestPath));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[source-directory]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Read_Should_Reject_Symlinked_Manifest_Directory()
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            var realDeploy = Path.Join(root, "real-deploy");
            Directory.CreateDirectory(realDeploy);
            await File.WriteAllTextAsync(Path.Join(realDeploy, "CNAME"), "docs.example.com");
            await File.WriteAllTextAsync(
                Path.Join(realDeploy, "export-extras.yml"),
                """
                version: 1
                extras:
                  - source: CNAME
                    publishPath: /CNAME
                """);
            var linkedDeploy = Path.Join(root, "linked-deploy");
            if (!TryCreateDirectorySymlink(linkedDeploy, realDeploy))
            {
                return;
            }

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportDeploymentExtrasManifestReader.Read(Path.Join(linkedDeploy, "export-extras.yml")));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[source-symlink]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }


    [Fact]
    public async Task Read_Should_Reject_Malformed_Yaml()
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            var manifestPath = Path.Join(root, "export-extras.yml");
            await File.WriteAllTextAsync(manifestPath, "version: [");

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportDeploymentExtrasManifestReader.Read(manifestPath));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[schema]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Theory]
    [InlineData(
        """
        version: 1
        extraField: true
        extras:
          - source: CNAME
            publishPath: /CNAME
        """)]
    [InlineData(
        """
        version: 1
        extras:
          - source:
            publishPath: /CNAME
        """)]
    [InlineData(
        """
        version: 1
        extras:
          - source: CNAME
            publishPath:
        """)]
    [InlineData(
        """
        version: 1
        extras:
          - source: CNAME
            publishPath: /CNAME
            unknown: value
        """)]
    [InlineData(
        """
        version: 1
        ---
        extras: []
        """)]
    [InlineData(
        """
        version: 1
        version: 1
        extras:
          - source: CNAME
            publishPath: /CNAME
        """)]
    [InlineData(
        """
        version: 1
        extras:
          - source: CNAME
            source: CNAME
            publishPath: /CNAME
        """)]
    [InlineData(
        """
        version: 1
        extras:
          - &extra
            source: CNAME
            publishPath: /CNAME
          - *extra
        """)]
    public async Task Read_Should_Reject_Schema_Violations(string manifest)
    {
        var root = Directory.CreateTempSubdirectory("razorwire-extras-manifest-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "CNAME"), "docs.example.com");
            var manifestPath = Path.Join(root, "export-extras.yml");
            await File.WriteAllTextAsync(manifestPath, manifest);

            var exception = Assert.Throws<ExportValidationException>(
                () => ExportDeploymentExtrasManifestReader.Read(manifestPath));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[schema]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
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
