#if APPSURFACE_WEB
using DependencyInjectionControllers;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using ManyDependencyInjectionControllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppSurfaceBenchmarks.Web.AppSurfaceWeb;

public class AppSurfaceWebServer : IWebBenchmarkServer
{
    private IHost? _host;

    /// <summary>
    /// Starts the historical minimal AppSurface benchmark host with default health options on the fixed
    /// <c>http://localhost:5000/</c> address.
    /// </summary>
    /// <remarks>
    /// Use this overload only for legacy benchmark cases that depend on the historical fixed-port host. Comparisons that
    /// vary health-probe configuration should use <see cref="StartMinimalAsync(bool)"/> and send requests to its returned
    /// ephemeral address.
    /// <para>
    /// Call <see cref="StopAsync"/> before starting another host with this server instance.
    /// </para>
    /// </remarks>
    public async Task StartMinimalAsync()
    {
        _ = await StartMinimalAsync(healthEnabled: null, useEphemeralPort: false);
    }

    /// <summary>
    /// Starts the minimal AppSurface benchmark host with the opt-in platform health endpoints either enabled or disabled.
    /// </summary>
    /// <param name="healthEnabled">
    /// <see langword="true"/> to include health-check services plus the default <c>/health</c> and <c>/ready</c> endpoints;
    /// otherwise, <see langword="false"/>.
    /// </param>
    /// <returns>
    /// The ephemeral loopback base address selected after the benchmark host starts. Use this address for benchmark
    /// requests instead of assuming a fixed port.
    /// </returns>
    /// <remarks>
    /// Use this overload for health A/B comparisons so both cases receive identical ephemeral-port behavior and the
    /// caller does not depend on the legacy fixed port.
    /// <para>
    /// Call <see cref="StopAsync"/> before starting another host with this server instance.
    /// </para>
    /// </remarks>
    public Task<Uri> StartMinimalAsync(bool healthEnabled) =>
        StartMinimalAsync(healthEnabled, useEphemeralPort: true);

    private async Task<Uri> StartMinimalAsync(bool? healthEnabled, bool useEphemeralPort)
    {
        var startup = new BenchmarkWebStartup<AppSurfaceBenchmarkModule>()
            .WithOptions(options =>
            {
                // Disabling MVC support when testing minimal APIs to avoid any overhead.
                options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.None };
                if (healthEnabled.HasValue)
                {
                    options.Health.Enabled = healthEnabled.Value;
                }

                options.MapEndpoints = endpoints => { endpoints.MapGet("/hello", () => "Hello World!"); };
            });

        // We need to create the host builder manually to get access to start/stop.
        var context = new StartupContext(WebBenchmarkHostConfiguration.CreateArguments(), new AppSurfaceBenchmarkModule());
        var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        if (useEphemeralPort)
        {
            builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));
        }

        _host = builder.Build();

        await _host.StartAsync();

        if (!useEphemeralPort)
        {
            return new Uri("http://localhost:5000/");
        }

        var addresses = _host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        var address = addresses?.SingleOrDefault()
            ?? throw new InvalidOperationException("The health A/B benchmark host did not publish one loopback address.");
        return new Uri(address.EndsWith("/", StringComparison.Ordinal) ? address : $"{address}/");
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
        var context = new StartupContext(WebBenchmarkHostConfiguration.CreateArguments(), new AppSurfaceBenchmarkModule());
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
        var context = new StartupContext(WebBenchmarkHostConfiguration.CreateArguments(), new AppSurfaceDependencyBenchmarkModule());
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

        var context = new StartupContext(WebBenchmarkHostConfiguration.CreateArguments(), new AppSurfaceManyDependencyBenchmarkModule());
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
