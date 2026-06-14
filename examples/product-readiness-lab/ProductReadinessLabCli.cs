using ForgeTrust.AppSurface.Flow.DurableTask;

namespace ProductReadinessLab;

/// <summary>
/// Command-line helpers for the product-readiness lab.
/// </summary>
internal static class ProductReadinessLabCli
{
    /// <summary>
    /// Returns whether the arguments request a readiness report.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns><see langword="true" /> when the report command was requested.</returns>
    public static bool IsReportCommand(string[] args) =>
        args.Any(arg => string.Equals(arg, "--report", StringComparison.Ordinal));

    /// <summary>
    /// Builds and writes the readiness report.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The report exit code.</returns>
    public static async Task<int> RunReportAsync(string[] args)
    {
        _ = args;
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddAuthorization();
        services.AddProductReadinessLab("Development", isDevelopment: true);
        services.AddSingleton<IFlowResumeAuthorizer, ProductReadinessResumeAuthorizer>();

        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<ProductReadinessReportService>().BuildAsync();
        Console.WriteLine(ReadinessReportMarkdownRenderer.Render(report));

        return ReadinessReportExitCodePolicy.GetExitCode(report);
    }
}
