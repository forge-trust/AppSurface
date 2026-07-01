using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using ForgeTrust.AppSurface.Workers;
using ForgeTrust.AppSurface.Workers.DurableTask;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Workers.DurableTask.Tests;

public sealed class DurableTaskWorkerChainRunnerTests
{
    [Fact]
    public async Task TryClaimAsync_ClaimedOutcomeSchedulesExecutorWithRetryPolicy()
    {
        var retry = new FlowRetryPolicy(3, TimeSpan.FromSeconds(2));
        var runner = CreateRunner(executorRetryPolicy: retry);
        var contract = new FakeProjectionContract
        {
            ClaimEnvelope = Envelope<string>(DurableWorkerProjectionOutcome.Claimed, "work"),
        };

        var decision = await runner.TryClaimAsync(contract, "work", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.ScheduleExecutor, decision.Kind);
        Assert.Equal("work", decision.Work);
        Assert.Same(retry, decision.RetryPolicy);
        Assert.Equal(1, contract.ClaimCalls);
    }

    [Fact]
    public async Task TryClaimAsync_DefaultOutcomeCompletesWithoutExecutor()
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            ClaimEnvelope = Envelope<string>(DurableWorkerProjectionOutcome.AlreadyCompleted, "work"),
        };

        var decision = await runner.TryClaimAsync(contract, "work", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Complete, decision.Kind);
        Assert.Equal(DurableWorkerProjectionOutcome.AlreadyCompleted, decision.SourceOutcome);
        Assert.Null(decision.Work);
    }

    [Fact]
    public async Task TryClaimAsync_TerminalConflictFaults()
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            ClaimEnvelope = Envelope<string>(
                DurableWorkerProjectionOutcome.Conflict,
                "work",
                DurableWorkerRetryability.OperatorRequired),
        };

        var decision = await runner.TryClaimAsync(contract, "work", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Fault, decision.Kind);
        Assert.Equal(DurableWorkerProjectionOutcome.Conflict, decision.SourceOutcome);
    }

    [Theory]
    [InlineData(DurableWorkerProjectionOutcome.Completed)]
    [InlineData(DurableWorkerProjectionOutcome.Reconciled)]
    public async Task TryClaimAsync_InvalidStageOutcomeFaults(DurableWorkerProjectionOutcome outcome)
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            ClaimEnvelope = Envelope<string>(outcome, "work"),
        };

        var decision = await runner.TryClaimAsync(contract, "work", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Fault, decision.Kind);
        Assert.Equal(outcome, decision.SourceOutcome);
    }

    [Fact]
    public async Task TryClaimAsync_RejectsNullInputsAndNullEnvelope()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await runner.TryClaimAsync(null!, "work", TestCorrelation()));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await runner.TryClaimAsync(new FakeProjectionContract(), "work", null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await runner.TryClaimAsync(new NullProjectionContract(), "work", TestCorrelation()));
    }

    [Fact]
    public async Task CompleteAsync_FreshCompletionSchedulesProjectionRepair()
    {
        var retry = new FlowRetryPolicy(4, TimeSpan.FromSeconds(1), backoffCoefficient: 2);
        var runner = CreateRunner(projectionRetryPolicy: retry);
        var contract = new FakeProjectionContract
        {
            CompletionEnvelope = Envelope<string>(DurableWorkerProjectionOutcome.Completed, "result"),
        };

        var decision = await runner.CompleteAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.RepairProjection, decision.Kind);
        Assert.Equal("result", decision.Result);
        Assert.Same(retry, decision.RetryPolicy);
        Assert.Equal(1, contract.CompleteCalls);
    }

    [Fact]
    public async Task CompleteAsync_DuplicateCompletionCompletesWithoutRepair()
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            CompletionEnvelope = Envelope<string>(DurableWorkerProjectionOutcome.AlreadyCompleted, "result"),
        };

        var decision = await runner.CompleteAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Complete, decision.Kind);
        Assert.Null(decision.RetryPolicy);
        Assert.Null(decision.Projection);
    }

    [Fact]
    public async Task CompleteAsync_StaleFenceIgnoresLateSignal()
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            CompletionEnvelope = Envelope<string>(DurableWorkerProjectionOutcome.StaleFence, "result"),
        };

        var decision = await runner.CompleteAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.IgnoreLateSignal, decision.Kind);
        Assert.Equal(DurableWorkerProjectionOutcome.StaleFence, decision.SourceOutcome);
    }

    [Theory]
    [InlineData(DurableWorkerProjectionOutcome.Claimed)]
    [InlineData(DurableWorkerProjectionOutcome.Reconciled)]
    public async Task CompleteAsync_InvalidStageOutcomeFaults(DurableWorkerProjectionOutcome outcome)
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            CompletionEnvelope = Envelope<string>(outcome, "result"),
        };

        var decision = await runner.CompleteAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Fault, decision.Kind);
        Assert.Equal(outcome, decision.SourceOutcome);
    }

    [Fact]
    public async Task CompleteAsync_RetryableConflictWaitsForProjectionRetryPolicy()
    {
        var retry = new FlowRetryPolicy(3, TimeSpan.FromSeconds(5));
        var runner = CreateRunner(projectionRetryPolicy: retry);
        var contract = new FakeProjectionContract
        {
            CompletionEnvelope = Envelope<string>(
                DurableWorkerProjectionOutcome.Conflict,
                "result",
                DurableWorkerRetryability.Retryable),
        };

        var decision = await runner.CompleteAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.WaitForRetry, decision.Kind);
        Assert.Same(retry, decision.RetryPolicy);
        Assert.Equal(DurableWorkerProjectionOutcome.Conflict, decision.SourceOutcome);
    }

    [Fact]
    public async Task CompleteAsync_RejectsNullInputsAndNullEnvelope()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await runner.CompleteAsync(null!, "work", "result", TestCorrelation()));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await runner.CompleteAsync(new FakeProjectionContract(), "work", "result", null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await runner.CompleteAsync(new NullProjectionContract(), "work", "result", TestCorrelation()));
    }

    [Fact]
    public async Task ReconcileProjectionAsync_ReconciledOutcomeNeverSchedulesExecutor()
    {
        var runner = CreateRunner(executorRetryPolicy: new FlowRetryPolicy(2, TimeSpan.FromSeconds(1)));
        var contract = new FakeProjectionContract
        {
            ProjectionEnvelope = Envelope<string>(DurableWorkerProjectionOutcome.Reconciled, "projection"),
        };

        var decision = await runner.ReconcileProjectionAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Complete, decision.Kind);
        Assert.NotEqual(DurableTaskWorkerDecisionKind.ScheduleExecutor, decision.Kind);
        Assert.Equal("projection", decision.Projection);
        Assert.Equal(1, contract.ReconcileCalls);
        Assert.Equal(0, contract.ClaimCalls);
    }

    [Fact]
    public async Task ReconcileProjectionAsync_RetryableConflictWaitsForProjectionRetryPolicy()
    {
        var retry = new FlowRetryPolicy(6, TimeSpan.FromSeconds(4));
        var runner = CreateRunner(projectionRetryPolicy: retry);
        var contract = new FakeProjectionContract
        {
            ProjectionEnvelope = Envelope<string>(
                DurableWorkerProjectionOutcome.Conflict,
                "projection",
                DurableWorkerRetryability.Retryable),
        };

        var decision = await runner.ReconcileProjectionAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.WaitForRetry, decision.Kind);
        Assert.Same(retry, decision.RetryPolicy);
        Assert.Equal(DurableWorkerProjectionOutcome.Conflict, decision.SourceOutcome);
    }

    [Fact]
    public async Task ReconcileProjectionAsync_StaleFenceIgnoresLateSignal()
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            ProjectionEnvelope = Envelope<string>(DurableWorkerProjectionOutcome.StaleFence, "projection"),
        };

        var decision = await runner.ReconcileProjectionAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.IgnoreLateSignal, decision.Kind);
        Assert.Equal(DurableWorkerProjectionOutcome.StaleFence, decision.SourceOutcome);
    }

    [Fact]
    public async Task ReconcileProjectionAsync_NoopCompletesWithoutProjectionPayload()
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            ProjectionEnvelope = Envelope<string>(DurableWorkerProjectionOutcome.Noop, "projection"),
        };

        var decision = await runner.ReconcileProjectionAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Complete, decision.Kind);
        Assert.Null(decision.Projection);
        Assert.Equal(DurableWorkerProjectionOutcome.Noop, decision.SourceOutcome);
    }

    [Theory]
    [InlineData(DurableWorkerProjectionOutcome.Claimed)]
    [InlineData(DurableWorkerProjectionOutcome.Completed)]
    [InlineData(DurableWorkerProjectionOutcome.AlreadyCompleted)]
    public async Task ReconcileProjectionAsync_InvalidStageOutcomeFaults(DurableWorkerProjectionOutcome outcome)
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            ProjectionEnvelope = Envelope<string>(outcome, "projection"),
        };

        var decision = await runner.ReconcileProjectionAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Fault, decision.Kind);
        Assert.Equal(outcome, decision.SourceOutcome);
    }

    [Theory]
    [InlineData(DurableWorkerProjectionOutcome.Conflict)]
    [InlineData(DurableWorkerProjectionOutcome.Unrecoverable)]
    public async Task ReconcileProjectionAsync_TerminalFailureFaults(DurableWorkerProjectionOutcome outcome)
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            ProjectionEnvelope = Envelope<string>(outcome, "projection", DurableWorkerRetryability.OperatorRequired),
        };

        var decision = await runner.ReconcileProjectionAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Fault, decision.Kind);
        Assert.Equal(outcome, decision.SourceOutcome);
    }

    [Fact]
    public async Task ReconcileProjectionAsync_RejectsNullInputsAndNullEnvelope()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await runner.ReconcileProjectionAsync(null!, "work", "result", TestCorrelation()));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await runner.ReconcileProjectionAsync(new FakeProjectionContract(), "work", "result", null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await runner.ReconcileProjectionAsync(new NullProjectionContract(), "work", "result", TestCorrelation()));
    }

    [Fact]
    public async Task TryClaimAsync_StaleFenceIgnoresLateSignal()
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            ClaimEnvelope = Envelope<string>(DurableWorkerProjectionOutcome.StaleFence, "work"),
        };

        var decision = await runner.TryClaimAsync(contract, "work", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.IgnoreLateSignal, decision.Kind);
        Assert.Equal(DurableWorkerProjectionOutcome.StaleFence, decision.SourceOutcome);
    }

    [Fact]
    public async Task TryClaimAsync_RetryableConflictWaitsForRetry()
    {
        var retry = new FlowRetryPolicy(5, TimeSpan.FromSeconds(3));
        var runner = CreateRunner(executorRetryPolicy: retry);
        var contract = new FakeProjectionContract
        {
            ClaimEnvelope = Envelope<string>(
                DurableWorkerProjectionOutcome.Conflict,
                "work",
                DurableWorkerRetryability.Retryable),
        };

        var decision = await runner.TryClaimAsync(contract, "work", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.WaitForRetry, decision.Kind);
        Assert.Same(retry, decision.RetryPolicy);
    }

    [Theory]
    [InlineData(DurableWorkerProjectionOutcome.Conflict)]
    [InlineData(DurableWorkerProjectionOutcome.Unrecoverable)]
    public async Task CompleteAsync_TerminalFailureFaults(DurableWorkerProjectionOutcome outcome)
    {
        var runner = CreateRunner();
        var contract = new FakeProjectionContract
        {
            CompletionEnvelope = Envelope<string>(outcome, "result", DurableWorkerRetryability.OperatorRequired),
        };

        var decision = await runner.CompleteAsync(contract, "work", "result", TestCorrelation());

        Assert.Equal(DurableTaskWorkerDecisionKind.Fault, decision.Kind);
    }

    [Fact]
    public void WaitForExternalEvent_CarriesEventNameAndTimeout()
    {
        var runner = CreateRunner();
        var timeout = new FlowTimeout(TimeSpan.FromMinutes(5));

        var decision = runner.WaitForExternalEvent(TestCorrelation(), "resume-approved", timeout);

        Assert.Equal(DurableTaskWorkerDecisionKind.WaitForExternalEvent, decision.Kind);
        Assert.Equal("resume-approved", decision.EventName);
        Assert.Same(timeout, decision.Timeout);
    }

    [Fact]
    public void IgnoreLateSignal_CreatesStaleFenceDecisionWithDiagnostic()
    {
        var runner = CreateRunner();
        var diagnostic = new DurableWorkerDiagnostic(
            "worker.signal-late",
            "The signal arrived after completion.",
            "The durable instance had already advanced.",
            "Ignore the stale signal.",
            DurableWorkerRetryability.Terminal);

        var decision = runner.IgnoreLateSignal(TestCorrelation(), "resume-approved", diagnostic);

        Assert.Equal(DurableTaskWorkerDecisionKind.IgnoreLateSignal, decision.Kind);
        Assert.Equal("resume-approved", decision.EventName);
        Assert.Equal(DurableWorkerProjectionOutcome.StaleFence, decision.SourceOutcome);
        Assert.Same(diagnostic, decision.Diagnostic);
    }

    [Fact]
    public void Constructor_RejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DurableTaskWorkerChainRunner<string, string, string>(null!));
    }

    [Fact]
    public void TimedOut_CarriesEventNameAndDiagnostic()
    {
        var runner = CreateRunner();
        var diagnostic = new DurableWorkerDiagnostic(
            "worker.timeout",
            "The worker timed out.",
            "No resume event arrived before the timer.",
            "Retry or surface operator action.",
            DurableWorkerRetryability.Retryable);

        var decision = runner.TimedOut(TestCorrelation(), "resume-approved", diagnostic);

        Assert.Equal(DurableTaskWorkerDecisionKind.TimedOut, decision.Kind);
        Assert.Equal("resume-approved", decision.EventName);
        Assert.Same(diagnostic, decision.Diagnostic);
    }

    private static DurableTaskWorkerChainRunner<string, string, string> CreateRunner(
        FlowRetryPolicy? executorRetryPolicy = null,
        FlowRetryPolicy? projectionRetryPolicy = null) =>
        new(Options.Create(new AppSurfaceWorkersDurableTaskOptions
        {
            ExecutorRetryPolicy = executorRetryPolicy,
            ProjectionRetryPolicy = projectionRetryPolicy,
        }));

    private static DurableWorkerEnvelope<T> Envelope<T>(
        DurableWorkerProjectionOutcome outcome,
        T payload,
        DurableWorkerRetryability retryability = DurableWorkerRetryability.Terminal) =>
        new(
            outcome,
            $"worker.{outcome.ToString().ToLowerInvariant()}",
            retryability,
            TestCorrelation(),
            payload);

    private static DurableWorkerCorrelation TestCorrelation() =>
        new("gmail-content-backfill", "work-1", "instance-1", "attempt-1");

    private sealed class FakeProjectionContract : IDurableWorkerProjectionContract<string, string, string>
    {
        public DurableWorkerEnvelope<string>? ClaimEnvelope { get; init; }

        public DurableWorkerEnvelope<string>? CompletionEnvelope { get; init; }

        public DurableWorkerEnvelope<string>? ProjectionEnvelope { get; init; }

        public int ClaimCalls { get; private set; }

        public int CompleteCalls { get; private set; }

        public int ReconcileCalls { get; private set; }

        public ValueTask<DurableWorkerEnvelope<string>> TryClaimAsync(
            string work,
            DurableWorkerCorrelation correlation,
            CancellationToken cancellationToken = default)
        {
            ClaimCalls++;
            return ValueTask.FromResult(ClaimEnvelope ?? Envelope(DurableWorkerProjectionOutcome.Claimed, work));
        }

        public ValueTask<DurableWorkerEnvelope<string>> CompleteAsync(
            string work,
            string result,
            DurableWorkerCorrelation correlation,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            return ValueTask.FromResult(CompletionEnvelope ?? Envelope(DurableWorkerProjectionOutcome.Completed, result));
        }

        public ValueTask<DurableWorkerEnvelope<string>> ReconcileProjectionAsync(
            string work,
            string result,
            DurableWorkerCorrelation correlation,
            CancellationToken cancellationToken = default)
        {
            ReconcileCalls++;
            return ValueTask.FromResult(ProjectionEnvelope ?? Envelope(DurableWorkerProjectionOutcome.Reconciled, result));
        }

        public async IAsyncEnumerable<DurableWorkerEnvelope<string>> ReconcilePendingProjectionsAsync(
            DurableWorkerProjectionRepairRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return ProjectionEnvelope ?? Envelope(DurableWorkerProjectionOutcome.Reconciled, "projection");
        }
    }

    private sealed class NullProjectionContract : IDurableWorkerProjectionContract<string, string, string>
    {
        public ValueTask<DurableWorkerEnvelope<string>> TryClaimAsync(
            string work,
            DurableWorkerCorrelation correlation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DurableWorkerEnvelope<string>>(null!);

        public ValueTask<DurableWorkerEnvelope<string>> CompleteAsync(
            string work,
            string result,
            DurableWorkerCorrelation correlation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DurableWorkerEnvelope<string>>(null!);

        public ValueTask<DurableWorkerEnvelope<string>> ReconcileProjectionAsync(
            string work,
            string result,
            DurableWorkerCorrelation correlation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DurableWorkerEnvelope<string>>(null!);

        public async IAsyncEnumerable<DurableWorkerEnvelope<string>> ReconcilePendingProjectionsAsync(
            DurableWorkerProjectionRepairRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }
}
