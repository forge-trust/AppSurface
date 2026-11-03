namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Original-task join controls only; no native process, physical settlement or custody is issued.</summary>
public sealed class LinuxWorkerMonitorJoinTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CompletedOriginalMonitorJoinsWithoutCancellation()
    {
        Assert.True(await LinuxWorkerProcess.JoinOriginalExitMonitorAsync(Task.CompletedTask, default));
    }

    [Fact]
    public async Task JobCancellationCannotDetachTheOriginalPendingMonitor()
    {
        using var job = new CancellationTokenSource();
        var monitor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var join = LinuxWorkerProcess.JoinOriginalExitMonitorAsync(monitor.Task, job.Token);
        try
        {
            job.Cancel();
            Assert.False(join.IsCompleted);
            Assert.False(monitor.Task.IsCompleted);
        }
        finally
        {
            monitor.TrySetCanceled(job.Token);
            await join.WaitAsync(Guard);
        }
        Assert.True(await join);
        Assert.True(monitor.Task.IsCanceled);
    }

    [Fact]
    public async Task UnexpectedMonitorCancellationIsStillRejected()
    {
        using var other = new CancellationTokenSource();
        other.Cancel();
        Assert.False(await LinuxWorkerProcess.JoinOriginalExitMonitorAsync(Task.FromCanceled(other.Token), default));
    }

    [Fact]
    public async Task FaultedCancellationExceptionCannotMasqueradeAsOriginalTaskCancellation()
    {
        using var job = new CancellationTokenSource();
        job.Cancel();
        var monitor = Task.FromException(new OperationCanceledException(job.Token));
        Assert.False(await LinuxWorkerProcess.JoinOriginalExitMonitorAsync(monitor, job.Token));
        Assert.True(monitor.IsFaulted);
    }

    [Fact]
    public async Task ConcurrentJobCancellationCannotEraseANativeIntegrityFault()
    {
        using var job = new CancellationTokenSource();
        job.Cancel();
        var monitor = Task.FromException(new InvalidOperationException("private-canary"));
        Assert.False(await LinuxWorkerProcess.JoinOriginalExitMonitorAsync(monitor, job.Token));
        Assert.True(monitor.IsFaulted);
    }

    [Fact]
    public async Task MissingOriginalTaskIsRejected()
    {
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            LinuxWorkerProcess.JoinOriginalExitMonitorAsync(null!, default));
        Assert.Equal("monitor", error.ParamName);
    }
}
