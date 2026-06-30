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

    [Fact]
    public void Envelope_AllowsNullMetadataAndCarriesPayloadAndDiagnostic()
    {
        var diagnostic = new DurableWorkerDiagnostic(
            "worker.claimed",
            "Work was claimed.",
            "The fence was current.",
            "Run the executor activity.",
            DurableWorkerRetryability.Terminal);

        var envelope = new DurableWorkerEnvelope<string>(
            DurableWorkerProjectionOutcome.Claimed,
            " worker.claimed ",
            DurableWorkerRetryability.Retryable,
            TestCorrelation(),
            "payload",
            diagnostic: diagnostic);

        Assert.Equal(DurableWorkerProjectionOutcome.Claimed, envelope.Outcome);
        Assert.Equal("worker.claimed", envelope.ReasonCode);
        Assert.Equal(DurableWorkerRetryability.Retryable, envelope.Retryability);
        Assert.Equal("payload", envelope.Payload);
        Assert.Empty(envelope.Metadata);
        Assert.Same(diagnostic, envelope.Diagnostic);
    }

    [Fact]
    public void Envelope_RejectsBlankReasonAndNullCorrelation()
    {
        Assert.Throws<ArgumentException>(() => new DurableWorkerEnvelope<string>(
            DurableWorkerProjectionOutcome.Claimed,
            " ",
            DurableWorkerRetryability.Terminal,
            TestCorrelation()));

        Assert.Throws<ArgumentNullException>(() => new DurableWorkerEnvelope<string>(
            DurableWorkerProjectionOutcome.Claimed,
            "worker.claimed",
            DurableWorkerRetryability.Terminal,
            null!));
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

    [Fact]
    public void Diagnostic_TrimsTextAndCopiesSafeMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["retry-count"] = "2",
        };

        var diagnostic = new DurableWorkerDiagnostic(
            " worker.retry ",
            " Retry is pending. ",
            " A transient conflict occurred. ",
            " Wait for the durable retry timer. ",
            DurableWorkerRetryability.Retryable,
            metadata);
        metadata["retry-count"] = "3";

        Assert.Equal("worker.retry", diagnostic.Code);
        Assert.Equal("Retry is pending.", diagnostic.Problem);
        Assert.Equal("A transient conflict occurred.", diagnostic.Cause);
        Assert.Equal("Wait for the durable retry timer.", diagnostic.Fix);
        Assert.Equal(DurableWorkerRetryability.Retryable, diagnostic.Retryability);
        Assert.Equal("2", diagnostic.Metadata["retry-count"]);
    }

    [Theory]
    [InlineData("", "Safe problem.", "Safe cause.", "Safe fix.")]
    [InlineData("worker.blank", " ", "Safe cause.", "Safe fix.")]
    [InlineData("worker.blank", "Safe problem.", "", "Safe fix.")]
    [InlineData("worker.blank", "Safe problem.", "Safe cause.", " ")]
    public void Diagnostic_RejectsBlankText(string code, string problem, string cause, string fix)
    {
        Assert.Throws<ArgumentException>(() => new DurableWorkerDiagnostic(
            code,
            problem,
            cause,
            fix,
            DurableWorkerRetryability.OperatorRequired));
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
    public void MetadataSafety_AllowsNullEmptyAndSafeMetadata()
    {
        Assert.Empty(DurableWorkerMetadataSafety.CopySafe(null));
        Assert.Empty(DurableWorkerMetadataSafety.CopySafe(new Dictionary<string, string>(StringComparer.Ordinal)));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason.code"] = "projection_repaired",
        };

        DurableWorkerMetadataSafety.EnsureSafe(metadata);
        Assert.Equal("projection_repaired", DurableWorkerMetadataSafety.CopySafe(metadata)["reason.code"]);
    }

    [Theory]
    [InlineData("", "safe")]
    [InlineData("safe", " ")]
    [InlineData("raw-payload", "safe")]
    [InlineData("provider.url", "safe")]
    [InlineData("safe", "password=value")]
    [InlineData("safe", "-----BEGIN PRIVATE KEY-----")]
    public void MetadataSafety_RejectsUnsafeKeysAndValues(string key, string value)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key] = value,
        };

        Assert.ThrowsAny<ArgumentException>(() => DurableWorkerMetadataSafety.CopySafe(metadata));
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

    [Fact]
    public void ProjectionRepairRequest_CapturesBounds()
    {
        var now = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var request = new DurableWorkerProjectionRepairRequest(now, TimeSpan.FromMinutes(5), 25);

        Assert.Equal(now, request.Now);
        Assert.Equal(TimeSpan.FromMinutes(5), request.MaxStaleness);
        Assert.Equal(25, request.MaxItems);
    }

    private static DurableWorkerCorrelation TestCorrelation() =>
        new("gmail-content-backfill", "work-1", "instance-1", "attempt-1");
}
