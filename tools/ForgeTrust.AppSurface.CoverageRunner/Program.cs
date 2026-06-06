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
    internal static Task<int> Main(string[] args) => RunAsync(args, Console.Out, Console.Error);

    /// <summary>
    /// Launches the coverage runner with injectable output writers.
    /// </summary>
    /// <param name="args">Command-line arguments supplied by <c>scripts/coverage-solution.sh</c>.</param>
    /// <param name="standardOut">Writer used for normal runner output.</param>
    /// <param name="standardError">Writer used for diagnostics and failures.</param>
    /// <returns>Process exit code where <c>0</c> indicates success.</returns>
    internal static async Task<int> RunAsync(string[] args, TextWriter standardOut, TextWriter standardError)
    {
        var environment = CreateEnvironmentSnapshot(Environment.GetEnvironmentVariables());
        var app = new CoverageRunnerApplication(
            new ProcessCommandRunner(),
            new SystemClock(),
            standardOut,
            standardError);

        return await app.RunAsync(args, Directory.GetCurrentDirectory(), environment);
    }

    /// <summary>
    /// Creates a snapshot of process environment variables using case-insensitive keys to match
    /// platform environment lookup behavior on Windows and macOS. If an unusual environment
    /// contains duplicate keys that differ only by case, the later enumerated value wins so snapshot
    /// creation remains best-effort instead of failing before argument parsing can report errors.
    /// </summary>
    /// <param name="variables">Environment variables returned by <see cref="Environment.GetEnvironmentVariables()"/>.</param>
    /// <returns>Case-insensitive environment snapshot used by runner option parsing.</returns>
    internal static IReadOnlyDictionary<string, string?> CreateEnvironmentSnapshot(IDictionary variables)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in variables)
        {
            environment[(string)entry.Key] = entry.Value as string;
        }

        return environment;
    }
}
