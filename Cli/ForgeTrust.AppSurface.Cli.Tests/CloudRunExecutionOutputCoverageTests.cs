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

    [Theory]
    [InlineData(CloudRunExecutionState.Succeeded, "ASDEPLOY183", 0)]
    [InlineData(CloudRunExecutionState.Running, "ASDEPLOY182", 3)]
    public async Task DeliverAsync_TextSuccessAndRunningAreWrittenToStandardOutput(CloudRunExecutionState state, string code, int exitCode)
    {
        var output = new CaptureWriter();
        var error = new CaptureWriter();
        var observation = new CloudRunExecutionObservation("job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", state, true, DateTimeOffset.UnixEpoch, code);
        var result = await new CloudRunExecutionOutput(output, error, false).DeliverAsync(new(observation, code), CancellationToken.None);

        // protects=text-mode stdout routing for successful and still-running matched observations; fails_when=these states are sent to stderr; no_claim=completion beyond this observation
        Assert.Equal(exitCode, result);
        Assert.Contains($"{code}: {state}; configurationMatched=true.", output.Text, StringComparison.Ordinal);
        Assert.Contains("Recovery: caller-owned; no remote mutation.", output.Text, StringComparison.Ordinal);
        Assert.Empty(error.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task DeliverAsync_InvalidLogicalIdIsRedacted(string logicalJobId)
    {
        var output = new CaptureWriter();
        var observation = new CloudRunExecutionObservation(logicalJobId, "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", CloudRunExecutionState.Succeeded, true, DateTimeOffset.UnixEpoch, "ASDEPLOY183");
        await new CloudRunExecutionOutput(output, new CaptureWriter(), true).DeliverAsync(new(observation, "ASDEPLOY183"), CancellationToken.None);

        // protects=safe envelope logical ID bound; fails_when=empty or overlong ID is serialized; no_claim=bundle identity match
        Assert.Contains("\"logicalJobId\":\"\"", output.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliverAsync_CancellationDuringSerializationReplacesUncommittedResult()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new CaptureWriter();
        var observation = new CloudRunExecutionObservation("job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", CloudRunExecutionState.Succeeded, true, DateTimeOffset.UnixEpoch, "ASDEPLOY183");
        var serializations = 0;
        string Serialize(CloudRunExecutionEnvelope envelope)
        {
            serializations++;
            cancellation.Cancel();
            return System.Text.Json.JsonSerializer.Serialize(envelope);
        }

        var result = await new CloudRunExecutionOutput(output, new CaptureWriter(), true, Serialize)
            .DeliverAsync(new(observation, "ASDEPLOY183"), cancellation.Token);

        // protects=late cancellation before first write replaces the uncommitted success envelope; fails_when=success bytes are committed after cancellation; no_claim=remote execution cancellation
        Assert.Equal(130, result);
        Assert.Equal(2, serializations);
        Assert.Contains("\"diagnosticCode\":\"ASDEPLOY186\"", output.Text, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"Unknown\"", output.Text, StringComparison.Ordinal);
        Assert.Contains("\"configurationMatched\":false", output.Text, StringComparison.Ordinal);
        Assert.Contains("\"logicalJobId\":\"\"", output.Text, StringComparison.Ordinal);
    }

    private sealed class CaptureWriter : StringWriter
    {
        public string Text => ToString();
    }
}
