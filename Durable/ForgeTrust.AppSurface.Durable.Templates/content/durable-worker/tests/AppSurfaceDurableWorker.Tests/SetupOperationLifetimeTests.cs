using System.Diagnostics;

namespace DurableWorkerTemplate.Tests;

public sealed class SetupOperationLifetimeTests
{
    [Fact]
    public async Task Cancellation_does_not_forget_copy_until_owned_container_stop_observes_it()
    {
        var lifetime = new SetupOperationLifetime();
        var copy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var awaitingCopy = lifetime.AwaitAsync("packaged-sql-copy", () => copy.Task, cancellation.Token);
        Assert.Equal(1, lifetime.PendingCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => awaitingCopy);
        Assert.Equal(1, lifetime.PendingCount);

        var failures = new List<string>();
        var settled = await lifetime.StopAndObservePendingAsync(
            _ =>
            {
                copy.TrySetResult();
                return Task.FromResult(true);
            },
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100),
            failures);

        Assert.True(settled);
        Assert.Empty(failures);
        await WaitForPendingCountAsync(lifetime, expected: 0);
    }

    [Fact]
    public async Task Cleanup_withholds_success_when_owned_setup_operation_outlives_container_stop()
    {
        var lifetime = new SetupOperationLifetime();
        var copy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = lifetime.AwaitAsync("packaged-sql-copy", () => copy.Task, CancellationToken.None);
        var failures = new List<string>();

        var settled = await lifetime.StopAndObservePendingAsync(
            _ => Task.FromResult(true),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(50),
            failures);

        Assert.False(settled);
        Assert.Contains("setup-operation-unfinished:packaged-sql-copy", failures);
        Assert.Equal(1, lifetime.PendingCount);

        copy.SetException(new IOException("late copy failure"));
        await WaitForPendingCountAsync(lifetime, expected: 0);
    }

    [Fact]
    public async Task Cleanup_bounds_and_observes_a_late_owned_container_stop()
    {
        var lifetime = new SetupOperationLifetime();
        var copy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = lifetime.AwaitAsync("container-start", () => copy.Task, CancellationToken.None);
        var failures = new List<string>();
        var startedAt = Stopwatch.GetTimestamp();

        var cleanup = lifetime.StopAndObservePendingAsync(
            _ =>
            {
                stopEntered.TrySetResult();
                releaseStop.Task.GetAwaiter().GetResult();
                return Task.FromResult(true);
            },
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(50),
            failures);

        try
        {
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var settled = await cleanup;
            Assert.False(settled);
            Assert.Contains("owned-resource-stop-unfinished", failures);
            Assert.Contains("setup-operation-unfinished:container-start", failures);
            Assert.InRange(Stopwatch.GetElapsedTime(startedAt), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseStop.TrySetResult();
            copy.TrySetResult();
        }

        await WaitForPendingCountAsync(lifetime, expected: 0);
    }

    [Fact]
    public async Task Cleanup_records_a_failed_owner_stop_even_when_setup_operation_settles()
    {
        var lifetime = new SetupOperationLifetime();
        var copy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = lifetime.AwaitAsync("container-start", () => copy.Task, CancellationToken.None);
        var failures = new List<string>();

        var settled = await lifetime.StopAndObservePendingAsync(
            _ =>
            {
                copy.TrySetResult();
                return Task.FromResult(false);
            },
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100),
            failures);

        Assert.True(settled);
        Assert.Contains("owned-resource-stop-failed", failures);
        await WaitForPendingCountAsync(lifetime, expected: 0);
    }

    [Fact]
    public async Task Cleanup_observes_setup_operation_faults_and_empty_lifetime_is_settled()
    {
        var lifetime = new SetupOperationLifetime();
        await Assert.ThrowsAsync<IOException>(() =>
            lifetime.AwaitAsync("fixture-provisioning", () => Task.FromException(new IOException()), CancellationToken.None));
        await WaitForPendingCountAsync(lifetime, expected: 0);

        var failures = new List<string>();
        var settled = await lifetime.StopAndObservePendingAsync(
            stopOwnerAsync: null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100),
            failures);

        Assert.True(settled);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Cancellation_bounds_adapter_factories_that_block_before_returning_a_task()
    {
        var lifetime = new SetupOperationLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var awaitingOperation = lifetime.AwaitAsync(
            "container-start",
            () =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return Task.CompletedTask;
            },
            cancellation.Token);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => awaitingOperation);
            Assert.Equal(1, lifetime.PendingCount);

            var failures = new List<string>();
            var settled = await lifetime.StopAndObservePendingAsync(
                _ =>
                {
                    release.TrySetResult();
                    return Task.FromResult(true);
                },
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(100),
                failures);

            Assert.True(settled);
            Assert.Empty(failures);
            await WaitForPendingCountAsync(lifetime, expected: 0);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static async Task WaitForPendingCountAsync(SetupOperationLifetime lifetime, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (lifetime.PendingCount != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), timeout.Token);
        }
    }
}
