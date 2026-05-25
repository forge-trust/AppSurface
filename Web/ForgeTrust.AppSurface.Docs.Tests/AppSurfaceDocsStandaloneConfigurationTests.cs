using System.Text.Json;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsStandaloneConfigurationTests
{
    [Fact]
    public void StandaloneHarvestIncludeGlobs_ShouldExposeRootChangelog()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var appSettingsPath = Path.Combine(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs.Standalone",
            "appsettings.json");

        using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
        var includeGlobs = document.RootElement
            .GetProperty("AppSurfaceDocs")
            .GetProperty("Harvest")
            .GetProperty("Paths")
            .GetProperty("IncludeGlobs")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        Assert.Contains("CHANGELOG.md", includeGlobs);
    }
}
