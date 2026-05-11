using DependencyInjectionControllers;
using ManyDependencyInjectionControllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SimpleApiController;

namespace AppSurfaceBenchmarks.Web.NativeDotnet;

public class NativeDotnetServer : IWebBenchmarkServer
{
    private WebApplication? _app;

    public async Task StartMinimalAsync()
    {
        var builder = WebApplication.CreateBuilder();
        _app = builder.Build();
        _app.MapGet("/hello", () => "Hello World!");

        await _app.StartAsync();
    }

    public async Task StartControllersAsync()
    {
        var builder = WebApplication.CreateBuilder();
        var mvc = builder.Services.AddControllers();
        mvc.AddApplicationPart(typeof(HelloController).Assembly);

        _app = builder.Build();

        _app.UseRouting();
        _app.MapControllers();

        await _app.StartAsync();
    }

    public async Task StartDependencyInjectionAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IMyDependencyService, MyDependencyService>();
        var mvc = builder.Services.AddControllers();
        mvc.AddApplicationPart(typeof(DependencyInjectionController).Assembly);

        _app = builder.Build();

        _app.UseRouting();
        _app.MapControllers();

        await _app.StartAsync();
    }

    public async Task StartManyDependencyInjectionAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddManyDiServices();
        var mvc = builder.Services.AddControllers();
        mvc.AddApplicationPart(typeof(ManyInjected01Controller).Assembly);

        _app = builder.Build();

        _app.UseRouting();
        _app.MapControllers();

        await _app.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
