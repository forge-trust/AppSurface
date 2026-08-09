namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class PackageIndexReleaseGuidanceLinkRegressionTests
{
    [Theory]
    [InlineData(
        ReleaseGuidanceRenderer.MaintainerGuideRelativePath,
        "https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.PackageIndex/README.md")]
    [InlineData(
        "releases/coordinated-release-links.md",
        "https://github.com/forge-trust/AppSurface/blob/main/releases/coordinated-release-links.md")]
    public void GetCanonicalRepositoryUrl_UsesTheRepositoryPageInsteadOfADocsRelativeRoute(
        string repositoryRelativePath,
        string expectedUrl)
    {
        // Regression: ISSUE-001 — package chooser maintainer links rendered as Docs-relative paths and returned 404.
        // Found by /qa on 2026-08-09.
        // Report: .gstack/qa-reports/qa-report-127-0-0-1-2026-08-09.md
        var actualUrl = PackageIndexGenerator.GetCanonicalRepositoryUrl(repositoryRelativePath);

        Assert.Equal(expectedUrl, actualUrl);
    }

    [Theory]
    [InlineData("../tools/ForgeTrust.AppSurface.PackageIndex/README.md")]
    [InlineData("/tools/ForgeTrust.AppSurface.PackageIndex/README.md")]
    [InlineData("tools//ForgeTrust.AppSurface.PackageIndex/README.md")]
    public void GetCanonicalRepositoryUrl_RejectsUnsafeRepositoryPaths(string repositoryRelativePath)
    {
        var error = Assert.Throws<PackageIndexException>(
            () => PackageIndexGenerator.GetCanonicalRepositoryUrl(repositoryRelativePath));

        Assert.Contains("must be a non-rooted path", error.Message, StringComparison.Ordinal);
    }
}
