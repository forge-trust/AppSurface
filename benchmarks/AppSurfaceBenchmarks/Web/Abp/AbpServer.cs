#if ABP_WEB
using AbpSimpleController;
using AppSurfaceBenchmarks.Web;
using DependencyInjectionControllers;
using ManyDependencyInjectionControllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SimpleApiController;
using Volo.Abp.AspNetCore;
using Volo.Abp.Modularity;

namespace AppSurfaceBenchmarks.Web.Abp;

public class AbpServer : IWebBenchmarkServer
{
    private WebApplication? _app;

    public async Task StartMinimalAsync()
    {
        var builder = WebApplication.CreateBuilder();
        await builder.Services.AddApplicationAsync<AbpBenchmarkModule>();
        _app = builder.Build();

        _app.MapGet("/hello", () => "Hello World!");

        await _app.InitializeApplicationAsync();
        await _app.StartAsync();
    }

    public async Task StartControllersAsync()
    {
        var builder = WebApplication.CreateBuilder();
        var mvc = builder.Services.AddControllers();
        mvc.AddApplicationPart(typeof(HelloController).Assembly);
        await builder.Services.AddApplicationAsync<AbpBenchmarkModule>();
        _app = builder.Build();

        _app.UseRouting();
        _app.UseConfiguredEndpoints(ep => ep.MapControllers());

        await _app.InitializeApplicationAsync();
        await _app.StartAsync();
    }

    public async Task StartAbpControllersAsync()
    {
        var builder = WebApplication.CreateBuilder();
        var mvc = builder.Services.AddControllers();
        mvc.AddApplicationPart(typeof(HelloAbpController).Assembly);
        await builder.Services.AddApplicationAsync<AbpBenchmarkModule>();
        _app = builder.Build();

        _app.UseRouting();
        _app.UseConfiguredEndpoints(ep => ep.MapControllers());

        await _app.InitializeApplicationAsync();
        await _app.StartAsync();
    }

    public async Task StartDependencyInjectionAsync()
    {
        var builder = WebApplication.CreateBuilder();
        var mvc = builder.Services.AddControllers();
        mvc.AddApplicationPart(typeof(DependencyInjectionController).Assembly);
        await builder.Services.AddApplicationAsync<AbpDependencyModule>();
        _app = builder.Build();

        _app.UseRouting();
        _app.UseConfiguredEndpoints(ep => ep.MapControllers());

        await _app.InitializeApplicationAsync();
        await _app.StartAsync();
    }

    public async Task StartManyDependencyInjectionAsync()
    {
        var builder = WebApplication.CreateBuilder();
        var mvc = builder.Services.AddControllers();
        mvc.AddApplicationPart(typeof(ManyInjected01Controller).Assembly);
        await builder.Services.AddApplicationAsync<AbpManyDependencyModule>();
        _app = builder.Build();

        _app.UseRouting();
        _app.UseConfiguredEndpoints(ep => ep.MapControllers());

        await _app.InitializeApplicationAsync();
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

[DependsOn(typeof(AbpAspNetCoreModule))]
public class AbpBenchmarkModule : AbpModule
{
}

[DependsOn(typeof(AbpAspNetCoreModule))]
public class AbpDependencyModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton<IMyDependencyService, MyDependencyService>();
    }
}

[DependsOn(typeof(AbpAspNetCoreModule))]
public class AbpManyDependencyModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddManyDiServices();
    }
}
#endif
