using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Workers.Tests;

public sealed class DurableWorkerExecutionIdentityTests
{
    [Fact]
    public void Identity_keeps_provider_key_separate_from_attempt_and_lease()
    {
        var identity = DurableWorkerExecutionIdentity.CreateInitial("activity", 2, 3, "epoch")
            .Advance(2, 3, 4, "epoch");

        Assert.Equal("activity", identity.ActivityId);
        Assert.Equal(2, identity.AttemptNumber);
        Assert.Equal(3, identity.LeaseGeneration);
        Assert.Equal(4, identity.ScopeGeneration);
        Assert.Equal("epoch", identity.RuntimeEpoch);
        Assert.Equal("activity", identity.ProviderKey);
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
        var initial = DurableWorkerExecutionIdentity.CreateInitial("activity", 1, 1, "epoch");
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => initial.Advance(
            attemptNumber,
            leaseGeneration,
            scopeGeneration,
            "next-epoch"));
        Assert.Equal(parameterName, error.ParamName);
    }

    [Theory]
    [InlineData(0, 1, 1, "attemptNumber")]
    [InlineData(1, 0, 1, "leaseGeneration")]
    [InlineData(1, 1, 0, "scopeGeneration")]
    public void Create_rejects_nonpositive_generations(
        int attemptNumber,
        long leaseGeneration,
        long scopeGeneration,
        string parameterName)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => DurableWorkerExecutionIdentity.Create(
            "activity",
            attemptNumber,
            leaseGeneration,
            scopeGeneration,
            "epoch"));

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
        var identity = DurableWorkerExecutionIdentity.CreateInitial("activity", 1, 1, "epoch");
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

    [Fact]
    public void Envelope_rejects_null_native_execution_identity()
    {
        var correlation = new DurableWorkerCorrelation("worker", "work", "instance", "attempt");

        var error = Assert.Throws<ArgumentNullException>(() => new DurableWorkerEnvelope<string>(
            DurableWorkerProjectionOutcome.Completed,
            "applied",
            DurableWorkerRetryability.Terminal,
            correlation,
            "result",
            executionIdentity: null!));

        Assert.Equal("executionIdentity", error.ParamName);
    }

    [Fact]
    public void Advance_retains_provider_identity_and_rejects_regression()
    {
        var initial = DurableWorkerExecutionIdentity.CreateInitial("activity", 3, 4, "epoch-1");
        var next = initial.Advance(2, 5, 5, "epoch-2");

        Assert.Equal(initial.ActivityId, next.ActivityId);
        Assert.Equal(initial.ProviderKey, next.ProviderKey);
        Assert.Equal(2, next.AttemptNumber);
        Assert.Equal(5, next.LeaseGeneration);
        Assert.Equal(5, next.ScopeGeneration);
        Assert.Equal("epoch-2", next.RuntimeEpoch);
        Assert.Throws<ArgumentOutOfRangeException>(() => next.Advance(1, 6, 5, "epoch-2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => next.Advance(2, 4, 5, "epoch-2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => next.Advance(2, 5, 4, "epoch-2"));
    }

    [Fact]
    public void Advance_rejects_an_unchanged_identity()
    {
        var identity = DurableWorkerExecutionIdentity.CreateInitial("activity", 3, 4, "epoch");

        var error = Assert.Throws<ArgumentException>(() => identity.Advance(1, 3, 4, "epoch"));

        Assert.Null(error.ParamName);
    }
}
