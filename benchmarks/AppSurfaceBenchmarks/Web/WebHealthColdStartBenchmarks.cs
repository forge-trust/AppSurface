using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;

#if APPSURFACE_WEB
using AppSurfaceBenchmarks.Web.AppSurfaceWeb;
#endif

namespace AppSurfaceBenchmarks.Web;

/// <summary>
/// Measures the incremental cold-start cost of AppSurface Web's opt-in platform health endpoints.
/// </summary>
/// <remarks>
/// Both cases start the same minimal AppSurface host and make the same first request. The disabled case is the baseline,
/// so the comparison targets health-check service registration and <c>/health</c> plus <c>/ready</c> endpoint mapping.
/// Small deltas may still fall within full-host cold-start variance and require repeated runs.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
public class WebHealthColdStartBenchmarks
{
#if APPSURFACE_WEB
    private readonly HttpClient _client = new();
    private static readonly AppSurfaceWebServer Server = new();
#endif

    /// <summary>Starts and exercises a minimal AppSurface host with platform health endpoints disabled.</summary>
    /// <returns>A task that completes after the host has served one request and stopped.</returns>
    [Benchmark(Baseline = true, Description = "Health_Disabled")]
    [BenchmarkCategory("AppSurface Health A/B")]
    public Task HealthDisabled() => RunAsync(healthEnabled: false);

    /// <summary>Starts and exercises a minimal AppSurface host with the opt-in platform health endpoints enabled.</summary>
    /// <returns>A task that completes after the host has served one request and stopped.</returns>
    [Benchmark(Description = "Health_Enabled")]
    [BenchmarkCategory("AppSurface Health A/B")]
    public Task HealthEnabled() => RunAsync(healthEnabled: true);

    private async Task RunAsync(bool healthEnabled)
    {
#if APPSURFACE_WEB
        var baseAddress = await Server.StartMinimalAsync(healthEnabled);
        try
        {
            using var response = await _client.GetAsync(new Uri(baseAddress, "hello"));
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            await Server.StopAsync();
        }
#else
        await Task.CompletedTask;
        throw new InvalidOperationException("The health A/B benchmark can run only in the AppSurface.Web job.");
#endif
    }
}
