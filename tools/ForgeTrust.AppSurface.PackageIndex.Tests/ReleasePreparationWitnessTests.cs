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
