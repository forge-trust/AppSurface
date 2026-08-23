using System.Runtime.Loader;

namespace AuthAspireKeycloakReadinessGate;

/// <summary>
/// Entry point for the finite sample-only readiness gate.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs one readiness attempt and returns its process-safe status code.
    /// </summary>
    public static async Task<int> Main()
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Action<AssemblyLoadContext> unloadingHandler = _ => cancellation.Cancel();
        EventHandler processExitHandler = (_, _) => cancellation.Cancel();
        Console.CancelKeyPress += cancelHandler;
        AssemblyLoadContext.Default.Unloading += unloadingHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            return await KeycloakReadinessGateRunner.RunAsync(
                Environment.GetEnvironmentVariable,
                Console.Error,
                cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AssemblyLoadContext.Default.Unloading -= unloadingHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        }
    }
}
