using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CloudRunExecutionOutputTests
{
    [Theory]
    [InlineData(CloudRunExecutionState.Succeeded, true, 0)]
    [InlineData(CloudRunExecutionState.Failed, true, 1)]
    [InlineData(CloudRunExecutionState.Cancelled, true, 1)]
    [InlineData(CloudRunExecutionState.Running, true, 3)]
    [InlineData(CloudRunExecutionState.Running, false, 2)]
    [InlineData(CloudRunExecutionState.Unknown, false, 2)]
    public async Task DeliverAsync_MapsOnlyMatchedStatesToTerminalSuccesses(CloudRunExecutionState state, bool matched, int expectedExit)
    {
        var output = new RecordingWriter();
        var result = Result(state, matched, "ASDEPLOY183");
        var exit = await new CloudRunExecutionOutput(output, new RecordingWriter(), json: true).DeliverAsync(result, CancellationToken.None);

        // protects=state/exit contract; fails_when=unmatched Running or Unknown is treated as success; no_claim=provider state freshness
        Assert.Equal(expectedExit, exit);
        Assert.Single(output.Writes);
    }

    [Fact]
    public async Task DeliverAsync_JsonHasStableShapeAndSanitizesInvalidIdentifiers()
    {
        var output = new RecordingWriter();
        var observation = new CloudRunExecutionObservation("bad id", "secret-provider-value", CloudRunExecutionState.Succeeded, true, DateTimeOffset.UtcNow, "ASDEPLOY183");
        var exit = await new CloudRunExecutionOutput(output, new RecordingWriter(), true).DeliverAsync(new CloudRunExecutionWorkflowResult(observation, "ASDEPLOY183"), CancellationToken.None);

        // protects=safe versioned JSON; fails_when=raw identifiers or provider values are emitted; no_claim=publisher authenticity
        Assert.Equal(0, exit);
        Assert.Contains("\"schemaVersion\":\"1.0\"", output.Text);
        Assert.Contains("\"logicalJobId\":\"\"", output.Text);
        Assert.DoesNotContain("secret-provider-value", output.Text);
    }

    [Fact]
    public async Task DeliverAsync_SerializerFailureWritesNothing()
    {
        var output = new RecordingWriter();
        var error = new RecordingWriter();
        var result = await new CloudRunExecutionOutput(output, error, true, _ => throw new InvalidOperationException()).DeliverAsync(Result(CloudRunExecutionState.Succeeded, true, "ASDEPLOY183"), CancellationToken.None);

        // protects=uncommitted serialization; fails_when=serializer failure emits partial/fallback output; no_claim=serializer exception detail
        Assert.Equal(2, result);
        Assert.Empty(output.Writes);
        Assert.Empty(error.Writes);
    }

    [Fact]
    public async Task DeliverAsync_WriteFailureDoesNotFallbackToError()
    {
        var output = new RecordingWriter(throwWrite: true);
        var error = new RecordingWriter();
        var result = await new CloudRunExecutionOutput(output, error, true).DeliverAsync(Result(CloudRunExecutionState.Succeeded, true, "ASDEPLOY183"), CancellationToken.None);

        // protects=single output owner; fails_when=stdout failure creates a second stderr message; no_claim=stream recovery
        Assert.Equal(2, result);
        Assert.Empty(error.Writes);
    }

    [Fact]
    public async Task DeliverAsync_FlushFailureDoesNotFallbackToError()
    {
        var output = new RecordingWriter(throwFlush: true);
        var error = new RecordingWriter();
        var result = await new CloudRunExecutionOutput(output, error, true).DeliverAsync(Result(CloudRunExecutionState.Succeeded, true, "ASDEPLOY183"), CancellationToken.None);

        // protects=flush failure latch; fails_when=flush failure emits another diagnostic; no_claim=durability after OS failure
        Assert.Equal(2, result);
        Assert.Empty(error.Writes);
    }

    [Fact]
    public async Task DeliverAsync_CancellationUsesOneSafeEnvelopeAnd130()
    {
        var output = new RecordingWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new CloudRunExecutionOutput(output, new RecordingWriter(), true).DeliverAsync(Result(CloudRunExecutionState.Succeeded, true, "ASDEPLOY183"), cancellation.Token);

        // protects=caller cancellation precedence; fails_when=success envelope is emitted after cancellation; no_claim=remote execution cancellation
        Assert.Equal(130, result);
        Assert.Single(output.Writes);
        Assert.Contains("ASDEPLOY186", output.Text);
    }

    [Fact]
    public async Task DeliverAsync_CancellationDuringSerializationReplacesUncommittedEnvelope()
    {
        var output = new RecordingWriter();
        using var cancellation = new CancellationTokenSource();
        var result = await new CloudRunExecutionOutput(output, new RecordingWriter(), true, envelope =>
        {
            if (!cancellation.IsCancellationRequested) cancellation.Cancel();
            return JsonSerializer.Serialize(envelope);
        }).DeliverAsync(Result(CloudRunExecutionState.Succeeded, true, "ASDEPLOY183"), cancellation.Token);

        // protects=uncommitted cancellation replacement; fails_when=cancelled success payload is committed; no_claim=output sink cancellation timing
        Assert.Equal(130, result);
        Assert.Single(output.Writes);
        Assert.Contains("ASDEPLOY186", output.Text);
    }

    [Fact]
    public async Task DeliverAsync_CancellationDuringWriteCommitsOnceAndReturns130()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new RecordingWriter(beforeWrite: cancellation.Cancel);
        var result = await new CloudRunExecutionOutput(output, new RecordingWriter(), true).DeliverAsync(Result(CloudRunExecutionState.Succeeded, true, "ASDEPLOY183"), cancellation.Token);

        // protects=write-time cancellation latch; fails_when=second cancellation envelope is written; no_claim=write durability
        Assert.Equal(130, result);
        Assert.Single(output.Writes);
        Assert.DoesNotContain("ASDEPLOY186", output.Text);
    }

    [Fact]
    public async Task DeliverAsync_CancellationAfterFlushCommitsOnceAndReturns130()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new RecordingWriter(afterFlush: cancellation.Cancel);
        var result = await new CloudRunExecutionOutput(output, new RecordingWriter(), true).DeliverAsync(Result(CloudRunExecutionState.Succeeded, true, "ASDEPLOY183"), cancellation.Token);

        // protects=post-delivery cancellation precedence; fails_when=post-success cancellation emits another object; no_claim=remote execution state
        Assert.Equal(130, result);
        Assert.Single(output.Writes);
    }

    [Fact]
    public async Task DeliverAsync_AlreadyBrokenCancellationSinkReturns130WithoutFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = new RecordingWriter(throwWrite: true);
        var error = new RecordingWriter();
        var result = await new CloudRunExecutionOutput(output, error, true).DeliverAsync(Result(CloudRunExecutionState.Succeeded, true, "ASDEPLOY183"), cancellation.Token);

        // protects=best-effort cancellation envelope; fails_when=broken sink produces fallback output; no_claim=delivery success
        Assert.Equal(130, result);
        Assert.Empty(error.Writes);
    }

    [Fact]
    public async Task DeliverAsync_TextFailureUsesErrorSinkAndSafeGuidance()
    {
        var output = new RecordingWriter();
        var error = new RecordingWriter();
        var result = await new CloudRunExecutionOutput(output, error, json: false).DeliverAsync(Result(CloudRunExecutionState.Unknown, false, "ASDEPLOY179"), CancellationToken.None);

        // protects=safe text diagnostic channel; fails_when=Unknown result writes unsafe/raw stdout; no_claim=provider remediation success
        Assert.Equal(2, result);
        Assert.Empty(output.Writes);
        Assert.Contains("ASDEPLOY179", error.Text);
        Assert.Contains("Fix:", error.Text);
        Assert.Contains("Docs:", error.Text);
    }

    private static CloudRunExecutionWorkflowResult Result(CloudRunExecutionState state, bool matched, string code) =>
        new(new CloudRunExecutionObservation("job", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", state, matched, DateTimeOffset.UtcNow, code), code);

    private sealed class RecordingWriter(bool throwWrite = false, bool throwFlush = false, Action? beforeWrite = null, Action? afterFlush = null) : TextWriter
    {
        public List<string> Writes { get; } = [];
        public string Text => string.Join("", Writes);
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value)
        {
            if (throwWrite) throw new IOException("sink");
            beforeWrite?.Invoke();
            Writes.Add(value + Environment.NewLine);
            return Task.CompletedTask;
        }
        public override Task FlushAsync()
        {
            if (throwFlush) throw new IOException("flush");
            afterFlush?.Invoke();
            return Task.CompletedTask;
        }
    }
}
