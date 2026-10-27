using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies optional merged-duplicate cleanup without replacing the primary collection result.</summary>
public sealed class CoverageArtifactMergedCleanupTests
{
    [Theory]
    [InlineData("generated")]
    [InlineData("canonical")]
    [InlineData("different")]
    [InlineData("identical")]
    public async Task Cleanup_MissingRootsWarnAndOnlyVerifiedDuplicateIsRemoved(string scenario)
    {
        using var fixture = TestDirectory.Create();
        var generatedDirectory = Path.Join(fixture.Path, "generated");
        var canonicalDirectory = Path.Join(fixture.Path, "canonical");
        var generated = Path.Join(generatedDirectory, "Cobertura.xml");
        var canonical = Path.Join(canonicalDirectory, "coverage.cobertura.xml");
        if (scenario != "generated")
        {
            Directory.CreateDirectory(generatedDirectory);
            await File.WriteAllTextAsync(generated, "same");
        }
        if (scenario != "canonical")
        {
            Directory.CreateDirectory(canonicalDirectory);
            await File.WriteAllTextAsync(canonical, scenario == "different" ? "different" : "same");
        }
        using var errors = new StringWriter();

        await CoverageRunWorkflow.RemoveRedundantMergedCoverageAsync(generated, canonicalDirectory, errors, CancellationToken.None);

        if (scenario == "identical")
        {
            Assert.False(File.Exists(generated));
            Assert.Empty(errors.ToString());
        }
        else
        {
            Assert.Contains("ASCOV201 duplicate-cleanup", errors.ToString(), StringComparison.Ordinal);
            Assert.Equal(scenario != "generated", File.Exists(generated));
        }
        if (scenario != "canonical")
            Assert.Equal(scenario == "different" ? "different" : "same", await File.ReadAllTextAsync(canonical));
    }

    [Fact]
    public async Task Cleanup_CancellationPropagatesAndPreservesBothReports()
    {
        using var fixture = TestDirectory.Create();
        var generated = fixture.WriteFile("generated/Cobertura.xml", "same");
        var canonical = fixture.WriteFile("canonical/coverage.cobertura.xml", "same");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CoverageRunWorkflow.RemoveRedundantMergedCoverageAsync(
            generated, Path.GetDirectoryName(canonical)!, TextWriter.Null, cancellation.Token));

        Assert.Equal("same", await File.ReadAllTextAsync(generated));
        Assert.Equal("same", await File.ReadAllTextAsync(canonical));
    }
}
