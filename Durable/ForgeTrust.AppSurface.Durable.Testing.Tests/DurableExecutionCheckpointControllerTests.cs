using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Testing;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Durable.Testing.Tests;

public sealed class DurableExecutionCheckpointControllerTests
{
    private static readonly DateTimeOffset ObservedAt = new(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public async Task Reach_records_bounded_immutable_observations_and_wait_returns_exact_checkpoint()
    {
        var clock = new FakeTimeProvider(ObservedAt);
        using var controller = new DurableExecutionCheckpointController(maximumObservations: 2, timeProvider: clock);

        await controller.ReachAsync(DurableExecutionCheckpointName.BeforePermit, 1);
        var waited = await controller.WaitForObservationAsync(DurableExecutionCheckpointName.BeforePermit);
        clock.Advance(TimeSpan.FromSeconds(1));
        await controller.ReachAsync(DurableExecutionCheckpointName.AfterPermitCommit, 2);
        await controller.ReachAsync(DurableExecutionCheckpointName.BeforeInvocationAdmission, 3);

        Assert.Equal(1, waited.Sequence);
        Assert.Equal(ObservedAt, waited.ObservedAtUtc);
        Assert.Equal(2, controller.Observations.Count);
        Assert.Equal([DurableExecutionCheckpointName.AfterPermitCommit, DurableExecutionCheckpointName.BeforeInvocationAdmission],
            controller.Observations.Select(static item => item.Name));
        Assert.Throws<NotSupportedException>(() => ((IList<DurableExecutionCheckpointObservation>)controller.Observations)
            .Add(waited));
    }

    [Fact]
    public async Task Wait_for_observation_returns_stage_after_bounded_history_evicts_it()
    {
        using var controller = new DurableExecutionCheckpointController(maximumObservations: 1);
        await controller.ReachAsync(DurableExecutionCheckpointName.BeforePermit, 7);
        await controller.ReachAsync(DurableExecutionCheckpointName.AfterPermitCommit, 8);

        var retained = Assert.Single(controller.Observations);
        Assert.Equal(DurableExecutionCheckpointName.AfterPermitCommit, retained.Name);
        Assert.Equal(2, retained.Sequence);

        var waited = await controller.WaitForObservationAsync(DurableExecutionCheckpointName.BeforePermit);

        Assert.Equal(DurableExecutionCheckpointName.BeforePermit, waited.Name);
        Assert.Equal(1, waited.Sequence);
        Assert.Equal(7, waited.AttemptNumber);
        Assert.Single(controller.Observations);
    }

    [Fact]
    public async Task Waiter_woken_before_eviction_still_observes_its_stage_after_eviction()
    {
        var waiterWoke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new DurableExecutionCheckpointController(
            maximumWait: TimeSpan.FromSeconds(2),
            maximumObservations: 1,
            timeProvider: null,
            afterObservationWaitWake: async () =>
            {
                waiterWoke.TrySetResult();
                await resumeWaiter.Task.ConfigureAwait(false);
            });
        using (controller)
        {
            var pending = controller.WaitForObservationAsync(DurableExecutionCheckpointName.BeforePermit).AsTask();
            await controller.ReachAsync(DurableExecutionCheckpointName.BeforePermit, 4);
            await waiterWoke.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await controller.ReachAsync(DurableExecutionCheckpointName.AfterPermitCommit, 5);
            Assert.Equal(DurableExecutionCheckpointName.AfterPermitCommit, Assert.Single(controller.Observations).Name);

            resumeWaiter.TrySetResult();
            var observed = await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(DurableExecutionCheckpointName.BeforePermit, observed.Name);
            Assert.Equal(1, observed.Sequence);
            Assert.Equal(4, observed.AttemptNumber);
        }
    }

    [Fact]
    public async Task Pause_once_waits_for_release_and_a_second_arm_uses_a_fresh_gate()
    {
        using var controller = new DurableExecutionCheckpointController(maximumWait: TimeSpan.FromSeconds(2));
        controller.PauseOnce(DurableExecutionCheckpointName.AfterPermitCommit);

        var first = controller.ReachAsync(DurableExecutionCheckpointName.AfterPermitCommit, 1).AsTask();
        await controller.WaitForObservationAsync(DurableExecutionCheckpointName.AfterPermitCommit);
        Assert.False(first.IsCompleted);
        Assert.True(controller.Release(DurableExecutionCheckpointName.AfterPermitCommit));
        await first;
        Assert.False(controller.Release(DurableExecutionCheckpointName.AfterPermitCommit));

        controller.PauseOnce(DurableExecutionCheckpointName.AfterPermitCommit);
        var second = controller.ReachAsync(DurableExecutionCheckpointName.AfterPermitCommit, 2).AsTask();
        Assert.Equal(2, controller.Observations.Count);
        Assert.False(second.IsCompleted);
        Assert.True(controller.Release(DurableExecutionCheckpointName.AfterPermitCommit));
        await second;
    }

    [Fact]
    public async Task Throw_once_throws_safe_checkpoint_facts_then_is_consumed()
    {
        using var controller = new DurableExecutionCheckpointController();
        controller.ThrowOnce(DurableExecutionCheckpointName.BeforeInvocationAdmission);

        var failure = await Assert.ThrowsAsync<DurableExecutionCheckpointException>(async () =>
            await controller.ReachAsync(DurableExecutionCheckpointName.BeforeInvocationAdmission, 4));
        await controller.ReachAsync(DurableExecutionCheckpointName.BeforeInvocationAdmission, 5);

        Assert.Equal(DurableExecutionCheckpointName.BeforeInvocationAdmission, failure.Name);
        Assert.Equal(4, failure.AttemptNumber);
        Assert.Equal(
            "Test execution checkpoint 'BeforeInvocationAdmission' threw once for attempt 4.",
            failure.Message);
        Assert.Equal(2, controller.Observations.Count);
    }

    [Fact]
    public async Task Unreleased_pause_times_out_and_cleans_up_the_active_arm()
    {
        using var controller = new DurableExecutionCheckpointController(maximumWait: TimeSpan.FromMilliseconds(1));
        controller.PauseOnce(DurableExecutionCheckpointName.BeforeCompletion);

        var failure = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await controller.ReachAsync(DurableExecutionCheckpointName.BeforeCompletion, 1));

        Assert.Contains(nameof(DurableExecutionCheckpointName.BeforeCompletion), failure.Message, StringComparison.Ordinal);
        Assert.False(controller.Release(DurableExecutionCheckpointName.BeforeCompletion));
        await controller.ReachAsync(DurableExecutionCheckpointName.BeforeCompletion, 2);
    }

