using System.Collections;

namespace ForgeTrust.AppSurface.CoverageRunner;

/// <summary>
/// CLI entry point for AppSurface solution coverage execution and coverage artifact merging.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Launches the coverage runner with process IO and environment variables.
    /// </summary>
    /// <param name="args">Command-line arguments supplied by <c>scripts/coverage-solution.sh</c>.</param>
    /// <returns>Process exit code where <c>0</c> indicates success.</returns>
    internal static async Task<int> Main(string[] args)
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string);
        var app = new CoverageRunnerApplication(
            new ProcessCommandRunner(),
            new SystemClock(),
            Console.Out,
            Console.Error);

        return await app.RunAsync(args, Directory.GetCurrentDirectory(), environment);
    }
}
