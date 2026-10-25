using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CloudRunExecutionOutputCoverageTests
{
    [Theory]
    [InlineData("ASDEPLOY170")]
    [InlineData("ASDEPLOY171")]
    [InlineData("ASDEPLOY172")]
    [InlineData("ASDEPLOY173")]
    [InlineData("ASDEPLOY174")]
    [InlineData("ASDEPLOY175")]
    [InlineData("ASDEPLOY176")]
    [InlineData("ASDEPLOY177")]
    [InlineData("ASDEPLOY178")]
    [InlineData("ASDEPLOY179")]
    [InlineData("ASDEPLOY180")]
    [InlineData("ASDEPLOY181")]
    [InlineData("ASDEPLOY182")]
    [InlineData("ASDEPLOY183")]
    [InlineData("ASDEPLOY184")]
    [InlineData("ASDEPLOY185")]
    [InlineData("ASDEPLOY186")]
    [InlineData("ASDEPLOY187")]
    [InlineData("ASDEPLOY188")]
    [InlineData("ASDEPLOY189")]
    public async Task DeliverAsync_TextDiagnosticsHaveSafeGuidanceAndCanonicalDocumentation(string code)
    {
        var error = new CaptureWriter();
        var stdout = new CaptureWriter();
        var result = await new CloudRunExecutionOutput(stdout, error, false).DeliverAsync(new(null, code), CancellationToken.None);

        // protects=all stable diagnostic text arms and safe recovery contract; fails_when=code lacks guidance or emits to stdout; no_claim=remediation outcome
        Assert.Equal(2, result);
        Assert.Empty(stdout.Text);
        Assert.Contains($"{code}: Unknown", error.Text, StringComparison.Ordinal);
        Assert.Contains("Fix:", error.Text, StringComparison.Ordinal);
        Assert.Contains($"reference.md#{code.ToLowerInvariant()}", error.Text, StringComparison.Ordinal);
        Assert.Contains("no remote mutation", error.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("ASDEPLOY169")]
    [InlineData("ASDEPLOY190")]
    [InlineData("ASDEPLOY999")]
    public async Task DeliverAsync_InvalidDiagnosticCodesAreNormalized(string code)
    {
        var error = new CaptureWriter();
        var result = await new CloudRunExecutionOutput(new CaptureWriter(), error, false).DeliverAsync(new(null, code), CancellationToken.None);

        // protects=bounded public diagnostic range; fails_when=invalid workflow code is exposed; no_claim=underlying failure classification
        Assert.Equal(2, result);
        Assert.Contains("ASDEPLOY189:", error.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(code + ":", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliverAsync_UndefinedObservationStateIsUnknownAndUnmatched()
    {
        var output = new CaptureWriter();
        var observation = new CloudRunExecutionObservation("job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", (CloudRunExecutionState)999, true, DateTimeOffset.UnixEpoch, "ASDEPLOY183");
        var result = await new CloudRunExecutionOutput(output, new CaptureWriter(), true).DeliverAsync(new(observation, "ASDEPLOY183"), CancellationToken.None);

        // protects=enum forward-safety and output sanitization; fails_when=undefined enum looks successful or leaks numeric state; no_claim=provider state validity
        Assert.Equal(2, result);
        Assert.Contains("\"state\":\"Unknown\"", output.Text, StringComparison.Ordinal);
        Assert.Contains("\"configurationMatched\":false", output.Text, StringComparison.Ordinal);
    }

    private sealed class CaptureWriter : StringWriter
    {
        public string Text => ToString();
    }
}
