using System.Text.Json;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DurableTraceContextDocumentationRegressionTests
{
    // Regression: ISSUE-001 — the visible trace-context guide link rendered Content missing.
    // Found by /qa on 2026-08-02.
    // Report: .gstack/qa-reports/qa-report-127-0-0-1-61385-2026-08-02.md
    [Fact]
    public void StandaloneHarvestIncludeGlobs_ShouldExposeDurableTraceContextGuide()
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

        const string guidePath = "Durable/flow-trace-context-v1.md";
        Assert.Contains(guidePath, includeGlobs);
        Assert.True(
            File.Exists(TestPathUtils.PathUnder(repoRoot, guidePath)),
            "The harvested durable trace-context guide must exist at the configured path.");
    }
}
