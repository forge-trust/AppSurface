namespace AuthAspireKeycloakLifecycleWorker;

/// <summary>
/// Starts the private finite worker used by the #782 feasibility AppHost graph.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the worker with its AppHost-projected private mode.
    /// </summary>
    /// <param name="args">Unused command-line arguments.</param>
    /// <returns>The finite mode exit code.</returns>
    public static Task<int> Main(string[] args) =>
        LifecycleWorkerRunner.RunAsync(
            Environment.GetEnvironmentVariable(AuthAspireKeycloakLifecycleWorkerEnvironment.Mode),
            CancellationToken.None);
}
