namespace AppSurfaceBenchmarks.Web;

/// <summary>
/// Provides host configuration shared by every web benchmark implementation.
/// </summary>
internal static class WebBenchmarkHostConfiguration
{
    /// <summary>
    /// Creates command-line arguments that disable automatic reload of default JSON configuration files.
    /// </summary>
    /// <returns>
    /// A new argument array suitable for <c>WebApplication.CreateBuilder</c> or an AppSurface
    /// <c>StartupContext</c>.
    /// </returns>
    /// <remarks>
    /// Web benchmarks create a complete host for each sample. Disabling configuration reload prevents file-watcher
    /// callbacks from adding unrelated timing and allocation noise, and applying the same argument to every benchmark
    /// implementation keeps framework comparisons symmetric.
    /// </remarks>
    internal static string[] CreateArguments() => ["--hostBuilder:reloadConfigOnChange=false"];
}
