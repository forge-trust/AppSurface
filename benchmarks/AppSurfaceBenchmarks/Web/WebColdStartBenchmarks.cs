using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;

#if APPSURFACE_WEB
using AppSurfaceBenchmarks.Web.AppSurfaceWeb;
#elif CARTER_WEB
using AppSurfaceBenchmarks.Web.Carter;
#elif ABP_WEB
using AppSurfaceBenchmarks.Web.Abp;
#else
using AppSurfaceBenchmarks.Web.NativeDotnet;
#endif

namespace AppSurfaceBenchmarks.Web;

[MemoryDiagnoser]
[CategoriesColumn]
[HideColumns(Column.Arguments)]
public class WebColdStartBenchmarks
{
    private HttpClient _client = new();

    private static readonly IWebBenchmarkServer _server =
#if APPSURFACE_WEB
        new AppSurfaceWebServer();
#elif CARTER_WEB
        new CarterServer();
#elif ABP_WEB
        new AbpServer();
#else
        new NativeDotnetServer();
#endif

    [Benchmark(Description = "Minimal_Endpoint")]
    [BenchmarkCategory("Minimal API")]
    public async Task Minimal()
    {
        await _server.StartMinimalAsync();
        await TestEndpoint("hello");
        await _server.StopAsync();
    }

    [Benchmark(Description = "One_Controller")]
    [BenchmarkCategory("Controllers")]
    public async Task Controllers()
    {
        await _server.StartControllersAsync();
        await TestEndpoint("api/hello");
        await _server.StopAsync();
    }

    [Benchmark(Description = "Dependency_Injection")]
    [BenchmarkCategory("Dependency Injection")]
    public async Task DependencyInjection()
    {
        await _server.StartDependencyInjectionAsync();
        await TestEndpoint("api/injected");
        await _server.StopAsync();
    }

    [Benchmark(Description = "Many_Dependency_Injection")]
    [BenchmarkCategory("Dependency Injection")]
    public async Task ManyDependencyInjection()
    {
        await _server.StartManyDependencyInjectionAsync();
        for (int i = 1; i <= 25; i++)
        {
            await TestEndpoint($"api/many/injected/{i}");
        }
        await _server.StopAsync();
    }

    // #if ABP_WEB
    //     [Benchmark(Description = "Abp_Controller", Baseline = _isBaseLine)]
    //     [BenchmarkCategory("Controllers")]
    //     public async Task AbpControllerBase()
    //     {
    //         var server = new AbpServer();
    //         await server.StartAbpControllersAsync();
    //         await TestEndpoint("api/hello");
    //         await server.StopAsync();
    //     }
    // #endif

    private async Task TestEndpoint(string endpoint)
    {
        var result = await _client.GetAsync($"http://localhost:5000/{endpoint}");
        result.EnsureSuccessStatusCode();
    }
}
