using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestHealthResponseTests
{
    [Fact]
    public void FromSnapshot_RedactsVcsIgnoreSamplePathsFromClientDiagnosticResponse()
    {
        var health = new DocHarvestHealthSnapshot(
            DocHarvestHealthStatus.Healthy,
            DateTimeOffset.UnixEpoch,
            "/private/repo",
            TotalHarvesters: 1,
            SuccessfulHarvesters: 1,
            FailedHarvesters: 0,
            TotalDocs: 1,
            [new DocHarvesterHealth("MarkdownHarvester", DocHarvesterHealthStatus.Succeeded, 1, null)],
            [
                new DocHarvestDiagnostic(
                    DocHarvestDiagnosticCodes.VcsIgnoreSummary,
                    DocHarvestDiagnosticSeverity.Information,
                    HarvesterType: null,
                    "Repository-owned Git ignore rules excluded 1 AppSurface Docs harvest candidate.",
                    "Samples: legacy/bower_components/jquery/README.md by .gitignore:1 'bower_components/'.",
                    "Use AllowGlobs for intentional public docs under ignored paths.")
            ]);

        var response = AppSurfaceDocsHarvestHealthResponse.FromSnapshot(health);
        var diagnostic = Assert.Single(response.Diagnostics);

        Assert.DoesNotContain("legacy/bower_components", diagnostic.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy/bower_components", diagnostic.Fix, StringComparison.Ordinal);
        Assert.Equal(DocHarvestDiagnosticCodes.VcsIgnoreSummary, diagnostic.Code);
    }
}
