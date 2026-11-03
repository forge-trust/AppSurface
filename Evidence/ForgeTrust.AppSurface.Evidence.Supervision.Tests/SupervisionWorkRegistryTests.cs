using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Exercises bookkeeping races only; no control creates admission or proves OS exit.</summary>
public sealed class SupervisionWorkRegistryTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PendingDispatchRemainsOwnedUntilExplicitLateCompletion()
    {
        var registry = new SupervisionWorkRegistry();
        var pending = registry.BeginWorkload();
        var reachedDispatch = Barrier();
        var releaseDispatch = Barrier();
        var dispatch = Task.Run(async () =>
        {
            reachedDispatch.SetResult();
            await releaseDispatch.Task.WaitAsync(Guard);
            return pending.Complete();
        });
        try
        {
            await reachedDispatch.Task.WaitAsync(Guard);
            var join = registry.CloseAndJoinWorkloadsAsync();
            Assert.True(registry.IsWorkloadAdmissionClosed);
            Assert.Equal(1, registry.ActiveWorkloads);
            Assert.False(join.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => registry.BeginWorkload());
            releaseDispatch.TrySetResult();
            Assert.True(await dispatch.WaitAsync(Guard));
            Assert.True(await join.WaitAsync(Guard));
            Assert.Equal(0, registry.ActiveWorkloads);
            Assert.False(registry.IsSettled);
            Assert.True(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
            Assert.True(registry.IsSettled);
        }
        finally
        {
            releaseDispatch.TrySetResult();
            await dispatch.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task CanceledWorkloadJoinRetainsOwnershipAndRetryCannotReopenGate()
    {
        var registry = new SupervisionWorkRegistry();
        var workload = registry.BeginWorkload();
        using var cancellation = new CancellationTokenSource();
        var canceledJoin = registry.CloseAndJoinWorkloadsAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledJoin.WaitAsync(Guard));
        Assert.True(registry.IsWorkloadAdmissionClosed);
        Assert.Equal(1, registry.ActiveWorkloads);
        Assert.False(registry.IsFailed);
        Assert.Throws<InvalidOperationException>(() => registry.BeginWorkload());
        var retry = registry.CloseAndJoinWorkloadsAsync();
        Assert.False(retry.IsCompleted);
        Assert.True(workload.Complete());
        Assert.True(await retry.WaitAsync(Guard));
        Assert.False(workload.Complete());
        Assert.True(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
        Assert.True(registry.IsSettled);
    }

    [Fact]
    public async Task PreCanceledEmptyJoinsStillCloseAdmissionAndCannotReturnSuccessfulJoin()
    {
        var registry = new SupervisionWorkRegistry();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.CloseAndJoinWorkloadsAsync(cancellation.Token).WaitAsync(Guard));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.CloseAndJoinControlsAsync(cancellation.Token).WaitAsync(Guard));
        Assert.True(registry.IsWorkloadAdmissionClosed);
        Assert.True(registry.IsControlAdmissionClosed);
        Assert.Throws<InvalidOperationException>(() => registry.BeginWorkload());
        Assert.Throws<InvalidOperationException>(() => registry.BeginControl());
        Assert.False(registry.IsFailed);
        Assert.True(await registry.CloseAndJoinWorkloadsAsync().WaitAsync(Guard));
        Assert.True(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
        Assert.True(registry.IsSettled);
    }

    [Fact]
    public async Task CurrentWaitHandlerIsExcludedAndStopControlsRemainAdmitted()
    {
        var registry = new SupervisionWorkRegistry();
        var workload = registry.BeginWorkload();
        using var waitHandler = registry.BeginControl();
        var workloadJoin = registry.CloseAndJoinWorkloadsAsync();
        using var stopHandler = registry.BeginControl();
        Assert.Equal(2, registry.ActiveControls);
        Assert.False(registry.IsControlAdmissionClosed);
        Assert.True(workload.Complete());
        Assert.True(await workloadJoin.WaitAsync(Guard));
        Assert.Equal(2, registry.ActiveControls);
        var controlJoin = registry.CloseAndJoinControlsAsync();
        Assert.False(controlJoin.IsCompleted);
        stopHandler.Dispose();
        Assert.Equal(1, registry.ActiveControls);
        Assert.False(controlJoin.IsCompleted);
        waitHandler.Dispose();
        Assert.True(await controlJoin.WaitAsync(Guard));
        Assert.True(registry.IsSettled);
    }

    [Fact]
    public async Task CanceledControlJoinRetainsHandlerUntilItCompletes()
    {
        var registry = new SupervisionWorkRegistry();
        using var handler = registry.BeginControl();
        using var cancellation = new CancellationTokenSource();
        var join = registry.CloseAndJoinControlsAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join.WaitAsync(Guard));
        Assert.True(registry.IsControlAdmissionClosed);
        Assert.Equal(1, registry.ActiveControls);
        Assert.Throws<InvalidOperationException>(() => registry.BeginControl());
        var workload = registry.BeginWorkload();
        var retry = registry.CloseAndJoinControlsAsync();
        Assert.False(retry.IsCompleted);
        Assert.True(handler.Complete());
        Assert.True(await retry.WaitAsync(Guard));
        Assert.Equal(1, registry.ActiveWorkloads);
        Assert.False(registry.IsSettled);
        workload.Complete();
        Assert.True(await registry.CloseAndJoinWorkloadsAsync().WaitAsync(Guard));
        Assert.True(registry.IsSettled);
    }

    [Fact]
    public async Task ConcurrentClosersJoinTheSameOwnedSetWithoutDroppingRegistrations()
    {
        var registry = new SupervisionWorkRegistry();
        var first = registry.BeginWorkload();
        var second = registry.BeginWorkload();
        var start = Barrier();
        var closers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task.WaitAsync(Guard);
            return await registry.CloseAndJoinWorkloadsAsync().WaitAsync(Guard);
        })).ToArray();
        start.SetResult();
        var witness = registry.CloseAndJoinWorkloadsAsync();
        Assert.Equal(2, registry.ActiveWorkloads);
        Assert.False(witness.IsCompleted);
        Assert.True(first.Complete());
        Assert.Equal(1, registry.ActiveWorkloads);
        Assert.False(witness.IsCompleted);
        Assert.True(second.Complete());
        Assert.True(await witness.WaitAsync(Guard));
        Assert.All(await Task.WhenAll(closers).WaitAsync(Guard), result => Assert.True(result));
        Assert.Throws<InvalidOperationException>(() => registry.BeginWorkload());
        Assert.True(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
    }

    [Fact]
    public async Task RegistrationRacingCloseIsRejectedOrRetainedByTheClosedJoin()
    {
        var registry = new SupervisionWorkRegistry();
        var start = Barrier();
        var closed = Barrier();
        var registration = Task.Run(async () =>
        {
            await start.Task.WaitAsync(Guard);
            try
            {
                return registry.BeginWorkload();
            }
            catch (InvalidOperationException)
            {
                return (SupervisionWorkRegistry.Workload?)null;
            }
        });
        var closing = Task.Run(async () =>
        {
            await start.Task.WaitAsync(Guard);
            var join = registry.CloseAndJoinWorkloadsAsync();
            closed.SetResult();
            return await join.WaitAsync(Guard);
        });
        start.SetResult();
        var workload = await registration.WaitAsync(Guard);
        await closed.Task.WaitAsync(Guard);
        try
        {
            Assert.True(registry.IsWorkloadAdmissionClosed);
            Assert.Throws<InvalidOperationException>(() => registry.BeginWorkload());
            if (workload is null)
            {
                Assert.Equal(0, registry.ActiveWorkloads);
            }
            else
            {
                Assert.Equal(1, registry.ActiveWorkloads);
                Assert.False(closing.IsCompleted);
                Assert.True(workload.Complete());
            }
            Assert.True(await closing.WaitAsync(Guard));
            Assert.True(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
            Assert.True(registry.IsSettled);
        }
        finally
        {
            workload?.Complete();
            await closing.WaitAsync(Guard);
        }
    }

    [Fact]
    public async Task ConcurrentCompletionAndReplayReleaseExactlyOneRegistration()
    {
        var registry = new SupervisionWorkRegistry(maximumActiveWorkloads: 1, maximumActiveControls: 1);
        var oldWorkload = registry.BeginWorkload();
        var completions = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => oldWorkload.Complete()))).WaitAsync(Guard);
        Assert.Equal(1, completions.Count(result => result));
        var currentWorkload = registry.BeginWorkload();
        Assert.False(oldWorkload.Complete());
        Assert.Equal(1, registry.ActiveWorkloads);
        using var oldControl = registry.BeginControl();
        Assert.True(oldControl.Complete());
        using var currentControl = registry.BeginControl();
        oldControl.Dispose();
        Assert.False(oldControl.Complete());
        Assert.Equal(1, registry.ActiveControls);
        var workloadJoin = registry.CloseAndJoinWorkloadsAsync();
        var controlJoin = registry.CloseAndJoinControlsAsync();
        Assert.False(workloadJoin.IsCompleted);
        Assert.False(controlJoin.IsCompleted);
        currentWorkload.Complete();
        currentControl.Dispose();
        Assert.True(await workloadJoin.WaitAsync(Guard));
        Assert.True(await controlJoin.WaitAsync(Guard));
        Assert.True(registry.IsSettled);
    }

    [Fact]
    public async Task ExactProtectedCapsRejectExtraOwnershipAndDrainWithoutFailure()
    {
        var registry = new SupervisionWorkRegistry();
        var workloads = Enumerable.Range(0, SupervisionWorkRegistry.MaximumActiveWorkloads)
            .Select(_ => registry.BeginWorkload()).ToArray();
        var controls = Enumerable.Range(0, SupervisionWorkRegistry.MaximumActiveControls)
            .Select(_ => registry.BeginControl()).ToArray();
        Assert.Throws<InvalidOperationException>(() => registry.BeginWorkload());
        Assert.Throws<InvalidOperationException>(() => registry.BeginControl());
        Assert.Equal(SupervisionWorkRegistry.MaximumActiveWorkloads, registry.ActiveWorkloads);
        Assert.Equal(SupervisionWorkRegistry.MaximumActiveControls, registry.ActiveControls);
        Assert.False(registry.IsFailed);
        var workloadJoin = registry.CloseAndJoinWorkloadsAsync();
        var controlJoin = registry.CloseAndJoinControlsAsync();
        Assert.False(workloadJoin.IsCompleted);
        Assert.False(controlJoin.IsCompleted);
        foreach (var workload in workloads) Assert.True(workload.Complete());
        foreach (var control in controls) control.Dispose();
        Assert.True(await workloadJoin.WaitAsync(Guard));
        Assert.True(await controlJoin.WaitAsync(Guard));
        Assert.True(registry.IsSettled);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(SupervisionWorkRegistry.MaximumActiveWorkloads + 1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, SupervisionWorkRegistry.MaximumActiveControls + 1)]
    public void ConstructorCannotRaiseProtectedCapsOrAcceptNonpositiveCaps(int workloads, int controls)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SupervisionWorkRegistry(workloads, controls));
    }

    [Fact]
    public async Task LowerCapsEnforceOneActiveRegistrationAndPermitReuseBeforeClosure()
    {
        var registry = new SupervisionWorkRegistry(1, 1);
        var first = registry.BeginWorkload();
        using var firstControl = registry.BeginControl();
        Assert.Throws<InvalidOperationException>(() => registry.BeginWorkload());
        Assert.Throws<InvalidOperationException>(() => registry.BeginControl());
        Assert.True(first.Complete());
        Assert.True(firstControl.Complete());
        var next = registry.BeginWorkload();
        using var nextControl = registry.BeginControl();
        Assert.False(first.Complete());
        Assert.False(firstControl.Complete());
        Assert.Equal(1, registry.ActiveWorkloads);
        Assert.Equal(1, registry.ActiveControls);
        next.Complete();
        nextControl.Dispose();
        Assert.True(await registry.CloseAndJoinWorkloadsAsync().WaitAsync(Guard));
        Assert.True(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
    }

    [Fact]
    public async Task FirstFailureIsStickyAndCannotBecomeSuccessfulSettlement()
    {
        var registry = new SupervisionWorkRegistry();
        var workload = registry.BeginWorkload();
        using var handler = registry.BeginControl();
        var join = registry.CloseAndJoinWorkloadsAsync();
        Assert.True(registry.RecordFailure(SupervisionWorkFailure.DispatchFailed));
        Assert.False(registry.RecordFailure(SupervisionWorkFailure.WorkloadFailed));
        Assert.Equal(SupervisionWorkFailure.DispatchFailed, registry.FirstFailure);
        Assert.True(registry.IsFailed);
        Assert.False(join.IsCompleted);
        Assert.Equal(1, registry.ActiveWorkloads);
        Assert.Throws<InvalidOperationException>(() => registry.BeginWorkload());
        using var stop = registry.BeginControl();
        workload.Complete();
        Assert.False(await join.WaitAsync(Guard));
        Assert.False(await registry.CloseAndJoinWorkloadsAsync().WaitAsync(Guard));
        stop.Dispose();
        handler.Dispose();
        Assert.False(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
        Assert.Equal(0, registry.ActiveWorkloads);
        Assert.Equal(0, registry.ActiveControls);
        Assert.False(registry.IsSettled);
    }

    [Theory]
    [InlineData((int)SupervisionWorkFailure.None)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidFailureReasonCannotConsumeTheFirstFailure(int value)
    {
        var registry = new SupervisionWorkRegistry();
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.RecordFailure((SupervisionWorkFailure)value));
        Assert.Equal(SupervisionWorkFailure.None, registry.FirstFailure);
        Assert.False(registry.IsFailed);
        Assert.False(registry.IsWorkloadAdmissionClosed);
        Assert.True(registry.RecordFailure(SupervisionWorkFailure.ControlFailed));
        Assert.True(registry.IsWorkloadAdmissionClosed);
        Assert.False(await registry.CloseAndJoinWorkloadsAsync().WaitAsync(Guard));
        Assert.False(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
        Assert.False(registry.IsSettled);
    }

    [Fact]
    public async Task DisposingControlScopeNeverCompletesPendingWorkload()
    {
        var registry = new SupervisionWorkRegistry();
        var workload = registry.BeginWorkload();
        using (registry.BeginControl())
        {
            Assert.Equal(1, registry.ActiveControls);
        }
        Assert.Equal(0, registry.ActiveControls);
        Assert.Equal(1, registry.ActiveWorkloads);
        var join = registry.CloseAndJoinWorkloadsAsync();
        Assert.False(join.IsCompleted);
        Assert.True(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
        Assert.False(registry.IsSettled);
        workload.Complete();
        Assert.True(await join.WaitAsync(Guard));
        Assert.True(registry.IsSettled);
    }

    [Fact]
    public async Task LaterFailureRevokesSettledDataWithoutReopeningEitherGate()
    {
        var registry = new SupervisionWorkRegistry();
        Assert.False(registry.IsSettled);
        Assert.True(await registry.CloseAndJoinWorkloadsAsync().WaitAsync(Guard));
        Assert.True(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
        Assert.True(registry.IsSettled);
        Assert.True(registry.RecordFailure(SupervisionWorkFailure.WorkloadFailed));
        Assert.True(registry.IsFailed);
        Assert.False(registry.IsSettled);
        Assert.False(await registry.CloseAndJoinWorkloadsAsync().WaitAsync(Guard));
        Assert.False(await registry.CloseAndJoinControlsAsync().WaitAsync(Guard));
        Assert.Throws<InvalidOperationException>(() => registry.BeginWorkload());
        Assert.Throws<InvalidOperationException>(() => registry.BeginControl());
    }

    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
