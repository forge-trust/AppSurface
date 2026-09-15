using System.Diagnostics.CodeAnalysis;

namespace AuthAspireKeycloakLifecycleWorker;

/// <summary>
/// Starts the private finite worker used by the #782 feasibility AppHost graph.
/// </summary>
[ExcludeFromCodeCoverage(
    Justification = "The executable entry point only forwards the AppHost-projected mode to the covered sample worker runner.")]
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