    [Fact]
    public async Task Wait_for_observation_honors_caller_cancellation_and_uses_bounded_timeout()
    {
        using var controller = new DurableExecutionCheckpointController(maximumWait: TimeSpan.FromMilliseconds(40));
        using var cancellation = new CancellationTokenSource();
        var pending = controller.WaitForObservationAsync(
            DurableExecutionCheckpointName.AfterProviderCall,
            cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await controller.WaitForObservationAsync(DurableExecutionCheckpointName.AfterProviderCall));
        Assert.Contains(nameof(DurableExecutionCheckpointName.AfterProviderCall), timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Positive_observation_wait_budget_that_expires_before_waiting_times_out_immediately()
    {
        using var controller = new DurableExecutionCheckpointController(maximumWait: TimeSpan.FromTicks(1));

        var timeout = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await controller.WaitForObservationAsync(DurableExecutionCheckpointName.AfterProviderCall));

        Assert.Contains(nameof(DurableExecutionCheckpointName.AfterProviderCall), timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_actions_are_rejected_while_armed_or_paused()
    {
        using var controller = new DurableExecutionCheckpointController();
        controller.PauseOnce(DurableExecutionCheckpointName.BeforePermit);
        Assert.Throws<InvalidOperationException>(() => controller.ThrowOnce(DurableExecutionCheckpointName.BeforePermit));

        var paused = controller.ReachAsync(DurableExecutionCheckpointName.BeforePermit, 1).AsTask();
        await controller.WaitForObservationAsync(DurableExecutionCheckpointName.BeforePermit);
        Assert.Throws<InvalidOperationException>(() => controller.PauseOnce(DurableExecutionCheckpointName.BeforePermit));
        Assert.True(controller.Cancel(DurableExecutionCheckpointName.BeforePermit));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await paused);
    }

    [Fact]
    public async Task Cancel_active_pause_cancels_only_that_checkpoint_and_can_be_rearmed()
    {
        using var controller = new DurableExecutionCheckpointController();
        controller.PauseOnce(DurableExecutionCheckpointName.AfterInvocationAdmission);
        var paused = controller.ReachAsync(DurableExecutionCheckpointName.AfterInvocationAdmission, 1).AsTask();
        await controller.WaitForObservationAsync(DurableExecutionCheckpointName.AfterInvocationAdmission);

        Assert.True(controller.Cancel(DurableExecutionCheckpointName.AfterInvocationAdmission));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await paused);
        Assert.False(controller.Release(DurableExecutionCheckpointName.AfterInvocationAdmission));

        controller.ThrowOnce(DurableExecutionCheckpointName.AfterInvocationAdmission);
        await Assert.ThrowsAsync<DurableExecutionCheckpointException>(async () =>
            await controller.ReachAsync(DurableExecutionCheckpointName.AfterInvocationAdmission, 2));
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_an_active_pause()
    {
        using var controller = new DurableExecutionCheckpointController();
        using var cancellation = new CancellationTokenSource();
        controller.PauseOnce(DurableExecutionCheckpointName.BeforeProviderCall);
        var paused = controller.ReachAsync(
            DurableExecutionCheckpointName.BeforeProviderCall,
            1,
            cancellation.Token).AsTask();
        await controller.WaitForObservationAsync(DurableExecutionCheckpointName.BeforeProviderCall);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await paused);
        Assert.False(controller.Release(DurableExecutionCheckpointName.BeforeProviderCall));
    }

    [Fact]
    public async Task Dispose_cancels_active_pauses_and_rejects_future_operations()
    {
        var controller = new DurableExecutionCheckpointController();
        controller.PauseOnce(DurableExecutionCheckpointName.AfterProviderCall);
        var paused = controller.ReachAsync(DurableExecutionCheckpointName.AfterProviderCall, 1).AsTask();
        await controller.WaitForObservationAsync(DurableExecutionCheckpointName.AfterProviderCall);

        await controller.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await paused);
        Assert.Throws<ObjectDisposedException>(() => controller.PauseOnce(DurableExecutionCheckpointName.BeforePermit));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await controller.ReachAsync(DurableExecutionCheckpointName.BeforePermit, 1));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await controller.WaitForObservationAsync(DurableExecutionCheckpointName.BeforePermit));
        Assert.False(controller.Release(DurableExecutionCheckpointName.AfterProviderCall));
        Assert.False(controller.Cancel(DurableExecutionCheckpointName.AfterProviderCall));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_reaches_consume_one_pause_exactly_once()
    {
        using var controller = new DurableExecutionCheckpointController(maximumWait: TimeSpan.FromSeconds(2));
        controller.PauseOnce(DurableExecutionCheckpointName.BeforeProviderCall);

        var paused = controller.ReachAsync(DurableExecutionCheckpointName.BeforeProviderCall, 1).AsTask();
        var unpaused = controller.ReachAsync(DurableExecutionCheckpointName.BeforeProviderCall, 2).AsTask();

        await unpaused.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(paused.IsCompleted);
        Assert.Equal(2, controller.Observations.Count);
        Assert.True(controller.Release(DurableExecutionCheckpointName.BeforeProviderCall));
        await paused;
        Assert.False(controller.Release(DurableExecutionCheckpointName.BeforeProviderCall));
    }

    [Fact]
    public async Task Invalid_bounds_names_and_attempts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExecutionCheckpointController(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExecutionCheckpointController(TimeSpan.FromMinutes(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExecutionCheckpointController(maximumObservations: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExecutionCheckpointController(maximumObservations: 4_097));

        using var controller = new DurableExecutionCheckpointController();
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.PauseOnce((DurableExecutionCheckpointName)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.ThrowOnce((DurableExecutionCheckpointName)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Release((DurableExecutionCheckpointName)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Cancel((DurableExecutionCheckpointName)99));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.ReachAsync((DurableExecutionCheckpointName)99, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.ReachAsync(DurableExecutionCheckpointName.BeforePermit, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.WaitForObservationAsync((DurableExecutionCheckpointName)99));
        Assert.Empty(controller.Observations);
    }

    [Fact]
    public void Execution_observation_captures_only_the_immutable_snapshot_facts()
    {
        var policy = DurableWorkExecutionPolicy.FromRetryPolicy(DurableWorkRetryPolicy.Default);
        var deadline = new DurableExecutionDeadline(ObservedAt.AddHours(1));
        var snapshot = new DurableWorkExecutionSnapshot(policy, deadline, ObservedAt, ObservedAt.AddMinutes(5), deadline.NotAfterUtc);

        var observed = DurableWorkExecutionObservation.Capture(snapshot);

        Assert.Same(policy, observed.Policy);
        Assert.Equal(deadline, observed.Deadline);
        Assert.Equal(ObservedAt, observed.AcceptedAtUtc);
        Assert.Equal(ObservedAt.AddMinutes(5), observed.NextEligibilityAtUtc);
        Assert.Equal(deadline.NotAfterUtc, observed.AdmissionCutoffUtc);
        Assert.Throws<ArgumentNullException>(() => DurableWorkExecutionObservation.Capture(null!));
    }
}
