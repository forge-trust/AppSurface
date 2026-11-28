using System.Diagnostics;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Confirms process exit and end-of-stream before the command owner releases its resources.</summary>
internal static class EvidenceProcessShutdown
{
    /// <summary>Terminates the owned process tree if necessary and checks the output capture.</summary>
    /// <param name="process">The started process owned by the command.</param>
    /// <param name="readers">Readers that complete successfully only after observing end-of-stream.</param>
    /// <param name="timeout">The maximum wait for each of exit and capture.</param>
    /// <param name="captureFailureCode">The stable failure code to use when output closure cannot be confirmed.</param>
    internal static Task ConfirmAsync(Process process, IReadOnlyList<Task> readers, TimeSpan timeout,
        string captureFailureCode = EvidenceCommandException.CleanupFailedCode) =>
        ConfirmAsync(() => TerminateIfRunning(process), () => process.WaitForExitAsync(), readers, timeout, captureFailureCode);

    /// <summary>Observes termination, exit and capture independently of a canceled command deadline.</summary>
    /// <param name="terminate">Requests termination if the process is still running.</param>
    /// <param name="waitForExit">Waits for the owned process to exit.</param>
    /// <param name="readers">Readers that complete successfully only after observing end-of-stream.</param>
    /// <param name="timeout">The maximum wait for each of exit and capture.</param>
    /// <param name="captureFailureCode">The stable failure code to use when output closure cannot be confirmed.</param>
    internal static async Task ConfirmAsync(Action terminate, Func<Task> waitForExit, IReadOnlyList<Task> readers, TimeSpan timeout,
        string captureFailureCode = EvidenceCommandException.CleanupFailedCode)
    {
        try
        {
            terminate();
            await waitForExit().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new EvidenceCommandException(EvidenceCommandException.CleanupFailedCode, "The evidence command process exit could not be confirmed.");
        }

        try { await Task.WhenAll(readers).WaitAsync(timeout).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new EvidenceCommandException(captureFailureCode, "The evidence command output capture could not confirm pipe closure.",
                cleanupUnconfirmed: true);
        }
    }

    private static void TerminateIfRunning(Process process)
    {
        if (process.HasExited)
            return;

        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The process exited between observation and termination.
        }
    }
}
