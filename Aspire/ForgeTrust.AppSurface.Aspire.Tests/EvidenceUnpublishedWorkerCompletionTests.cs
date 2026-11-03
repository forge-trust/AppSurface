using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceUnpublishedWorkerCompletionTests
{
    [Fact]
    public async Task Completion_JoinsOutputCloseBeforeOneExitWithoutWritingEvidence()
    {
        var clock = new FakeTimeProvider();
        var budget = Budget(clock);
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation,
            EvidenceHostAdmissionTestRun.CreateObservationPlan());
        var lifecycle = Lifecycle(run, clock);
        Assert.True(await lifecycle.StopAndDisposeAsync());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var output = new Output(async () =>
        {
            order.Add("close-entered");
            entered.SetResult();
            await release.Task;
            order.Add("close-returned");
        });
        var completion = EvidenceHostBootstrap.CompleteUnpublishedWorkerAsync(budget, lifecycle, output, token =>
        {
            Assert.True(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            order.Add("exit");
            return run.Supervisor.CompleteWorkerAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(completion.IsCompleted);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
        release.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["close-entered", "close-returned", "exit"], order);
        Assert.Equal(1, output.Closes);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
        Assert.True(lifecycle.CollectionCompleted);
        Assert.Null(lifecycle.TrackOwnedWork(_ => throw new InvalidOperationException("Admission must stay closed.")));
        Assert.False(Directory.Exists(run.ArtifactDirectory));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("exit")]
    [InlineData("expired-before-exit")]
    public async Task FailedCloseExitOrExpiredReserveNeverProducesSuccessfulCompletion(string fault)
    {
        var clock = new FakeTimeProvider();
        var budget = Budget(clock);
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation,
            EvidenceHostAdmissionTestRun.CreateObservationPlan());
        var lifecycle = Lifecycle(run, clock);
        Assert.True(await lifecycle.StopAndDisposeAsync());
        var exits = 0;
        var output = new Output(() =>
        {
            if (fault == "close") throw new IOException("Output close failed.");
            if (fault == "expired-before-exit") clock.Advance(TimeSpan.FromSeconds(2));
            return ValueTask.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceHostBootstrap.CompleteUnpublishedWorkerAsync(budget, lifecycle, output, _ =>
            {
                exits++;
                return ValueTask.FromException(new IOException("Exit failed."));
            }));

        Assert.Equal("ASEVD410", error.Code);
        Assert.Equal(1, output.Closes);
        Assert.Equal(fault == "exit" ? 1 : 0, exits);
        Assert.False(lifecycle.CollectionCompleted);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
        Assert.False(Directory.Exists(run.ArtifactDirectory));
    }

    [Fact]
    public async Task OriginalCollectionReserveCanExpireBeforeTheLifecycleToken()
    {
        // Separate deterministic clocks keep the lifecycle token usable while the original
        // host budget expires, so this exercises the original-budget guard independently.
        var budgetClock = new FakeTimeProvider();
        var lifecycleClock = new FakeTimeProvider();
        var budget = Budget(budgetClock);
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation,
            EvidenceHostAdmissionTestRun.CreateObservationPlan());
        var lifecycle = Lifecycle(run, lifecycleClock);
        Assert.True(await lifecycle.StopAndDisposeAsync());
        var exits = 0;
        var output = new Output(() =>
        {
            budgetClock.Advance(TimeSpan.FromSeconds(1));
            return ValueTask.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceHostBootstrap.CompleteUnpublishedWorkerAsync(budget, lifecycle, output, _ =>
            {
                exits++;
                return ValueTask.CompletedTask;
            }));

        Assert.Equal("ASEVD410", error.Code);
        var original = Assert.IsType<EvidenceAdmissionException>(lifecycle.TerminalException);
        Assert.Equal("ASEVD410", original.Code);
        Assert.Contains("reserve expired", original.Message, StringComparison.Ordinal);
        Assert.Equal(EvidenceWorkerTerminalCode.StageFailed, lifecycle.TerminalCode);
        Assert.Equal(1, output.Closes);
        Assert.Equal(0, exits);
        Assert.False(lifecycle.CollectionCompleted);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
        Assert.False(Directory.Exists(run.ArtifactDirectory));
    }

    [Fact]
    public async Task ExitAcknowledgementAfterCollectionExpiryCannotCompleteCollection()
    {
        var clock = new FakeTimeProvider();
        var budget = Budget(clock);
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation,
            EvidenceHostAdmissionTestRun.CreateObservationPlan());
        var lifecycle = Lifecycle(run, clock);
        Assert.True(await lifecycle.StopAndDisposeAsync());
        var output = new Output(() => ValueTask.CompletedTask);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = EvidenceHostBootstrap.CompleteUnpublishedWorkerAsync(budget, lifecycle, output, async token =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            Assert.False(token.IsCancellationRequested);
            entered.TrySetResult();
            await release.Task;
            // Model a transport reply which arrives after cancellation. The test supervisor
            // records that late ACK; the real lifecycle must retain its original timeout.
            Assert.True(token.IsCancellationRequested);
            await run.Supervisor.CompleteWorkerAsync(token);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
            clock.Advance(TimeSpan.FromSeconds(2));
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var original = Assert.IsType<TimeoutException>(lifecycle.TerminalException);
            Assert.Equal(EvidenceWorkerTerminalCode.DeadlineExceeded, lifecycle.TerminalCode);
            Assert.False(completion.IsCompleted);
            Assert.False(lifecycle.CollectionCompleted);
            Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
            release.TrySetResult();

            var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
                completion.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal("ASEVD410", error.Code);
            Assert.Equal(EvidenceWorkerTerminalCode.DeadlineExceeded, lifecycle.TerminalCode);
            Assert.Same(original, lifecycle.TerminalException);
            Assert.Equal(1, output.Closes);
            Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
            Assert.True(run.Supervisor.CompletionToken?.IsCancellationRequested);
            Assert.False(lifecycle.CollectionCompleted);
            Assert.False(Directory.Exists(run.ArtifactDirectory));
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (EvidenceAdmissionException) { }
        }
    }

    [Theory]
    [InlineData("budget-cleanup-pending")]
    [InlineData("budget-expired")]
    [InlineData("lifecycle-cleanup-pending")]
    public async Task UnavailableReserveOrUnjoinedCleanupRejectsBeforeOutputAndExit(string fault)
    {
        var clock = new FakeTimeProvider();
        var budget = Budget(clock, cleaned: fault != "budget-cleanup-pending");
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation,
            EvidenceHostAdmissionTestRun.CreateObservationPlan());
        var lifecycle = Lifecycle(run, clock);
        if (fault != "lifecycle-cleanup-pending") Assert.True(await lifecycle.StopAndDisposeAsync());
        if (fault == "budget-expired") clock.Advance(TimeSpan.FromMinutes(3));
        var exits = 0;
        var output = new Output(() => ValueTask.CompletedTask);

        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceHostBootstrap.CompleteUnpublishedWorkerAsync(budget, lifecycle, output, _ =>
            {
                exits++;
                return ValueTask.CompletedTask;
            }));

        Assert.Equal("ASEVD410", error.Code);
        Assert.Equal(0, output.Closes);
        Assert.Equal(0, exits);
        Assert.False(lifecycle.CollectionCompleted);
        Assert.False(Directory.Exists(run.ArtifactDirectory));
    }

    [Fact]
    public async Task CompletionCannotReplayItsOutputCloseOrExit()
    {
        var clock = new FakeTimeProvider();
        var budget = Budget(clock);
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation,
            EvidenceHostAdmissionTestRun.CreateObservationPlan());
        var lifecycle = Lifecycle(run, clock);
        Assert.True(await lifecycle.StopAndDisposeAsync());
        var output = new Output(() => ValueTask.CompletedTask);
        await EvidenceHostBootstrap.CompleteUnpublishedWorkerAsync(budget, lifecycle, output,
            run.Supervisor.CompleteWorkerAsync);

        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceHostBootstrap.CompleteUnpublishedWorkerAsync(budget, lifecycle, output,
                run.Supervisor.CompleteWorkerAsync));

        Assert.Equal("ASEVD410", error.Code);
        Assert.Equal(1, output.Closes);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
        Assert.False(Directory.Exists(run.ArtifactDirectory));
    }

    private static EvidenceWorkerExecution Lifecycle(EvidenceHostAdmissionTestRun run, TimeProvider clock) =>
        new(run.Supervisor, clock, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1),
            fatalTermination: message => new EvidenceWorkerTestInterruptionException(message),
            collectionReserve: TimeSpan.FromSeconds(1));

    private static EvidenceRunTimeBudget Budget(TimeProvider clock, bool cleaned = true)
    {
        Assert.True(EvidenceRunTimeBudget.TryCreateFromAllowance(clock, TimeSpan.FromMinutes(2), [],
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), out var budget));
        if (cleaned)
        {
            Assert.True(budget!.TryAbandonStagesAndBeginCleanup(stageClosed: true));
            Assert.True(budget.CompleteCleanup());
        }
        return budget!;
    }

    private sealed class Output(Func<ValueTask> close) : IAsyncDisposable
    {
        internal int Closes { get; private set; }

        public ValueTask DisposeAsync()
        {
            Closes++;
            return close();
        }
    }
}
