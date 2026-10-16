namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable guards for the data-only half of producer artifact discovery.</summary>
/// <remarks>These tests do not exercise native producer readiness or Linux root custody.</remarks>
public sealed class LinuxProducerArtifactCustodyTests
{
    [Fact]
    public void ReportPathsAreCopiedSortedAndLimitedToCoberturaBasenames()
    {
        var source = new[]
        {
            "guid-b/coverage.cobertura.xml",
            "coverage.cobertura.xml",
            "guid-a/coverage.cobertura.xml",
        };

        var result = LinuxProducerArtifactCustody.ValidateReportPaths(source);
        source[0] = "changed.txt";

        Assert.Equal(new[]
        {
            "coverage.cobertura.xml",
            "guid-a/coverage.cobertura.xml",
            "guid-b/coverage.cobertura.xml",
        }, result);
        Assert.Empty(LinuxProducerArtifactCustody.ValidateReportPaths([]));
        Assert.Equal(LinuxRetainedReportSet.MaximumFiles,
            LinuxProducerArtifactCustody.ValidateReportPaths(Enumerable.Range(0,
                LinuxRetainedReportSet.MaximumFiles).Select(i => $"{i}/coverage.cobertura.xml").ToArray()).Length);
    }

    [Theory]
    [InlineData("coverage.xml")]
    [InlineData("coverage.cobertura.xml.bak")]
    [InlineData("Coverage.cobertura.xml")]
    [InlineData("guid/coverage.xml")]
    [InlineData("guid/readme.txt")]
    [InlineData("guid/coverage.cobertura.xml/child")]
    public void UnexpectedReportBasenameRejects(string path) =>
        Assert.Throws<IOException>(() => LinuxProducerArtifactCustody.ValidateReportPaths([path]));

    [Fact]
    public void ReportInventoryRetainsUnderlyingCanonicalCollisionAndCountGuards()
    {
        Assert.Throws<IOException>(() => LinuxProducerArtifactCustody.ValidateReportPaths(
            ["coverage.cobertura.xml", "guid/../coverage.cobertura.xml"]));
        Assert.Throws<IOException>(() => LinuxProducerArtifactCustody.ValidateReportPaths(
            Enumerable.Range(0, LinuxRetainedReportSet.MaximumFiles + 1)
                .Select(i => $"{i}/coverage.cobertura.xml").ToArray()));
    }
}
