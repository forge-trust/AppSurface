using System.Text.Json;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DurableOperationalAssessmentDocumentationRegressionTests
{
    // Regression: ISSUE-001 — the release-note adoption-guide link rendered a 404.
    // Found by /qa on 2026-09-10.
    // Report: .gstack/qa-reports/qa-report-127-0-0-1-5055-2026-09-10.md
    [Fact]
    public void StandaloneHarvestIncludeGlobs_ShouldExposeDurableOperationalAssessmentGuide()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var appSettingsPath = Path.GetFullPath(
            Path.Join("Web", "ForgeTrust.AppSurface.Docs.Standalone", "appsettings.json"),
            repoRoot);

        using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
        var includeGlobs = document.RootElement
            .GetProperty("AppSurfaceDocs")
            .GetProperty("Harvest")
            .GetProperty("Paths")
            .GetProperty("IncludeGlobs")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        const string guidePath = "Durable/operational-assessments.md";
        Assert.Contains(guidePath, includeGlobs);
        Assert.True(
            File.Exists(TestPathUtils.PathUnder(repoRoot, guidePath)),
            "The harvested Durable operational-assessment guide must exist at the configured path.");
    }
}
