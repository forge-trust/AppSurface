namespace AuthAspireKeycloakLifecycleWorker;

/// <summary>
/// Runs the private finite-worker modes used to observe public Aspire completion behavior.
/// </summary>
internal static class LifecycleWorkerRunner
{
    internal const int SuccessExitCode = 0;
    internal const int FailureExitCode = 1;
    internal const int InvalidModeExitCode = 2;
    internal const int CancellationExitCode = 124;

    /// <summary>
    /// Runs the selected feasibility mode.
    /// </summary>
    /// <param name="mode">The private worker mode projected by the AppHost.</param>
    /// <param name="cancellationToken">Cancels a hanging worker during AppHost shutdown.</param>
    /// <returns>A finite process exit code, except while the hang mode awaits cancellation.</returns>
    internal static async Task<int> RunAsync(string? mode, CancellationToken cancellationToken)
    {
        try
        {
            return mode switch
            {
                AuthAspireKeycloakLifecycleWorkerEnvironment.Success => SuccessExitCode,
                AuthAspireKeycloakLifecycleWorkerEnvironment.Failure => FailureExitCode,
                AuthAspireKeycloakLifecycleWorkerEnvironment.Timeout => await TimeoutAsync(cancellationToken).ConfigureAwait(false),
                AuthAspireKeycloakLifecycleWorkerEnvironment.Hang => await HangAsync(cancellationToken).ConfigureAwait(false),
                _ => InvalidModeExitCode,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancellationExitCode;
        }
    }

    private static async Task<int> TimeoutAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        return FailureExitCode;
    }

    private static async Task<int> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return SuccessExitCode;
    }
}
