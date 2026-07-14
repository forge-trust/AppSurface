using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceCanaryDocumentationTests
{
    [Fact]
    public void NamedCanaryDocumentation_PreservesAdoptionAndReleaseContract()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var readme = File.ReadAllText(Path.Join(repositoryRoot, "Web", "ForgeTrust.AppSurface.Web", "README.md"));
        var packageIndex = File.ReadAllText(Path.Join(repositoryRoot, "packages", "package-index.yml"));
        var generatedChooser = File.ReadAllText(Path.Join(repositoryRoot, "packages", "README.md"));
        var unreleased = File.ReadAllText(Path.Join(repositoryRoot, "releases", "unreleased.md"));

        Assert.Contains("### Named Canary Endpoints", readme, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceCanaryCompletedResponseMode.StatusCode", readme, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceCanaryCompletedResponseMode.AlwaysOk", readme, StringComparison.Ordinal);
        Assert.Contains("caller triggers synthetic work", readme, StringComparison.Ordinal);
        Assert.Contains("issues/645", readme, StringComparison.Ordinal);
        Assert.Contains("protected preview named deploy evidence", packageIndex, StringComparison.Ordinal);
        Assert.Contains("protected preview named deploy evidence", generatedChooser, StringComparison.Ordinal);
        Assert.Contains("preview named canary evaluation", unreleased, StringComparison.Ordinal);
    }
}
