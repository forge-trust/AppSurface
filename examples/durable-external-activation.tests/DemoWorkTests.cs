using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Examples.DurableExternalActivation;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

public sealed class DemoWorkTests
{
    // Value: protects=DemoWork requires an envelope and persisted payload before deterministic transformation; fails_when=either guard is removed; why_new=lifecycle tests cover valid persisted work only; seam=none
    [Fact]
    public async Task Executor_rejects_missing_work_and_transforms_a_valid_payload()
    {
        var executor = new DemoWorkExecutor();

        Assert.Throws<ArgumentNullException>(() => executor.ExecuteAsync(null!));
        Assert.Throws<InvalidOperationException>(() => executor.ExecuteAsync(CreateEnvelope(payload: null)));

        var result = await executor.ExecuteAsync(CreateEnvelope(new DemoWork("sample")));

        Assert.Equal("processed:sample", result.Value);
    }

    // Value: protects=DemoWork honors caller cancellation and preserves its token; fails_when=execution proceeds or throws with another token; why_new=lifecycle tests only invoke valid uncanceled work; seam=none
    [Fact]
    public void Executor_throws_for_canceled_work_with_the_callers_token()
    {
        var executor = new DemoWorkExecutor();
        var envelope = CreateEnvelope(new DemoWork("sample"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var observed = Assert.Throws<OperationCanceledException>(() =>
            executor.ExecuteAsync(envelope, cancellation.Token));

        Assert.Equal(cancellation.Token, observed.CancellationToken);
    }

    private static DurableWorkerEnvelope<DemoWork> CreateEnvelope(DemoWork? payload) => new(
        DurableWorkerProjectionOutcome.Claimed,
        "demo-test",
        DurableWorkerRetryability.Retryable,
        new DurableWorkerCorrelation("demo-worker", "work-1", "instance-1", "attempt-1"),
        payload);
}
