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
        Assert.Contains("legacy/bower_components", diagnostic.Cause, StringComparison.Ordinal);
        Assert.Equal(diagnosticCode, diagnostic.Code);
    }

    [Fact]
    public void FromSnapshot_RedactsUnsafeDiagnosticCauseText()
    {
        const string dirtyCause = """
            InvalidOperationException: boom at /Users/andrew/private/repo/secret.js; token=super-secret-value Bearer abc.def.ghi ghp_1234567890abcdef sk-proj-1234567890abcdef xoxb-1234567890abcdef
            Sidecar path /Users/andrew/private/repo/.env and Windows path C:\repo\Secret.cs were observed.
               at ForgeTrust.Secret.Run() in C:\repo\Secret.cs:line 42
            Follow-up diagnostic context remains separate.
            """;
        var harvesterDiagnostic = new DocHarvestDiagnostic(
            DocHarvestDiagnosticCodes.HarvesterFailed,
            DocHarvestDiagnosticSeverity.Error,
            "CustomHarvester",
            "A custom harvester failed.",
            dirtyCause,
            "Inspect trusted server logs for the raw details.");
        var health = new DocHarvestHealthSnapshot(
            DocHarvestHealthStatus.Degraded,
            DateTimeOffset.UnixEpoch,
            "/Users/andrew/private/repo",
            TotalHarvesters: 1,
            SuccessfulHarvesters: 0,
            FailedHarvesters: 1,
            TotalDocs: 0,
            [new DocHarvesterHealth("CustomHarvester", DocHarvesterHealthStatus.Failed, 0, harvesterDiagnostic)],
            [harvesterDiagnostic]);

        var response = AppSurfaceDocsHarvestHealthResponse.FromSnapshot(health);
        var aggregateDiagnostic = Assert.Single(response.Diagnostics);
        var harvesterDiagnosticResponse = Assert.Single(response.Harvesters).Diagnostic;
        Assert.NotNull(harvesterDiagnosticResponse);
        var harvesterCause = harvesterDiagnosticResponse.Cause;
        var combinedCause = aggregateDiagnostic.Cause + "\n" + harvesterCause;

        Assert.DoesNotContain("/Users/andrew", combinedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\repo", combinedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", combinedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", combinedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", combinedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def.ghi", combinedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_1234567890abcdef", combinedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-proj-1234567890abcdef", combinedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-1234567890abcdef", combinedCause, StringComparison.Ordinal);
        Assert.Contains("[redacted exception detail]", combinedCause, StringComparison.Ordinal);
        Assert.Contains("[redacted path]", combinedCause, StringComparison.Ordinal);
        Assert.Contains("token=[redacted]", combinedCause, StringComparison.Ordinal);
        Assert.Contains("Bearer [redacted]", combinedCause, StringComparison.Ordinal);
        Assert.Contains("[redacted token]", combinedCause, StringComparison.Ordinal);
        Assert.Contains("[redacted stack frame]", combinedCause, StringComparison.Ordinal);
        Assert.Contains(
            "[redacted stack frame]\nFollow-up diagnostic context remains separate.",
            combinedCause,
            StringComparison.Ordinal);
    }
}
