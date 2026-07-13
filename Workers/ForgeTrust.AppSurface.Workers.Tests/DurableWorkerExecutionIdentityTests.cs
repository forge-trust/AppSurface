using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Workers.Tests;

public sealed class DurableWorkerExecutionIdentityTests
{
    [Fact]
    public void Identity_keeps_provider_key_separate_from_attempt_and_lease()
    {
        var identity = new DurableWorkerExecutionIdentity(
            "activity",
            2,
            3,
            4,
            "epoch",
            "provider-activity");

        Assert.Equal("activity", identity.ActivityId);
        Assert.Equal(2, identity.AttemptNumber);
        Assert.Equal(3, identity.LeaseGeneration);
        Assert.Equal(4, identity.ScopeGeneration);
        Assert.Equal("epoch", identity.RuntimeEpoch);
        Assert.Equal("provider-activity", identity.ProviderKey);
    }

    [Theory]
    [InlineData(0, 1, 1, "attemptNumber")]
    [InlineData(1, 0, 1, "leaseGeneration")]
    [InlineData(1, 1, 0, "scopeGeneration")]
    public void Identity_rejects_nonpositive_generations(
        int attemptNumber,
        long leaseGeneration,
        long scopeGeneration,
        string parameterName)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkerExecutionIdentity(
            "activity",
            attemptNumber,
            leaseGeneration,
            scopeGeneration,
            "epoch",
            "provider"));
        Assert.Equal(parameterName, error.ParamName);
    }

    [Fact]
    public void Envelope_preserves_optional_execution_identity_without_breaking_legacy_callers()
    {
        var correlation = new DurableWorkerCorrelation("worker", "work", "instance", "attempt");
        var legacy = new DurableWorkerEnvelope<string>(
            DurableWorkerProjectionOutcome.Completed,
            "applied",
            DurableWorkerRetryability.Terminal,
            correlation,
            "result");
        var identity = new DurableWorkerExecutionIdentity("activity", 1, 1, 1, "epoch", "provider");
        var native = new DurableWorkerEnvelope<string>(
            DurableWorkerProjectionOutcome.Completed,
            "applied",
            DurableWorkerRetryability.Terminal,
            correlation,
            "result",
            executionIdentity: identity);

        Assert.Null(legacy.ExecutionIdentity);
        Assert.Same(identity, native.ExecutionIdentity);
    }
}
