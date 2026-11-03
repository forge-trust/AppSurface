using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Original-operation ordering and exception controls; these create no native owner or physical-exit proof.</summary>
public sealed class SupervisionCleanupAttemptTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SuccessfulOperationsRunOnceInCallerOrderWithoutFailureRecording()
    {
        var order = new List<string>();
        SupervisionCleanupAttempt.Run(() => order.Add("sync"), _ => Assert.Fail("Unexpected failure."));
        await SupervisionCleanupAttempt.RunAsync(() =>
        {
            order.Add("async");
            return Task.CompletedTask;
        }, _ => Assert.Fail("Unexpected failure."));
        Assert.Equal(["sync", "async"], order);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("admission")]
    public async Task SynchronousDispatchAndAsynchronousFaultsRetainTheOriginalException(string kind)
    {
        Exception expected = kind switch
        {
            "io" => new IOException("private-cleanup-canary"),
            "cancel" => new OperationCanceledException(),
            _ => LinuxProtectedDeployment.InvalidDeployment(),
        };
        var errors = new List<Exception>();
        SupervisionCleanupAttempt.Run(() => throw expected, errors.Add);
        await SupervisionCleanupAttempt.RunAsync(() => throw expected, errors.Add);
        await SupervisionCleanupAttempt.RunAsync(() => Task.FromException(expected), errors.Add);
        Assert.Equal(3, errors.Count);
        Assert.All(errors, actual => Assert.Same(expected, actual));
    }

    [Fact]
    public async Task IgnoredCallerCancellationCannotSubstituteForJoiningTheOriginalCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        var failures = new List<Exception>();
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Original()
        {
            using var registration = caller.Token.Register(() => cancellationObserved.TrySetResult());
            entered.SetResult();
            await release.Task;
        }
        var task = SupervisionCleanupAttempt.RunAsync(Original, failures.Add);
        try
        {
            await entered.Task.WaitAsync(Guard);
            caller.Cancel();
            await cancellationObserved.Task.WaitAsync(Guard);
            Assert.False(task.IsCompleted);
            Assert.Empty(failures);
        }
        finally
        {
            release.TrySetResult();
            await task.WaitAsync(Guard);
        }
        Assert.Empty(failures);
    }

    [Fact]
    public async Task FailureRecorderRunsBeforeTheNextAttemptAndCannotClearTheCallerLatch()
    {
        var order = new List<string>();
        var failed = false;
        var first = new IOException();
        await SupervisionCleanupAttempt.RunAsync(() => Task.FromException(first), error =>
        {
            Assert.Same(first, error);
            order.Add("record");
            failed = true;
        });
        await SupervisionCleanupAttempt.RunAsync(() =>
        {
            Assert.True(failed);
            order.Add("next");
            return Task.CompletedTask;
        }, _ => Assert.Fail("Unexpected failure."));
        Assert.True(failed);
        Assert.Equal(["record", "next"], order);
    }

    [Fact]
    public async Task FatalRuntimeErrorsAreNeverConvertedIntoRecoverableCleanup()
    {
        var expected = new OutOfMemoryException();
        var records = 0;
        Assert.Same(expected, Assert.Throws<OutOfMemoryException>(() =>
            SupervisionCleanupAttempt.Run(() => throw expected, _ => records++)));
        Assert.Same(expected, await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            SupervisionCleanupAttempt.RunAsync(() => Task.FromException(expected), _ => records++)));
        Assert.Equal(0, records);
    }

    [Fact]
    public async Task FailureRecorderExceptionsPropagateWithoutASecondCatchOrRetry()
    {
        var expected = new InvalidOperationException("recorder");
        var records = 0;
        void Record(Exception _)
        {
            records++;
            throw expected;
        }
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            SupervisionCleanupAttempt.Run(() => throw new IOException(), Record)));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SupervisionCleanupAttempt.RunAsync(() => Task.FromException(new IOException()), Record)));
        Assert.Equal(2, records);
    }

    [Fact]
    public async Task MissingOperationOrRecorderRejectsBeforeDispatch()
    {
        var calls = 0;
        Assert.Equal("operation", Assert.Throws<ArgumentNullException>(() =>
            SupervisionCleanupAttempt.Run(null!, _ => calls++)).ParamName);
        Assert.Equal("recordFailure", Assert.Throws<ArgumentNullException>(() =>
            SupervisionCleanupAttempt.Run(() => calls++, null!)).ParamName);
        Assert.Equal("operation", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SupervisionCleanupAttempt.RunAsync(null!, _ => calls++))).ParamName);
        Assert.Equal("recordFailure", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SupervisionCleanupAttempt.RunAsync(() => { calls++; return Task.CompletedTask; }, null!))).ParamName);
        Assert.Equal(0, calls);
    }
}
