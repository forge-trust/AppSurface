using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.PackageIndex;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>
/// Regression coverage for the read-only PackageIndex release-preparation witness command surface.
/// </summary>
public sealed class ReleasePreparationWitnessTests
{
    [Fact]
    public async Task WitnessCommandRequiresBothExplicitIdentityAndOutputOptions()
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();

        var missingBase = await Program.RunAsync(
            ["release-prep-witness", "--witness", "witness.json"],
            output,
            error,
            Directory.GetCurrentDirectory());
        var missingWitness = await Program.RunAsync(
            ["release-prep-witness", "--base-ref", "HEAD"],
            output,
            error,
            Directory.GetCurrentDirectory());

        Assert.Equal(1, missingBase);
        Assert.Equal(1, missingWitness);
        Assert.Contains("requires '--base-ref", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("requires '--witness", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedRegionExtractionPreservesExactBodyBytesAndRejectsDuplicateMarkers()
    {
        var content = "before\r\n<!-- appsurface-release-guidance: begin -->\r\nbody\r\n<!-- appsurface-release-guidance: end -->\r\nafter\r\n";

        var body = ReleaseGuidanceRenderer.ExtractManagedRegionBody(content, "src/Package/README.md");

        Assert.Equal("body\r\n", body);
        Assert.Throws<PackageIndexException>(() => ReleaseGuidanceRenderer.ExtractManagedRegionBody(
            content + "<!-- appsurface-release-guidance: begin -->\n",
            "src/Package/README.md"));
        Assert.Throws<PackageIndexException>(() => ReleaseGuidanceRenderer.ExtractManagedRegionBody(
            "<!-- appsurface-release-guidance: end -->\nbody\n<!-- appsurface-release-guidance: begin -->\n",
            "src/Package/README.md"));
    }

    [Fact]
    public async Task WitnessWriterEmitsTheExactReadOnlyJsonContract()
    {
        var directory = Path.Join(Path.GetTempPath(), "ReleasePreparationWitnessTests", Guid.NewGuid().ToString("N"));
        var path = Path.Join(directory, "witness.json");
        var witness = new ReleasePreparationWitness(
            ReleasePreparationWitnessBuilder.Schema,
            "origin/main",
            new string('a', 40),
            new string('b', 40),
            new string('c', 40),
            "verified",
            [new ReleasePreparationWitnessInput("package-index-manifest", "packages/package-index.yml", ["packages/README.md"])],
            [new ReleasePreparationWitnessSurface("chooser", "packages/README.md", new string('d', 64))]);

        try
        {
            await ReleasePreparationWitnessBuilder.WriteAsync(witness, path);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(ReleasePreparationWitnessBuilder.Schema, document.RootElement.GetProperty("schema").GetString());
            Assert.Equal("packages/README.md", document.RootElement.GetProperty("surfaces")[0].GetProperty("path").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WitnessWriterReportsAFilesystemFailureAsAPackageIndexDiagnostic()
    {
        var directory = Path.Join(Path.GetTempPath(), "ReleasePreparationWitnessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var witness = new ReleasePreparationWitness(
            ReleasePreparationWitnessBuilder.Schema,
            "origin/main",
            new string('a', 40),
            new string('b', 40),
            new string('c', 40),
            "verified",
            [],
            []);

        try
        {
            var error = await Assert.ThrowsAsync<PackageIndexException>(() => ReleasePreparationWitnessBuilder.WriteAsync(witness, directory));

            Assert.Contains("could not be written", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WitnessCommandBuildsAReadOnlySnapshotForTheCurrentCheckout()
    {
        var path = Path.Join(Path.GetTempPath(), "ReleasePreparationWitnessTests", Guid.NewGuid().ToString("N"), "witness.json");
        var repositoryRoot = GetRepositoryRoot();
        await using var output = new StringWriter();
        await using var error = new StringWriter();

        try
        {
            var exitCode = await Program.RunAsync(
                ["release-prep-witness", "--repo-root", repositoryRoot, "--base-ref", "HEAD", "--witness", path],
                output,
                error,
                repositoryRoot);

            Assert.True(exitCode == 0, error.ToString());
            Assert.True(File.Exists(path), error.ToString());
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("verified", document.RootElement.GetProperty("verification").GetString());
            Assert.Empty(document.RootElement.GetProperty("changedInputs").EnumerateArray());
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WitnessCommandRecordsManifestChangesFromAnIndependentGitHistory()
    {
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationWitnessTests", Guid.NewGuid().ToString("N"));
        var witnessPath = Path.Join(repositoryRoot, "artifacts", "witness.json");
        try
        {
            await WriteRepositoryFileAsync(
                repositoryRoot,
                "packages/package-index.yml",
                """
                packages:
                  - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                    product_family: appsurface
                    classification: public
                    publish_decision: publish
                    release_guidance_variant: default
                    order: 10
                    use_when: Use the test package.
                    includes: Test package hosting.
                    does_not_include: Optional packages.
                    start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
                """);
            await WriteRepositoryFileAsync(repositoryRoot, "packages/README.md.yml", "title: Package chooser\n");
            await WriteRepositoryFileAsync(
                repositoryRoot,
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <PackageId>ForgeTrust.AppSurface.Web</PackageId>
                    <IsPackable>true</IsPackable>
                  </PropertyGroup>
                </Project>
                """);
            await WriteRepositoryFileAsync(
                repositoryRoot,
                "Web/ForgeTrust.AppSurface.Web/README.md",
                """
                # Web package

                <!-- appsurface-release-guidance: begin -->
                baseline guidance
                <!-- appsurface-release-guidance: end -->
                """);
            await WriteRepositoryFileAsync(repositoryRoot, "examples/web-app/README.md", "# Web example\n");
            await WriteRepositoryFileAsync(repositoryRoot, "start-here/first-success-path.md", "# Package-first path\n");
            await WriteRepositoryFileAsync(repositoryRoot, "releases/README.md", "# Releases\n");
            await WriteRepositoryFileAsync(repositoryRoot, "releases/upgrade-policy.md", "# Upgrade policy\n");
            await WriteRepositoryFileAsync(repositoryRoot, "CHANGELOG.md", "# Changelog\n");
            await WriteRepositoryFileAsync(repositoryRoot, "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template", ValidReleaseGuidanceTemplate("Baseline"));
            await RunGitAsync(repositoryRoot, "init");
            await RunGitAsync(repositoryRoot, "config", "user.email", "tests@example.test");
            await RunGitAsync(repositoryRoot, "config", "user.name", "PackageIndex Tests");
            await RunGitAsync(repositoryRoot, "add", ".");
            await RunGitAsync(repositoryRoot, "commit", "-m", "baseline");
            var manifestPath = Path.Join(repositoryRoot, "packages", "package-index.yml");
            var manifest = await File.ReadAllTextAsync(manifestPath);
            await File.WriteAllTextAsync(manifestPath, manifest.Replace("order: 10", "order: 11", StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.PackageIndex", "release-guidance.template"), ValidReleaseGuidanceTemplate("Updated"));
            await RunGitAsync(repositoryRoot, "add", "packages/package-index.yml", "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template");
            await RunGitAsync(repositoryRoot, "commit", "-m", "change semantic inputs");

            await using var output = new StringWriter();
            await using var error = new StringWriter();
            var exitCode = await Program.RunAsync(
                ["release-prep-witness", "--repo-root", repositoryRoot, "--base-ref", "HEAD~1", "--witness", witnessPath],
                output,
                error,
                repositoryRoot);

            Assert.True(exitCode == 0, error.ToString());
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(witnessPath));
            var changedInputs = document.RootElement.GetProperty("changedInputs").EnumerateArray().ToArray();
            Assert.Equal(2, changedInputs.Length);
            var manifestInput = Assert.Single(changedInputs, input => input.GetProperty("path").GetString() == "packages/package-index.yml");
            Assert.Equal("package-index-manifest", manifestInput.GetProperty("kind").GetString());
            Assert.Equal(3, manifestInput.GetProperty("surfaces").GetArrayLength());
            var templateInput = Assert.Single(changedInputs, input => input.GetProperty("path").GetString() == "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template");
            Assert.Equal("release-guidance-template", templateInput.GetProperty("kind").GetString());
            Assert.Equal(["Web/ForgeTrust.AppSurface.Web/README.md"], templateInput.GetProperty("surfaces").EnumerateArray().Select(surface => surface.GetString()));
        }
        finally
        {
            if (Directory.Exists(repositoryRoot))
            {
                Directory.Delete(repositoryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WitnessCommandReportsAnUnavailableBaseRefWithoutWritingOutput()
    {
        var path = Path.Join(Path.GetTempPath(), "ReleasePreparationWitnessTests", Guid.NewGuid().ToString("N"), "witness.json");
        await using var output = new StringWriter();
        await using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["release-prep-witness", "--base-ref", "refs/heads/does-not-exist", "--witness", path],
            output,
            error,
            GetRepositoryRoot());

        Assert.Equal(1, exitCode);
        Assert.Contains("Git command failed", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WitnessCommandRejectsAnAmbiguousMergeBaseWithoutWritingOutput()
    {
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationWitnessTests", Guid.NewGuid().ToString("N"));
        var witnessPath = Path.Join(repositoryRoot, "witness.json");
        try
        {
            Directory.CreateDirectory(repositoryRoot);
            await RunGitAsync(repositoryRoot, "init");
            await RunGitAsync(repositoryRoot, "config", "user.email", "tests@example.test");
            await RunGitAsync(repositoryRoot, "config", "user.name", "PackageIndex Tests");

            await WriteRepositoryFileAsync(repositoryRoot, "README.md", "baseline\n");
            await RunGitAsync(repositoryRoot, "add", "README.md");
            await RunGitAsync(repositoryRoot, "commit", "-m", "baseline");
            await WriteRepositoryFileAsync(repositoryRoot, "first.txt", "first parent\n");
            await RunGitAsync(repositoryRoot, "add", "first.txt");
            await RunGitAsync(repositoryRoot, "commit", "-m", "first parent");
            await RunGitAsync(repositoryRoot, "branch", "first-merge");
            await RunGitAsync(repositoryRoot, "checkout", "-b", "second-merge", "HEAD~1");

            await WriteRepositoryFileAsync(repositoryRoot, "second.txt", "second parent\n");
            await RunGitAsync(repositoryRoot, "add", "second.txt");
            await RunGitAsync(repositoryRoot, "commit", "-m", "second parent");
            await RunGitAsync(repositoryRoot, "branch", "second-parent");
            await RunGitAsync(repositoryRoot, "checkout", "first-merge");
            await RunGitAsync(repositoryRoot, "merge", "--no-ff", "second-parent", "-m", "first merge");
            await RunGitAsync(repositoryRoot, "checkout", "second-merge");
            await RunGitAsync(repositoryRoot, "merge", "--no-ff", "first-merge~1", "-m", "second merge");

            await using var output = new StringWriter();
            await using var error = new StringWriter();
            var exitCode = await Program.RunAsync(
                ["release-prep-witness", "--repo-root", repositoryRoot, "--base-ref", "first-merge", "--witness", witnessPath],
                output,
                error,
                repositoryRoot);

            Assert.Equal(1, exitCode);
            Assert.Contains("exactly one merge base", error.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(witnessPath));
        }
        finally
        {
            if (Directory.Exists(repositoryRoot))
            {
                Directory.Delete(repositoryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PackageGateRoutesThroughTheFallbackWhenNoExplicitCommandMatches()
    {
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationWitnessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        try
        {
            var exitCode = await Program.RunAsync(["gate", "--repo-root", repositoryRoot], output, error, repositoryRoot);

            Assert.Equal(1, exitCode);
            Assert.Contains("does not exist", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    private static async Task WriteRepositoryFileAsync(string repositoryRoot, string relativePath, string content)
    {
        var path = TestPathUtils.PathUnder(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(path);
        Assert.NotNull(directory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content);
    }

    private static async Task RunGitAsync(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, standardError);
    }

    private static string ValidReleaseGuidanceTemplate(string description) =>
        """
        <!-- appsurface-release-guidance-template: default begin -->
        ## Release Guidance

        __DESCRIPTION__ {{PackageChooserUrl}} {{ReleaseHubUrl}}
        <!-- appsurface-release-guidance-template: default end -->

        <!-- appsurface-release-guidance-template: apphost begin -->
        ## Release Guidance

        __DESCRIPTION__ {{PackageChooserUrl}} {{ReleaseHubUrl}}
        <!-- appsurface-release-guidance-template: apphost end -->

        <!-- appsurface-release-guidance-template: experimental begin -->
        ## Release Guidance

        __DESCRIPTION__ {{PackageChooserUrl}} {{ReleaseHubUrl}}
        <!-- appsurface-release-guidance-template: experimental end -->
        """.Replace("__DESCRIPTION__", description, StringComparison.Ordinal);

    private static string GetRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        var current = new DirectoryInfo(sourcePath);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from this test source file.");
    }
}
