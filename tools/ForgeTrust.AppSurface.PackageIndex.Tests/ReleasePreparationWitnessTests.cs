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
}
