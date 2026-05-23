using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsHarvestHealthResponseTests
{
    [Theory]
    [InlineData(DocHarvestDiagnosticCodes.VcsIgnoreSummary)]
    [InlineData(DocHarvestDiagnosticCodes.VcsIgnoreWarning)]
    public void FromSnapshot_RedactsVcsIgnoreLocalDetailsFromClientDiagnosticResponse(string diagnosticCode)
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
                    diagnosticCode,
                    diagnosticCode == DocHarvestDiagnosticCodes.VcsIgnoreWarning
                        ? DocHarvestDiagnosticSeverity.Warning
                        : DocHarvestDiagnosticSeverity.Information,
                    HarvesterType: null,
                    diagnosticCode == DocHarvestDiagnosticCodes.VcsIgnoreWarning
                        ? "AppSurface Docs could not read a repository-owned Git ignore file."
                        : "Repository-owned Git ignore rules excluded 1 AppSurface Docs harvest candidate.",
                    "Samples: legacy/bower_components/jquery/README.md by .gitignore:1 'bower_components/'.",
                    diagnosticCode == DocHarvestDiagnosticCodes.VcsIgnoreWarning
                        ? "Fix filesystem permissions or remove the unreadable ignore file if it is not needed for docs harvesting."
                        : "Use AllowGlobs for intentional public docs under ignored paths.")
            ]);

        var response = AppSurfaceDocsHarvestHealthResponse.FromSnapshot(health);
        var diagnostic = Assert.Single(response.Diagnostics);

        Assert.DoesNotContain("legacy/bower_components", diagnostic.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy/bower_components", diagnostic.Fix, StringComparison.Ordinal);
        Assert.Equal(diagnosticCode, diagnostic.Code);
    }
}
