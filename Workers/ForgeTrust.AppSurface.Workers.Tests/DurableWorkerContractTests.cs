using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Workers.Tests;

public sealed class DurableWorkerContractTests
{
    [Fact]
    public void ProjectionOutcome_NumericValuesRemainStable()
    {
        Assert.Equal(0, (int)DurableWorkerProjectionOutcome.Claimed);
        Assert.Equal(1, (int)DurableWorkerProjectionOutcome.Completed);
        Assert.Equal(2, (int)DurableWorkerProjectionOutcome.AlreadyCompleted);
        Assert.Equal(3, (int)DurableWorkerProjectionOutcome.Reconciled);
        Assert.Equal(4, (int)DurableWorkerProjectionOutcome.Noop);
        Assert.Equal(5, (int)DurableWorkerProjectionOutcome.StaleFence);
        Assert.Equal(6, (int)DurableWorkerProjectionOutcome.Conflict);
        Assert.Equal(7, (int)DurableWorkerProjectionOutcome.Unrecoverable);
    }

    [Fact]
    public void Retryability_NumericValuesRemainStable()
    {
        Assert.Equal(0, (int)DurableWorkerRetryability.Retryable);
        Assert.Equal(1, (int)DurableWorkerRetryability.Terminal);
        Assert.Equal(2, (int)DurableWorkerRetryability.OperatorRequired);
    }

    [Fact]
    public void Correlation_TrimsIdentifiers()
    {
        var correlation = new DurableWorkerCorrelation(" worker ", " work-1 ", " instance-1 ", " attempt-1 ");

        Assert.Equal("worker", correlation.WorkerName);
        Assert.Equal("work-1", correlation.WorkId);
        Assert.Equal("instance-1", correlation.InstanceId);
        Assert.Equal("attempt-1", correlation.AttemptId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Correlation_RejectsBlankIdentifiers(string value)
    {
        Assert.Throws<ArgumentException>(() => new DurableWorkerCorrelation(value, "work", "instance", "attempt"));
    }

    [Fact]
    public void Envelope_CopiesSafeMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["state"] = "claimed",
        };

        var envelope = new DurableWorkerEnvelope<string>(
            DurableWorkerProjectionOutcome.Claimed,
            "worker.claimed",
            DurableWorkerRetryability.Terminal,
            TestCorrelation(),
            "payload",
            metadata);
        metadata["state"] = "mutated";

        Assert.Equal("claimed", envelope.Metadata["state"]);
    }

    [Theory]
    [InlineData("oauth_token", "redacted")]
    [InlineData("safe", "Bearer abc")]
    [InlineData("rawPayload", "redacted")]
    [InlineData("safe", "client_secret=value")]
    public void Envelope_RejectsUnsafeMetadata(string key, string value)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key] = value,
        };

        Assert.Throws<DurableWorkerUnsafeMetadataException>(() => new DurableWorkerEnvelope<string>(
            DurableWorkerProjectionOutcome.Claimed,
            "worker.claimed",
            DurableWorkerRetryability.Terminal,
            TestCorrelation(),
            "payload",
            metadata));
    }

    [Fact]
    public void Diagnostic_RejectsUnsafeMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["providerUrl"] = "redacted",
        };

        Assert.Throws<DurableWorkerUnsafeMetadataException>(() => new DurableWorkerDiagnostic(
            "worker.provider-url",
            "Provider payload was unsafe.",
            "The payload contained a provider URL.",
            "Store the URL in app-owned private state instead.",
            DurableWorkerRetryability.OperatorRequired,
            metadata));
    }

    [Theory]
    [InlineData("Bearer abc", "Safe problem.", "Safe cause.", "Safe fix.")]
    [InlineData("worker.unsafe", "Authorization: Bearer abc", "Safe cause.", "Safe fix.")]
    [InlineData("worker.unsafe", "Safe problem.", "Provider payload was provider://gmail/message/1.", "Safe fix.")]
    [InlineData("worker.unsafe", "Safe problem.", "Safe cause.", "Delete the refresh_token value.")]
    public void Diagnostic_RejectsUnsafeText(string code, string problem, string cause, string fix)
    {
        Assert.Throws<DurableWorkerUnsafeMetadataException>(() => new DurableWorkerDiagnostic(
            code,
            problem,
            cause,
            fix,
            DurableWorkerRetryability.OperatorRequired));
    }

    [Fact]
    public void Diagnostic_AllowsStableProviderReasonCode()
    {
        var diagnostic = new DurableWorkerDiagnostic(
            "worker.provider-url",
            "Provider payload was unsafe.",
            "The payload carried a provider URL.",
            "Store the provider URL in app-owned private state instead.",
            DurableWorkerRetryability.OperatorRequired);

        Assert.Equal("worker.provider-url", diagnostic.Code);
    }

    [Fact]
    public void ProjectionRepairRequest_RequiresPositiveBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkerProjectionRepairRequest(
            DateTimeOffset.UtcNow,
            TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkerProjectionRepairRequest(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            0));
    }

    private static DurableWorkerCorrelation TestCorrelation() =>
        new("gmail-content-backfill", "work-1", "instance-1", "attempt-1");
}
