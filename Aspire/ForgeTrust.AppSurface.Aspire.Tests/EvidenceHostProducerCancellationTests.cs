using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed partial class EvidenceHostBootstrapTests
{
    [Fact]
    public async Task RunSharedCore_CallerCancellationDuringProducerJoinsBeforeCleanup()
    {
        var firstDeclaration = CreatePlan().Profile.Producers.Single();
        var plan = SealPlan(CreatePlan().Profile with
        {
            Producers = [firstDeclaration, firstDeclaration with { Id = "later" }],
        });
        var resource = new DisposableReadyResource("postgres");
        var producer = new CancellationBarrierProducer("coverage");
        var later = new DisposablePassingProducer("later", "coverage/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            registration.AddResource(resource);
            registration.AddProducer(producer);
            registration.AddProducer(later);
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, plan);
        using var cancellation = new CancellationTokenSource();

        var execution = run.RunAsync(host, cancellation.Token);
        EvidenceManifest manifest;
        try
        {
            await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await producer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await run.Supervisor.OwnedExitObserved.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(execution.IsCompleted);
            Assert.Equal(0, producer.DisposeCount);
            Assert.Equal(0, resource.DisposeCount);
            Assert.Equal(0, later.RunCount);
            Assert.Equal(1, run.Supervisor.ExitAcknowledgements);
            Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);

            producer.ReleaseSettlement.TrySetResult();
            manifest = await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            cancellation.Cancel();
            producer.ReleaseSettlement.TrySetResult();
            try
            {
                await execution.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception) when (execution.IsFaulted || execution.IsCanceled)
            {
                // Preserve an earlier assertion while still joining the host-owned execution.
            }
        }

        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
        Assert.Equal(nameof(EvidenceWorkerTerminalCode.CallerCancelled), manifest.Metrics.TerminalFailureCode);
        Assert.True(manifest.Metrics.CleanupCompleted);
        Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(0, later.RunCount);
        Assert.Equal(1, later.DisposeCount);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, run.Supervisor.StopRequests);
        Assert.Equal(1, run.Supervisor.ExitAcknowledgements);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
    }

    private sealed class CancellationBarrierProducer(string id) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id { get; } = id;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSettlement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RunCount { get; private set; }

        public int DisposeCount { get; private set; }

        public async ValueTask<EvidenceProducerResult> ProduceAsync(
            EvidenceProducerContext context,
            CancellationToken cancellationToken)
        {
            RunCount++;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await ReleaseSettlement.Task;
            }

            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
