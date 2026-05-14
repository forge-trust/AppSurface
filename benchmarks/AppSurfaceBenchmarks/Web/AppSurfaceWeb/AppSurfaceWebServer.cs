#if APPSURFACE_WEB
using DependencyInjectionControllers;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using ManyDependencyInjectionControllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppSurfaceBenchmarks.Web.AppSurfaceWeb;

public class AppSurfaceWebServer : IWebBenchmarkServer
{
    private IHost? _host;

    public async Task StartMinimalAsync()
    {
        var startup = new BenchmarkWebStartup<AppSurfaceBenchmarkModule>()
            .WithOptions(options =>
            {
                // Disabling MVC support when testing minimal APIs to avoid any overhead.
                options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.None };

                options.MapEndpoints = endpoints => { endpoints.MapGet("/hello", () => "Hello World!"); };
            });

        // We need to create the host builder manually to get access to start/stop.
        var context = new StartupContext([], new AppSurfaceBenchmarkModule());
        _host = ((IAppSurfaceStartup)startup).CreateHostBuilder(context).Build();

        await _host.StartAsync();
    }

    public async Task StartControllersAsync()
    {
        var startup = new BenchmarkWebStartup<AppSurfaceBenchmarkModule>()
            .WithOptions(options =>
            {
                options.Mvc = options.Mvc with
                {
                    ConfigureMvc = mvc =>
                    {
                        mvc.AddApplicationPart(typeof(SimpleApiController.HelloController).Assembly);
                    }
                };
            });

        // We need to create the host builder manually to get access to start/stop.
        var context = new StartupContext([], new AppSurfaceBenchmarkModule());
        _host = ((IAppSurfaceStartup)startup).CreateHostBuilder(context).Build();

        await _host.StartAsync();
    }

    public async Task StartDependencyInjectionAsync()
    {
        var startup = new BenchmarkWebStartup<AppSurfaceDependencyBenchmarkModule>()
            .WithOptions(options =>
            {
                options.Mvc = options.Mvc with
                {
                    ConfigureMvc = mvc =>
                    {
                        mvc.AddApplicationPart(typeof(DependencyInjectionController).Assembly);
                    }
                };
            });

        // We need to create the host builder manually to get access to start/stop.
        var context = new StartupContext([], new AppSurfaceDependencyBenchmarkModule());
        _host = ((IAppSurfaceStartup)startup).CreateHostBuilder(context).Build();

        await _host.StartAsync();
    }

    public async Task StartManyDependencyInjectionAsync()
    {
        var startup = new BenchmarkWebStartup<AppSurfaceManyDependencyBenchmarkModule>()
            .WithOptions(options =>
            {
                options.Mvc = options.Mvc with
                {
                    ConfigureMvc = mvc => { mvc.AddApplicationPart(typeof(ManyInjected01Controller).Assembly); }
                };
            });

        var context = new StartupContext([], new AppSurfaceManyDependencyBenchmarkModule());
        _host = ((IAppSurfaceStartup)startup).CreateHostBuilder(context).Build();

        await _host.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private class BenchmarkWebStartup<T> : WebStartup<T>
        where T : IAppSurfaceWebModule, new();

    private class AppSurfaceBenchmarkModule : IAppSurfaceWebModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }
    }

    private class AppSurfaceDependencyBenchmarkModule : IAppSurfaceWebModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services.AddSingleton<IMyDependencyService, MyDependencyService>();
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }
    }

    private class AppSurfaceManyDependencyBenchmarkModule : IAppSurfaceWebModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services.AddManyDiServices();
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
        {
        }
    }
}
#endif
