namespace AppSurfaceBenchmarks.Web;

internal interface IWebBenchmarkServer
{
    Task StartMinimalAsync();
    Task StartControllersAsync();
    Task StopAsync();
    Task StartDependencyInjectionAsync();
    Task StartManyDependencyInjectionAsync();
}
