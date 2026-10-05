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

    [Fact]
    public async Task Expired_cleanup_still_enters_every_owned_factory_and_observes_late_faults()
    {
        var lifetime = new SetupOperationLifetime();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<TimeSpan>();
        var failures = new List<string>();
        var expiredAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency;

        try
        {
            await lifetime.AwaitCleanupAsync("host", remaining =>
            {
                lock (received) received.Add(remaining);
                firstEntered.TrySetResult();
                return firstRelease.Task;
            }, expiredAt, TimeSpan.FromMilliseconds(100), failures);
            await lifetime.AwaitCleanupAsync("fixture", remaining =>
            {
                lock (received) received.Add(remaining);
                secondEntered.TrySetResult();
                return secondRelease.Task;
            }, expiredAt, TimeSpan.FromMilliseconds(100), failures);

            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(2, lifetime.PendingCount);
            Assert.Contains("host-cleanup-budget-exhausted", failures);
            Assert.Contains("fixture-cleanup-budget-exhausted", failures);
            Assert.All(received, remaining => Assert.Equal(TimeSpan.FromMilliseconds(1), remaining));
        }
        finally
        {
            firstRelease.TrySetException(new IOException("private late host fault"));
            secondRelease.TrySetException(new IOException("private late fixture fault"));
        }
        await WaitForPendingCountAsync(lifetime, 0);
        Assert.DoesNotContain(failures, failure => failure.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleanup_bounds_synchronous_host_disposal_and_continues_to_fixture_after_expiry()
    {
        var lifetime = new SetupOperationLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixtureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<string>();
        var startedAt = Stopwatch.GetTimestamp();
        var budget = TimeSpan.FromMilliseconds(100);

        try
        {
            var host = lifetime.AwaitCleanupAsync("host", _ =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return Task.CompletedTask;
            }, startedAt, budget, failures);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await host.WaitAsync(TimeSpan.FromSeconds(1));
            while (Stopwatch.GetElapsedTime(startedAt) < budget)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1));
            }
            await lifetime.AwaitCleanupAsync("fixture", _ =>
            {
                fixtureEntered.TrySetResult();
                return Task.CompletedTask;
            }, startedAt, budget, failures);

            await fixtureEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Contains(failures, failure => failure is "host-OperationCanceledException" or "host-TaskCanceledException");
            Assert.Contains("fixture-cleanup-budget-exhausted", failures);
            Assert.True(lifetime.PendingCount > 0);
        }
        finally
        {
            release.TrySetResult();
        }
        await WaitForPendingCountAsync(lifetime, 0);
    }

    [Fact]
    public async Task Faulted_cleanup_does_not_skip_the_next_resource_or_refresh_its_allowance()
    {
        var lifetime = new SetupOperationLifetime();
        var failures = new List<string>();
        var startedAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        var budget = TimeSpan.FromSeconds(2);
        var subsequentInvocations = 0;

        await lifetime.AwaitCleanupAsync("host", _ => Task.FromException(new IOException("private host detail")),
            startedAt, budget, failures);
        await lifetime.AwaitCleanupAsync("fixture", remaining =>
        {
            Assert.InRange(remaining, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
            Interlocked.Increment(ref subsequentInvocations);
            return Task.CompletedTask;
        }, startedAt, budget, failures);

        Assert.Equal(1, subsequentInvocations);
        Assert.Equal(new[] { "host-IOException" }, failures);
        await WaitForPendingCountAsync(lifetime, 0);
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
